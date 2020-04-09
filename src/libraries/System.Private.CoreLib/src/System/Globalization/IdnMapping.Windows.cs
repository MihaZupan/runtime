// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace System.Globalization
{
    public sealed partial class IdnMapping
    {
        private unsafe string GetAsciiCore(string unicodeString, ReadOnlySpan<char> unicode)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(unicodeString != null && unicodeString.Length >= unicode.Length);

            uint flags = Flags;

            // Determine the required length
            int length = Interop.Normaliz.IdnToAscii(flags, unicode, Span<char>.Empty);
            if (length == 0)
            {
                ThrowForZeroLength(unicode: true);
            }

            // Do the conversion
            const int StackAllocThreshold = 512; // arbitrary limit to switch from stack to heap allocation
            if (length <= StackAllocThreshold)
            {
                return GetAsciiCore(unicodeString, unicode, flags, stackalloc char[StackAllocThreshold]);
            }
            else
            {
                char[] outputBuffer = ArrayPool<char>.Shared.Rent(length);
                try
                {
                    return GetAsciiCore(unicodeString, unicode, flags, outputBuffer);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(outputBuffer);
                }
            }
        }

        private unsafe string GetAsciiCore(string unicodeString, ReadOnlySpan<char> unicode, uint flags, Span<char> output)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(unicodeString != null && unicodeString.Length >= unicode.Length);

            int length = Interop.Normaliz.IdnToAscii(flags, unicode, output);
            if (length == 0)
            {
                ThrowForZeroLength(unicode: true);
            }
            return GetStringForOutput(unicodeString, unicode, output.Slice(0, length));
        }

        private unsafe string GetUnicodeCore(string asciiString, ReadOnlySpan<char> ascii)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(asciiString != null && asciiString.Length >= ascii.Length);

            uint flags = Flags;

            // Determine the required length
            int length = Interop.Normaliz.IdnToUnicode(flags, ascii, Span<char>.Empty);
            if (length == 0)
            {
                ThrowForZeroLength(unicode: false);
            }

            // Do the conversion
            const int StackAllocThreshold = 512; // arbitrary limit to switch from stack to heap allocation
            if (length <= StackAllocThreshold)
            {
                return GetUnicodeCore(asciiString, ascii, flags, stackalloc char[StackAllocThreshold]);
            }
            else
            {
                char[] outputBuffer = ArrayPool<char>.Shared.Rent(length);
                try
                {
                    return GetUnicodeCore(asciiString, ascii, flags, outputBuffer);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(outputBuffer);
                }
            }
        }

        private unsafe string GetUnicodeCore(string asciiString, ReadOnlySpan<char> ascii, uint flags, Span<char> output)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(asciiString != null && asciiString.Length >= ascii.Length);

            int length = Interop.Normaliz.IdnToUnicode(flags, ascii, output);
            if (length == 0)
            {
                ThrowForZeroLength(unicode: false);
            }
            return GetStringForOutput(asciiString, ascii, output.Slice(0, length));
        }

        // -----------------------------
        // ---- PAL layer ends here ----
        // -----------------------------

        private uint Flags
        {
            get
            {
                int flags =
                    (AllowUnassigned ? Interop.Normaliz.IDN_ALLOW_UNASSIGNED : 0) |
                    (UseStd3AsciiRules ? Interop.Normaliz.IDN_USE_STD3_ASCII_RULES : 0);
                return (uint)flags;
            }
        }

        [DoesNotReturn]
        private static void ThrowForZeroLength(bool unicode)
        {
            int lastError = Marshal.GetLastWin32Error();

            throw new ArgumentException(
                lastError == Interop.Errors.ERROR_INVALID_NAME ? SR.Argument_IdnIllegalName :
                    (unicode ? SR.Argument_InvalidCharSequenceNoIndex : SR.Argument_IdnBadPunycode),
                unicode ? "unicode" : "ascii");
        }
    }
}
