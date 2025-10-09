// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net.Http
{
    internal static class ConnectHelper
    {
        /// <summary>
        /// Helper type used by HttpClientHandler when wrapping SocketsHttpHandler to map its
        /// certificate validation callback to the one used by SslStream.
        /// </summary>
        internal sealed class CertificateCallbackMapper
        {
            public readonly Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool> FromHttpClientHandler;
            public readonly RemoteCertificateValidationCallback ForSocketsHttpHandler;

            public CertificateCallbackMapper(Func<HttpRequestMessage, X509Certificate2?, X509Chain?, SslPolicyErrors, bool> fromHttpClientHandler)
            {
                FromHttpClientHandler = fromHttpClientHandler;
                ForSocketsHttpHandler = (object sender, X509Certificate? certificate, X509Chain? chain, SslPolicyErrors sslPolicyErrors) =>
                    FromHttpClientHandler((HttpRequestMessage)sender, certificate as X509Certificate2, chain, sslPolicyErrors);
            }
        }

#pragma warning disable IDE0060 // Remove unused parameter
        public static async ValueTask<SslStream> EstablishSslConnectionAsync(HttpConnectionSettings settings, string host, HttpRequestMessage request, bool async, Stream stream, CancellationToken cancellationToken)
#pragma warning restore IDE0060
        {
            var sslStream = new SslStream(stream, leaveInnerStreamOpen: false, settings._remoteCertificateValidationCallback, settings._localCertificateSelectionCallback);

            try
            {
                if (async)
                {
                    FrameworkHttp2ALPNSslStreamPatch.UseInCurrentContext();

                    await sslStream.AuthenticateAsClientAsync(host).ConfigureAwait(false);
                }
                else
                {
                    throw new NotImplementedException();
                }
            }
            catch (Exception e)
            {
                sslStream.Dispose();

                if (e is OperationCanceledException)
                {
                    throw;
                }

                if (CancellationHelper.ShouldWrapInOperationCanceledException(e, cancellationToken))
                {
                    throw CancellationHelper.CreateOperationCanceledException(e, cancellationToken);
                }

                HttpRequestException ex = new HttpRequestExceptionEx(HttpRequestError.SecureConnectionError, SR.net_http_ssl_connection_failed, e);
                throw ex;
            }

            // Handle race condition if cancellation happens after SSL auth completes but before the registration is disposed
            if (cancellationToken.IsCancellationRequested)
            {
                sslStream.Dispose();
                throw CancellationHelper.CreateOperationCanceledException(null, cancellationToken);
            }

            return sslStream;
        }

        internal static Exception CreateWrappedException(Exception exception, string host, int port, CancellationToken cancellationToken)
        {
            return CancellationHelper.ShouldWrapInOperationCanceledException(exception, cancellationToken) ?
                CancellationHelper.CreateOperationCanceledException(exception, cancellationToken) :
                new HttpRequestExceptionEx(DeduceError(exception), $"{exception.Message} ({host}:{port})", exception);

            static HttpRequestError DeduceError(Exception exception)
            {
                if (exception is AuthenticationException)
                {
                    return HttpRequestError.SecureConnectionError;
                }

                // Resolving a non-existent hostname often leads to EAI_AGAIN/TryAgain on Linux, indicating a non-authoritative failure, eg. timeout.
                // Getting EAGAIN/TryAgain from a TCP connect() is not possible on Windows or Mac according to the docs and indicates lack of kernel resources on Linux,
                // which should be a very rare error in practice. As a result, mapping SocketError.TryAgain to HttpRequestError.NameResolutionError
                // leads to a more reliable distinction between NameResolutionError and ConnectionError.
                if (exception is SocketException socketException &&
                    socketException.SocketErrorCode is SocketError.HostNotFound or SocketError.TryAgain)
                {
                    return HttpRequestError.NameResolutionError;
                }

                return HttpRequestError.ConnectionError;
            }
        }
    }
}
