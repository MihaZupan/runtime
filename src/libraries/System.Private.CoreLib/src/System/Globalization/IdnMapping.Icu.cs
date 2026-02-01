// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Diagnostics;

namespace System.Globalization
{
    public sealed partial class IdnMapping
    {
        private string IcuConvertCore(ReadOnlySpan<char> source, string? backingString, bool fromUnicode)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(!GlobalizationMode.UseNls);
            Debug.Assert(backingString is null || source.SequenceEqual(backingString));

            uint flags = IcuFlags;
            CheckInvalidIdnCharacters(source, flags, fromUnicode);

            Span<char> output = source.Length <= StackallocThreshold
                ? stackalloc char[StackallocThreshold]
                : new char[source.Length];

            int actualLength = fromUnicode
                ? Interop.Globalization.ToAscii(flags, source, source.Length, output, output.Length)
                : Interop.Globalization.ToUnicode(flags, source, source.Length, output, output.Length);

            if (actualLength > output.Length)
            {
                // Retry with a larger buffer
                output = new char[actualLength];

                actualLength = fromUnicode
                    ? Interop.Globalization.ToAscii(flags, output, source.Length, output, output.Length)
                    : Interop.Globalization.ToUnicode(flags, output, source.Length, output, output.Length);
            }

            if (actualLength == 0 || actualLength > output.Length)
            {
                ThrowIdnIllegalName(fromUnicode);
            }

            return GetStringForOutput(backingString, output.Slice(0, actualLength));
        }

        private bool IcuTryConvertCore(ReadOnlySpan<char> source, Span<char> destination, out int charsWritten, bool fromUnicode)
        {
            Debug.Assert(!GlobalizationMode.Invariant);
            Debug.Assert(!GlobalizationMode.UseNls);

            uint flags = IcuFlags;
            CheckInvalidIdnCharacters(source, flags, fromUnicode);

            charsWritten = fromUnicode
                ? Interop.Globalization.ToAscii(flags, source, source.Length, destination, destination.Length)
                : Interop.Globalization.ToUnicode(flags, source, source.Length, destination, destination.Length);

            if (charsWritten == 0)
            {
                ThrowIdnIllegalName(fromUnicode);
            }

            if (charsWritten > destination.Length)
            {
                charsWritten = 0;
                return false;
            }

            return true;
        }

        private uint IcuFlags
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
        private static void CheckInvalidIdnCharacters(ReadOnlySpan<char> text, uint flags, bool fromUnicode)
        {
            if ((flags & Interop.Globalization.UseStd3AsciiRules) == 0 &&
                text.ContainsAny(s_invalidIdnCharacters))
            {
                ThrowIdnIllegalName(fromUnicode);
            }
        }

        private static void ThrowIdnIllegalName(bool fromUnicode) =>
            throw new ArgumentException(SR.Argument_IdnIllegalName, fromUnicode ? "unicode" : "ascii");

        // These characters are prohibited regardless of the UseStd3AsciiRules property.
        // See https://msdn.microsoft.com/library/system.globalization.idnmapping.usestd3asciirules(v=vs.110).aspx
        // [0..0x1F] + [0x7F]
        private static readonly SearchValues<char> s_invalidIdnCharacters = SearchValues.Create(
            "\u0000\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008\u0009\u000A\u000B\u000C\u000D\u000E\u000F" +
            "\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\u001B\u001C\u001D\u001E\u001F" +
            "\u007F");
    }
}
