// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Net.NetworkInformation;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    // General flow of requests through the various layers:
    //
    // (1) HttpConnectionPoolManager.SendAsync: Does proxy lookup
    // (2) HttpConnectionPoolManager.SendAsyncCore: Find or create connection pool
    // (3) HttpConnectionPool.SendAsync: Handle basic/digest request auth
    // (4) HttpConnectionPool.SendWithProxyAuthAsync: Handle basic/digest proxy auth
    // (5) HttpConnectionPool.SendWithRetryAsync: Retrieve connection from pool, or create new
    //                                            Also, handle retry for failures on connection reuse
    // (6) HttpConnection.SendAsync: Handle negotiate/ntlm connection auth
    // (7) HttpConnection.SendWithNtProxyAuthAsync: Handle negotiate/ntlm proxy auth
    // (8) HttpConnection.SendAsyncCore: Write request to connection and read response
    //                                   Also, handle cookie processing
    //
    // Redirect and decompression handling are done above HttpConnectionPoolManager,
    // in RedirectHandler and DecompressionHandler respectively.

    /// <summary>Provides a set of connection pools, each for its own endpoint.</summary>
    internal sealed class HttpConnectionPoolManager : IDisposable
    {
        /// <summary>How frequently an operation should be initiated to clean out old pools and connections in those pools.</summary>
        private readonly TimeSpan _cleanPoolTimeout;
        /// <summary>The pools, indexed by endpoint.</summary>
        private readonly ConcurrentDictionary<HttpConnectionKey, HttpConnectionPool> _pools;
        /// <summary>Timer used to initiate cleaning of the pools.</summary>
        private readonly Timer? _cleaningTimer;
        /// <summary>Heart beat timer currently used for Http2 ping only.</summary>
        private readonly Timer? _heartBeatTimer;

        private readonly HttpConnectionSettings _settings;

        /// <summary>
        /// Keeps track of whether or not the cleanup timer is running. It helps us avoid the expensive
        /// <see cref="ConcurrentDictionary{TKey,TValue}.IsEmpty"/> call.
        /// </summary>
        private bool _timerIsRunning;
        /// <summary>Object used to synchronize access to state in the pool.</summary>
        private object SyncObj => _pools;

        /// <summary>Initializes the pools.</summary>
        public HttpConnectionPoolManager(HttpConnectionSettings settings)
        {
            _settings = settings;
            _pools = new ConcurrentDictionary<HttpConnectionKey, HttpConnectionPool>();

            // As an optimization, we can sometimes avoid the overheads associated with
            // storing connections.  This is possible when we would immediately terminate
            // connections anyway due to either the idle timeout or the lifetime being
            // set to zero, as in that case the timeout effectively immediately expires.
            // However, we can only do such optimizations if we're not also tracking
            // connections per server, as we use data in the associated data structures
            // to do that tracking.
            // Additionally, we should not avoid storing connections if keep-alive ping is configured,
            // as the heartbeat timer is needed for ping functionality.
            bool avoidStoringConnections =
                settings._maxConnectionsPerServer == int.MaxValue &&
                (settings._pooledConnectionIdleTimeout == TimeSpan.Zero ||
                 settings._pooledConnectionLifetime == TimeSpan.Zero) &&
                settings._keepAlivePingDelay == Timeout.InfiniteTimeSpan;

            // Start out with the timer not running, since we have no pools.
            // When it does run, run it with a frequency based on the idle timeout.
            if (!avoidStoringConnections)
            {
                if (settings._pooledConnectionIdleTimeout == Timeout.InfiniteTimeSpan)
                {
                    const int DefaultScavengeSeconds = 30;
                    _cleanPoolTimeout = TimeSpan.FromSeconds(DefaultScavengeSeconds);
                }
                else
                {
                    const int ScavengesPerIdle = 4;
                    const int MinScavengeSeconds = 1;
                    TimeSpan timerPeriod = TimeSpan.FromTicks(settings._pooledConnectionIdleTimeout.Ticks / ScavengesPerIdle);
                    _cleanPoolTimeout = timerPeriod.TotalSeconds >= MinScavengeSeconds ? timerPeriod : TimeSpan.FromSeconds(MinScavengeSeconds);
                }

                using (ExecutionContextEx.SuppressFlow()) // Don't capture the current ExecutionContext and its AsyncLocals onto the timer causing them to live forever
                {
                    // Create the timer.  Ensure the Timer has a weak reference to this manager; otherwise, it
                    // can introduce a cycle that keeps the HttpConnectionPoolManager rooted by the Timer
                    // implementation until the handler is Disposed (or indefinitely if it's not).
                    var thisRef = new WeakReference<HttpConnectionPoolManager>(this);

                    _cleaningTimer = new Timer(static s =>
                    {
                        var wr = (WeakReference<HttpConnectionPoolManager>)s!;
                        if (wr.TryGetTarget(out HttpConnectionPoolManager? thisRef))
                        {
                            thisRef.RemoveStalePools();
                        }
                    }, thisRef, Timeout.Infinite, Timeout.Infinite);


                    // For now heart beat is used only for ping functionality.
                    if (_settings._keepAlivePingDelay != Timeout.InfiniteTimeSpan)
                    {
                        long heartBeatInterval = (long)Math.Max(1000, Math.Min(_settings._keepAlivePingDelay.TotalMilliseconds, _settings._keepAlivePingTimeout.TotalMilliseconds) / 4);

                        _heartBeatTimer = new Timer(static state =>
                        {
                            var wr = (WeakReference<HttpConnectionPoolManager>)state!;
                            if (wr.TryGetTarget(out HttpConnectionPoolManager? thisRef))
                            {
                                thisRef.HeartBeat();
                            }
                        }, thisRef, heartBeatInterval, heartBeatInterval);
                    }
                }
            }
        }

        public HttpConnectionSettings Settings => _settings;

        private static HttpConnectionKey GetConnectionKey(HttpRequestMessage request)
        {
            Uri? uri = request.RequestUri;
            Debug.Assert(uri != null);

            string? sslHostName = null;
            if (HttpUtilities.IsSupportedSecureScheme(uri.Scheme))
            {
                string? hostHeader = request.Headers.Host;
                if (hostHeader != null)
                {
                    sslHostName = HttpUtilities.ParseHostNameFromHeader(hostHeader);
                }
                else
                {
                    // No explicit Host header. Use host from uri.
                    sslHostName = uri.IdnHost;
                }
            }

            if (sslHostName != null)
            {
                return new HttpConnectionKey(HttpConnectionKind.Https, uri.IdnHost, uri.Port, sslHostName);
            }
            else
            {
                return new HttpConnectionKey(HttpConnectionKind.Http, uri.IdnHost, uri.Port, null);
            }
        }

        // Picks the value of the 'server.address' tag following rules specified in
        // https://github.com/open-telemetry/semantic-conventions/blob/728e5d1/docs/http/http-spans.md#http-client-span
        // When there is no proxy, we need to prioritize the contents of the Host header.
        private static string? GetTelemetryServerAddress(HttpRequestMessage request, HttpConnectionKey key)
        {
            if (GlobalHttpSettings.DiagnosticsHandler.EnableActivityPropagation)
            {
                Uri? uri = request.RequestUri;
                Debug.Assert(uri is not null);

                if (key.SslHostName is not null)
                {
                    return key.SslHostName;
                }

                string? hostHeader = request.Headers.Host;
                return hostHeader is null ? uri.IdnHost : HttpUtilities.ParseHostNameFromHeader(hostHeader);
            }

            return null;
        }

        public ValueTask<HttpResponseMessage> SendAsyncCore(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpConnectionKey key = GetConnectionKey(request);

            HttpConnectionPool? pool;
            while (!_pools.TryGetValue(key, out pool))
            {
                pool = new HttpConnectionPool(this, key.Kind, key.Host, key.Port, key.SslHostName, GetTelemetryServerAddress(request, key));

                if (_cleaningTimer == null)
                {
                    // There's no cleaning timer, which means we're not adding connections into pools, but we still need
                    // the pool object for this request.  We don't need or want to add the pool to the pools, though,
                    // since we don't want it to sit there forever, which it would without the cleaning timer.
                    break;
                }

                if (_pools.TryAdd(key, pool))
                {
                    // We need to ensure the cleanup timer is running if it isn't
                    // already now that we added a new connection pool.
                    lock (SyncObj)
                    {
                        if (!_timerIsRunning)
                        {
                            SetCleaningTimer(_cleanPoolTimeout);
                        }
                    }
                    break;
                }

                // We created a pool and tried to add it to our pools, but some other thread got there before us.
                // We don't need to Dispose the pool, as that's only needed when it contains connections
                // that need to be closed.
            }

            return pool.SendAsync(request, cancellationToken);
        }

        public ValueTask<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return SendAsyncCore(request, cancellationToken);
        }

        /// <summary>Disposes of the pools, disposing of each individual pool.</summary>
        public void Dispose()
        {
            _cleaningTimer?.Dispose();
            _heartBeatTimer?.Dispose();
            foreach (KeyValuePair<HttpConnectionKey, HttpConnectionPool> pool in _pools)
            {
                pool.Value.Dispose();
            }
        }

        /// <summary>Sets <see cref="_cleaningTimer"/> and <see cref="_timerIsRunning"/> based on the specified timeout.</summary>
        private void SetCleaningTimer(TimeSpan timeout)
        {
            if (_cleaningTimer!.Change(timeout, Timeout.InfiniteTimeSpan))
            {
                _timerIsRunning = timeout != Timeout.InfiniteTimeSpan;
            }
        }

        /// <summary>Removes unusable connections from each pool, and removes stale pools entirely.</summary>
        private void RemoveStalePools()
        {
            Debug.Assert(_cleaningTimer != null);

            // Iterate through each pool in the set of pools.  For each, ask it to clear out
            // any unusable connections (e.g. those which have expired, those which have been closed, etc.)
            // The pool may detect that it's empty and long unused, in which case it'll dispose of itself,
            // such that any connections returned to the pool to be cached will be disposed of.  In such
            // a case, we also remove the pool from the set of pools to avoid a leak.
            foreach (KeyValuePair<HttpConnectionKey, HttpConnectionPool> entry in _pools)
            {
                if (entry.Value.CleanCacheAndDisposeIfUnused())
                {
                    _pools.TryRemove(entry.Key, out _);
                }
            }

            // Restart the timer if we have any pools to clean up.
            lock (SyncObj)
            {
                SetCleaningTimer(!_pools.IsEmpty ? _cleanPoolTimeout : Timeout.InfiniteTimeSpan);
            }

            // NOTE: There is a possible race condition with regards to a pool getting cleaned up at the same
            // time it's about to be used for another request.  The timer cleanup could start running, see that
            // a pool is empty, and initiate its disposal.  Concurrently, the pools could hand out the pool
            // to a request looking to get a connection, because the pool may not have been removed yet
            // from the pools.  Worst case here is that connection will end up getting returned to an
            // already disposed pool, in which case the connection will also end up getting disposed rather
            // than reused.  This should be a rare occurrence, so for now we don't worry about it.  In the
            // future, there are a variety of possible ways to address it, such as allowing connections to
            // be returned to pools they weren't associated with.
        }

        private void HeartBeat()
        {
            foreach (KeyValuePair<HttpConnectionKey, HttpConnectionPool> pool in _pools)
            {
                pool.Value.HeartBeat();
            }
        }

        internal readonly struct HttpConnectionKey : IEquatable<HttpConnectionKey>
        {
            public readonly HttpConnectionKind Kind;
            public readonly string? Host;
            public readonly int Port;
            public readonly string? SslHostName;     // null if not SSL

            public HttpConnectionKey(HttpConnectionKind kind, string? host, int port, string? sslHostName)
            {
                Kind = kind;
                Host = host;
                Port = port;
                SslHostName = sslHostName;
            }

            // In the common case, SslHostName (when present) is equal to Host.  If so, don't include in hash.
            public override int GetHashCode() =>
                (SslHostName == Host ?
                    HashCode.Combine(Kind, Host, Port) :
                    HashCode.Combine(Kind, Host, Port, SslHostName));

            public override bool Equals([NotNullWhen(true)] object? obj) =>
                obj is HttpConnectionKey hck &&
                Equals(hck);

            public bool Equals(HttpConnectionKey other) =>
                Kind == other.Kind &&
                Host == other.Host &&
                Port == other.Port &&
                SslHostName == other.SslHostName;
        }
    }
}
