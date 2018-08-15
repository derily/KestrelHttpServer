// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO.Pipelines;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Http2.HPack;
using Microsoft.AspNetCore.Server.Kestrel.Core.Internal.Infrastructure;
using Microsoft.AspNetCore.Server.Kestrel.Transport.Abstractions.Internal;
using Microsoft.AspNetCore.Testing;
using Microsoft.Net.Http.Headers;
using Moq;
using Xunit.Abstractions;

namespace Microsoft.AspNetCore.Server.Kestrel.Core.Tests
{
    public class Http2TestBase : TestApplicationErrorLoggerLoggedTest, IDisposable, IHttpHeadersHandler
    {
        protected static readonly string _largeHeaderValue = new string('a', HPackDecoder.MaxStringOctets);

        protected static readonly IEnumerable<KeyValuePair<string, string>> _browserRequestHeaders = new[]
        {
            new KeyValuePair<string, string>(HeaderNames.Method, "GET"),
            new KeyValuePair<string, string>(HeaderNames.Path, "/"),
            new KeyValuePair<string, string>(HeaderNames.Scheme, "http"),
            new KeyValuePair<string, string>(HeaderNames.Authority, "localhost:80"),
            new KeyValuePair<string, string>("user-agent", "Mozilla/5.0 (X11; Ubuntu; Linux x86_64; rv:54.0) Gecko/20100101 Firefox/54.0"),
            new KeyValuePair<string, string>("accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8"),
            new KeyValuePair<string, string>("accept-language", "en-US,en;q=0.5"),
            new KeyValuePair<string, string>("accept-encoding", "gzip, deflate, br"),
            new KeyValuePair<string, string>("upgrade-insecure-requests", "1"),
        };

        private readonly MemoryPool<byte> _memoryPool = KestrelMemoryPool.Create();
        internal readonly DuplexPipe.DuplexPipePair _pair;

        protected readonly Http2PeerSettings _clientSettings = new Http2PeerSettings();
        protected readonly HPackEncoder _hpackEncoder = new HPackEncoder();
        protected readonly HPackDecoder _hpackDecoder;

        protected readonly ConcurrentDictionary<int, TaskCompletionSource<object>> _runningStreams = new ConcurrentDictionary<int, TaskCompletionSource<object>>();
        protected readonly Dictionary<string, string> _decodedHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        protected readonly HashSet<int> _abortedStreamIds = new HashSet<int>();
        protected readonly object _abortedStreamIdsLock = new object();
        protected readonly TaskCompletionSource<object> _closingStateReached = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);
        protected readonly TaskCompletionSource<object> _closedStateReached = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        protected readonly RequestDelegate _noopApplication;
        protected readonly RequestDelegate _waitForAbortApplication;
        protected readonly RequestDelegate _waitForAbortFlushingApplication;
        protected readonly RequestDelegate _waitForAbortWithDataApplication;

        protected Task _connectionTask;
        protected Http2ConnectionContext _connectionContext;
        protected Http2Connection _connection;

        public Http2TestBase()
        {
            // Always dispatch test code back to the ThreadPool. This prevents deadlocks caused by continuing
            // Http2Connection.ProcessRequestsAsync() loop with writer locks acquired. Run product code inline to make
            // it easier to verify request frames are processed correctly immediately after sending the them.
            var inputPipeOptions = new PipeOptions(
                pool: _memoryPool,
                readerScheduler: PipeScheduler.Inline,
                writerScheduler: PipeScheduler.ThreadPool,
                useSynchronizationContext: false
            );
            var outputPipeOptions = new PipeOptions(
                pool: _memoryPool,
                readerScheduler: PipeScheduler.ThreadPool,
                writerScheduler: PipeScheduler.Inline,
                useSynchronizationContext: false
            );

            _pair = DuplexPipe.CreateConnectionPair(inputPipeOptions, outputPipeOptions);
            _hpackDecoder = new HPackDecoder((int)_clientSettings.HeaderTableSize);

            _noopApplication = context => Task.CompletedTask;

            _waitForAbortApplication = async context =>
            {
                var streamIdFeature = context.Features.Get<IHttp2StreamIdFeature>();
                var sem = new SemaphoreSlim(0);

                context.RequestAborted.Register(() =>
                {
                    lock (_abortedStreamIdsLock)
                    {
                        _abortedStreamIds.Add(streamIdFeature.StreamId);
                    }

                    sem.Release();
                });

                await sem.WaitAsync().DefaultTimeout();

                _runningStreams[streamIdFeature.StreamId].TrySetResult(null);
            };

            _waitForAbortFlushingApplication = async context =>
            {
                var streamIdFeature = context.Features.Get<IHttp2StreamIdFeature>();
                var sem = new SemaphoreSlim(0);

                context.RequestAborted.Register(() =>
                {
                    lock (_abortedStreamIdsLock)
                    {
                        _abortedStreamIds.Add(streamIdFeature.StreamId);
                    }

                    sem.Release();
                });

                await sem.WaitAsync().DefaultTimeout();

                await context.Response.Body.FlushAsync();

                _runningStreams[streamIdFeature.StreamId].TrySetResult(null);
            };

            _waitForAbortWithDataApplication = async context =>
            {
                var streamIdFeature = context.Features.Get<IHttp2StreamIdFeature>();
                var sem = new SemaphoreSlim(0);

                context.RequestAborted.Register(() =>
                {
                    lock (_abortedStreamIdsLock)
                    {
                        _abortedStreamIds.Add(streamIdFeature.StreamId);
                    }

                    sem.Release();
                });

                await sem.WaitAsync().DefaultTimeout();

                await context.Response.Body.WriteAsync(new byte[10], 0, 10);

                _runningStreams[streamIdFeature.StreamId].TrySetResult(null);
            };
        }

        public override void Initialize(MethodInfo methodInfo, object[] testMethodArguments, ITestOutputHelper testOutputHelper)
        {
            base.Initialize(methodInfo, testMethodArguments, testOutputHelper);

            var mockKestrelTrace = new Mock<IKestrelTrace>();
            mockKestrelTrace
                .Setup(m => m.Http2ConnectionClosing(It.IsAny<string>()))
                .Callback(() => _closingStateReached.SetResult(null));
            mockKestrelTrace
                .Setup(m => m.Http2ConnectionClosed(It.IsAny<string>(), It.IsAny<int>()))
                .Callback(() => _closedStateReached.SetResult(null));

            _connectionContext = new Http2ConnectionContext
            {
                ConnectionFeatures = new FeatureCollection(),
                ServiceContext = new TestServiceContext(LoggerFactory, mockKestrelTrace.Object),
                MemoryPool = _memoryPool,
                Application = _pair.Application,
                Transport = _pair.Transport
            };

            _connection = new Http2Connection(_connectionContext);
        }

        public override void Dispose()
        {
            _pair.Application.Input.Complete();
            _pair.Application.Output.Complete();
            _pair.Transport.Input.Complete();
            _pair.Transport.Output.Complete();
            _memoryPool.Dispose();

            base.Dispose();
        }

        void IHttpHeadersHandler.OnHeader(Span<byte> name, Span<byte> value)
        {
            _decodedHeaders[name.GetAsciiStringNonNullCharacters()] = value.GetAsciiStringNonNullCharacters();
        }
    }
}
