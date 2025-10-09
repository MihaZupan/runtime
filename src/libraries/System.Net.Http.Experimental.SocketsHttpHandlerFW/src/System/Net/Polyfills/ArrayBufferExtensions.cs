// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace System.Net
{
    internal static class ArrayBufferExtensions
    {
        public static Task<int> ReadAsync(this ArrayBuffer buffer, Stream stream, CancellationToken cancellationToken = default)
        {
            return stream.ReadAsync(buffer.DangerousGetUnderlyingBuffer(), buffer.Capacity - buffer.AvailableLength, buffer.AvailableLength, cancellationToken);
        }

        public static Task WriteAsync(this ArrayBuffer buffer, Stream stream, CancellationToken cancellationToken = default)
        {
            return stream.WriteAsync(buffer.DangerousGetUnderlyingBuffer(), buffer.ActiveStartOffset, buffer.ActiveLength, cancellationToken);
        }
    }

}
