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
    internal sealed class TwoStringSearchValuesThreeChars<TCaseSensitivity> : StringSearchValuesBase
        where TCaseSensitivity : struct, ICaseSensitivity
    {
        private const ushort CaseConversionMask = unchecked((ushort)~0x20);

        private readonly string _value0;
        private readonly string _value1;
        private readonly nint _minusMinValueTailLength;
        private readonly nuint _ch2ByteOffset;
        private readonly nuint _ch3ByteOffset;
        private readonly ushort _ch1_0;
        private readonly ushort _ch1_1;
        private readonly ushort _ch2_0;
        private readonly ushort _ch2_1;
        private readonly ushort _ch3_0;
        private readonly ushort _ch3_1;

        public TwoStringSearchValuesThreeChars(ReadOnlySpan<string> values, HashSet<string> uniqueValues) : base(uniqueValues)
        {
            Debug.Assert(values.Length == 2);

            string value0 = values[0];
            string value1 = values[1];

            Debug.Assert(value0.Length > 1 && value1.Length > 1);

            bool ignoreCase = typeof(TCaseSensitivity) != typeof(CaseSensitive);

            CharacterFrequencyHelper.GetMultiStringThreeCharacterOffsets(values, ignoreCase, out int ch2Offset, out int ch3Offset);

            Debug.Assert(ch3Offset == 0 || ch3Offset > ch2Offset);

            _value0 = value0;
            _value1 = value1;
            _minusMinValueTailLength = -(Math.Min(value0.Length, value1.Length) - 1);

            _ch1_0 = value0[0];
            _ch1_1 = value1[0];
            _ch2_0 = value0[ch2Offset];
            _ch2_1 = value1[ch2Offset];
            _ch3_0 = value0[ch3Offset];
            _ch3_1 = value1[ch3Offset];

            if (ignoreCase)
            {
                _ch1_0 &= CaseConversionMask;
                _ch1_1 &= CaseConversionMask;
                _ch2_0 &= CaseConversionMask;
                _ch2_1 &= CaseConversionMask;
                _ch3_0 &= CaseConversionMask;
                _ch3_1 &= CaseConversionMask;
            }

            _ch2ByteOffset = (nuint)ch2Offset * 2;
            _ch3ByteOffset = (nuint)ch3Offset * 2;
        }

        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span)
        {
            ref char searchSpace = ref MemoryMarshal.GetReference(span);

            nint searchSpaceMinusValueTailLength = span.Length + _minusMinValueTailLength;

            if (!Vector128.IsHardwareAccelerated || searchSpaceMinusValueTailLength < Vector128<ushort>.Count)
            {
                goto ShortInput;
            }

            nuint ch2ByteOffset = _ch2ByteOffset;
            nuint ch3ByteOffset = _ch3ByteOffset;

            if (Vector512.IsHardwareAccelerated && searchSpaceMinusValueTailLength - Vector512<ushort>.Count >= 0)
            {
                Vector512<ushort> ch1_0 = Vector512.Create(_ch1_0);
                Vector512<ushort> ch1_1 = Vector512.Create(_ch1_1);
                Vector512<ushort> ch2_0 = Vector512.Create(_ch2_0);
                Vector512<ushort> ch2_1 = Vector512.Create(_ch2_1);
                Vector512<ushort> ch3_0 = Vector512.Create(_ch3_0);
                Vector512<ushort> ch3_1 = Vector512.Create(_ch3_1);

                ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - Vector512<ushort>.Count);

                while (true)
                {
                    Vector512<byte> result = GetComparisonResult(ref searchSpace, ch2ByteOffset, ch3ByteOffset, ch1_0, ch1_1, ch2_0, ch2_1, ch3_0, ch1_1);

                    if (result != Vector512<byte>.Zero)
                    {
                        goto CandidateFound;
                    }

                LoopFooter:
                    searchSpace = ref Unsafe.Add(ref searchSpace, Vector512<ushort>.Count);

                    if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpace))
                    {
                        if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpace, Vector512<ushort>.Count)))
                        {
                            return -1;
                        }

                        searchSpace = ref lastSearchSpace;
                    }

                    continue;

                CandidateFound:
                    if (TryMatch(span, ref searchSpace, result.ExtractMostSignificantBits(), out int resultOffset))
                    {
                        return resultOffset;
                    }
                    goto LoopFooter;
                }
            }
            else if (Vector256.IsHardwareAccelerated && searchSpaceMinusValueTailLength - Vector256<ushort>.Count >= 0)
            {
                Vector256<ushort> ch1_0 = Vector256.Create(_ch1_0);
                Vector256<ushort> ch1_1 = Vector256.Create(_ch1_1);
                Vector256<ushort> ch2_0 = Vector256.Create(_ch2_0);
                Vector256<ushort> ch2_1 = Vector256.Create(_ch2_1);
                Vector256<ushort> ch3_0 = Vector256.Create(_ch3_0);
                Vector256<ushort> ch3_1 = Vector256.Create(_ch3_1);

                ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - Vector256<ushort>.Count);

                while (true)
                {
                    Vector256<byte> result = GetComparisonResult(ref searchSpace, ch2ByteOffset, ch3ByteOffset, ch1_0, ch1_1, ch2_0, ch2_1, ch3_0, ch1_1);

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

                        searchSpace = ref lastSearchSpace;
                    }

                    continue;

                CandidateFound:
                    if (TryMatch(span, ref searchSpace, result.ExtractMostSignificantBits(), out int resultOffset))
                    {
                        return resultOffset;
                    }
                    goto LoopFooter;
                }
            }
            else
            {
                Vector128<ushort> ch1_0 = Vector128.Create(_ch1_0);
                Vector128<ushort> ch1_1 = Vector128.Create(_ch1_1);
                Vector128<ushort> ch2_0 = Vector128.Create(_ch2_0);
                Vector128<ushort> ch2_1 = Vector128.Create(_ch2_1);
                Vector128<ushort> ch3_0 = Vector128.Create(_ch3_0);
                Vector128<ushort> ch3_1 = Vector128.Create(_ch3_1);

                ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - Vector128<ushort>.Count);

                while (true)
                {
                    Vector128<byte> result = GetComparisonResult(ref searchSpace, ch2ByteOffset, ch3ByteOffset, ch1_0, ch1_1, ch2_0, ch2_1, ch3_0, ch1_1);

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

                        searchSpace = ref lastSearchSpace;
                    }

                    continue;

                CandidateFound:
                    if (TryMatch(span, ref searchSpace, result.ExtractMostSignificantBits(), out int resultOffset))
                    {
                        return resultOffset;
                    }
                    goto LoopFooter;
                }
            }

        ShortInput:
            string value0 = _value0;
            string value1 = _value1;
            char valueHead0 = value0.GetRawStringData();
            char valueHead1 = value1.GetRawStringData();

            for (nint i = 0; i < searchSpaceMinusValueTailLength; i++)
            {
                ref char cur = ref Unsafe.Add(ref searchSpace, i);

                if (typeof(TCaseSensitivity) != typeof(CaseInsensitiveUnicode))
                {
                    char normalized = TCaseSensitivity.TransformInput(cur);

                    if (normalized != valueHead0 && normalized != valueHead1)
                    {
                        continue;
                    }
                }

                int lengthRemaining = span.Length - (int)i;

                if (StartsWith<TCaseSensitivity>(ref cur, lengthRemaining, value0) ||
                    StartsWith<TCaseSensitivity>(ref cur, lengthRemaining, value1))
                {
                    return (int)i;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> GetComparisonResult(
            ref char searchSpace, nuint ch2ByteOffset, nuint ch3ByteOffset,
            Vector128<ushort> ch1_0, Vector128<ushort> ch1_1, Vector128<ushort> ch2_0,
            Vector128<ushort> ch2_1, Vector128<ushort> ch3_0, Vector128<ushort> ch3_1)
        {
            if (typeof(TCaseSensitivity) == typeof(CaseSensitive))
            {
                Vector128<ushort> input1 = Vector128.LoadUnsafe(ref searchSpace);
                Vector128<ushort> cmpCh1 = Vector128.Equals(ch1_0, input1) | Vector128.Equals(ch1_1, input1);

                Vector128<ushort> input2 = Vector128.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch2ByteOffset).AsUInt16();
                Vector128<ushort> cmpCh2 = Vector128.Equals(ch2_0, input2) | Vector128.Equals(ch2_1, input2);

                Vector128<ushort> input3 = Vector128.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch3ByteOffset).AsUInt16();
                Vector128<ushort> cmpCh3 = Vector128.Equals(ch3_0, input3) | Vector128.Equals(ch3_1, input3);

                return (cmpCh1 & cmpCh2 & cmpCh3).AsByte();
            }
            else
            {
                Vector128<ushort> caseConversion = Vector128.Create(CaseConversionMask);

                Vector128<ushort> input1 = Vector128.LoadUnsafe(ref searchSpace) & caseConversion;
                Vector128<ushort> cmpCh1 = Vector128.Equals(ch1_0, input1) | Vector128.Equals(ch1_1, input1);

                Vector128<ushort> input2 = Vector128.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch2ByteOffset).AsUInt16() & caseConversion;
                Vector128<ushort> cmpCh2 = Vector128.Equals(ch2_0, input2) | Vector128.Equals(ch2_1, input2);

                Vector128<ushort> input3 = Vector128.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch3ByteOffset).AsUInt16() & caseConversion;
                Vector128<ushort> cmpCh3 = Vector128.Equals(ch3_0, input3) | Vector128.Equals(ch3_1, input3);

                return (cmpCh1 & cmpCh2 & cmpCh3).AsByte();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector256<byte> GetComparisonResult(
            ref char searchSpace, nuint ch2ByteOffset, nuint ch3ByteOffset,
            Vector256<ushort> ch1_0, Vector256<ushort> ch1_1, Vector256<ushort> ch2_0,
            Vector256<ushort> ch2_1, Vector256<ushort> ch3_0, Vector256<ushort> ch3_1)
        {
            if (typeof(TCaseSensitivity) == typeof(CaseSensitive))
            {
                Vector256<ushort> input1 = Vector256.LoadUnsafe(ref searchSpace);
                Vector256<ushort> cmpCh1 = Vector256.Equals(ch1_0, input1) | Vector256.Equals(ch1_1, input1);

                Vector256<ushort> input2 = Vector256.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch2ByteOffset).AsUInt16();
                Vector256<ushort> cmpCh2 = Vector256.Equals(ch2_0, input2) | Vector256.Equals(ch2_1, input2);

                Vector256<ushort> input3 = Vector256.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch3ByteOffset).AsUInt16();
                Vector256<ushort> cmpCh3 = Vector256.Equals(ch3_0, input3) | Vector256.Equals(ch3_1, input3);

                return (cmpCh1 & cmpCh2 & cmpCh3).AsByte();
            }
            else
            {
                Vector256<ushort> caseConversion = Vector256.Create(CaseConversionMask);

                Vector256<ushort> input1 = Vector256.LoadUnsafe(ref searchSpace) & caseConversion;
                Vector256<ushort> cmpCh1 = Vector256.Equals(ch1_0, input1) | Vector256.Equals(ch1_1, input1);

                Vector256<ushort> input2 = Vector256.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch2ByteOffset).AsUInt16() & caseConversion;
                Vector256<ushort> cmpCh2 = Vector256.Equals(ch2_0, input2) | Vector256.Equals(ch2_1, input2);

                Vector256<ushort> input3 = Vector256.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch3ByteOffset).AsUInt16() & caseConversion;
                Vector256<ushort> cmpCh3 = Vector256.Equals(ch3_0, input3) | Vector256.Equals(ch3_1, input3);

                return (cmpCh1 & cmpCh2 & cmpCh3).AsByte();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector512<byte> GetComparisonResult(
            ref char searchSpace, nuint ch2ByteOffset, nuint ch3ByteOffset,
            Vector512<ushort> ch1_0, Vector512<ushort> ch1_1, Vector512<ushort> ch2_0,
            Vector512<ushort> ch2_1, Vector512<ushort> ch3_0, Vector512<ushort> ch3_1)
        {
            if (typeof(TCaseSensitivity) == typeof(CaseSensitive))
            {
                Vector512<ushort> input1 = Vector512.LoadUnsafe(ref searchSpace);
                Vector512<ushort> cmpCh1 = Vector512.Equals(ch1_0, input1) | Vector512.Equals(ch1_1, input1);

                Vector512<ushort> input2 = Vector512.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch2ByteOffset).AsUInt16();
                Vector512<ushort> cmpCh2 = Vector512.Equals(ch2_0, input2) | Vector512.Equals(ch2_1, input2);

                Vector512<ushort> input3 = Vector512.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch3ByteOffset).AsUInt16();
                Vector512<ushort> cmpCh3 = Vector512.Equals(ch3_0, input3) | Vector512.Equals(ch3_1, input3);

                return (cmpCh1 & cmpCh2 & cmpCh3).AsByte();
            }
            else
            {
                Vector512<ushort> caseConversion = Vector512.Create(CaseConversionMask);

                Vector512<ushort> input1 = Vector512.LoadUnsafe(ref searchSpace) & caseConversion;
                Vector512<ushort> cmpCh1 = Vector512.Equals(ch1_0, input1) | Vector512.Equals(ch1_1, input1);

                Vector512<ushort> input2 = Vector512.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch2ByteOffset).AsUInt16() & caseConversion;
                Vector512<ushort> cmpCh2 = Vector512.Equals(ch2_0, input2) | Vector512.Equals(ch2_1, input2);

                Vector512<ushort> input3 = Vector512.LoadUnsafe(ref Unsafe.As<char, byte>(ref searchSpace), ch3ByteOffset).AsUInt16() & caseConversion;
                Vector512<ushort> cmpCh3 = Vector512.Equals(ch3_0, input3) | Vector512.Equals(ch3_1, input3);

                return (cmpCh1 & cmpCh2 & cmpCh3).AsByte();
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryMatch(ReadOnlySpan<char> span, ref char searchSpace, uint mask, out int offsetFromStart)
        {
            do
            {
                int bitPos = BitOperations.TrailingZeroCount(mask);
                Debug.Assert(bitPos % 2 == 0);

                ref char matchRef = ref Unsafe.As<byte, char>(ref Unsafe.Add(ref Unsafe.As<char, byte>(ref searchSpace), bitPos));
                offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref matchRef) / 2);
                int lengthRemaining = span.Length - offsetFromStart;

                if (StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, _value0) ||
                    StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, _value1))
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
        private bool TryMatch(ReadOnlySpan<char> span, ref char searchSpace, ulong mask, out int offsetFromStart)
        {
            do
            {
                int bitPos = BitOperations.TrailingZeroCount(mask);
                Debug.Assert(bitPos % 2 == 0);

                ref char matchRef = ref Unsafe.As<byte, char>(ref Unsafe.Add(ref Unsafe.As<char, byte>(ref searchSpace), bitPos));
                offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref matchRef) / 2);
                int lengthRemaining = span.Length - offsetFromStart;

                if (StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, _value0) ||
                    StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, _value1))
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
