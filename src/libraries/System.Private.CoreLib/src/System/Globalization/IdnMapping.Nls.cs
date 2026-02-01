// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace System.Globalization
{
    public sealed partial class IdnMapping
    {
        private string NlsConvertCore(ReadOnlySpan<char> source, string? backingString, bool fromUnicode)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(GlobalizationMode.UseNls);
            Debug.Assert(backingString is null || source.SequenceEqual(backingString));

            uint flags = NlsFlags;

            // Determine the required length
            int length = fromUnicode
                ? Interop.Normaliz.IdnToAscii(flags, source, source.Length, null, 0)
                : Interop.Normaliz.IdnToUnicode(flags, source, source.Length, null, 0);

            if (length == 0)
            {
                ThrowForNativeError(Marshal.GetLastPInvokeError(), fromUnicode);
            }

            Span<char> output = length <= StackallocThreshold
                ? stackalloc char[StackallocThreshold]
                : new char[length];

            if (!NlsTryConvertCore(source, output, out int charsWritten, fromUnicode))
            {
                // This should only happen if the source changed concurrently to the call to IdnMapping.
                // Throw just in case to avoid exposing uninitialized memory.
                ThrowForNativeError(Interop.Errors.ERROR_INSUFFICIENT_BUFFER, fromUnicode);
            }

            Debug.Assert(charsWritten == length);

            return GetStringForOutput(backingString, output.Slice(0, length));
        }

        private bool NlsTryConvertCore(ReadOnlySpan<char> source, Span<char> destination, out int charsWritten, bool fromUnicode)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(GlobalizationMode.UseNls);

            uint flags = NlsFlags;

            charsWritten = fromUnicode
                ? Interop.Normaliz.IdnToAscii(flags, source, source.Length, destination, destination.Length)
                : Interop.Normaliz.IdnToUnicode(flags, source, source.Length, destination, destination.Length);

            if (charsWritten == 0)
            {
                int error = Marshal.GetLastPInvokeError();

                if (error == Interop.Errors.ERROR_INSUFFICIENT_BUFFER)
                {
                    charsWritten = 0;
                    return false;
                }

                ThrowForNativeError(error, fromUnicode);
            }

            return true;
        }

        private uint NlsFlags
        {
            get
            {
                int flags =
                    (AllowUnassigned ? Interop.Normaliz.IDN_ALLOW_UNASSIGNED : 0) |
                    (UseStd3AsciiRules ? Interop.Normaliz.IDN_USE_STD3_ASCII_RULES : 0);
                return (uint)flags;
            }
        }

        private static void ThrowForNativeError(int error, bool unicode)
        {
            throw new ArgumentException(
                error == Interop.Errors.ERROR_INVALID_NAME ? SR.Argument_IdnIllegalName :
                    (unicode ? SR.Argument_InvalidCharSequenceNoIndex : SR.Argument_IdnBadPunycode),
                unicode ? "unicode" : "ascii");
        }
    }
}
