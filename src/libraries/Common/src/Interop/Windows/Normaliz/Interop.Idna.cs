// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;

internal static partial class Interop
{
    internal static partial class Normaliz
    {
        internal static int IdnToAscii(uint dwFlags, ReadOnlySpan<char> unicode, Span<char> ascii) =>
            IdnToAscii(dwFlags, ref MemoryMarshal.GetReference(unicode), unicode.Length, ref MemoryMarshal.GetReference(ascii), ascii.Length);

        [DllImport("Normaliz.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern unsafe int IdnToAscii(
                                        uint dwFlags,
                                        ref char lpUnicodeCharStr,
                                        int cchUnicodeChar,
                                        ref char lpASCIICharStr,
                                        int cchASCIIChar);

        internal static int IdnToUnicode(uint dwFlags, ReadOnlySpan<char> ascii, Span<char> unicode) =>
            IdnToUnicode(dwFlags, ref MemoryMarshal.GetReference(ascii), ascii.Length, ref MemoryMarshal.GetReference(unicode), unicode.Length);

        [DllImport("Normaliz.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern unsafe int IdnToUnicode(
                                        uint dwFlags,
                                        ref char lpASCIICharStr,
                                        int cchASCIIChar,
                                        ref char lpUnicodeCharStr,
                                        int cchUnicodeChar);

        internal const int IDN_ALLOW_UNASSIGNED = 0x1;
        internal const int IDN_USE_STD3_ASCII_RULES = 0x2;
    }
}
