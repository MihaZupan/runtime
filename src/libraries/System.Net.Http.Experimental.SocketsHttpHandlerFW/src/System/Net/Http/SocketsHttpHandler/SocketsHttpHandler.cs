// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Security;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http.DoNotUseInProduction.TestingOnly
{
    /// <summary>
    /// A partial port of .NET 10's SocketsHttpHandler to .NET Framework for HTTP/2 testing.
    /// </summary>
    [UnsupportedOSPlatform("browser")]
    public sealed class SocketsHttpHandler : HttpMessageHandler
    {
        static SocketsHttpHandler()
        {
            FrameworkHttp2ALPNSslStreamPatch.Apply();
        }

        private readonly HttpConnectionSettings _settings = new HttpConnectionSettings();
        private HttpMessageHandlerStage? _handler;
        private Task<HttpMessageHandlerStage>? _handlerChainSetupTask;
        private bool _disposed;

        // Accessed via UnsafeAccessor from HttpWebRequest.
        internal HttpConnectionSettings Settings => _settings;

        private void CheckDisposedOrStarted()
        {
            ObjectDisposedExceptionEx.ThrowIf(_disposed, this);
            if (_handler != null)
            {
                throw new InvalidOperationException(SR.net_http_operation_started);
            }
        }

        /// <summary>
        /// Gets or sets a value that indicates whether the handler should use cookies.
        /// </summary>
        public bool UseCookies
        {
            get => _settings._useCookies;
            set
            {
                CheckDisposedOrStarted();
                _settings._useCookies = value;
            }
        }

        /// <summary>
        /// Gets or sets the managed cookie container object.
        /// </summary>
        [AllowNull]
        public CookieContainer CookieContainer
        {
            get => _settings._cookieContainer ??= new CookieContainer();
            set
            {
                CheckDisposedOrStarted();
                _settings._cookieContainer = value;
            }
        }

        /// <summary>
        /// Gets or sets a value that indicates whether the handler should follow redirection responses.
        /// </summary>
        public bool AllowAutoRedirect
        {
            get => _settings._allowAutoRedirect;
            set
            {
                CheckDisposedOrStarted();
                _settings._allowAutoRedirect = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of allowed HTTP redirects.
        /// </summary>
        public int MaxAutomaticRedirections
        {
            get => _settings._maxAutomaticRedirections;
            set
            {
                ArgumentOutOfRangeExceptionEx.ThrowIfNegativeOrZero(value);

                CheckDisposedOrStarted();
                _settings._maxAutomaticRedirections = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum number of simultaneous TCP connections allowed to a single server.
        /// </summary>
        public int MaxConnectionsPerServer
        {
            get => _settings._maxConnectionsPerServer;
            set
            {
                ArgumentOutOfRangeExceptionEx.ThrowIfNegativeOrZero(value);

                CheckDisposedOrStarted();
                _settings._maxConnectionsPerServer = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum amount of data that can be drained from responses in bytes.
        /// </summary>
        public int MaxResponseDrainSize
        {
            get => _settings._maxResponseDrainSize;
            set
            {
                ArgumentOutOfRangeExceptionEx.ThrowIfNegative(value);

                CheckDisposedOrStarted();
                _settings._maxResponseDrainSize = value;
            }
        }

        /// <summary>
        /// Gets or sets the timespan to wait for data to be drained from responses.
        /// </summary>
        public TimeSpan ResponseDrainTimeout
        {
            get => _settings._maxResponseDrainTime;
            set
            {
                if ((value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan) ||
                    (value.TotalMilliseconds > int.MaxValue))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                CheckDisposedOrStarted();
                _settings._maxResponseDrainTime = value;
            }
        }

        /// <summary>
        /// Gets or sets the maximum length, in kilobytes (1024 bytes), of the response headers.
        /// </summary>
        public int MaxResponseHeadersLength
        {
            get => _settings._maxResponseHeadersLength;
            set
            {
                ArgumentOutOfRangeExceptionEx.ThrowIfNegativeOrZero(value);

                CheckDisposedOrStarted();
                _settings._maxResponseHeadersLength = value;
            }
        }

        /// <summary>
        /// Gets or sets how long a connection can be in the pool to be considered reusable.
        /// </summary>
        public TimeSpan PooledConnectionLifetime
        {
            get => _settings._pooledConnectionLifetime;
            set
            {
                if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                CheckDisposedOrStarted();
                _settings._pooledConnectionLifetime = value;
            }
        }

        /// <summary>
        /// Gets or sets how long a connection can be idle in the pool to be considered reusable.
        /// </summary>
        public TimeSpan PooledConnectionIdleTimeout
        {
            get => _settings._pooledConnectionIdleTimeout;
            set
            {
                if (value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                CheckDisposedOrStarted();
                _settings._pooledConnectionIdleTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets a custom callback used to open new connections.
        /// </summary>
        public TimeSpan ConnectTimeout
        {
            get => _settings._connectTimeout;
            set
            {
                if ((value <= TimeSpan.Zero && value != Timeout.InfiniteTimeSpan) ||
                    (value.TotalMilliseconds > int.MaxValue))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                CheckDisposedOrStarted();
                _settings._connectTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the time-out value for server HTTP 100 Continue response.
        /// </summary>
        public TimeSpan Expect100ContinueTimeout
        {
            get => _settings._expect100ContinueTimeout;
            set
            {
                if ((value < TimeSpan.Zero && value != Timeout.InfiniteTimeSpan) ||
                    (value.TotalMilliseconds > int.MaxValue))
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                CheckDisposedOrStarted();
                _settings._expect100ContinueTimeout = value;
            }
        }

        /// <summary>
        /// Defines the initial HTTP2 stream receive window size for all connections opened by the this <see cref="SocketsHttpHandler"/>.
        /// </summary>
        /// <remarks>
        /// Larger the values may lead to faster download speed, but potentially higher memory footprint.
        /// The property must be set to a value between 65535 and the configured maximum window size, which is 16777216 by default.
        /// </remarks>
        public int InitialHttp2StreamWindowSize
        {
            get => _settings._initialHttp2StreamWindowSize;
            set
            {
                if (value < HttpHandlerDefaults.DefaultInitialHttp2StreamWindowSize || value > GlobalHttpSettings.SocketsHttpHandler.MaxHttp2StreamWindowSize)
                {
                    string message = SR.Format(
                        SR.net_http_http2_invalidinitialstreamwindowsize,
                        HttpHandlerDefaults.DefaultInitialHttp2StreamWindowSize,
                        GlobalHttpSettings.SocketsHttpHandler.MaxHttp2StreamWindowSize);

                    throw new ArgumentOutOfRangeException(nameof(InitialHttp2StreamWindowSize), message);
                }
                CheckDisposedOrStarted();
                _settings._initialHttp2StreamWindowSize = value;
            }
        }

        /// <summary>
        /// Gets or sets the keep alive ping delay. The client will send a keep alive ping to the server if it
        /// doesn't receive any frames on a connection for this period of time. This property is used together with
        /// <see cref="SocketsHttpHandler.KeepAlivePingTimeout"/> to close broken connections.
        /// <para>
        /// Delay value must be greater than or equal to 1 second. Set to <see cref="Timeout.InfiniteTimeSpan"/> to
        /// disable the keep alive ping.
        /// Defaults to <see cref="Timeout.InfiniteTimeSpan"/>.
        /// </para>
        /// </summary>
        public TimeSpan KeepAlivePingDelay
        {
            get => _settings._keepAlivePingDelay;
            set
            {
                if (value.Ticks < TimeSpan.TicksPerSecond && value != Timeout.InfiniteTimeSpan)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, SR.Format(SR.net_http_value_must_be_greater_than_or_equal, value, TimeSpan.FromSeconds(1)));
                }

                CheckDisposedOrStarted();
                _settings._keepAlivePingDelay = value;
            }
        }

        /// <summary>
        /// Gets or sets the keep alive ping timeout. Keep alive pings are sent when a period of inactivity exceeds
        /// the configured <see cref="KeepAlivePingDelay"/> value. The client will close the connection if it
        /// doesn't receive any frames within the timeout.
        /// <para>
        /// Timeout must be greater than or equal to 1 second. Set to <see cref="Timeout.InfiniteTimeSpan"/> to
        /// disable the keep alive ping timeout.
        /// Defaults to 20 seconds.
        /// </para>
        /// </summary>
        public TimeSpan KeepAlivePingTimeout
        {
            get => _settings._keepAlivePingTimeout;
            set
            {
                if (value.Ticks < TimeSpan.TicksPerSecond && value != Timeout.InfiniteTimeSpan)
                {
                    throw new ArgumentOutOfRangeException(nameof(value), value, SR.Format(SR.net_http_value_must_be_greater_than_or_equal, value, TimeSpan.FromSeconds(1)));
                }

                CheckDisposedOrStarted();
                _settings._keepAlivePingTimeout = value;
            }
        }

        /// <summary>
        /// Gets or sets the keep alive ping behaviour. Keep alive pings are sent when a period of inactivity exceeds
        /// the configured <see cref="KeepAlivePingDelay"/> value.
        /// </summary>
        public HttpKeepAlivePingPolicy KeepAlivePingPolicy
        {
            get => _settings._keepAlivePingPolicy;
            set
            {
                CheckDisposedOrStarted();
                _settings._keepAlivePingPolicy = value;
            }
        }

        /// <summary>
        /// Gets or sets a value that indicates whether additional HTTP/2 connections can be established to the same server.
        /// </summary>
        /// <remarks>
        /// Enabling multiple connections to the same server explicitly goes against <see href="https://www.rfc-editor.org/rfc/rfc9113.html#section-9.1-2">RFC 9113 - HTTP/2</see>.
        /// </remarks>
        public bool EnableMultipleHttp2Connections
        {
            get => _settings._enableMultipleHttp2Connections;
            set
            {
                CheckDisposedOrStarted();

                _settings._enableMultipleHttp2Connections = value;
            }
        }

        internal const bool SupportsRedirectConfiguration = true;

        /// <summary>
        /// When non-null, a custom callback used to open new connections.
        /// </summary>
        public Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? ConnectCallback
        {
            get => _settings._connectCallback;
            set
            {
                CheckDisposedOrStarted();
                _settings._connectCallback = value;
            }
        }

        /// <summary>
        /// Gets or sets a custom callback that provides access to the plaintext HTTP protocol stream.
        /// </summary>
        public Func<SocketsHttpPlaintextStreamFilterContext, CancellationToken, ValueTask<Stream>>? PlaintextStreamFilter
        {
            get => _settings._plaintextStreamFilter;
            set
            {
                CheckDisposedOrStarted();
                _settings._plaintextStreamFilter = value;
            }
        }

        /// <summary>
        /// Gets a writable dictionary (that is, a map) of custom properties for the HttpClient requests. The dictionary is initialized empty; you can insert and query key-value pairs for your custom handlers and special processing.
        /// </summary>
        public IDictionary<string, object?> Properties =>
            _settings._properties ??= new Dictionary<string, object?>();

        /// <summary>
        /// Gets or sets a callback that returns the <see cref="Encoding"/> to encode the value for the specified request header name,
        /// or <see langword="null"/> to use the default behavior.
        /// </summary>
        public HeaderEncodingSelector<HttpRequestMessage>? RequestHeaderEncodingSelector
        {
            get => _settings._requestHeaderEncodingSelector;
            set
            {
                CheckDisposedOrStarted();
                _settings._requestHeaderEncodingSelector = value;
            }
        }

        /// <summary>
        /// Gets or sets a callback that returns the <see cref="Encoding"/> to decode the value for the specified response header name,
        /// or <see langword="null"/> to use the default behavior.
        /// </summary>
        public HeaderEncodingSelector<HttpRequestMessage>? ResponseHeaderEncodingSelector
        {
            get => _settings._responseHeaderEncodingSelector;
            set
            {
                CheckDisposedOrStarted();
                _settings._responseHeaderEncodingSelector = value;
            }
        }

        /// <summary>
        /// Gets or sets a <see cref="RemoteCertificateValidationCallback"/> delegate that's responsible for validating the certificate supplied by the remote party.
        /// </summary>
        public RemoteCertificateValidationCallback? RemoteCertificateValidationCallback
        {
            get => _settings._remoteCertificateValidationCallback;
            set
            {
                CheckDisposedOrStarted();
                _settings._remoteCertificateValidationCallback = value;
            }
        }

        /// <summary>
        /// Gets or sets a <see cref="LocalCertificateSelectionCallback"/> delegate that's responsible for selecting the client authentication certificate used for authentication.
        /// </summary>
        public LocalCertificateSelectionCallback? LocalCertificateSelectionCallback
        {
            get => _settings._localCertificateSelectionCallback;
            set
            {
                CheckDisposedOrStarted();
                _settings._localCertificateSelectionCallback = value;
            }
        }

        /// <inheritdoc/>
        protected override void Dispose(bool disposing)
        {
            if (disposing && !_disposed)
            {
                _disposed = true;
                _handler?.Dispose();
            }

            base.Dispose(disposing);
        }

        private HttpMessageHandlerStage SetupHandlerChain()
        {
            // Clone the settings to get a relatively consistent view that won't change after this point.
            // (This isn't entirely complete, as some of the collections it contains aren't currently deeply cloned.)
            HttpConnectionSettings settings = _settings.CloneAndNormalize();

            HttpConnectionPoolManager poolManager = new HttpConnectionPoolManager(settings);
            HttpMessageHandlerStage handler = new HttpConnectionHandler(poolManager);

            if (settings._allowAutoRedirect)
            {
                // Just as with WinHttpHandler, for security reasons, we do not support authentication on redirects
                // if the credential is anything other than a CredentialCache.
                // We allow credentials in a CredentialCache since they are specifically tied to URIs.
                handler = new RedirectHandler(settings._maxAutomaticRedirections, handler);
            }

            // Ensure a single handler is used for all requests.
            if (Interlocked.CompareExchange(ref _handler, handler, null) != null)
            {
                handler.Dispose();
            }

            return _handler;
        }

        /// <inheritdoc/>
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(request);

            ObjectDisposedExceptionEx.ThrowIf(_disposed, this);

            if (cancellationToken.IsCancellationRequested)
            {
                return Task.FromCanceled<HttpResponseMessage>(cancellationToken);
            }

            Exception? error = ValidateAndNormalizeRequest(request);
            if (error != null)
            {
                return Task.FromException<HttpResponseMessage>(error);
            }

            return _handler is { } handler
                ? handler.SendAsync(request, async: true, cancellationToken).AsTask()
                : CreateHandlerAndSendAsync(request, cancellationToken);

            // SetupHandlerChain may block for a few seconds in some environments.
            // E.g. during the first access of HttpClient.DefaultProxy - https://github.com/dotnet/runtime/issues/115301.
            // The setup procedure is enqueued to thread pool to prevent the caller from blocking.
            async Task<HttpResponseMessage> CreateHandlerAndSendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                _handlerChainSetupTask ??= Task.Run(SetupHandlerChain);
                HttpMessageHandlerStage handler = await _handlerChainSetupTask.ConfigureAwait(false);
                return await handler.SendAsync(request, async: true, cancellationToken).ConfigureAwait(false);
            }
        }

        private static Exception? ValidateAndNormalizeRequest(HttpRequestMessage request)
        {
            if (request.Version != HttpVersionEx.Version20)
            {
                return ExceptionDispatchInfo.SetCurrentStackTrace(new NotSupportedException(SR.net_http_unsupported_version));
            }

            // Add headers to define content transfer, if not present
            if (request.Headers.TransferEncodingChunked.GetValueOrDefault())
            {
                if (request.Content == null)
                {
                    return ExceptionDispatchInfo.SetCurrentStackTrace(new HttpRequestExceptionEx(SR.net_http_client_execution_error,
                        ExceptionDispatchInfo.SetCurrentStackTrace(new InvalidOperationException(SR.net_http_chunked_not_allowed_with_empty_content))));
                }

                // Since the user explicitly set TransferEncodingChunked to true, we need to remove
                // the Content-Length header if present, as sending both is invalid.
                request.Content.Headers.ContentLength = null;
            }
            else if (request.Content != null && request.Content.Headers.ContentLength == null)
            {
                // We have content, but neither Transfer-Encoding nor Content-Length is set.
                request.Headers.TransferEncodingChunked = true;
            }

            if (request.Version.Minor == 0 && request.Version.Major == 1)
            {
                // HTTP 1.0 does not support chunking
                if (request.Headers.TransferEncodingChunked == true)
                {
                    return ExceptionDispatchInfo.SetCurrentStackTrace(new NotSupportedException(SR.net_http_unsupported_chunking));
                }

                // HTTP 1.0 does not support Expect: 100-continue; just disable it.
                if (request.Headers.ExpectContinue == true)
                {
                    request.Headers.ExpectContinue = false;
                }
            }

            Uri? requestUri = request.RequestUri;
            if (requestUri is null || !requestUri.IsAbsoluteUri)
            {
                return ExceptionDispatchInfo.SetCurrentStackTrace(new InvalidOperationException(SR.net_http_client_invalid_requesturi));
            }

            if (!HttpUtilities.IsSupportedScheme(requestUri.Scheme))
            {
                return ExceptionDispatchInfo.SetCurrentStackTrace(new NotSupportedException(SR.Format(SR.net_http_unsupported_requesturi_scheme, requestUri.Scheme)));
            }

            return null;
        }
    }
}
