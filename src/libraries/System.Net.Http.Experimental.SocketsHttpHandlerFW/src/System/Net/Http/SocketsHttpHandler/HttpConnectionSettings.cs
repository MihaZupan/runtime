// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.IO;
using System.Net.Security;
using System.Security.Principal;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    /// <summary>Provides a state bag of settings for configuring HTTP connections.</summary>
    internal sealed class HttpConnectionSettings
    {
        internal bool _useCookies = HttpHandlerDefaults.DefaultUseCookies;
        internal CookieContainer? _cookieContainer;

        internal TokenImpersonationLevel _impersonationLevel = HttpHandlerDefaults.DefaultImpersonationLevel;   // this is here to support impersonation on HttpWebRequest

        internal bool _allowAutoRedirect = HttpHandlerDefaults.DefaultAutomaticRedirection;
        internal int _maxAutomaticRedirections = HttpHandlerDefaults.DefaultMaxAutomaticRedirections;

        internal int _maxConnectionsPerServer = HttpHandlerDefaults.DefaultMaxConnectionsPerServer;
        internal int _maxResponseDrainSize = HttpHandlerDefaults.DefaultMaxResponseDrainSize;
        internal TimeSpan _maxResponseDrainTime = HttpHandlerDefaults.DefaultResponseDrainTimeout;
        internal int _maxResponseHeadersLength = HttpHandlerDefaults.DefaultMaxResponseHeadersLength;

        internal TimeSpan _pooledConnectionLifetime = HttpHandlerDefaults.DefaultPooledConnectionLifetime;
        internal TimeSpan _pooledConnectionIdleTimeout = HttpHandlerDefaults.DefaultPooledConnectionIdleTimeout;
        internal TimeSpan _expect100ContinueTimeout = HttpHandlerDefaults.DefaultExpect100ContinueTimeout;
        internal TimeSpan _keepAlivePingTimeout = HttpHandlerDefaults.DefaultKeepAlivePingTimeout;
        internal TimeSpan _keepAlivePingDelay = HttpHandlerDefaults.DefaultKeepAlivePingDelay;
        internal HttpKeepAlivePingPolicy _keepAlivePingPolicy = HttpHandlerDefaults.DefaultKeepAlivePingPolicy;
        internal TimeSpan _connectTimeout = HttpHandlerDefaults.DefaultConnectTimeout;

        internal HeaderEncodingSelector<HttpRequestMessage>? _requestHeaderEncodingSelector;
        internal HeaderEncodingSelector<HttpRequestMessage>? _responseHeaderEncodingSelector;

        internal RemoteCertificateValidationCallback? _remoteCertificateValidationCallback;
        internal LocalCertificateSelectionCallback? _localCertificateSelectionCallback;

        internal bool _enableMultipleHttp2Connections;

        internal Func<SocketsHttpConnectionContext, CancellationToken, ValueTask<Stream>>? _connectCallback;
        internal Func<SocketsHttpPlaintextStreamFilterContext, CancellationToken, ValueTask<Stream>>? _plaintextStreamFilter;

        internal IDictionary<string, object?>? _properties;

        // Http2 flow control settings:
        internal int _initialHttp2StreamWindowSize = HttpHandlerDefaults.DefaultInitialHttp2StreamWindowSize;

        /// <summary>Creates a copy of the settings but with some values normalized to suit the implementation.</summary>
        public HttpConnectionSettings CloneAndNormalize()
        {
            // Force creation of the cookie container if needed, so the original and clone share the same instance.
            if (_useCookies && _cookieContainer == null)
            {
                _cookieContainer = new CookieContainer();
            }

            var settings = new HttpConnectionSettings()
            {
                _allowAutoRedirect = _allowAutoRedirect,
                _cookieContainer = _cookieContainer,
                _connectTimeout = _connectTimeout,
                _expect100ContinueTimeout = _expect100ContinueTimeout,
                _maxAutomaticRedirections = _maxAutomaticRedirections,
                _maxConnectionsPerServer = _maxConnectionsPerServer,
                _maxResponseDrainSize = _maxResponseDrainSize,
                _maxResponseDrainTime = _maxResponseDrainTime,
                _maxResponseHeadersLength = _maxResponseHeadersLength,
                _pooledConnectionLifetime = _pooledConnectionLifetime,
                _pooledConnectionIdleTimeout = _pooledConnectionIdleTimeout,
                _properties = _properties,
                _useCookies = _useCookies,
                _keepAlivePingTimeout = _keepAlivePingTimeout,
                _keepAlivePingDelay = _keepAlivePingDelay,
                _keepAlivePingPolicy = _keepAlivePingPolicy,
                _requestHeaderEncodingSelector = _requestHeaderEncodingSelector,
                _responseHeaderEncodingSelector = _responseHeaderEncodingSelector,
                _remoteCertificateValidationCallback = _remoteCertificateValidationCallback,
                _localCertificateSelectionCallback = _localCertificateSelectionCallback,
                _enableMultipleHttp2Connections = _enableMultipleHttp2Connections,
                _connectCallback = _connectCallback,
                _plaintextStreamFilter = _plaintextStreamFilter,
                _initialHttp2StreamWindowSize = _initialHttp2StreamWindowSize,
                _impersonationLevel = _impersonationLevel,
            };

            return settings;
        }

        public int MaxResponseHeadersByteLength => (int)Math.Min(int.MaxValue, _maxResponseHeadersLength * 1024L);

        public bool EnableMultipleHttp2Connections => _enableMultipleHttp2Connections;
    }
}
