// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Net.Http.DoNotUseInProduction.TestingOnly;
using System.Net.Http.Headers;
using System.Net.Http.HPack;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    /// <summary>Provides a pool of connections to the same endpoint.</summary>
    internal sealed partial class HttpConnectionPool : IDisposable
    {
        /// <summary>The maximum number of times to retry a request after a failure on an established connection.</summary>
        private const int MaxConnectionFailureRetries = 3;
        public const int DefaultHttpPort = 80;
        public const int DefaultHttpsPort = 443;

        private readonly HttpConnectionPoolManager _poolManager;
        private readonly HttpConnectionKind _kind;
        private readonly string? _telemetryServerAddress;

        /// <summary>The origin authority used to construct the <see cref="HttpConnectionPool"/>.</summary>
        private readonly HttpAuthority _originAuthority;

        // These settings are advertised by the server via SETTINGS_MAX_HEADER_LIST_SIZE and SETTINGS_MAX_FIELD_SECTION_SIZE.
        // If we had previous connections to the same host in this pool, memorize the last value seen.
        // This value is used as an initial value for new connections before they have a chance to observe the SETTINGS frame.
        // Doing so avoids immediately exceeding the server limit on the first request, potentially causing the connection to be torn down.
        // 0 means there were no previous connections, or they hadn't advertised this limit.
        // There is no need to lock when updating these values - we're only interested in saving _a_ value, not necessarily the min/max/last.
        internal uint _lastSeenHttp2MaxHeaderListSize;

        /// <summary>Whether the pool has been used since the last time a cleanup occurred.</summary>
        private bool _usedSinceLastCleanup = true;
        /// <summary>Whether the pool has been disposed.</summary>
        private bool _disposed;

        /// <summary>Initializes the pool.</summary>
        /// <param name="poolManager">The manager associated with this pool.</param>
        /// <param name="kind">The kind of HTTP connections stored in this pool.</param>
        /// <param name="host">The host with which this pool is associated.</param>
        /// <param name="port">The port with which this pool is associated.</param>
        /// <param name="sslHostName">The SSL host with which this pool is associated.</param>
        /// <param name="telemetryServerAddress">The value of the 'server.address' tag to be emitted by Metrics and Distributed Tracing.</param>
        public HttpConnectionPool(HttpConnectionPoolManager poolManager, HttpConnectionKind kind, string? host, int port, string? sslHostName, string? telemetryServerAddress)
        {
            _poolManager = poolManager;
            _kind = kind;
            _telemetryServerAddress = telemetryServerAddress;

            // The only case where 'host' will not be set is if this is a Proxy connection pool.
            Debug.Assert(host is not null);
            _originAuthority = new HttpAuthority(host!, port);

            switch (kind)
            {
                case HttpConnectionKind.Http:
                    Debug.Assert(host != null);
                    Debug.Assert(port != 0);
                    Debug.Assert(sslHostName == null);
                    break;

                case HttpConnectionKind.Https:
                    Debug.Assert(host != null);
                    Debug.Assert(port != 0);
                    Debug.Assert(sslHostName != null);
                    break;

                default:
                    Debug.Fail("Unknown HttpConnectionKind in HttpConnectionPool.ctor");
                    break;
            }

            string? hostHeader = null;
            if (host is not null)
            {
                // Precalculate ASCII bytes for Host header
                // Note that if _host is null, this is a (non-tunneled) proxy connection, and we can't cache the hostname.
                hostHeader = IsDefaultPort
                    ? _originAuthority.HostValue
                    : $"{_originAuthority.HostValue}:{_originAuthority.Port}";
            }

            if (hostHeader is not null)
            {
                _http2EncodedAuthorityHostHeader = HPackEncoder.EncodeLiteralHeaderFieldWithoutIndexingToAllocatedArray(H2StaticTable.Authority, hostHeader);
            }

            _http2RequestQueue = new RequestQueue<Http2Connection?>();
            _availableHttp2Connections = new List<Http2Connection>();

            if (NetEventSource.Log.IsEnabled()) Trace($"{this}");
        }

        public string? TelemetryServerAddress => _telemetryServerAddress;
        public HttpAuthority OriginAuthority => _originAuthority;
        public HttpConnectionSettings Settings => _poolManager.Settings;
        public HttpConnectionKind Kind => _kind;
        public bool IsSecure => _kind == HttpConnectionKind.Https;
        public bool IsDefaultPort => OriginAuthority.Port == (IsSecure ? DefaultHttpsPort : DefaultHttpPort);

        /// <summary>Object used to synchronize access to state in the pool.</summary>
        private object SyncObj
        {
            get
            {
                Debug.Assert(!Monitor.IsEntered(_availableHttp2Connections));
                return _availableHttp2Connections;
            }
        }

        public bool HasSyncObjLock => Monitor.IsEntered(_availableHttp2Connections);

        // Overview of connection management (mostly HTTP version independent):
        //
        // Each version of HTTP (1.1, 2, 3) has its own connection pool, and each of these work in a similar manner,
        // allowing for differences between the versions (most notably, HTTP/1.1 is not multiplexed.)
        //
        // When a request is submitted for a particular version (e.g. HTTP/1.1), we first look in the pool for available connections.
        // An "available" connection is one that is (hopefully) usable for a new request.
        //      For HTTP/1.1, this is just an idle connection.
        //      For HTTP2/3, this is a connection that (hopefully) has available streams to use for new requests.
        // If we find an available connection, we will attempt to validate it and then use it.
        //      We check the lifetime of the connection and discard it if the lifetime is exceeded.
        //      We check that the connection has not shut down; if so we discard it.
        //      For HTTP2/3, we reserve a stream on the connection. If this fails, we cannot use the connection right now.
        // If validation fails, we will attempt to find a different available connection.
        //
        // Once we have found a usable connection, we use it to process the request.
        //      For HTTP/1.1, a connection can handle only a single request at a time, thus it is immediately removed from the list of available connections.
        //      For HTTP2/3, a connection is only removed from the available list when it has no more available streams.
        //      In either case, the connection still counts against the total associated connection count for the pool.
        //
        // If we cannot find a usable available connection, then the request is added the to the request queue for the appropriate version.
        //
        // Whenever a request is queued, or an existing connection shuts down, we will check to see if we should inject a new connection.
        // Injection policy depends on both user settings and some simple heuristics.
        // See comments on the relevant routines for details on connection injection policy.
        //
        // When a new connection is successfully created, or an existing unavailable connection becomes available again,
        // we will attempt to use this connection to handle any queued requests (subject to lifetime restrictions on existing connections).
        // This may result in the connection becoming unavailable again, because it cannot handle any more requests at the moment.
        // If not, we will return the connection to the pool as an available connection for use by new requests.
        //
        // When a connection shuts down, either gracefully (e.g. GOAWAY) or abortively (e.g. IOException),
        // we will remove it from the list of available connections, if it is present there.
        // If not, then it must be unavailable at the moment; we will detect this and ensure it is not added back to the available pool.

        public ValueTask<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return SendWithVersionDetectionAndRetryAsync(request, cancellationToken);
        }

        public async ValueTask<HttpResponseMessage> SendWithVersionDetectionAndRetryAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            _usedSinceLastCleanup = true;

            // Loop on connection failures (or other problems like version downgrade) and retry if possible.
            int retryCount = 0;
            while (true)
            {
                HttpConnectionWaiter<Http2Connection?>? http2ConnectionWaiter = null;
                try
                {
                    HttpResponseMessage? response = null;

                    if (!TryGetPooledHttp2Connection(request, out Http2Connection? connection, out http2ConnectionWaiter) &&
                                http2ConnectionWaiter != null)
                    {
                        connection = await http2ConnectionWaiter.WaitForConnectionAsync(request, this, cancellationToken).ConfigureAwait(false);
                    }

                    Debug.Assert(connection is not null);
                    if (connection is not null)
                    {
                        response = await connection.SendAsync(request, cancellationToken).ConfigureAwait(false);
                    }

                    return response!;
                }
                catch (HttpRequestExceptionEx e) when (e.AllowRetry == RequestRetryType.RetryOnConnectionFailure)
                {
                    Debug.Assert(retryCount >= 0 && retryCount <= MaxConnectionFailureRetries);

                    if (retryCount == MaxConnectionFailureRetries)
                    {
                        if (NetEventSource.Log.IsEnabled())
                        {
                            Trace($"MaxConnectionFailureRetries limit of {MaxConnectionFailureRetries} hit. Retryable request will not be retried. Exception: {e}");
                        }

                        throw;
                    }

                    retryCount++;

                    if (NetEventSource.Log.IsEnabled())
                    {
                        Trace($"Retry attempt {retryCount} after connection failure. Connection exception: {e}");
                    }

                    // Eat exception and try again.
                }
                catch (HttpRequestExceptionEx e) when (e.AllowRetry == RequestRetryType.RetryOnStreamLimitReached)
                {
                    if (NetEventSource.Log.IsEnabled())
                    {
                        Trace($"Retrying request on another HTTP/2 connection after active streams limit is reached on existing one: {e}");
                    }

                    // Eat exception and try again.
                }
                finally
                {
                    // We never cancel both attempts at the same time. When downgrade happens, it's possible that both waiters are non-null,
                    // but in that case http2ConnectionWaiter.ConnectionCancellationTokenSource shall be null.
                    http2ConnectionWaiter?.SetTimeoutToPendingConnectionAttempt(this, cancellationToken.IsCancellationRequested);
                }
            }
        }

        private async ValueTask<(Stream, TransportContext?)> ConnectAsync(HttpRequestMessage request, bool async, CancellationToken cancellationToken)
        {
            Stream? stream = null;
            TransportContext? transportContext = null;

            switch (_kind)
            {
                case HttpConnectionKind.Http:
                case HttpConnectionKind.Https:
                    stream = await ConnectToTcpHostAsync(_originAuthority.IdnHost, _originAuthority.Port, request, async, cancellationToken).ConfigureAwait(false);
                    break;
            }

            Debug.Assert(stream != null);

            if (IsSecure)
            {
                SslStream? sslStream = stream as SslStream;

                sslStream ??= await ConnectHelper.EstablishSslConnectionAsync(Settings, OriginAuthority.HostValue, request, async, stream!, cancellationToken).ConfigureAwait(false);

                transportContext = sslStream.TransportContext;
                stream = sslStream;
            }

            return (stream, transportContext)!;
        }

        private async ValueTask<Stream> ConnectToTcpHostAsync(string host, int port, HttpRequestMessage initialRequest, bool async, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var endPoint = new DnsEndPoint(host, port);
            Stream? stream = null;
            try
            {
                // If a ConnectCallback was supplied, use that to establish the connection.
                if (Settings._connectCallback != null)
                {
                    ValueTask<Stream> streamTask = Settings._connectCallback(new SocketsHttpConnectionContext(endPoint, initialRequest), cancellationToken);

                    if (!async && !streamTask.IsCompleted)
                    {
                        // User-provided ConnectCallback is completing asynchronously but the user is making a synchronous request; if the user cares, they should
                        // set it up so that synchronous requests are made on a handler with a synchronously-completing ConnectCallback supplied. If in the future,
                        // we could add a Boolean to SocketsHttpConnectionContext (https://github.com/dotnet/runtime/issues/44876) to let the callback know whether
                        // this request is sync or async.
                        Trace($"{nameof(SocketsHttpHandler.ConnectCallback)} completing asynchronously for a synchronous request.");
                    }

                    stream = await streamTask.ConfigureAwait(false) ?? throw new HttpRequestExceptionEx(SR.net_http_null_from_connect_callback);
                }
                else
                {
                    // Otherwise, create and connect a socket using default settings.
                    Socket socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        if (async)
                        {
                            using var reg = cancellationToken.UnsafeRegister(static s => ((Socket)s!).Dispose(), socket);

                            await socket.ConnectAsync(endPoint).ConfigureAwait(false);
                        }
                        else
                        {
                            throw new NotImplementedException();
                        }

                        stream = new NetworkStream(socket, ownsSocket: true);
                    }
                    catch
                    {
                        socket.Dispose();
                        throw;
                    }
                }

                return stream;
            }
            catch (Exception ex)
            {
                throw ex is OperationCanceledException oce && oce.CancellationToken == cancellationToken ?
                    CancellationHelper.CreateOperationCanceledException(innerException: null, cancellationToken) :
                    ConnectHelper.CreateWrappedException(ex, host, port, cancellationToken);
            }
        }

        private async ValueTask<Stream> ApplyPlaintextFilterAsync(bool async, Stream stream, Version httpVersion, HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Settings._plaintextStreamFilter is null)
            {
                return stream;
            }

            Stream newStream;
            try
            {
                ValueTask<Stream> streamTask = Settings._plaintextStreamFilter(new SocketsHttpPlaintextStreamFilterContext(stream, httpVersion, request), cancellationToken);

                if (!async && !streamTask.IsCompleted)
                {
                    // User-provided PlaintextStreamFilter is completing asynchronously but the user is making a synchronous request; if the user cares, they should
                    // set it up so that synchronous requests are made on a handler with a synchronously-completing PlaintextStreamFilter supplied. If in the future,
                    // we could add a Boolean to SocketsHttpPlaintextStreamFilterContext (https://github.com/dotnet/runtime/issues/44876) to let the callback know whether
                    // this request is sync or async.
                    Trace($"{nameof(SocketsHttpHandler.PlaintextStreamFilter)} completing asynchronously for a synchronous request.");
                }

                newStream = await streamTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException oce) when (oce.CancellationToken == cancellationToken)
            {
                stream.Dispose();
                throw;
            }
            catch (Exception e)
            {
                stream.Dispose();
                throw new HttpRequestExceptionEx(SR.net_http_exception_during_plaintext_filter, e);
            }

            if (newStream == null)
            {
                stream.Dispose();
                throw new HttpRequestExceptionEx(SR.net_http_null_from_plaintext_filter);
            }

            return newStream;
        }

        private CancellationTokenSource GetConnectTimeoutCancellationTokenSource<T>(HttpConnectionWaiter<T> waiter)
            where T : HttpConnectionBase?
        {
            var cts = new CancellationTokenSource(Settings._connectTimeout);

            lock (waiter)
            {
                // After a request completes (or is canceled), it will call into SetTimeoutToPendingConnectionAttempt,
                // which will no-op if ConnectionCancellationTokenSource is not set, assuming that the connection attempt is done.
                // As the initiating request for this connection attempt may complete concurrently at any time,
                // there is a race condition where the first call to SetTimeoutToPendingConnectionAttempt may happen
                // before we were able to set the CTS, so no timeout will be applied even though the request is already done.
                waiter.ConnectionCancellationTokenSource = cts;

                // To fix that, we check whether the waiter already completed now that we're holding a lock.
                // If it had, call SetTimeoutToPendingConnectionAttempt again now that the CTS is set.
                if (waiter.Task.IsCompleted)
                {
                    waiter.SetTimeoutToPendingConnectionAttempt(this, requestCancelled: waiter.Task.IsCanceled);
                    waiter.ConnectionCancellationTokenSource = null;
                }
            }

            return cts;
        }

        private static Exception CreateConnectTimeoutException(OperationCanceledException oce)
        {
            // The pattern for request timeouts (on HttpClient) is to throw an OCE with an inner exception of TimeoutException.
            // Do the same for ConnectTimeout-based timeouts.
            TimeoutException te = new TimeoutException(SR.net_http_connect_timedout, oce.InnerException);
            Exception newException = CancellationHelper.CreateOperationCanceledException(te, oce.CancellationToken);
            ExceptionDispatchInfo.SetCurrentStackTrace(newException);
            return newException;
        }

        private bool CheckExpirationOnGet(HttpConnectionBase connection)
        {
            Debug.Assert(!HasSyncObjLock);

            TimeSpan pooledConnectionLifetime = _poolManager.Settings._pooledConnectionLifetime;
            if (pooledConnectionLifetime != Timeout.InfiniteTimeSpan)
            {
                return connection.GetLifetimeTicks(EnvironmentEx.TickCount64) > pooledConnectionLifetime.TotalMilliseconds;
            }

            return false;
        }

        private bool CheckExpirationOnReturn(HttpConnectionBase connection)
        {
            TimeSpan lifetime = _poolManager.Settings._pooledConnectionLifetime;
            if (lifetime != Timeout.InfiniteTimeSpan)
            {
                return lifetime == TimeSpan.Zero || connection.GetLifetimeTicks(EnvironmentEx.TickCount64) > lifetime.TotalMilliseconds;
            }

            return false;
        }

        /// <summary>
        /// Disposes the connection pool.  This is only needed when the pool currently contains
        /// or has associated connections.
        /// </summary>
        public void Dispose()
        {
            List<Http2Connection>? toDispose = null;

            lock (SyncObj)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                if (NetEventSource.Log.IsEnabled()) Trace("Disposing the pool.");

                if (_availableHttp2Connections is not null)
                {
                    toDispose = _availableHttp2Connections.ToList();
                    _associatedHttp2ConnectionCount -= _availableHttp2Connections.Count;
                    _availableHttp2Connections.Clear();
                }

                Debug.Assert((_availableHttp2Connections?.Count ?? 0) == 0, $"Expected {nameof(_availableHttp2Connections)}.{nameof(_availableHttp2Connections.Count)} == 0");
            }

            toDispose?.ForEach(c => c.Dispose());
        }

        /// <summary>
        /// Removes any unusable connections from the pool, and if the pool
        /// is then empty and stale, disposes of it.
        /// </summary>
        /// <returns>
        /// true if the pool disposes of itself; otherwise, false.
        /// </returns>
        public bool CleanCacheAndDisposeIfUnused()
        {
            TimeSpan pooledConnectionLifetime = _poolManager.Settings._pooledConnectionLifetime;
            TimeSpan pooledConnectionIdleTimeout = _poolManager.Settings._pooledConnectionIdleTimeout;
            long nowTicks = EnvironmentEx.TickCount64;

            List<HttpConnectionBase>? toDispose = null;

            lock (SyncObj)
            {
                // If there are now no connections associated with this pool, we can dispose of it. We
                // avoid aggressively cleaning up pools that have recently been used but currently aren't;
                // if a pool was used since the last time we cleaned up, give it another chance. New pools
                // start out saying they've recently been used, to give them a bit of breathing room and time
                // for the initial collection to be added to it.
                if (!_usedSinceLastCleanup && _associatedHttp2ConnectionCount == 0)
                {
                    _disposed = true;
                    return true; // Pool is disposed of.  It should be removed.
                }

                // Reset the cleanup flag.  Any pools that are empty and not used since the last cleanup
                // will be purged next time around.
                _usedSinceLastCleanup = false;

                if (_availableHttp2Connections is not null)
                {
                    int removed = ScavengeHttp2ConnectionList(_availableHttp2Connections, ref toDispose, nowTicks, pooledConnectionLifetime, pooledConnectionIdleTimeout);
                    _associatedHttp2ConnectionCount -= removed;

                    // Note: Http11 connections will decrement the _associatedHttp11ConnectionCount when disposed.
                    // Http2 connections will not, hence the difference in handing _associatedHttp2ConnectionCount.
                }
            }

            // Dispose the stale connections outside the pool lock, to avoid holding the lock too long.
            // Dispose them asynchronously to not to block the caller on closing the SslStream or NetworkStream.
            if (toDispose is not null)
            {
                Task.Factory.StartNew(static s => ((List<HttpConnectionBase>)s!).ForEach(c => c.Dispose()), toDispose,
                    CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
            }

            // Pool is active.  Should not be removed.
            return false;
        }

        // For diagnostic purposes
        public override string ToString() => $"{nameof(HttpConnectionPool)} {_originAuthority}";

        public void Trace(string? message, [CallerMemberName] string? memberName = null) =>
            NetEventSource.Log.HandlerMessage(
                GetHashCode(),               // pool ID
                0,                           // connection ID
                0,                           // request ID
                memberName,                  // method name
                message);                    // message
    }
}
