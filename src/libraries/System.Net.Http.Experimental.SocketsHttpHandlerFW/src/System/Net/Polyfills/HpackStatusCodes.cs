// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System.Net.Http.HPack
{
    internal static partial class StatusCodes
    {
        public static ReadOnlySpan<byte> ToStatusBytes(int statusCode)
        {
            // This logic is only called by the server, so not relevant for the client.
            throw new NotImplementedException();
        }
    }
}
