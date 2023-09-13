// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

namespace System.Buffers
{
    internal static class SearchValuesHelper
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector512<byte> DuplicateTo512(Vector128<byte> vector)
        {
            Vector256<byte> vector256 = Vector256.Create(vector, vector);
            return Vector512.Create(vector256, vector256);
        }
    }
}
