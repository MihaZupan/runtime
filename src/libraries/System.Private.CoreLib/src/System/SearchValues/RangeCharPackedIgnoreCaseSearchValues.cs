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
        private readonly char _lowInclusive, _rangeInclusive;
        private readonly uint _lowUint, _highMinusLow;
        private IndexOfAnyAsciiSearcher.AsciiState _state;

        public RangeCharPackedIgnoreCaseSearchValues(ReadOnlySpan<char> values, char lowInclusive, char highInclusive)
        {
            Debug.Assert((lowInclusive | 0x20) == lowInclusive);

            (_lowInclusive, _rangeInclusive) = (lowInclusive, (char)(highInclusive - lowInclusive));
            _lowUint = lowInclusive;
            _highMinusLow = (uint)(highInclusive - lowInclusive);
            IndexOfAnyAsciiSearcher.ComputeAsciiState(values, out _state);
        }

        internal override char[] GetValues()
        {
            char[] values = new char[_rangeInclusive + 1];

            int low = _lowInclusive;
            for (int i = 0; i < values.Length; i++)
            {
                values[i] = (char)(low + i);
            }

            return values;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(char value) =>
            (uint)(value | 0x20) - _lowUint <= _highMinusLow;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Sse2))]
        internal override int IndexOfAny(ReadOnlySpan<char> span) =>
            PackedSpanHelpers.IndexOfAnyInRangeIgnoreCase(ref MemoryMarshal.GetReference(span), _lowInclusive, _rangeInclusive, span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Sse2))]
        internal override int IndexOfAnyExcept(ReadOnlySpan<char> span) =>
            PackedSpanHelpers.IndexOfAnyExceptInRangeIgnoreCase(ref MemoryMarshal.GetReference(span), _lowInclusive, _rangeInclusive, span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd))]
        [CompExactlyDependsOn(typeof(PackedSimd))]
        internal override int LastIndexOfAny(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAnyVectorized<IndexOfAnyAsciiSearcher.DontNegate, IndexOfAnyAsciiSearcher.Default>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd))]
        [CompExactlyDependsOn(typeof(PackedSimd))]
        internal override int LastIndexOfAnyExcept(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAnyVectorized<IndexOfAnyAsciiSearcher.Negate, IndexOfAnyAsciiSearcher.Default>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);
    }
}
