// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.Wasm;
using System.Runtime.Intrinsics.X86;

namespace System.Buffers
{
    internal sealed class RangeCharPackedIgnoreCaseSearchValues : SearchValues<char>
    {
        private readonly char _lowInclusive, _highMinusLow;
        private IndexOfAnyAsciiSearcher.AsciiState _state;

        public RangeCharPackedIgnoreCaseSearchValues(ReadOnlySpan<char> values, char firstRangeLow, char secondRangeLow, int range)
        {
            Debug.Assert((firstRangeLow & 0x20) == 0 && (firstRangeLow | 0x20) == secondRangeLow);
            Debug.Assert(PackedSpanHelpers.CanUsePackedIndexOf(secondRangeLow + range - 1));

            // PackedSpanHelpers.IndexOfAnyInRangeIgnoreCase assumes that the range describes the uppercase range.
            _lowInclusive = secondRangeLow;
            _highMinusLow = (char)(range - 1);
            IndexOfAnyAsciiSearcher.ComputeAsciiState(values, out _state);
        }

        internal override char[] GetValues() =>
            _state.Lookup.GetCharValues();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(char value) =>
            _state.Lookup.Contains128(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Sse2))]
        internal override int IndexOfAny(ReadOnlySpan<char> span) =>
            PackedSpanHelpers.IndexOfAnyInRangeIgnoreCase(ref MemoryMarshal.GetReference(span), _lowInclusive, _highMinusLow, span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Sse2))]
        internal override int IndexOfAnyExcept(ReadOnlySpan<char> span) =>
            PackedSpanHelpers.IndexOfAnyExceptInRangeIgnoreCase(ref MemoryMarshal.GetReference(span), _lowInclusive, _highMinusLow, span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd))]
        [CompExactlyDependsOn(typeof(PackedSimd))]
        internal override int LastIndexOfAny(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.DontNegate, IndexOfAnyAsciiSearcher.Default>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd))]
        [CompExactlyDependsOn(typeof(PackedSimd))]
        internal override int LastIndexOfAnyExcept(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Default>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);
    }
}
