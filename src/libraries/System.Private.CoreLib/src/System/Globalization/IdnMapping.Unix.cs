// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Buffers;
using System.Diagnostics;

namespace System.Globalization
{
    public sealed partial class IdnMapping
    {
        private string GetAsciiCore(string unicodeString, ReadOnlySpan<char> unicode)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(unicodeString != null && unicodeString.Length >= unicode.Length);

            uint flags = Flags;
            CheckInvalidIdnCharacters(unicode, flags, nameof(unicode));

            const int StackallocThreshold = 512;
            // Each unicode character is represented by up to 3 ASCII chars
            // and the whole string is prefixed by "xn--" (length 4)
            int actualLength;
            if (unicode.Length * 3L + 4 <= StackallocThreshold)
            {
                Span<char> outputStack = stackalloc char[StackallocThreshold];
                actualLength = Interop.Globalization.ToAscii(flags, unicode, outputStack);
                if (actualLength > 0 && actualLength <= StackallocThreshold)
                {
                    return GetStringForOutput(unicodeString, unicode, outputStack.AsSpan(0, actualLength));
                }
            }
            else
            {
                actualLength = Interop.Globalization.ToAscii(flags, unicode, Span<char>.Empty);
            }

            if (actualLength == 0)
            {
                throw new ArgumentException(SR.Argument_IdnIllegalName, nameof(unicode));
            }

            char[] outputBuffer = ArrayPool<char>.Shared.Rent(actualLength);
            try
            {
                actualLength = Interop.Globalization.ToAscii(flags, unicode, outputBuffer);
                if (finalLength == 0 || actualLength > outputBuffer.Length)
                {
                    throw new ArgumentException(SR.Argument_IdnIllegalName, nameof(unicode));
                }
                return GetStringForOutput(unicodeString, unicode, outputBuffer.AsSpan(0, actualLength));
            }
            finally
            {
                ArrayPool<char>.Shared.Return(outputBuffer);
            }
        }

        private unsafe string GetUnicodeCore(string asciiString, ReadOnlySpan<char> ascii)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(asciiString != null && asciiString.Length >= ascii.Length);

            uint flags = Flags;
            CheckInvalidIdnCharacters(ascii, flags, nameof(ascii));

            const int StackAllocThreshold = 512;
            if (ascii.Length <= StackAllocThreshold)
            {
                return GetUnicodeCore(asciiString, ascii, flags, stackalloc char[StackAllocThreshold], reattempt: true);
            }
            else
            {
                char[] outputBuffer = ArrayPool<char>.Shared.Rent(count);
                try
                {
                    return GetUnicodeCore(asciiString, ascii, flags, outputBuffer, reattempt: true);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(outputBuffer);
                }
            }
        }

        private unsafe string GetUnicodeCore(string asciiString, ReadOnlySpan<char> ascii, uint flags, Span<char> output, bool reattempt)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(asciiString != null && asciiString.Length >= ascii.Length);

            int realLen = Interop.Globalization.ToUnicode(flags, ascii, output);

            if (realLen == 0)
            {
                throw new ArgumentException(SR.Argument_IdnIllegalName, nameof(ascii));
            }
            else if (realLen <= outputLength)
            {
                return GetStringForOutput(asciiString, ascii, output.Slice(0, realLen));
            }
            else if (reattempt)
            {
                char[] outputBuffer = ArrayPool<char>.Shared.Rent(realLen);
                try
                {
                    return GetUnicodeCore(asciiString, ascii, flags, outputBuffer, reattempt: true);
                }
                finally
                {
                    ArrayPool<char>.Shared.Return(outputBuffer);
                }
            }

            throw new ArgumentException(SR.Argument_IdnIllegalName, nameof(ascii));
        }

        // -----------------------------
        // ---- PAL layer ends here ----
        // -----------------------------

        private uint Flags
        {
            get
            {
                int flags =
                    (AllowUnassigned ? Interop.Globalization.AllowUnassigned : 0) |
                    (UseStd3AsciiRules ? Interop.Globalization.UseStd3AsciiRules : 0);
                return (uint)flags;
            }
        }

        /// <summary>
        /// ICU doesn't check for invalid characters unless the STD3 rules option
        /// is enabled.
        ///
        /// To match Windows behavior, we walk the string ourselves looking for these
        /// bad characters so we can continue to throw ArgumentException in these cases.
        /// </summary>
        private static unsafe void CheckInvalidIdnCharacters(ReadOnlySpan<char> source, uint flags, string paramName)
        {
            if ((flags & Interop.Globalization.UseStd3AsciiRules) == 0)
            {
                foreach (char c in source)
                {
                    // These characters are prohibited regardless of the UseStd3AsciiRules property.
                    // See https://msdn.microsoft.com/en-us/library/system.globalization.idnmapping.usestd3asciirules(v=vs.110).aspx
                    if (c <= 0x1F || c == 0x7F)
                    {
                        throw new ArgumentException(SR.Argument_IdnIllegalName, paramName);
                    }
                }
            }
        }
    }
}
