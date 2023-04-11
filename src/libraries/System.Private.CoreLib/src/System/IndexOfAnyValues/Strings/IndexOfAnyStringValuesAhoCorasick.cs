// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace System.Buffers
{
    internal sealed class IndexOfAnyStringValuesAhoCorasick<TCaseSensitivity, TFastScanVariant> : IndexOfAnyStringValuesBase
        where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
        where TFastScanVariant : struct, AhoCorasick.IFastScan
    {
        private readonly AhoCorasick _ahoCorasick;
        private readonly int _maxValueLength;

        public IndexOfAnyStringValuesAhoCorasick(AhoCorasick ahoCorasick, HashSet<string> uniqueValues) : base(uniqueValues)
        {
            _ahoCorasick = ahoCorasick;

            if (typeof(TCaseSensitivity) == typeof(TeddyHelper.CaseInsensitiveUnicode))
            {
                foreach (string value in uniqueValues)
                {
                    _maxValueLength = Math.Max(_maxValueLength, value.Length);
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) =>
            typeof(TCaseSensitivity) == typeof(TeddyHelper.CaseInsensitiveUnicode)
                ? IndexOfAnyMultiStringCaseInsensitiveUnicode(span)
                : _ahoCorasick.IndexOfAny<TCaseSensitivity, TFastScanVariant>(span);

        // TODO: We could do better for Invariant/ICU inside of the Aho-Corasick implementation
        // by trying to parse out surrogates and uppercasing them individually.
        // With NLS we're calling into the OS, so ¯\_(ツ)_/¯.
        private int IndexOfAnyMultiStringCaseInsensitiveUnicode(ReadOnlySpan<char> span)
        {
            if (span.Length <= 256)
            {
                Span<char> upperCase = stackalloc char[256].Slice(0, span.Length);

                int charsWritten = Ordinal.ToUpperOrdinal(span, upperCase);
                Debug.Assert(charsWritten == upperCase.Length);

                // CaseSensitive instead of CaseInsensitiveUnicode as we've already done the case conversion.
                return _ahoCorasick.IndexOfAny<TeddyHelper.CaseSensitive, TFastScanVariant>(upperCase);
            }
            else
            {
                if (span.IsEmpty)
                {
                    return -1;
                }

                // If the input is large, we avoid uppercasing all of it upfront.
                // We may find a match at position 0, so we want to behave closer to O(match offset) than O(input length).
#if DEBUG
                const int MinArraySize = 128; // Make it easier to test with shorter inputs
#else
                const int MinArraySize = 512;
#endif

                char[] buffer = ArrayPool<char>.Shared.Rent((int)Math.Clamp(_maxValueLength * 2L, MinArraySize, string.MaxLength + 2));

                int leftoverFromPreviousIteration = 0;
                int offsetFromStart = 0;
                int result;

                while (true)
                {
                    Span<char> newSpaceAvailable = buffer.AsSpan(leftoverFromPreviousIteration);
                    int toConvert = Math.Min(span.Length, newSpaceAvailable.Length);

                    int charsWritten = Ordinal.ToUpperOrdinal(span.Slice(0, toConvert), newSpaceAvailable);
                    Debug.Assert(charsWritten == toConvert);
                    span = span.Slice(toConvert);

                    Span<char> upperCaseBuffer = buffer.AsSpan(0, leftoverFromPreviousIteration + toConvert);
                    result = _ahoCorasick.IndexOfAny<TeddyHelper.CaseSensitive, TFastScanVariant>(upperCaseBuffer);

                    if (result >= 0 && (span.IsEmpty || result <= buffer.Length - _maxValueLength))
                    {
                        result += offsetFromStart;
                        break;
                    }

                    if (span.IsEmpty)
                    {
                        result = -1;
                        break;
                    }

                    leftoverFromPreviousIteration = _maxValueLength - 1;
                    buffer.AsSpan(buffer.Length - leftoverFromPreviousIteration).CopyTo(buffer);
                    offsetFromStart += buffer.Length - leftoverFromPreviousIteration;
                }

                ArrayPool<char>.Shared.Return(buffer);
                return result;
            }
        }
    }
}
