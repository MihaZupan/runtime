// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

#pragma warning disable IDE0060 // https://github.com/dotnet/roslyn-analyzers/issues/6228

namespace System.Buffers
{
    /// <summary>Data structure used to optimize checks for whether a char is in a set of chars.</summary>
    /// <remarks>
    /// Like a Bloom filter, the idea is to create a bit map of the characters we are
    /// searching for and use this map as a "cheap" check to decide if the current
    /// character in the string exists in the array of input characters. There are
    /// 256 bits in the map, with each character mapped to 2 bits. Every character is
    /// divided into 2 bytes, and then every byte is mapped to 1 bit. The character map
    /// is an array of 8 integers acting as map blocks. The 3 lsb in each byte in the
    /// character is used to index into this map to get the right block, the value of
    /// the remaining 5 msb are used as the bit position inside this block.
    /// </remarks>
    [StructLayout(LayoutKind.Sequential)]
    internal readonly struct ProbabilisticMap
    {
        private static bool IsVectorizationSupported => Sse41.IsSupported || AdvSimd.Arm64.IsSupported;

        private static uint IndexMask => IsVectorizationSupported ? 31u : 7u;
        private static int IndexShift => IsVectorizationSupported ? 5 : 3;

        private readonly uint _e0, _e1, _e2, _e3, _e4, _e5, _e6, _e7;

        public ProbabilisticMap(ReadOnlySpan<char> values)
        {
            bool hasAscii = false;
            ref uint charMap = ref _e0;

            for (int i = 0; i < values.Length; ++i)
            {
                int c = values[i];

                // Map low bit
                SetCharBit(ref charMap, (byte)c);

                // Map high bit
                c >>= 8;

                if (c == 0)
                {
                    hasAscii = true;
                }
                else
                {
                    SetCharBit(ref charMap, (byte)c);
                }
            }

            if (hasAscii)
            {
                // Common to search for ASCII symbols. Just set the high value once.
                SetCharBit(ref charMap, 0);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static void SetCharBit(ref uint charMap, byte value)
        {
            if (IsVectorizationSupported)
            {
                Unsafe.Add(ref Unsafe.As<uint, byte>(ref charMap), value & IndexMask) |= (byte)(1u << (value >> IndexShift));
            }
            else
            {
                Unsafe.Add(ref charMap, value & IndexMask) |= 1u << (value >> IndexShift);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool IsCharBitSet(ref uint charMap, byte value) => IsVectorizationSupported
            ? (Unsafe.Add(ref Unsafe.As<uint, byte>(ref charMap), value & IndexMask) & (1u << (value >> IndexShift))) != 0
            : (Unsafe.Add(ref charMap, value & IndexMask) & (1u << (value >> IndexShift))) != 0;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static bool Contains(ref uint charMap, ReadOnlySpan<char> values, int ch) =>
            IsCharBitSet(ref charMap, (byte)ch) &&
            IsCharBitSet(ref charMap, (byte)(ch >> 8)) &&
            Contains(values, (char)ch);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool Contains(ReadOnlySpan<char> values, char ch) =>
            SpanHelpers.NonPackedContainsValueType(
                ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(values)),
                (short)ch,
                values.Length);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> ContainsMask16Chars(Vector128<byte> charMapLower, Vector128<byte> charMapUpper, ref char searchSpace)
        {
            Vector128<ushort> source0 = Vector128.LoadUnsafe(ref Unsafe.As<char, ushort>(ref searchSpace));
            Vector128<ushort> source1 = Vector128.LoadUnsafe(ref Unsafe.As<char, ushort>(ref searchSpace), (nuint)Vector128<ushort>.Count);

            Vector128<byte> sourceLower = Sse2.IsSupported
                ? Sse2.PackUnsignedSaturate((source0 & Vector128.Create((ushort)255)).AsInt16(), (source1 & Vector128.Create((ushort)255)).AsInt16())
                : AdvSimd.Arm64.UnzipEven(source0.AsByte(), source1.AsByte());

            Vector128<byte> sourceUpper = Sse2.IsSupported
                ? Sse2.PackUnsignedSaturate(Vector128.ShiftRightLogical(source0, 8).AsInt16(), Vector128.ShiftRightLogical(source1, 8).AsInt16())
                : AdvSimd.Arm64.UnzipOdd(source0.AsByte(), source1.AsByte());

            Vector128<byte> resultLower = IsCharBitNotSet(charMapLower, charMapUpper, sourceLower);
            Vector128<byte> resultUpper = IsCharBitNotSet(charMapLower, charMapUpper, sourceUpper);

            return ~(resultLower | resultUpper);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> IsCharBitNotSet(Vector128<byte> charMapLower, Vector128<byte> charMapUpper, Vector128<byte> values)
        {
            Vector128<byte> bitPositions = Shuffle(Vector128.Create(0x8040201008040201).AsByte(), Vector128.ShiftRightLogical(values, IndexShift));

            Vector128<byte> index = values & Vector128.Create((byte)IndexMask);
            Vector128<byte> bitMaskLower = Shuffle(charMapLower, index);
            Vector128<byte> bitMaskUpper = Shuffle(charMapUpper, index - Vector128.Create((byte)16));
            Vector128<byte> mask = Vector128.GreaterThan(index, Vector128.Create((byte)15));
            Vector128<byte> bitMask = Vector128.ConditionalSelect(mask, bitMaskUpper, bitMaskLower);

            return Vector128.Equals(bitMask & bitPositions, Vector128<byte>.Zero);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Shuffle(Vector128<byte> vector, Vector128<byte> indices)
        {
            // We're not using Vector128.Shuffle as the caller already accounts for and relies on differences in behavior between platforms.
            return Ssse3.IsSupported
                ? Ssse3.Shuffle(vector, indices)
                : AdvSimd.Arm64.VectorTableLookup(vector, indices);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static bool ShouldUseSimpleLoop(int searchSpaceLength, int valuesLength)
        {
            // We can perform either
            // - a simple O(haystack * needle) search or
            // - compute a character map of the values in O(needle), followed by an O(haystack) search
            // As the constant factor to compute the character map is relatively high, it's more efficient
            // to perform a simple loop search for short inputs.
            //
            // The following check does an educated guess as to whether computing the bitmap is more expensive.
            // The limit of 20 on the haystack length is arbitrary, determined by experimentation.
            return searchSpaceLength < Vector128<short>.Count
                || (searchSpaceLength < 20 && searchSpaceLength < (valuesLength >> 1));
        }

        public static int IndexOfAny(ref char searchSpace, int searchSpaceLength, ref char values, int valuesLength) =>
            IndexOfAny<SpanHelpers.DontNegate<char>>(ref searchSpace, searchSpaceLength, ref values, valuesLength);

        public static int IndexOfAnyExcept(ref char searchSpace, int searchSpaceLength, ref char values, int valuesLength) =>
            IndexOfAny<SpanHelpers.Negate<char>>(ref searchSpace, searchSpaceLength, ref values, valuesLength);

        public static int LastIndexOfAny(ref char searchSpace, int searchSpaceLength, ref char values, int valuesLength) =>
            LastIndexOfAny<SpanHelpers.DontNegate<char>>(ref searchSpace, searchSpaceLength, ref values, valuesLength);

        public static int LastIndexOfAnyExcept(ref char searchSpace, int searchSpaceLength, ref char values, int valuesLength) =>
            LastIndexOfAny<SpanHelpers.Negate<char>>(ref searchSpace, searchSpaceLength, ref values, valuesLength);

        private static int IndexOfAny<TNegator>(ref char searchSpace, int searchSpaceLength, ref char values, int valuesLength)
            where TNegator : struct, SpanHelpers.INegator<char>
        {
            var valuesSpan = new ReadOnlySpan<char>(ref values, valuesLength);

            // If the search space is relatively short compared to the needle, do a simple O(n * m) search.
            if (ShouldUseSimpleLoop(searchSpaceLength, valuesLength))
            {
                ref char searchSpaceEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength);
                ref char cur = ref searchSpace;

                while (!Unsafe.AreSame(ref cur, ref searchSpaceEnd))
                {
                    char c = cur;
                    if (TNegator.NegateIfNeeded(valuesSpan.Contains(c)))
                    {
                        return (int)(Unsafe.ByteOffset(ref searchSpace, ref cur) / sizeof(char));
                    }

                    cur = ref Unsafe.Add(ref cur, 1);
                }

                return -1;
            }

            if (typeof(TNegator) == typeof(SpanHelpers.DontNegate<char>)
                ? IndexOfAnyAsciiSearcher.TryIndexOfAny(ref searchSpace, searchSpaceLength, valuesSpan, out int index)
                : IndexOfAnyAsciiSearcher.TryIndexOfAnyExcept(ref searchSpace, searchSpaceLength, valuesSpan, out index))
            {
                return index;
            }

            return ProbabilisticIndexOfAny<TNegator>(ref searchSpace, searchSpaceLength, ref values, valuesLength);
        }

        private static int LastIndexOfAny<TNegator>(ref char searchSpace, int searchSpaceLength, ref char values, int valuesLength)
            where TNegator : struct, SpanHelpers.INegator<char>
        {
            var valuesSpan = new ReadOnlySpan<char>(ref values, valuesLength);

            // If the search space is relatively short compared to the needle, do a simple O(n * m) search.
            if (ShouldUseSimpleLoop(searchSpaceLength, valuesLength))
            {
                for (int i = searchSpaceLength - 1; i >= 0; i--)
                {
                    char c = Unsafe.Add(ref searchSpace, i);
                    if (TNegator.NegateIfNeeded(valuesSpan.Contains(c)))
                    {
                        return i;
                    }
                }

                return -1;
            }

            if (typeof(TNegator) == typeof(SpanHelpers.DontNegate<char>)
                ? IndexOfAnyAsciiSearcher.TryLastIndexOfAny(ref searchSpace, searchSpaceLength, valuesSpan, out int index)
                : IndexOfAnyAsciiSearcher.TryLastIndexOfAnyExcept(ref searchSpace, searchSpaceLength, valuesSpan, out index))
            {
                return index;
            }

            return ProbabilisticLastIndexOfAny<TNegator>(ref searchSpace, searchSpaceLength, ref values, valuesLength);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ProbabilisticIndexOfAny<TNegator>(ref char searchSpace, int searchSpaceLength, ref char values, int valuesLength)
            where TNegator : struct, SpanHelpers.INegator<char>
        {
            var valuesSpan = new ReadOnlySpan<char>(ref values, valuesLength);

            var map = new ProbabilisticMap(valuesSpan);
            ref uint charMap = ref Unsafe.As<ProbabilisticMap, uint>(ref map);

            return typeof(TNegator) == typeof(SpanHelpers.DontNegate<char>)
                ? IndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(ref charMap, ref searchSpace, searchSpaceLength, valuesSpan)
                : IndexOfAny<IndexOfAnyAsciiSearcher.Negate>(ref charMap, ref searchSpace, searchSpaceLength, valuesSpan);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static int ProbabilisticLastIndexOfAny<TNegator>(ref char searchSpace, int searchSpaceLength, ref char values, int valuesLength)
            where TNegator : struct, SpanHelpers.INegator<char>
        {
            var valuesSpan = new ReadOnlySpan<char>(ref values, valuesLength);

            var map = new ProbabilisticMap(valuesSpan);
            ref uint charMap = ref Unsafe.As<ProbabilisticMap, uint>(ref map);

            return typeof(TNegator) == typeof(SpanHelpers.DontNegate<char>)
                ? LastIndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(ref charMap, ref searchSpace, searchSpaceLength, valuesSpan)
                : LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate>(ref charMap, ref searchSpace, searchSpaceLength, valuesSpan);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int IndexOfAny<TNegator>(ref uint charMap, ref char searchSpace, int searchSpaceLength, ReadOnlySpan<char> values)
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            ref char searchSpaceEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength);
            ref char cur = ref searchSpace;

            if (IsVectorizationSupported && typeof(TNegator) == typeof(IndexOfAnyAsciiSearcher.DontNegate) && searchSpaceLength >= 2 * Vector128<short>.Count)
            {
                Vector128<byte> charMap0 = Vector128.LoadUnsafe(ref Unsafe.As<uint, byte>(ref charMap));
                Vector128<byte> charMap1 = Vector128.LoadUnsafe(ref Unsafe.As<uint, byte>(ref charMap), (nuint)Vector128<byte>.Count);

                ref char oneVectorAwayFromEnd = ref Unsafe.Subtract(ref searchSpaceEnd, 2 * Vector128<short>.Count);

                do
                {
                    Vector128<byte> result = ContainsMask16Chars(charMap0, charMap1, ref cur);

                    if (result != Vector128<byte>.Zero)
                    {
                        uint mask = result.ExtractMostSignificantBits();
                        do
                        {
                            ref char candidatePos = ref Unsafe.Add(ref cur, BitOperations.TrailingZeroCount(mask));

                            if (Contains(values, candidatePos))
                            {
                                return (int)((nuint)Unsafe.ByteOffset(ref searchSpace, ref candidatePos) / sizeof(char));
                            }

                            mask = BitOperations.ResetLowestSetBit(mask);
                        }
                        while (mask != 0);
                    }

                    cur = ref Unsafe.Add(ref cur, Vector128<short>.Count);
                }
                while (!Unsafe.IsAddressGreaterThan(ref cur, ref oneVectorAwayFromEnd));
            }

            while (!Unsafe.AreSame(ref cur, ref searchSpaceEnd))
            {
                int ch = cur;
                if (TNegator.NegateIfNeeded(Contains(ref charMap, values, ch)))
                {
                    return (int)(Unsafe.ByteOffset(ref searchSpace, ref cur) / sizeof(char));
                }

                cur = ref Unsafe.Add(ref cur, 1);
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static int LastIndexOfAny<TNegator>(ref uint charMap, ref char searchSpace, int searchSpaceLength, ReadOnlySpan<char> values)
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            for (int i = searchSpaceLength - 1; i >= 0; i--)
            {
                int ch = Unsafe.Add(ref searchSpace, i);
                if (TNegator.NegateIfNeeded(Contains(ref charMap, values, ch)))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
