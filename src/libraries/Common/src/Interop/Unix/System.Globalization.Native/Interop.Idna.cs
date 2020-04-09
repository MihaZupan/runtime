// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Globalization
    {
        internal const int AllowUnassigned = 0x1;
        internal const int UseStd3AsciiRules = 0x2;

        internal static int ToAscii(uint flags, ReadOnlySpan<char> unicode, Span<char> ascii) =>
            ToAscii(flags, ref MemoryMarshal.GetReference(unicode), unicode.Length, ref MemoryMarshal.GetReference(ascii), ascii.Length);

        [DllImport(Libraries.GlobalizationNative, CharSet = CharSet.Unicode, EntryPoint = "GlobalizationNative_ToAscii")]
        private static extern unsafe int ToAscii(uint flags, ref char src, int srcLen, ref char dstBuffer, int dstBufferCapacity);

        internal static int ToUnicode(uint flags, ReadOnlySpan<char> ascii, Span<char> unicode) =>
            ToUnicode(flags, ref MemoryMarshal.GetReference(ascii), ascii.Length, ref MemoryMarshal.GetReference(unicode), unicode.Length);

        [DllImport(Libraries.GlobalizationNative, CharSet = CharSet.Unicode, EntryPoint = "GlobalizationNative_ToUnicode")]
        private static extern unsafe int ToUnicode(uint flags, ref char src, int srcLen, ref char dstBuffer, int dstBufferCapacity);
    }
}
