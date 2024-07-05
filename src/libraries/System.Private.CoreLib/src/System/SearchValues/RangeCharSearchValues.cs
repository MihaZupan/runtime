// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers
{
    internal sealed class RangeCharSearchValues<TShouldUsePacked> : SearchValues<char>
        where TShouldUsePacked : struct, SearchValues.IRuntimeConst
    {
        private readonly char _highMinusLow;
        private char _lowInclusive, _highInclusive;
        private readonly uint _lowUint, _highMinusLowUint;

        public RangeCharSearchValues(char lowInclusive, char highInclusive)
        {
            (_lowInclusive, _highMinusLow, _highInclusive) = (lowInclusive, (char)(highInclusive - lowInclusive), highInclusive);
            _lowUint = lowInclusive;
            _highMinusLowUint = _highMinusLow;
        }

        internal override char[] GetValues()
        {
            char[] values = new char[_highMinusLow + 1];

            int low = _lowInclusive;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (char)(low + i);
            }

            return values;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(char value) =>
            value - _lowUint <= _highMinusLowUint;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAny(ReadOnlySpan<char> span) =>
            (PackedSpanHelpers.PackedIndexOfIsSupported && TShouldUsePacked.Value)
                ? PackedSpanHelpers.IndexOfAnyInRange(ref MemoryMarshal.GetReference(span), _lowInclusive, _highMinusLow, span.Length)
                : SpanHelpers.NonPackedIndexOfAnyInRangeUnsignedNumber<ushort, SpanHelpers.DontNegate<ushort>>(
                    ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(span)),
                    Unsafe.As<char, ushort>(ref _lowInclusive),
                    Unsafe.As<char, ushort>(ref _highInclusive),
                    span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyExcept(ReadOnlySpan<char> span) =>
            (PackedSpanHelpers.PackedIndexOfIsSupported && TShouldUsePacked.Value)
                ? PackedSpanHelpers.IndexOfAnyExceptInRange(ref MemoryMarshal.GetReference(span), _lowInclusive, _highMinusLow, span.Length)
                : SpanHelpers.NonPackedIndexOfAnyInRangeUnsignedNumber<ushort, SpanHelpers.Negate<ushort>>(
                    ref Unsafe.As<char, ushort>(ref MemoryMarshal.GetReference(span)),
                    Unsafe.As<char, ushort>(ref _lowInclusive),
                    Unsafe.As<char, ushort>(ref _highInclusive),
                    span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAny(ReadOnlySpan<char> span) =>
            span.LastIndexOfAnyInRange(_lowInclusive, _highInclusive);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAnyExcept(ReadOnlySpan<char> span) =>
            span.LastIndexOfAnyExceptInRange(_lowInclusive, _highInclusive);
    }
}
