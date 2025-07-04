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
    /// It is a variant of <see cref="AsciiByteSearchValues{TUniqueLowNibble}"/>,
    /// but instead of only looking for unique nibbles, we can handle any set of ASCII values with unique low 6 bits.
    /// Generic ASCII logic is used for the <see cref="Vector128"/> and <see cref="Vector256"/> fallbacks for short inputs.
    /// </summary>
    internal sealed class AsciiUniqueLow6BitsByteSearchValues : SearchValues<byte>
    {
        private IndexOfAnyAsciiSearcher.UniqueLow6BitsState _state;

        public AsciiUniqueLow6BitsByteSearchValues(ReadOnlySpan<byte> values) =>
            IndexOfAnyAsciiSearcher.ComputeUniqueLow6BitsState(values, out _state);

        internal override byte[] GetValues() =>
            _state.Lookup.GetByteValues();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(byte value) =>
            _state.Lookup.Contains(value);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAny(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyExcept(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.IndexOfAny<IndexOfAnyAsciiSearcher.Negate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAny(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAnyExcept(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsAny(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.ContainsAny<IndexOfAnyAsciiSearcher.DontNegate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);

        [CompExactlyDependsOn(typeof(Avx512Vbmi))]
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsAnyExcept(ReadOnlySpan<byte> span) =>
            IndexOfAnyAsciiSearcher.ContainsAny<IndexOfAnyAsciiSearcher.Negate>(
                ref MemoryMarshal.GetReference(span), span.Length, ref _state);
    }
}
