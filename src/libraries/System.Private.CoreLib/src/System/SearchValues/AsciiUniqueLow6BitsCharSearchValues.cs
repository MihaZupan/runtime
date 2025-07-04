// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace System.Buffers
{
    /// <summary>
    /// This type is specific to <see cref="Avx512Vbmi"/> hardware.
    /// It is a variant of <see cref="AsciiCharSearchValues{TOptimizations, TUniqueLowNibble}"/>,
    /// but instead of only looking for unique nibbles, we can handle any set of ASCII values with unique low 6 bits.
    /// Generic ASCII logic is used for the <see cref="Vector128"/> and <see cref="Vector256"/> fallbacks for short inputs.
    /// </summary>
    internal sealed class AsciiUniqueLow6BitsCharSearchValues<TOptimizations> : SearchValues<char>
        where TOptimizations : struct, IndexOfAnyAsciiSearcher.IOptimizations
    {
        private IndexOfAnyAsciiSearcher.UniqueLow6BitsState _state;

        public AsciiUniqueLow6BitsCharSearchValues(ReadOnlySpan<char> values) =>
            IndexOfAnyAsciiSearcher.ComputeUniqueLow6BitsState(values, out _state);

        internal override char[] GetValues() =>
            _state.Lookup.GetCharValues();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(char value) =>
            _state.Lookup.Contains128(value);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAny(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.DontNegate, TOptimizations>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyExcept(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.Negate, TOptimizations>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAny(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.DontNegate, TOptimizations>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAnyExcept(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate, TOptimizations>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsAny(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.ContainsAny<IndexOfAnyAsciiSearcher.DontNegate, TOptimizations>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsAnyExcept(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.ContainsAny<IndexOfAnyAsciiSearcher.Negate, TOptimizations>(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, ref _state);
    }
}
