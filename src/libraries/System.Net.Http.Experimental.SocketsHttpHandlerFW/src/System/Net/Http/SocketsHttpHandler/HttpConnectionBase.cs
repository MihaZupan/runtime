// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal abstract class HttpConnectionBase : IDisposable, IHttpTrace
    {
        protected readonly HttpConnectionPool _pool;

        private static long s_connectionCounter = -1;

        // Indicates whether we've counted this connection as established, so that we can
        // avoid decrementing the counter once it's closed in case telemetry was enabled in between.
        private bool _httpTelemetryMarkedConnectionAsOpened;

        private readonly long _creationTickCount = EnvironmentEx.TickCount64;
        private long? _idleSinceTickCount;

        /// <summary>Cached string for the last Date header received on this connection.</summary>
        private string? _lastDateHeaderValue;
        /// <summary>Cached string for the last Server header received on this connection.</summary>
        private string? _lastServerHeaderValue;

        public long Id { get; } = Interlocked.Increment(ref s_connectionCounter);

        public HttpConnectionBase(HttpConnectionPool pool)
        {
            Debug.Assert(pool != null);
            _pool = pool!;
        }

        public HttpConnectionBase(HttpConnectionPool pool, IPEndPoint? remoteEndPoint)
            : this(pool)
        {
            MarkConnectionAsEstablished(remoteEndPoint);
        }

        protected void MarkConnectionAsEstablished(IPEndPoint? remoteEndPoint)
        {
            _idleSinceTickCount = _creationTickCount;

            if (HttpTelemetry.Log.IsEnabled())
            {
                _httpTelemetryMarkedConnectionAsOpened = true;

                string scheme = _pool.IsSecure ? "https" : "http";
                string host = _pool.OriginAuthority.HostValue;
                int port = _pool.OriginAuthority.Port;

                HttpTelemetry.Log.Http20ConnectionEstablished(Id, scheme, host, port, remoteEndPoint);
            }
        }

        public void MarkConnectionAsClosed()
        {
            if (HttpTelemetry.Log.IsEnabled())
            {
                // Only decrement the connection count if we counted this connection
                if (_httpTelemetryMarkedConnectionAsOpened)
                {
                    HttpTelemetry.Log.Http20ConnectionClosed(Id);
                }
            }
        }

        public void MarkConnectionAsIdle()
        {
            _idleSinceTickCount = EnvironmentEx.TickCount64;
        }

        public void MarkConnectionAsNotIdle()
        {
            _idleSinceTickCount = null;
        }

        /// <summary>Uses <see cref="HeaderDescriptor.GetHeaderValue"/>, but first special-cases several known headers for which we can use caching.</summary>
        public string GetResponseHeaderValueWithCaching(HeaderDescriptor descriptor, ReadOnlySpan<byte> value, Encoding? valueEncoding)
        {
            return
                descriptor.Equals(KnownHeaders.Date) ? GetOrAddCachedValue(ref _lastDateHeaderValue, descriptor, value, valueEncoding) :
                descriptor.Equals(KnownHeaders.Server) ? GetOrAddCachedValue(ref _lastServerHeaderValue, descriptor, value, valueEncoding) :
                descriptor.GetHeaderValue(value, valueEncoding);

            static string GetOrAddCachedValue([NotNull] ref string? cache, HeaderDescriptor descriptor, ReadOnlySpan<byte> value, Encoding? encoding)
            {
                string? lastValue = cache;
                if (lastValue is null || !Ascii.Equals(value, lastValue))
                {
                    cache = lastValue = descriptor.GetHeaderValue(value, encoding);
                }
                Debug.Assert(cache is not null);
#pragma warning disable CS8777 // Parameter must have a non-null value when exiting.
                return lastValue;
#pragma warning restore CS8777 // Parameter must have a non-null value when exiting.
            }
        }

        public abstract void Trace(string message, [CallerMemberName] string? memberName = null);

        protected void TraceConnection(Stream stream)
        {
            if (stream is SslStream sslStream)
            {
#pragma warning disable SYSLIB0058 // Use NegotiatedCipherSuite.
                Trace(
                    $"{this}. Id:{Id}, " +
                    //$"SslProtocol:{sslStream.SslProtocol}, NegotiatedApplicationProtocol:{sslStream.NegotiatedApplicationProtocol}, " +
                    //$"NegotiatedCipherSuite:{sslStream.NegotiatedCipherSuite}, CipherAlgorithm:{sslStream.CipherAlgorithm}, CipherStrength:{sslStream.CipherStrength}, " +
                    $"HashAlgorithm:{sslStream.HashAlgorithm}, HashStrength:{sslStream.HashStrength}, " +
                    $"KeyExchangeAlgorithm:{sslStream.KeyExchangeAlgorithm}, KeyExchangeStrength:{sslStream.KeyExchangeStrength}, " +
                    $"LocalCertificate:{sslStream.LocalCertificate}, RemoteCertificate:{sslStream.RemoteCertificate}");
#pragma warning restore SYSLIB0058 // Use NegotiatedCipherSuite.
            }
            else
            {
                Trace($"{this}. Id:{Id}");
            }
        }

        public long GetLifetimeTicks(long nowTicks) => nowTicks - _creationTickCount;

        public long GetIdleTicks(long nowTicks) => _idleSinceTickCount is long idleSinceTickCount ? nowTicks - idleSinceTickCount : 0;

        /// <summary>Check whether a connection is still usable, or should be scavenged.</summary>
        /// <returns>True if connection can be used.</returns>
        public virtual bool CheckUsabilityOnScavenge() => true;

        internal static bool IsDigit(byte c) => (uint)(c - '0') <= '9' - '0';

        internal static int ParseStatusCode(ReadOnlySpan<byte> value)
        {
            byte status1, status2, status3;
            if (value.Length != 3 ||
                !IsDigit(status1 = value[0]) ||
                !IsDigit(status2 = value[1]) ||
                !IsDigit(status3 = value[2]))
            {
                throw new HttpRequestExceptionEx(HttpRequestError.InvalidResponse, SR.Format(SR.net_http_invalid_response_status_code, Encoding.ASCII.GetString(value)));
            }

            return 100 * (status1 - '0') + 10 * (status2 - '0') + (status3 - '0');
        }

        /// <summary>Awaits a task, logging any resulting exceptions (which are otherwise ignored).</summary>
        internal void LogExceptions(Task task)
        {
            if (task.IsCompleted)
            {
                if (task.IsFaulted)
                {
                    LogFaulted(this, task);
                }
            }
            else
            {
                task.ContinueWith(static (t, state) => LogFaulted((HttpConnectionBase)state!, t), this,
                    CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously | TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
            }

            static void LogFaulted(HttpConnectionBase connection, Task task)
            {
                Debug.Assert(task.IsFaulted);
                Exception? e = task.Exception!.InnerException; // Access Exception even if not tracing, to avoid TaskScheduler.UnobservedTaskException firing
                if (NetEventSource.Log.IsEnabled()) connection.Trace($"Exception from asynchronous processing: {e}");
            }
        }

        public abstract void Dispose();

        /// <summary>
        /// Called by <see cref="HttpConnectionPool.CleanCacheAndDisposeIfUnused"/> while holding the lock.
        /// </summary>
        public bool IsUsable(long nowTicks, TimeSpan pooledConnectionLifetime, TimeSpan pooledConnectionIdleTimeout)
        {
            // Validate that the connection hasn't been idle in the pool for longer than is allowed.
            if (pooledConnectionIdleTimeout != Timeout.InfiniteTimeSpan)
            {
                long idleTicks = GetIdleTicks(nowTicks);
                if (idleTicks > pooledConnectionIdleTimeout.TotalMilliseconds)
                {
                    if (NetEventSource.Log.IsEnabled()) Trace($"Scavenging connection. Idle {TimeSpan.FromMilliseconds(idleTicks)} > {pooledConnectionIdleTimeout}.");
                    return false;
                }
            }

            // Validate that the connection lifetime has not been exceeded.
            if (pooledConnectionLifetime != Timeout.InfiniteTimeSpan)
            {
                long lifetimeTicks = GetLifetimeTicks(nowTicks);
                if (lifetimeTicks > pooledConnectionLifetime.TotalMilliseconds)
                {
                    if (NetEventSource.Log.IsEnabled()) Trace($"Scavenging connection. Lifetime {TimeSpan.FromMilliseconds(lifetimeTicks)} > {pooledConnectionLifetime}.");
                    return false;
                }
            }

            if (!CheckUsabilityOnScavenge())
            {
                if (NetEventSource.Log.IsEnabled()) Trace($"Scavenging connection. Keep-Alive timeout exceeded, unexpected data or EOF received.");
                return false;
            }

            return true;
        }
    }
}
