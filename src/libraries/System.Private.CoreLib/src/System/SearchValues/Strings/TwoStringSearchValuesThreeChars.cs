// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static System.Buffers.StringSearchValuesHelper;

namespace System.Buffers
{
    // Based on SpanHelpers.IndexOf(ref char, int, ref char, int)
    // This implementation uses 3 precomputed anchor points when searching.
    // This implementation may also be used for length=2 values, in which case two anchors point at the same position.
    // Has an O(i * m) worst-case, with the expected time closer to O(n) for most inputs.
    internal sealed class TwoStringSearchValuesThreeChars : StringSearchValuesBase
    {
        private readonly string _value0;
        private readonly string _value1;
        private readonly nint _minusValueTailLength;
        private readonly nint _minusValue0TailLength;
        private readonly nuint _ch2ByteOffset;
        private readonly nuint _ch3ByteOffset;
        private readonly ushort _ch0_1;
        private readonly ushort _ch0_2;
        private readonly ushort _ch0_3;
        private readonly ushort _ch1_1;
        private readonly ushort _ch1_2;
        private readonly ushort _ch1_3;

        public TwoStringSearchValuesThreeChars(HashSet<string> uniqueValues, string value0, string value1) : base(uniqueValues)
        {
            Debug.Assert(value0.Length > 1);
            Debug.Assert(value1.Length > 1);
            Debug.Assert(value0.Length >= value1.Length);

            CharacterFrequencyHelper.GetMultiStringMultiCharacterOffsets([value0, value1], ignoreCase: false, out int ch2Offset, out int ch3Offset);

            Debug.Assert(ch3Offset == 0 || ch3Offset > ch2Offset);

            _value0 = value0;
            _value1 = value1;

            int minLength = value1.Length;

            _minusValueTailLength = -(minLength - 1);
            _minusValue0TailLength = -(value0.Length - 1);

            _ch0_1 = value0[0];
            _ch0_2 = value0[ch2Offset];
            _ch0_3 = value0[ch3Offset];

            _ch1_1 = value1[0];
            _ch1_2 = value1[ch2Offset];
            _ch1_3 = value1[ch3Offset];

            _ch2ByteOffset = (nuint)ch2Offset * 2;
            _ch3ByteOffset = (nuint)ch3Offset * 2;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) =>
            IndexOf(ref MemoryMarshal.GetReference(span), span.Length);

        private int IndexOf(ref char searchSpace, int searchSpaceLength)
        {
            ref char searchSpaceStart = ref searchSpace;

            nint searchSpaceMinusValueTailLength = searchSpaceLength + _minusValueTailLength;

            if (!Vector128.IsHardwareAccelerated || searchSpaceMinusValueTailLength < Vector128<ushort>.Count)
            {
                goto ShortInput;
            }

            nuint ch2ByteOffset = _ch2ByteOffset;
            nuint ch3ByteOffset = _ch3ByteOffset;

            if (Vector512.IsHardwareAccelerated && searchSpaceMinusValueTailLength - Vector512<ushort>.Count >= 0)
            {
                Vector512<ushort> ch0_1 = Vector512.Create(_ch0_1);
                Vector512<ushort> ch0_2 = Vector512.Create(_ch0_2);
                Vector512<ushort> ch0_3 = Vector512.Create(_ch0_3);
                Vector512<ushort> ch1_1 = Vector512.Create(_ch1_1);
                Vector512<ushort> ch1_2 = Vector512.Create(_ch1_2);
                Vector512<ushort> ch1_3 = Vector512.Create(_ch1_3);

                ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - Vector512<ushort>.Count);

                while (true)
                {
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector512<ushort>.Count);
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector512<ushort>.Count + (int)(_ch2ByteOffset / 2));
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector512<ushort>.Count + (int)(_ch3ByteOffset / 2));

                    // Find which starting positions likely contain a match (likely match all 3 anchor characters).
                    Vector512<byte> result = GetComparisonResult(ref searchSpace, ch2ByteOffset, ch3ByteOffset, ch0_1, ch0_2, ch0_3, ch1_1, ch1_2, ch1_3);

                    if (result != Vector512<byte>.Zero)
                    {
                        goto CandidateFound;
                    }

                LoopFooter:
                    // We haven't found a match. Update the input position and check if we've reached the end.
                    searchSpace = ref Unsafe.Add(ref searchSpace, Vector512<ushort>.Count);

                    if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpace))
                    {
                        if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpace, Vector512<ushort>.Count)))
                        {
                            return -1;
                        }

                        // We have fewer than 32 characters remaining. Adjust the input position such that we will do one last loop iteration.
                        searchSpace = ref lastSearchSpace;
                    }

                    continue;

                CandidateFound:
                    // We found potential matches, but they may be false-positives, so we must verify each one.
                    if (TryMatch(ref searchSpaceStart, searchSpaceLength, ref searchSpace, result.ExtractMostSignificantBits(), out int offset))
                    {
                        return offset;
                    }
                    goto LoopFooter;
                }
            }
            else if (Vector256.IsHardwareAccelerated && searchSpaceMinusValueTailLength - Vector256<ushort>.Count >= 0)
            {
                Vector256<ushort> ch0_1 = Vector256.Create(_ch0_1);
                Vector256<ushort> ch0_2 = Vector256.Create(_ch0_2);
                Vector256<ushort> ch0_3 = Vector256.Create(_ch0_3);
                Vector256<ushort> ch1_1 = Vector256.Create(_ch1_1);
                Vector256<ushort> ch1_2 = Vector256.Create(_ch1_2);
                Vector256<ushort> ch1_3 = Vector256.Create(_ch1_3);

                ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - Vector256<ushort>.Count);

                while (true)
                {
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector256<ushort>.Count);
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector256<ushort>.Count + (int)(_ch2ByteOffset / 2));
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector256<ushort>.Count + (int)(_ch3ByteOffset / 2));

                    // Find which starting positions likely contain a match (likely match all 3 anchor characters).
                    Vector256<byte> result = GetComparisonResult(ref searchSpace, ch2ByteOffset, ch3ByteOffset, ch0_1, ch0_2, ch0_3, ch1_1, ch1_2, ch1_3);

                    if (result != Vector256<byte>.Zero)
                    {
                        goto CandidateFound;
                    }

                LoopFooter:
                    searchSpace = ref Unsafe.Add(ref searchSpace, Vector256<ushort>.Count);

                    if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpace))
                    {
                        if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpace, Vector256<ushort>.Count)))
                        {
                            return -1;
                        }

                        // We have fewer than 16 characters remaining. Adjust the input position such that we will do one last loop iteration.
                        searchSpace = ref lastSearchSpace;
                    }

                    continue;

                CandidateFound:
                    // We found potential matches, but they may be false-positives, so we must verify each one.
                    if (TryMatch(ref searchSpaceStart, searchSpaceLength, ref searchSpace, result.ExtractMostSignificantBits(), out int offset))
                    {
                        return offset;
                    }
                    goto LoopFooter;
                }
            }
            else
            {
                Vector128<ushort> ch0_1 = Vector128.Create(_ch0_1);
                Vector128<ushort> ch0_2 = Vector128.Create(_ch0_2);
                Vector128<ushort> ch0_3 = Vector128.Create(_ch0_3);
                Vector128<ushort> ch1_1 = Vector128.Create(_ch1_1);
                Vector128<ushort> ch1_2 = Vector128.Create(_ch1_2);
                Vector128<ushort> ch1_3 = Vector128.Create(_ch1_3);

                ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - Vector128<ushort>.Count);

                while (true)
                {
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector128<ushort>.Count);
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector128<ushort>.Count + (int)(_ch2ByteOffset / 2));
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector128<ushort>.Count + (int)(_ch3ByteOffset / 2));

                    // Find which starting positions likely contain a match (likely match all 3 anchor characters).
                    Vector128<byte> result = GetComparisonResult(ref searchSpace, ch2ByteOffset, ch3ByteOffset, ch0_1, ch0_2, ch0_3, ch1_1, ch1_2, ch1_3);

                    if (result != Vector128<byte>.Zero)
                    {
                        goto CandidateFound;
                    }

                LoopFooter:
                    searchSpace = ref Unsafe.Add(ref searchSpace, Vector128<ushort>.Count);

                    if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpace))
                    {
                        if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpace, Vector128<ushort>.Count)))
                        {
                            return -1;
                        }

                        // We have fewer than 8 characters remaining. Adjust the input position such that we will do one last loop iteration.
                        searchSpace = ref lastSearchSpace;
                    }

                    continue;

                CandidateFound:
                    // We found potential matches, but they may be false-positives, so we must verify each one.
                    if (TryMatch(ref searchSpaceStart, searchSpaceLength, ref searchSpace, result.ExtractMostSignificantBits(), out int offset))
                    {
                        return offset;
                    }
                    goto LoopFooter;
                }
            }

        ShortInput:
            string value0 = _value0;
            string value1 = _value1;
            char value0Head = value0.GetRawStringData();
            char value1Head = value1.GetRawStringData();

            nint searchSpaceMinusValue0TailLength = searchSpaceLength + _minusValue0TailLength;

            for (nint i = 0; i < searchSpaceMinusValueTailLength; i++)
            {
                ref char cur = ref Unsafe.Add(ref searchSpace, i);

                if (CaseSensitive.Equals(ref cur, value1) ||
                    (i < searchSpaceMinusValue0TailLength && CaseSensitive.Equals(ref cur, value0)))
                {
                    return (int)i;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> GetComparisonResult(ref char searchSpace, nuint ch2ByteOffset, nuint ch3ByteOffset, Vector128<ushort> ch0_1, Vector128<ushort> ch0_2, Vector128<ushort> ch0_3, Vector128<ushort> ch1_1, Vector128<ushort> ch1_2, Vector128<ushort> ch1_3)
        {
            Vector128<ushort> source0 = Vector128.LoadUnsafe(ref searchSpace);
            Vector128<ushort> source1 = Vector128.LoadUnsafe(ref searchSpace, ch2ByteOffset);
            Vector128<ushort> source2 = Vector128.LoadUnsafe(ref searchSpace, ch3ByteOffset);

            Vector128<ushort> cmp0 = Vector128.Equals(source0, ch0_1) & Vector128.Equals(source1, ch0_2) & Vector128.Equals(source2, ch0_3);
            Vector128<ushort> cmp1 = Vector128.Equals(source0, ch1_1) & Vector128.Equals(source1, ch1_2) & Vector128.Equals(source2, ch1_3);

            return (cmp0 | cmp1).AsByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> GetComparisonResult(ref char searchSpace, nuint ch2ByteOffset, nuint ch3ByteOffset, Vector256<ushort> ch0_1, Vector256<ushort> ch0_2, Vector256<ushort> ch0_3, Vector256<ushort> ch1_1, Vector256<ushort> ch1_2, Vector256<ushort> ch1_3)
        {
            Vector256<ushort> source0 = Vector256.LoadUnsafe(ref searchSpace);
            Vector256<ushort> source1 = Vector256.LoadUnsafe(ref searchSpace, ch2ByteOffset);
            Vector256<ushort> source2 = Vector256.LoadUnsafe(ref searchSpace, ch3ByteOffset);

            Vector256<ushort> cmp0 = Vector256.Equals(source0, ch0_1) & Vector256.Equals(source1, ch0_2) & Vector256.Equals(source2, ch0_3);
            Vector256<ushort> cmp1 = Vector256.Equals(source0, ch1_1) & Vector256.Equals(source1, ch1_2) & Vector256.Equals(source2, ch1_3);

            return (cmp0 | cmp1).AsByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> GetComparisonResult(ref char searchSpace, nuint ch2ByteOffset, nuint ch3ByteOffset, Vector512<ushort> ch0_1, Vector512<ushort> ch0_2, Vector512<ushort> ch0_3, Vector512<ushort> ch1_1, Vector512<ushort> ch1_2, Vector512<ushort> ch1_3)
        {
            Vector512<ushort> source0 = Vector512.LoadUnsafe(ref searchSpace);
            Vector512<ushort> source1 = Vector512.LoadUnsafe(ref searchSpace, ch2ByteOffset);
            Vector512<ushort> source2 = Vector512.LoadUnsafe(ref searchSpace, ch3ByteOffset);

            Vector512<ushort> cmp0 = Vector512.Equals(source0, ch0_1) & Vector512.Equals(source1, ch0_2) & Vector512.Equals(source2, ch0_3);
            Vector512<ushort> cmp1 = Vector512.Equals(source0, ch1_1) & Vector512.Equals(source1, ch1_2) & Vector512.Equals(source2, ch1_3);

            return (cmp0 | cmp1).AsByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryMatch(ref char searchSpaceStart, int searchSpaceLength, ref char searchSpace, uint mask, out int offsetFromStart)
        {
            // 'mask' encodes the input positions where at least 3 characters likely matched.
            // Verify each one to see if we've found a match, otherwise return back to the vectorized loop.
            do
            {
                int bitPos = BitOperations.TrailingZeroCount(mask);
                Debug.Assert(bitPos % 2 == 0);

                ref char matchRef = ref Unsafe.AddByteOffset(ref searchSpace, bitPos);
                offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref searchSpaceStart, ref matchRef) / 2);
                int lengthRemaining = searchSpaceLength - offsetFromStart;

                Debug.Assert(_value0.Length >= _value1.Length);
                Debug.Assert(_value1.Length <= lengthRemaining);
                ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref matchRef, _value1.Length);

                if (CaseSensitive.Equals(ref matchRef, _value1))
                {
                    return true;
                }

                string value0 = _value0;

                if (value0.Length >= lengthRemaining && CaseSensitive.Equals(ref matchRef, value0))
                {
                    return true;
                }

                mask = BitOperations.ResetLowestSetBit(BitOperations.ResetLowestSetBit(mask));
            }
            while (mask != 0);

            offsetFromStart = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryMatch(ref char searchSpaceStart, int searchSpaceLength, ref char searchSpace, ulong mask, out int offsetFromStart)
        {
            // 'mask' encodes the input positions where at least 3 characters likely matched.
            // Verify each one to see if we've found a match, otherwise return back to the vectorized loop.
            do
            {
                int bitPos = BitOperations.TrailingZeroCount(mask);
                Debug.Assert(bitPos % 2 == 0);

                ref char matchRef = ref Unsafe.AddByteOffset(ref searchSpace, bitPos);
                offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref searchSpaceStart, ref matchRef) / 2);
                int lengthRemaining = searchSpaceLength - offsetFromStart;

                Debug.Assert(_value0.Length >= _value1.Length);
                Debug.Assert(_value1.Length <= lengthRemaining);
                ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref matchRef, _value1.Length);

                if (CaseSensitive.Equals(ref matchRef, _value1))
                {
                    return true;
                }

                string value0 = _value0;

                if (value0.Length >= lengthRemaining && CaseSensitive.Equals(ref matchRef, value0))
                {
                    return true;
                }

                mask = BitOperations.ResetLowestSetBit(BitOperations.ResetLowestSetBit(mask));
            }
            while (mask != 0);

            offsetFromStart = 0;
            return false;
        }
    }
}
