// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace System.Buffers
{
    internal sealed class IndexOfAnyAsciiWithNonAsciiFallbackCharValues<TOptimizations> : IndexOfAnyValues<char>
        where TOptimizations : struct, IndexOfAnyAsciiSearcher.IOptimizations
    {
        private readonly Vector128<byte> _inverseBitmap;
        private readonly uint[] _charBitmap;

        public IndexOfAnyAsciiWithNonAsciiFallbackCharValues(Vector128<byte> inverseBitmap, uint[] charBitmap)
        {
            Debug.Assert(charBitmap.Length == 65536 / 32);

            _inverseBitmap = inverseBitmap;
            _charBitmap = charBitmap;
        }

        internal override char[] GetValues()
        {
            var chars = new List<char>();
            for (int i = 0; i < 65536; i++)
            {
                if (ContainsCore((char)i))
                {
                    chars.Add((char)i);
                }
            }
            return chars.ToArray();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override bool ContainsCore(char value)
        {
            int c = value;
            return (_charBitmap[c >> 5] & (1u << c)) != 0;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAny(ReadOnlySpan<char> span) =>
            IndexOfAnyInverse<IndexOfAnyAsciiSearcher.Negate>(ref MemoryMarshal.GetReference(span), span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyExcept(ReadOnlySpan<char> span) =>
            IndexOfAnyInverse<IndexOfAnyAsciiSearcher.DontNegate>(ref MemoryMarshal.GetReference(span), span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAny(ReadOnlySpan<char> span) =>
            LastIndexOfAnyInverse<IndexOfAnyAsciiSearcher.Negate>(ref MemoryMarshal.GetReference(span), span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int LastIndexOfAnyExcept(ReadOnlySpan<char> span) =>
            LastIndexOfAnyInverse<IndexOfAnyAsciiSearcher.DontNegate>(ref MemoryMarshal.GetReference(span), span.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int IndexOfAnyInverse<TNegator>(ref char searchSpace, int searchSpaceLength)
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            if (!IndexOfAnyAsciiSearcher.IsVectorizationSupported)
            {
                throw new PlatformNotSupportedException();
            }

            return IndexOfAnyAsciiSearcher.IndexOfAnyVectorizedInverseWithNonAsciiFallback<TNegator, TOptimizations>(
                ref Unsafe.As<char, short>(ref searchSpace),
                searchSpaceLength,
                _inverseBitmap,
                ref MemoryMarshal.GetArrayDataReference(_charBitmap));
        }

        private int LastIndexOfAnyInverse<TNegator>(ref char searchSpace, int searchSpaceLength)
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            if (!IndexOfAnyAsciiSearcher.IsVectorizationSupported)
            {
                throw new PlatformNotSupportedException();
            }

            // TODO: LastIndexOfAnyVectorizedInverseWithNonAsciiFallback
            int i = IndexOfAnyAsciiSearcher.LastIndexOfAnyVectorized<TNegator, TOptimizations>(
                ref Unsafe.As<char, short>(ref searchSpace),
                searchSpaceLength,
                _inverseBitmap);

            if (i >= 0)
            {
                if (!TNegator.NegateIfNeeded(Unsafe.Add(ref searchSpace, i) < 128))
                {
                    return i;
                }

                ref uint charBitmap = ref MemoryMarshal.GetArrayDataReference(_charBitmap);

                do
                {
                    int c = Unsafe.Add(ref searchSpace, i);
                    uint offset = (uint)c >> 5;
                    uint significantBit = 1u << c;

                    if (TNegator.NegateIfNeeded((Unsafe.Add(ref charBitmap, offset) & significantBit) == 0))
                    {
                        return i;
                    }

                    i--;
                }
                while (i >= 0);
            }

            return -1;
        }
    }
}
