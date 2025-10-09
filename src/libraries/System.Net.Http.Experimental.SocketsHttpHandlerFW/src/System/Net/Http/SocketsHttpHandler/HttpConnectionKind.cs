// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Http
{
    internal enum HttpConnectionKind : byte
    {
        Http,               // Non-secure connection with no proxy.
        Https,              // Secure connection with no proxy.
    }
}
