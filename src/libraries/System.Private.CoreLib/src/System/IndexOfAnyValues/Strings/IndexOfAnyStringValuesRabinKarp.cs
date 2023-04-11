// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers
{
    internal abstract class IndexOfAnyStringValuesRabinKarp<TCaseSensitivity> : IndexOfAnyStringValuesBase
        where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
    {
        private readonly RabinKarp _rabinKarp;

        public IndexOfAnyStringValuesRabinKarp(RabinKarp rabinKarp, HashSet<string> uniqueValues) : base(uniqueValues)
        {
            _rabinKarp = rabinKarp;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected int ShortInputFallback(ReadOnlySpan<char> span, ref char current) =>
            typeof(TCaseSensitivity) == typeof(TeddyHelper.CaseInsensitiveUnicode)
                ? IndexOfAnyMultiStringCaseInsensitiveUnicode(span, ref current)
                : _rabinKarp.IndexOfAny<TCaseSensitivity>(span, ref current);

        private int IndexOfAnyMultiStringCaseInsensitiveUnicode(ReadOnlySpan<char> span, ref char current)
        {
            Debug.Assert(span.Length - (Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref current) / 2) < 34,
                "Teddy should have handled the start of the input.");

            int startOffset = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref current) / sizeof(char));
            span = span.Slice(startOffset);

            Span<char> upperCase = stackalloc char[34].Slice(0, span.Length);

            int charsWritten = Ordinal.ToUpperOrdinal(span, upperCase);
            Debug.Assert(charsWritten == upperCase.Length);

            // CaseSensitive instead of CaseInsensitiveUnicode as we've already done the case conversion.
            int offset = _rabinKarp.IndexOfAny<TeddyHelper.CaseSensitive>(upperCase, ref MemoryMarshal.GetReference(upperCase));

            if (offset >= 0)
            {
                offset += startOffset;
            }

            return offset;
        }
    }
}
