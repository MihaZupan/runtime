// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Http
{
    internal static class HttpUtilities
    {
        internal static bool IsSupportedScheme(string scheme) =>
            IsSupportedNonSecureScheme(scheme) ||
            IsSupportedSecureScheme(scheme);

        internal static bool IsSupportedNonSecureScheme(string scheme) =>
            ReferenceEquals(scheme, Uri.UriSchemeHttp) || IsNonSecureWebSocketScheme(scheme);

        internal static bool IsSupportedSecureScheme(string scheme) =>
            ReferenceEquals(scheme, Uri.UriSchemeHttps) || IsSecureWebSocketScheme(scheme);

        internal static bool IsNonSecureWebSocketScheme(string scheme) =>
            ReferenceEquals(scheme, Uri.UriSchemeWs);

        internal static bool IsSecureWebSocketScheme(string scheme) =>
            ReferenceEquals(scheme, Uri.UriSchemeWss);

        internal static bool IsSupportedProxyScheme(string scheme) =>
            ReferenceEquals(scheme, Uri.UriSchemeHttp) || IsSocksScheme(scheme);

        internal static bool IsSocksScheme(string scheme) =>
            string.Equals(scheme, "socks5", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scheme, "socks4a", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(scheme, "socks4", StringComparison.OrdinalIgnoreCase);
    }
}
