// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace System
{
    /// <summary>
    /// Stores information used to accelerate IndexOfAny computations for ASCII values.
    /// </summary>
    public readonly struct IndexOfAnyAsciiValues
    {
        private readonly IndexOfAnyAsciiSearcher _searcher;

        internal IndexOfAnyAsciiValues(ReadOnlySpan<char> asciiValues) =>
            _searcher = new IndexOfAnyAsciiSearcher(asciiValues);

        internal readonly IndexOfAnyAsciiSearcher Searcher =>
            _searcher ?? ThrowForUninitializedValues();

        private static IndexOfAnyAsciiSearcher ThrowForUninitializedValues() =>
            throw new InvalidOperationException("");
    }

    internal sealed class IndexOfAnyAsciiSearcher
    {
        private static bool IsVectorizationSupported => Ssse3.IsSupported || AdvSimd.Arm64.IsSupported;

        private readonly Vector128<byte> _bitmap;
        private readonly bool _needleContainsZero;
        private readonly BitVector256 _lookup;

        public IndexOfAnyAsciiSearcher(ReadOnlySpan<char> asciiValues)
        {
            // Not using TryComputeBitmap here as we also want to setup the BitVector256.
            unsafe
            {
                Vector128<byte> bitmap = default;
                byte* bitmapPtr = (byte*)&bitmap;

                foreach (char c in asciiValues)
                {
                    if (c > 127)
                    {
                        throw new ArgumentOutOfRangeException(nameof(asciiValues), "Only characters in the [0, 127] range are supported.");
                    }

                    _lookup.Set(c);

                    int highNibble = c >> 4;
                    int lowNibble = c & 0xF;

                    bitmapPtr[(uint)lowNibble] |= (byte)(1 << highNibble);
                }

                _bitmap = bitmap;
            }

            _needleContainsZero = _lookup.Contains(0);
        }

        private bool Contains(char c) => c < 128 && _lookup.Contains(c);

        private bool Contains(byte b) => _lookup.Contains(b);

        public int IndexOfAny(ReadOnlySpan<char> searchSpace) =>
            IndexOfAny<DontNegate>(searchSpace);

        public int IndexOfAnyExcept(ReadOnlySpan<char> searchSpace) =>
            IndexOfAny<Negate>(searchSpace);

        public int LastIndexOfAny(ReadOnlySpan<char> searchSpace) =>
            LastIndexOfAny<DontNegate>(searchSpace);

        public int LastIndexOfAnyExcept(ReadOnlySpan<char> searchSpace) =>
            LastIndexOfAny<Negate>(searchSpace);

        public int IndexOfAny(ReadOnlySpan<byte> searchSpace) =>
            IndexOfAny<DontNegate>(searchSpace);

        public int IndexOfAnyExcept(ReadOnlySpan<byte> searchSpace) =>
            IndexOfAny<Negate>(searchSpace);

        public int LastIndexOfAny(ReadOnlySpan<byte> searchSpace) =>
            LastIndexOfAny<DontNegate>(searchSpace);

        public int LastIndexOfAnyExcept(ReadOnlySpan<byte> searchSpace) =>
            LastIndexOfAny<Negate>(searchSpace);

        private int IndexOfAny<TNegator>(ReadOnlySpan<char> searchSpace)
            where TNegator : struct, INegator
        {
            if (IsVectorizationSupported && searchSpace.Length >= Vector128<short>.Count)
            {
                ref short shortRef = ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(searchSpace));

                return Ssse3.IsSupported && _needleContainsZero
                    ? IndexOfAnyVectorized<TNegator, Ssse3HandleZeroInNeedle>(ref shortRef, searchSpace.Length, _bitmap)
                    : IndexOfAnyVectorized<TNegator, Default>(ref shortRef, searchSpace.Length, _bitmap);
            };

            for (int i = 0; i < searchSpace.Length; i++)
            {
                if (TNegator.NegateIfNeeded(Contains(searchSpace[i])))
                {
                    return i;
                }
            }

            return -1;
        }

        private int LastIndexOfAny<TNegator>(ReadOnlySpan<char> searchSpace)
            where TNegator : struct, INegator
        {
            if (IsVectorizationSupported && searchSpace.Length >= Vector128<short>.Count)
            {
                ref short shortRef = ref Unsafe.As<char, short>(ref MemoryMarshal.GetReference(searchSpace));

                return Ssse3.IsSupported && _needleContainsZero
                    ? LastIndexOfAnyVectorized<TNegator, Ssse3HandleZeroInNeedle>(ref shortRef, searchSpace.Length, _bitmap)
                    : LastIndexOfAnyVectorized<TNegator, Default>(ref shortRef, searchSpace.Length, _bitmap);
            };

            ref char searchSpaceRef = ref MemoryMarshal.GetReference(searchSpace);

            for (int i = searchSpace.Length - 1; i >= 0; i--)
            {
                if (TNegator.NegateIfNeeded(Contains(Unsafe.Add(ref searchSpaceRef, i))))
                {
                    return i;
                }
            }

            return -1;
        }

        private int IndexOfAny<TNegator>(ReadOnlySpan<byte> searchSpace)
            where TNegator : struct, INegator
        {
            if (IsVectorizationSupported && searchSpace.Length >= Vector128<short>.Count)
            {
                ref byte searchSpaceRef = ref MemoryMarshal.GetReference(searchSpace);

                return Ssse3.IsSupported && _needleContainsZero
                    ? IndexOfAnyVectorized<TNegator, Ssse3HandleZeroInNeedle>(ref searchSpaceRef, searchSpace.Length, _bitmap)
                    : IndexOfAnyVectorized<TNegator, Default>(ref searchSpaceRef, searchSpace.Length, _bitmap);
            };

            for (int i = 0; i < searchSpace.Length; i++)
            {
                if (TNegator.NegateIfNeeded(Contains(searchSpace[i])))
                {
                    return i;
                }
            }

            return -1;
        }

        private int LastIndexOfAny<TNegator>(ReadOnlySpan<byte> searchSpace)
            where TNegator : struct, INegator
        {
            ref byte searchSpaceRef = ref MemoryMarshal.GetReference(searchSpace);

            if (IsVectorizationSupported && searchSpace.Length >= Vector128<short>.Count)
            {
                return Ssse3.IsSupported && _needleContainsZero
                    ? LastIndexOfAnyVectorized<TNegator, Ssse3HandleZeroInNeedle>(ref searchSpaceRef, searchSpace.Length, _bitmap)
                    : LastIndexOfAnyVectorized<TNegator, Default>(ref searchSpaceRef, searchSpace.Length, _bitmap);
            };

            for (int i = searchSpace.Length - 1; i >= 0; i--)
            {
                if (TNegator.NegateIfNeeded(Contains(Unsafe.Add(ref searchSpaceRef, i))))
                {
                    return i;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool TryComputeBitmap(ReadOnlySpan<char> values, byte* bitmap, out bool needleContainsZero)
        {
            byte* bitmapLocal = bitmap;

            foreach (char c in values)
            {
                if (c > 127)
                {
                    needleContainsZero = false;
                    return false;
                }

                int highNibble = c >> 4;
                int lowNibble = c & 0xF;

                bitmapLocal[(uint)lowNibble] |= (byte)(1 << highNibble);
            }

            needleContainsZero = (bitmap[0] & 1) != 0;
            return true;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryIndexOfAny(ref char searchSpace, int searchSpaceLength, ReadOnlySpan<char> asciiValues, out int index) =>
            TryIndexOfAny<DontNegate>(ref Unsafe.As<char, short>(ref searchSpace), searchSpaceLength, asciiValues, out index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryIndexOfAnyExcept(ref char searchSpace, int searchSpaceLength, ReadOnlySpan<char> asciiValues, out int index) =>
            TryIndexOfAny<Negate>(ref Unsafe.As<char, short>(ref searchSpace), searchSpaceLength, asciiValues, out index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryLastIndexOfAny(ref char searchSpace, int searchSpaceLength, ReadOnlySpan<char> asciiValues, out int index) =>
            TryLastIndexOfAny<DontNegate>(ref Unsafe.As<char, short>(ref searchSpace), searchSpaceLength, asciiValues, out index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryLastIndexOfAnyExcept(ref char searchSpace, int searchSpaceLength, ReadOnlySpan<char> asciiValues, out int index) =>
            TryLastIndexOfAny<Negate>(ref Unsafe.As<char, short>(ref searchSpace), searchSpaceLength, asciiValues, out index);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool TryIndexOfAny<TNegator>(ref short searchSpace, int searchSpaceLength, ReadOnlySpan<char> asciiValues, out int index)
            where TNegator : struct, INegator
        {
            Debug.Assert(searchSpaceLength >= Vector128<short>.Count);

            if (IsVectorizationSupported)
            {
                Vector128<byte> bitmap = default;
                if (TryComputeBitmap(asciiValues, (byte*)&bitmap, out bool needleContainsZero))
                {
                    index = Ssse3.IsSupported && needleContainsZero
                        ? IndexOfAnyVectorized<TNegator, Ssse3HandleZeroInNeedle>(ref searchSpace, searchSpaceLength, bitmap)
                        : IndexOfAnyVectorized<TNegator, Default>(ref searchSpace, searchSpaceLength, bitmap);
                    return true;
                }
            }

            index = default;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static unsafe bool TryLastIndexOfAny<TNegator>(ref short searchSpace, int searchSpaceLength, ReadOnlySpan<char> asciiValues, out int index)
            where TNegator : struct, INegator
        {
            Debug.Assert(searchSpaceLength >= Vector128<short>.Count);

            if (IsVectorizationSupported)
            {
                Vector128<byte> bitmap = default;
                if (TryComputeBitmap(asciiValues, (byte*)&bitmap, out bool needleContainsZero))
                {
                    index = Ssse3.IsSupported && needleContainsZero
                        ? LastIndexOfAnyVectorized<TNegator, Ssse3HandleZeroInNeedle>(ref searchSpace, searchSpaceLength, bitmap)
                        : LastIndexOfAnyVectorized<TNegator, Default>(ref searchSpace, searchSpaceLength, bitmap);
                    return true;
                }
            }

            index = default;
            return false;
        }

        private static int IndexOfAnyVectorized<TNegator, TOptimizations>(ref short searchSpace, int searchSpaceLength, Vector128<byte> bitmap)
            where TNegator : struct, INegator
            where TOptimizations : struct, IOptimizations
        {
            ref short currentSearchSpace = ref searchSpace;

            if (searchSpaceLength > 2 * Vector128<short>.Count)
            {
                // Process the input in chunks of 16 characters (2 * Vector128<short>).
                // We're mainly interested in a single byte of each character, and the core lookup operates on a Vector128<byte>.
                // As packing two Vector128<short>s into a Vector128<byte> is cheap compared to the lookup, we can effectively double the throughput.
                // If the input length is a multiple of 16, don't consume the last 16 characters in this loop.
                // Let the fallback below handle it instead. This is why the condition is
                // ">" instead of ">=" above, and why "IsAddressLessThan" is used instead of "!IsAddressGreaterThan".
                ref short twoVectorsAwayFromEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength - (2 * Vector128<short>.Count));

                do
                {
                    Vector128<short> source0 = Vector128.LoadUnsafe(ref currentSearchSpace);
                    Vector128<short> source1 = Vector128.LoadUnsafe(ref currentSearchSpace, (nuint)Vector128<short>.Count);

                    Vector128<byte> result = IndexOfAnyLookup<TNegator, TOptimizations>(source0, source1, bitmap);
                    if (result != Vector128<byte>.Zero)
                    {
                        return ComputeFirstIndex<short, TNegator>(ref searchSpace, ref currentSearchSpace, result);
                    }

                    currentSearchSpace = ref Unsafe.Add(ref currentSearchSpace, 2 * Vector128<short>.Count);
                }
                while (Unsafe.IsAddressLessThan(ref currentSearchSpace, ref twoVectorsAwayFromEnd));
            }

            // We have 1-16 characters remaining. Process the first and last vector in the search space.
            // They may overlap, but we'll handle that in the index calculation if we do get a match.
            Debug.Assert(searchSpaceLength >= Vector128<short>.Count, "We expect that the input is long enough for us to load a whole vector.");
            {
                ref short oneVectorAwayFromEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength - Vector128<short>.Count);

                ref short firstVector = ref Unsafe.IsAddressGreaterThan(ref currentSearchSpace, ref oneVectorAwayFromEnd)
                    ? ref oneVectorAwayFromEnd
                    : ref currentSearchSpace;

                Vector128<short> source0 = Vector128.LoadUnsafe(ref firstVector);
                Vector128<short> source1 = Vector128.LoadUnsafe(ref oneVectorAwayFromEnd);

                Vector128<byte> result = IndexOfAnyLookup<TNegator, TOptimizations>(source0, source1, bitmap);
                if (result != Vector128<byte>.Zero)
                {
                    return ComputeFirstIndexOverlapped<short, TNegator>(ref searchSpace, ref firstVector, ref oneVectorAwayFromEnd, result);
                }
            }

            return -1;
        }

        private static int LastIndexOfAnyVectorized<TNegator, TOptimizations>(ref short searchSpace, int searchSpaceLength, Vector128<byte> bitmap)
            where TNegator : struct, INegator
            where TOptimizations : struct, IOptimizations
        {
            ref short currentSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceLength);

            if (searchSpaceLength > 2 * Vector128<short>.Count)
            {
                // Process the input in chunks of 16 characters (2 * Vector128<short>).
                // We're mainly interested in a single byte of each character, and the core lookup operates on a Vector128<byte>.
                // As packing two Vector128<short>s into a Vector128<byte> is cheap compared to the lookup, we can effectively double the throughput.
                // If the input length is a multiple of 16, don't consume the last 16 characters in this loop.
                // Let the fallback below handle it instead. This is why the condition is
                // ">" instead of ">=" above, and why "IsAddressGreaterThan" is used instead of "!IsAddressLessThan".
                ref short twoVectorsAfterStart = ref Unsafe.Add(ref searchSpace, 2 * Vector128<short>.Count);

                do
                {
                    currentSearchSpace = ref Unsafe.Subtract(ref currentSearchSpace, 2 * Vector128<short>.Count);

                    Vector128<short> source0 = Vector128.LoadUnsafe(ref currentSearchSpace);
                    Vector128<short> source1 = Vector128.LoadUnsafe(ref currentSearchSpace, (nuint)Vector128<short>.Count);

                    Vector128<byte> result = IndexOfAnyLookup<TNegator, TOptimizations>(source0, source1, bitmap);
                    if (result != Vector128<byte>.Zero)
                    {
                        return ComputeLastIndex<short, TNegator>(ref searchSpace, ref currentSearchSpace, result);
                    }
                }
                while (Unsafe.IsAddressGreaterThan(ref currentSearchSpace, ref twoVectorsAfterStart));
            }

            // We have 1-16 characters remaining. Process the first and last vector in the search space.
            // They may overlap, but we'll handle that in the index calculation if we do get a match.
            Debug.Assert(searchSpaceLength >= Vector128<short>.Count, "We expect that the input is long enough for us to load a whole vector.");
            {
                ref short oneVectorAfterStart = ref Unsafe.Add(ref searchSpace, Vector128<short>.Count);

                ref short secondVector = ref Unsafe.IsAddressGreaterThan(ref currentSearchSpace, ref oneVectorAfterStart)
                    ? ref Unsafe.Subtract(ref currentSearchSpace, Vector128<short>.Count)
                    : ref searchSpace;

                Vector128<short> source0 = Vector128.LoadUnsafe(ref searchSpace);
                Vector128<short> source1 = Vector128.LoadUnsafe(ref secondVector);

                Vector128<byte> result = IndexOfAnyLookup<TNegator, TOptimizations>(source0, source1, bitmap);
                if (result != Vector128<byte>.Zero)
                {
                    return ComputeLastIndexOverlapped<short, TNegator>(ref searchSpace, ref secondVector, result);
                }
            }

            return -1;
        }

        private static int IndexOfAnyVectorized<TNegator, TOptimizations>(ref byte searchSpace, int searchSpaceLength, Vector128<byte> bitmap)
            where TNegator : struct, INegator
            where TOptimizations : struct, IOptimizations
        {
            ref byte currentSearchSpace = ref searchSpace;

            if (searchSpaceLength > Vector128<byte>.Count)
            {
                // Process the input in chunks of 16 bytes.
                // If the input length is a multiple of 16, don't consume the last 16 characters in this loop.
                // Let the fallback below handle it instead. This is why the condition is
                // ">" instead of ">=" above, and why "IsAddressLessThan" is used instead of "!IsAddressGreaterThan".
                ref byte vectorAwayFromEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength - Vector128<byte>.Count);

                do
                {
                    Vector128<byte> source = Vector128.LoadUnsafe(ref currentSearchSpace);

                    Vector128<byte> result = IndexOfAnyLookup<TNegator, TOptimizations>(source, bitmap);
                    if (result != Vector128<byte>.Zero)
                    {
                        return ComputeFirstIndex<byte, TNegator>(ref searchSpace, ref currentSearchSpace, result);
                    }

                    currentSearchSpace = ref Unsafe.Add(ref currentSearchSpace, Vector128<byte>.Count);
                }
                while (Unsafe.IsAddressLessThan(ref currentSearchSpace, ref vectorAwayFromEnd));
            }

            // We have 1-16 bytes remaining. Process the first and last half vectors in the search space.
            // They may overlap, but we'll handle that in the index calculation if we do get a match.
            Debug.Assert(searchSpaceLength >= sizeof(ulong), "We expect that the input is long enough for us to load a ulong.");
            {
                ref byte halfVectorAwayFromEnd = ref Unsafe.Add(ref searchSpace, searchSpaceLength - Vector128<byte>.Count / 2);

                ref byte firstVector = ref Unsafe.IsAddressGreaterThan(ref currentSearchSpace, ref halfVectorAwayFromEnd)
                    ? ref halfVectorAwayFromEnd
                    : ref currentSearchSpace;

                ulong source0 = Unsafe.ReadUnaligned<ulong>(ref firstVector);
                ulong source1 = Unsafe.ReadUnaligned<ulong>(ref halfVectorAwayFromEnd);
                Vector128<byte> source = Vector128.Create(source0, source1).AsByte();

                Vector128<byte> result = IndexOfAnyLookup<TNegator, TOptimizations>(source, bitmap);
                if (result != Vector128<byte>.Zero)
                {
                    return ComputeFirstIndexOverlapped<byte, TNegator>(ref searchSpace, ref firstVector, ref halfVectorAwayFromEnd, result);
                }
            }

            return -1;
        }

        private static int LastIndexOfAnyVectorized<TNegator, TOptimizations>(ref byte searchSpace, int searchSpaceLength, Vector128<byte> bitmap)
            where TNegator : struct, INegator
            where TOptimizations : struct, IOptimizations
        {
            ref byte currentSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceLength);

            if (searchSpaceLength > Vector128<byte>.Count)
            {
                // Process the input in chunks of 16 bytes.
                // If the input length is a multiple of 16, don't consume the last 16 characters in this loop.
                // Let the fallback below handle it instead. This is why the condition is
                // ">" instead of ">=" above, and why "IsAddressGreaterThan" is used instead of "!IsAddressLessThan".
                ref byte vectorAfterStart = ref Unsafe.Add(ref searchSpace, Vector128<byte>.Count);

                do
                {
                    currentSearchSpace = ref Unsafe.Subtract(ref currentSearchSpace, Vector128<byte>.Count);

                    Vector128<byte> source = Vector128.LoadUnsafe(ref currentSearchSpace);

                    Vector128<byte> result = IndexOfAnyLookup<TNegator, TOptimizations>(source, bitmap);
                    if (result != Vector128<byte>.Zero)
                    {
                        return ComputeLastIndex<byte, TNegator>(ref searchSpace, ref currentSearchSpace, result);
                    }
                }
                while (Unsafe.IsAddressGreaterThan(ref currentSearchSpace, ref vectorAfterStart));
            }

            // We have 1-16 bytes remaining. Process the first and last half vectors in the search space.
            // They may overlap, but we'll handle that in the index calculation if we do get a match.
            Debug.Assert(searchSpaceLength >= sizeof(ulong), "We expect that the input is long enough for us to load a ulong.");
            {
                ref byte halfVectorAfterStart = ref Unsafe.Add(ref searchSpace, Vector128<byte>.Count / 2);

                ref byte secondVector = ref Unsafe.IsAddressGreaterThan(ref currentSearchSpace, ref halfVectorAfterStart)
                    ? ref Unsafe.Subtract(ref currentSearchSpace, Vector128<short>.Count)
                    : ref searchSpace;

                ulong source0 = Unsafe.ReadUnaligned<ulong>(ref searchSpace);
                ulong source1 = Unsafe.ReadUnaligned<ulong>(ref secondVector);
                Vector128<byte> source = Vector128.Create(source0, source1).AsByte();

                Vector128<byte> result = IndexOfAnyLookup<TNegator, TOptimizations>(source, bitmap);
                if (result != Vector128<byte>.Zero)
                {
                    return ComputeLastIndexOverlapped<byte, TNegator>(ref searchSpace, ref secondVector, result);
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> IndexOfAnyLookup<TNegator, TOptimizations>(Vector128<short> source0, Vector128<short> source1, Vector128<byte> bitmapLookup)
            where TNegator : struct, INegator
            where TOptimizations : struct, IOptimizations
        {
            // Pack two vectors of characters into bytes. While the type is Vector128<short>, these are really UInt16 characters.
            // X86: Downcast every character using saturation.
            // - Values <= 32767 result in min(value, 255).
            // - Values  > 32767 result in 0. Because of this we must do more work to handle needles that contain 0.
            // ARM64: Take the low byte of each character.
            // - All values result in (value & 0xFF).
            Vector128<byte> source = Sse2.IsSupported
                ? Sse2.PackUnsignedSaturate(source0, source1)
                : AdvSimd.Arm64.UnzipEven(source0.AsByte(), source1.AsByte());

            Vector128<byte> result = IndexOfAnyLookupCore(source, bitmapLookup);

            // On ARM64, we ignored the high byte of every character when packing (see above).
            // The 'result' can therefore contain false positives - e.g. 0x141 would match 0x41 ('A').
            // On X86, PackUnsignedSaturate resulted in values becoming 0 for inputs above 32767.
            // Any value above 32767 would therefore match against 0. If 0 is present in the needle, we must clear the false positives.
            // In both cases, we can correct the result by clearing any bits that matched with a non-ascii source character.
            if (AdvSimd.Arm64.IsSupported || TOptimizations.NeedleContainsZero)
            {
                Vector128<short> ascii0 = Vector128.LessThan(source0.AsUInt16(), Vector128.Create((ushort)128)).AsInt16();
                Vector128<short> ascii1 = Vector128.LessThan(source1.AsUInt16(), Vector128.Create((ushort)128)).AsInt16();
                Vector128<byte> ascii = Sse2.IsSupported
                    ? Sse2.PackSignedSaturate(ascii0, ascii1).AsByte()
                    : AdvSimd.Arm64.UnzipEven(ascii0.AsByte(), ascii1.AsByte());
                result &= ascii;
            }

            return TNegator.NegateIfNeeded(result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> IndexOfAnyLookup<TNegator, TOptimizations>(Vector128<byte> source, Vector128<byte> bitmapLookup)
            where TNegator : struct, INegator
            where TOptimizations : struct, IOptimizations
        {
            Vector128<byte> result = IndexOfAnyLookupCore(source, bitmapLookup);

            if (AdvSimd.Arm64.IsSupported || TOptimizations.NeedleContainsZero)
            {
                Vector128<byte> ascii = Vector128.LessThan(source, Vector128.Create((byte)128));
                result &= ascii;
            }

            return TNegator.NegateIfNeeded(result);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> IndexOfAnyLookupCore(Vector128<byte> source, Vector128<byte> bitmapLookup)
        {
            // On X86, the Ssse3.Shuffle instruction will already perform an implicit 'AND 0xF' on the indices, so we can skip it.
            // For values above 127, Ssse3.Shuffle will also set the result to 0. This saves us from explicitly checking whether the input was ascii below.
            Vector128<byte> lowerNibbles = Ssse3.IsSupported
                ? source
                : source & Vector128.Create((byte)0xF);

            Vector128<byte> highNibbles = Vector128.ShiftRightLogical(source.AsInt32(), 4).AsByte() & Vector128.Create((byte)0xF);

            // The bitmapLookup represents a 8x16 table of bits, indicating whether a character is present in the needle.
            // Lookup the rows via the lower nibble and the column via the higher nibble.
            Vector128<byte> bitMask = Shuffle(bitmapLookup, lowerNibbles);
            Vector128<byte> bitPositions = Shuffle(Vector128.Create(0x8040201008040201).AsByte(), highNibbles);

            Vector128<byte> result = bitMask & bitPositions;

            return result;
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
        private static int ComputeFirstIndex<T, TNegator>(ref T searchSpace, ref T current, Vector128<byte> result)
            where TNegator : struct, INegator
        {
            uint mask = TNegator.ExtractMask(result);
            int offsetInVector = BitOperations.TrailingZeroCount(mask);
            return offsetInVector + (int)(Unsafe.ByteOffset(ref searchSpace, ref current) / Unsafe.SizeOf<T>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeFirstIndexOverlapped<T, TNegator>(ref T searchSpace, ref T current0, ref T current1, Vector128<byte> result)
            where TNegator : struct, INegator
        {
            uint mask = TNegator.ExtractMask(result);
            int offsetInVector = BitOperations.TrailingZeroCount(mask);
            if (offsetInVector >= Vector128<short>.Count)
            {
                // We matched within the second vector
                current0 = ref current1;
                offsetInVector -= Vector128<short>.Count;
            }
            return offsetInVector + (int)(Unsafe.ByteOffset(ref searchSpace, ref current0) / Unsafe.SizeOf<T>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeLastIndex<T, TNegator>(ref T searchSpace, ref T current, Vector128<byte> result)
            where TNegator : struct, INegator
        {
            uint mask = TNegator.ExtractMask(result) & 0xFFFF;
            int offsetInVector = 31 - BitOperations.LeadingZeroCount(mask);
            return offsetInVector + (int)(Unsafe.ByteOffset(ref searchSpace, ref current) / Unsafe.SizeOf<T>());
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static int ComputeLastIndexOverlapped<T, TNegator>(ref T searchSpace, ref T secondVector, Vector128<byte> result)
            where TNegator : struct, INegator
        {
            uint mask = TNegator.ExtractMask(result) & 0xFFFF;
            int offsetInVector = 31 - BitOperations.LeadingZeroCount(mask);
            if (offsetInVector < Vector128<short>.Count)
            {
                return offsetInVector;
            }

            // We matched within the second vector
            return offsetInVector - Vector128<short>.Count + (int)(Unsafe.ByteOffset(ref searchSpace, ref secondVector) / Unsafe.SizeOf<T>());
        }

        private interface INegator
        {
            static abstract bool NegateIfNeeded(bool result);
            static abstract Vector128<byte> NegateIfNeeded(Vector128<byte> result);
            static abstract uint ExtractMask(Vector128<byte> result);
        }

        private struct DontNegate : INegator
        {
            public static bool NegateIfNeeded(bool result) => result;
            public static Vector128<byte> NegateIfNeeded(Vector128<byte> result) => result;
            public static uint ExtractMask(Vector128<byte> result) => ~Vector128.Equals(result, Vector128<byte>.Zero).ExtractMostSignificantBits();
        }

        private struct Negate : INegator
        {
            public static bool NegateIfNeeded(bool result) => !result;
            // This is intentionally testing for equality with 0 instead of "~result".
            // We want to know if any character didn't match, as that means it should be treated as a match for the -Except method.
            public static Vector128<byte> NegateIfNeeded(Vector128<byte> result) => Vector128.Equals(result, Vector128<byte>.Zero);
            public static uint ExtractMask(Vector128<byte> result) => result.ExtractMostSignificantBits();
        }

        private interface IOptimizations
        {
            static abstract bool NeedleContainsZero { get; }
        }

        private struct Ssse3HandleZeroInNeedle : IOptimizations
        {
            public static bool NeedleContainsZero => true;
        }

        private struct Default : IOptimizations
        {
            public static bool NeedleContainsZero => false;
        }

        private unsafe struct BitVector256
        {
            private fixed uint _values[8];

            public void Set(char c)
            {
                Debug.Assert(c < 128);
                uint offset = (uint)(c >> 5);
                uint significantBit = 1u << (c & 31);
                _values[offset] |= significantBit;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly bool Contains(char c)
            {
                Debug.Assert(c < 128);
                uint offset = (uint)(c >> 5);
                uint significantBit = 1u << (c & 31);
                return (_values[offset] & significantBit) != 0;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public readonly bool Contains(byte b)
            {
                uint offset = (uint)(b >> 5);
                uint significantBit = 1u << (b & 31);
                return (_values[offset] & significantBit) != 0;
            }
        }
    }
}
