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
    internal sealed class SingleStringSearchValuesThreeChars<TCaseSensitivity> : StringSearchValuesBase
        where TCaseSensitivity : struct, ICaseSensitivity
    {
        private const ushort CaseConversionMask = unchecked((ushort)~0x20);

        private readonly string _value;
        private readonly nint _minusValueTailLength;
        private readonly nuint _ch2ByteOffset;
        private readonly nuint _ch3ByteOffset;
        private readonly ushort _ch1;
        private readonly ushort _ch2;
        private readonly ushort _ch3;

        private static bool IgnoreCase => typeof(TCaseSensitivity) != typeof(CaseSensitive);

        public SingleStringSearchValuesThreeChars(HashSet<string>? uniqueValues, string value) : base(uniqueValues)
        {
            // We could have more than one entry in 'uniqueValues' if this value is an exact prefix of all the others.
            Debug.Assert(value.Length > 1);

            CharacterFrequencyHelper.GetSingleStringMultiCharacterOffsets(value, IgnoreCase, out int ch2Offset, out int ch3Offset);

            Debug.Assert(ch3Offset == 0 || ch3Offset > ch2Offset);

            _value = value;
            _minusValueTailLength = -(value.Length - 1);

            _ch1 = value[0];
            _ch2 = value[ch2Offset];
            _ch3 = value[ch3Offset];

            if (IgnoreCase)
            {
                _ch1 &= CaseConversionMask;
                _ch2 &= CaseConversionMask;
                _ch3 &= CaseConversionMask;
            }

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

            if (Vector512.IsHardwareAccelerated && searchSpaceMinusValueTailLength - Vector512<ushort>.Count >= 0)
            {
                return IndexOfCore<Vector512<byte>, Vector512<ushort>>(ref searchSpaceStart, ref searchSpace, searchSpaceLength, searchSpaceMinusValueTailLength);
            }
            else if (Vector256.IsHardwareAccelerated && searchSpaceMinusValueTailLength - Vector256<ushort>.Count >= 0)
            {
                return IndexOfCore<Vector256<byte>, Vector256<ushort>>(ref searchSpaceStart, ref searchSpace, searchSpaceLength, searchSpaceMinusValueTailLength);
            }
            else
            {
                return IndexOfCore<Vector128<byte>, Vector128<ushort>>(ref searchSpaceStart, ref searchSpace, searchSpaceLength, searchSpaceMinusValueTailLength);
            }

        ShortInput:
            string value = _value;
            char valueHead = value.GetRawStringData();

            for (nint i = 0; i < searchSpaceMinusValueTailLength; i++)
            {
                ref char cur = ref Unsafe.Add(ref searchSpace, i);

                // CaseInsensitiveUnicode doesn't support single-character transformations, so we skip checking the first character first.
                if ((typeof(TCaseSensitivity) == typeof(CaseInsensitiveUnicode) || TCaseSensitivity.TransformInput(cur) == valueHead) &&
                    TCaseSensitivity.Equals(ref cur, value))
                {
                    return (int)i;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private int IndexOfCore<TByteVector, TUshortVector>(ref char searchSpaceStart, ref char searchSpace, int searchSpaceLength, nint searchSpaceMinusValueTailLength)
            where TByteVector : struct, ISimdVector<TByteVector, byte>
            where TUshortVector : struct, ISimdVector<TUshortVector, ushort>
        {
            TUshortVector ch1 = TUshortVector.Create(_ch1);
            TUshortVector ch2 = TUshortVector.Create(_ch2);
            TUshortVector ch3 = TUshortVector.Create(_ch3);

            nuint ch2ByteOffset = _ch2ByteOffset;
            nuint ch3ByteOffset = _ch3ByteOffset;

            ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - TUshortVector.Count);

            while (true)
            {
                ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, TUshortVector.Count);
                ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, TUshortVector.Count + (int)(_ch2ByteOffset / 2));
                ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, TUshortVector.Count + (int)(_ch3ByteOffset / 2));

                // Find which starting positions likely contain a match (likely match all 3 anchor characters).
                TByteVector result = GetComparisonResult<TByteVector, TUshortVector>(ref searchSpace, ch2ByteOffset, ch3ByteOffset, ch1, ch2, ch3);

                if (result != TByteVector.Zero)
                {
                    goto CandidateFound;
                }

            LoopFooter:
                // We haven't found a match. Update the input position and check if we've reached the end.
                searchSpace = ref Unsafe.Add(ref searchSpace, TUshortVector.Count);

                if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpace))
                {
                    if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpace, TUshortVector.Count)))
                    {
                        return -1;
                    }

                    // We have fewer than 32 characters remaining. Adjust the input position such that we will do one last loop iteration.
                    searchSpace = ref lastSearchSpace;
                }

                continue;

            CandidateFound:
                // We found potential matches, but they may be false-positives, so we must verify each one.
                if (typeof(TByteVector) == typeof(Vector512<byte>))
                {
                    ulong resultMask = Unsafe.BitCast<TByteVector, Vector512<byte>>(result).ExtractMostSignificantBits();

                    if (TryMatch(ref searchSpaceStart, searchSpaceLength, ref searchSpace, resultMask, out int offset))
                    {
                        return offset;
                    }
                }
                else
                {
                    uint resultMask = typeof(TByteVector) == typeof(Vector128<byte>)
                        ? Unsafe.BitCast<TByteVector, Vector128<byte>>(result).ExtractMostSignificantBits()
                        : Unsafe.BitCast<TByteVector, Vector256<byte>>(result).ExtractMostSignificantBits();

                    if (TryMatch(ref searchSpaceStart, searchSpaceLength, ref searchSpace, resultMask, out int offset))
                    {
                        return offset;
                    }
                }

                goto LoopFooter;
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static TByteVector GetComparisonResult<TByteVector, TUshortVector>(ref char searchSpace, nuint ch2ByteOffset, nuint ch3ByteOffset, TUshortVector ch1, TUshortVector ch2, TUshortVector ch3)
            where TByteVector : struct, ISimdVector<TByteVector, byte>
            where TUshortVector : struct, ISimdVector<TUshortVector, ushort>
        {
            // Load 3 vectors from the input.
            // One from the current search space, the other two at an offset based on the distance of those characters from the first one.
            TUshortVector inputCh1 = TUshortVector.LoadUnsafe(ref Unsafe.As<char, ushort>(ref searchSpace));
            TUshortVector inputCh2 = Unsafe.BitCast<TByteVector, TUshortVector>(TByteVector.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch2ByteOffset));
            TUshortVector inputCh3 = Unsafe.BitCast<TByteVector, TUshortVector>(TByteVector.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch3ByteOffset));

            if (typeof(TCaseSensitivity) != typeof(CaseSensitive))
            {
                // For each, AND the value with ~0x20 so that letters are uppercased.
                // For characters that aren't ASCII letters, this may produce wrong results, but only false-positives.
                // We will take care of those in the verification step if the other characters also indicate a possible match.
                TUshortVector caseConversion = TUshortVector.Create(CaseConversionMask);

                inputCh1 &= caseConversion;
                inputCh2 &= caseConversion;
                inputCh3 &= caseConversion;
            }

            TUshortVector cmpCh1 = TUshortVector.Equals(ch1, inputCh1);
            TUshortVector cmpCh2 = TUshortVector.Equals(ch2, inputCh2);
            TUshortVector cmpCh3 = TUshortVector.Equals(ch3, inputCh3);

            // AND all 3 together to get a mask of possible match positions that match in at least 3 places.
            return Unsafe.BitCast<TUshortVector, TByteVector>((cmpCh1 & cmpCh2 & cmpCh3));
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

                ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref matchRef, _value.Length);

                if (TCaseSensitivity.Equals(ref matchRef, _value))
                {
                    offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref searchSpaceStart, ref matchRef) / 2);
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

                ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref matchRef, _value.Length);

                if (TCaseSensitivity.Equals(ref matchRef, _value))
                {
                    offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref searchSpaceStart, ref matchRef) / 2);
                    return true;
                }

                mask = BitOperations.ResetLowestSetBit(BitOperations.ResetLowestSetBit(mask));
            }
            while (mask != 0);

            offsetFromStart = 0;
            return false;
        }


        internal override bool ContainsCore(string value) => HasUniqueValues
            ? base.ContainsCore(value)
            : _value.Equals(value, IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        internal override string[] GetValues() => HasUniqueValues
            ? base.GetValues()
            : new string[] { _value };
    }
}
