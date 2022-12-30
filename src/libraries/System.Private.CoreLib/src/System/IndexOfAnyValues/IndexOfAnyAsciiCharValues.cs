// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace System.Buffers
{
    internal sealed class IndexOfAnyAsciiCharValues<TOptimizations> : IndexOfAnyValues<char>
        where TOptimizations : struct, IndexOfAnyAsciiSearcher.IOptimizations
    {
        private readonly Vector128<byte> _bitmap;
        private BitVector256 _lookup;

        public IndexOfAnyAsciiCharValues(Vector128<byte> bitmap, BitVector256 lookup)
        {
            _bitmap = bitmap;
            _lookup = lookup;
        }

        internal override char[] GetValues() => _lookup.GetCharValues();

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(char value) =>
            _lookup.Contains128(value);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAny(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.IndexOfAnyVectorized<IndexOfAnyAsciiSearcher.DontNegate, TOptimizations>(ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, _bitmap, ref _lookup);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyExcept(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.IndexOfAnyVectorized<IndexOfAnyAsciiSearcher.Negate, TOptimizations>(ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, _bitmap, ref _lookup);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAny(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAnyVectorized<IndexOfAnyAsciiSearcher.DontNegate, TOptimizations>(ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, _bitmap);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAnyExcept(ReadOnlySpan<char> span) =>
            IndexOfAnyAsciiSearcher.LastIndexOfAnyVectorized<IndexOfAnyAsciiSearcher.Negate, TOptimizations>(ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(span)), span.Length, _bitmap);
    }
}
