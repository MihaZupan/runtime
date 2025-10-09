// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// ------------------------------------------------------------------------------
// Changes to this file must follow the https://aka.ms/api-review process.
// ------------------------------------------------------------------------------

namespace System.Net.Http
{
    public delegate System.Text.Encoding? HeaderEncodingSelector<TContext>(string headerName, TContext context);
    public partial class HttpIOException : System.IO.IOException
    {
        public HttpIOException(System.Net.Http.HttpRequestError httpRequestError, string? message = null, System.Exception? innerException = null) { }
        public System.Net.Http.HttpRequestError HttpRequestError { get { throw null; } }
        public override string Message { get { throw null; } }
    }
    public sealed partial class HttpProtocolException : System.Net.Http.HttpIOException
    {
        public HttpProtocolException(long errorCode, string message, System.Exception? innerException) : base(default(System.Net.Http.HttpRequestError), default(string), default(System.Exception)) { }
        public long ErrorCode { get { throw null; } }
    }
    public enum HttpRequestError
    {
        Unknown = 0,
        NameResolutionError = 1,
        ConnectionError = 2,
        SecureConnectionError = 3,
        HttpProtocolError = 4,
        ExtendedConnectNotSupported = 5,
        VersionNegotiationError = 6,
        UserAuthenticationError = 7,
        ProxyTunnelError = 8,
        InvalidResponse = 9,
        ResponseEnded = 10,
        ConfigurationLimitExceeded = 11,
    }
    public sealed partial class HttpRequestExceptionEx : System.Net.Http.HttpRequestException
    {
        public HttpRequestExceptionEx() { }
        public HttpRequestExceptionEx(System.Net.Http.HttpRequestError httpRequestError, string? message = null, System.Exception? inner = null, System.Net.HttpStatusCode? statusCode = default(System.Net.HttpStatusCode?)) { }
        public HttpRequestExceptionEx(string? message) { }
        public HttpRequestExceptionEx(string? message, System.Exception? inner) { }
        public HttpRequestExceptionEx(string? message, System.Exception? inner, System.Net.HttpStatusCode? statusCode) { }
        public System.Net.Http.HttpRequestError HttpRequestError { get { throw null; } }
        public System.Net.HttpStatusCode? StatusCode { get { throw null; } }
    }
    public enum HttpKeepAlivePingPolicy
    {
        WithActiveRequests = 0,
        Always = 1,
    }
    public sealed partial class SocketsHttpConnectionContext
    {
        internal SocketsHttpConnectionContext() { }
        public System.Net.DnsEndPoint DnsEndPoint { get { throw null; } }
        public System.Net.Http.HttpRequestMessage InitialRequestMessage { get { throw null; } }
    }
    public sealed partial class SocketsHttpPlaintextStreamFilterContext
    {
        internal SocketsHttpPlaintextStreamFilterContext() { }
        public System.Net.Http.HttpRequestMessage InitialRequestMessage { get { throw null; } }
        public System.Version NegotiatedHttpVersion { get { throw null; } }
        public System.IO.Stream PlaintextStream { get { throw null; } }
    }
}
namespace System.Net.Http.DoNotUseInProduction.TestingOnly
{
    public sealed partial class SocketsHttpHandler : System.Net.Http.HttpMessageHandler
    {
        public SocketsHttpHandler() { }
        public bool AllowAutoRedirect { get { throw null; } set { } }
        public System.Func<System.Net.Http.SocketsHttpConnectionContext, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.IO.Stream>>? ConnectCallback { get { throw null; } set { } }
        public System.TimeSpan ConnectTimeout { get { throw null; } set { } }
        [System.Diagnostics.CodeAnalysis.AllowNullAttribute]
        public System.Net.CookieContainer CookieContainer { get { throw null; } set { } }
        public bool EnableMultipleHttp2Connections { get { throw null; } set { } }
        public System.TimeSpan Expect100ContinueTimeout { get { throw null; } set { } }
        public int InitialHttp2StreamWindowSize { get { throw null; } set { } }
        public System.TimeSpan KeepAlivePingDelay { get { throw null; } set { } }
        public System.Net.Http.HttpKeepAlivePingPolicy KeepAlivePingPolicy { get { throw null; } set { } }
        public System.TimeSpan KeepAlivePingTimeout { get { throw null; } set { } }
        public int MaxAutomaticRedirections { get { throw null; } set { } }
        public int MaxConnectionsPerServer { get { throw null; } set { } }
        public int MaxResponseDrainSize { get { throw null; } set { } }
        public int MaxResponseHeadersLength { get { throw null; } set { } }
        public System.Func<System.Net.Http.SocketsHttpPlaintextStreamFilterContext, System.Threading.CancellationToken, System.Threading.Tasks.ValueTask<System.IO.Stream>>? PlaintextStreamFilter { get { throw null; } set { } }
        public System.TimeSpan PooledConnectionIdleTimeout { get { throw null; } set { } }
        public System.TimeSpan PooledConnectionLifetime { get { throw null; } set { } }
        public System.Collections.Generic.IDictionary<string, object?> Properties { get { throw null; } }
        public System.Net.Http.HeaderEncodingSelector<System.Net.Http.HttpRequestMessage>? RequestHeaderEncodingSelector { get { throw null; } set { } }
        public System.TimeSpan ResponseDrainTimeout { get { throw null; } set { } }
        public System.Net.Http.HeaderEncodingSelector<System.Net.Http.HttpRequestMessage>? ResponseHeaderEncodingSelector { get { throw null; } set { } }
        public bool UseCookies { get { throw null; } set { } }
        protected override void Dispose(bool disposing) { }
        protected override System.Threading.Tasks.Task<System.Net.Http.HttpResponseMessage> SendAsync(System.Net.Http.HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) { throw null; }
        public System.Net.Security.LocalCertificateSelectionCallback? LocalCertificateSelectionCallback { get { throw null; } set { } }
        public System.Net.Security.RemoteCertificateValidationCallback? RemoteCertificateValidationCallback { get { throw null; } set { } }
    }
}
