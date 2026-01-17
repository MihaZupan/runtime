// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using static System.Buffers.StringSearchValuesHelper;

namespace System.Buffers
{
    internal sealed class TwoStringSearchValuesPackedTwoChars<TValue0Length, TValue1Length, TCaseSensitivity> : StringSearchValuesBase
        where TValue0Length : struct, IValueLength
        where TValue1Length : struct, IValueLength
        where TCaseSensitivity : struct, ICaseSensitivity
    {
        private const byte CaseConversionMask = unchecked((byte)~0x20);

        private readonly SingleValueState _value0State;
        private readonly SingleValueState _value1State;
        private readonly nint _minusValueTailLength;
        private readonly int _minValueLength;
        private readonly int _maxValueLength;

        // First character anchors (at offset 0) for each value
        private readonly byte _v0Ch1;
        private readonly byte _v1Ch1;

        // Second character anchors at shared offset
        private readonly nuint _ch2ByteOffset;
        private readonly byte _v0Ch2;
        private readonly byte _v1Ch2;

        public TwoStringSearchValuesPackedTwoChars(HashSet<string> uniqueValues, string value0, string value1, int ch2Offset) : base(uniqueValues)
        {
            Debug.Assert(Sse2.IsSupported || AdvSimd.Arm64.IsSupported);
            Debug.Assert(value0.Length > 1);
            Debug.Assert(value0.Length <= value1.Length);
            Debug.Assert(ch2Offset > 0);
            Debug.Assert(ch2Offset < value0.Length);
            Debug.Assert(value0[0] <= byte.MaxValue && value0[ch2Offset] <= byte.MaxValue);
            Debug.Assert(value1[0] <= byte.MaxValue && value1[ch2Offset] <= byte.MaxValue);

            _value0State = new SingleValueState(value0, typeof(TCaseSensitivity) != typeof(CaseSensitive));
            _value1State = new SingleValueState(value1, typeof(TCaseSensitivity) != typeof(CaseSensitive));

            _minValueLength = value0.Length;
            _maxValueLength = value1.Length;
            _minusValueTailLength = -(value0.Length - 1);

            _v0Ch1 = (byte)value0[0];
            _v1Ch1 = (byte)value1[0];
            _v0Ch2 = (byte)value0[ch2Offset];
            _v1Ch2 = (byte)value1[ch2Offset];

            if (typeof(TCaseSensitivity) != typeof(CaseSensitive))
            {
                _v0Ch1 &= CaseConversionMask;
                _v1Ch1 &= CaseConversionMask;
                _v0Ch2 &= CaseConversionMask;
                _v1Ch2 &= CaseConversionMask;
            }

            _ch2ByteOffset = (nuint)ch2Offset * sizeof(char);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) =>
            IndexOf(ref MemoryMarshal.GetReference(span), span.Length);

        private int IndexOf(ref char searchSpace, int searchSpaceLength)
        {
            ref char searchSpaceStart = ref searchSpace;

            // Calculate how many positions we can safely search (accounting for max offset needed)
            nint searchSpaceMinusValueTailLength = searchSpaceLength + _minusValueTailLength;

            nuint ch2ByteOffset = _ch2ByteOffset;

            // Packed variant processes Vector<byte>.Count characters at a time
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported && searchSpaceMinusValueTailLength - Vector512<byte>.Count >= 0)
            {
                Vector512<byte> v0Ch1 = Vector512.Create(_v0Ch1);
                Vector512<byte> v1Ch1 = Vector512.Create(_v1Ch1);
                Vector512<byte> v0Ch2 = Vector512.Create(_v0Ch2);
                Vector512<byte> v1Ch2 = Vector512.Create(_v1Ch2);

                ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - Vector512<byte>.Count);

                while (true)
                {
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector512<byte>.Count);
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector512<byte>.Count + (int)(ch2ByteOffset / sizeof(char)));

                    Vector512<byte> packedSource0 = LoadPacked512(ref searchSpace, 0);
                    Vector512<byte> packedSource1 = LoadPacked512(ref searchSpace, ch2ByteOffset);

                    if (typeof(TCaseSensitivity) != typeof(CaseSensitive))
                    {
                        packedSource0 &= Vector512.Create(CaseConversionMask);
                        packedSource1 &= Vector512.Create(CaseConversionMask);
                    }

                    Vector512<byte> result =
                        (Vector512.Equals(v0Ch1, packedSource0) & Vector512.Equals(v0Ch2, packedSource1)) |
                        (Vector512.Equals(v1Ch1, packedSource0) & Vector512.Equals(v1Ch2, packedSource1));

                    if (result != Vector512<byte>.Zero)
                    {
                        goto CandidateFound512;
                    }

                LoopFooter512:
                    searchSpace = ref Unsafe.Add(ref searchSpace, Vector512<byte>.Count);

                    if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpace))
                    {
                        if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpace, Vector512<byte>.Count)))
                        {
                            return -1;
                        }

                        searchSpace = ref lastSearchSpace;
                    }

                    continue;

                CandidateFound512:
                    if (TryMatch(ref searchSpaceStart, searchSpaceLength, ref searchSpace, PackedSpanHelpers.FixUpPackedVector512Result(result).ExtractMostSignificantBits(), out int offset))
                    {
                        return offset;
                    }
                    goto LoopFooter512;
                }
            }
            else if (Vector256.IsHardwareAccelerated && Avx2.IsSupported && searchSpaceMinusValueTailLength - Vector256<byte>.Count >= 0)
            {
                Vector256<byte> v0Ch1 = Vector256.Create(_v0Ch1);
                Vector256<byte> v1Ch1 = Vector256.Create(_v1Ch1);
                Vector256<byte> v0Ch2 = Vector256.Create(_v0Ch2);
                Vector256<byte> v1Ch2 = Vector256.Create(_v1Ch2);

                ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - Vector256<byte>.Count);

                while (true)
                {
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector256<byte>.Count);
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector256<byte>.Count + (int)(ch2ByteOffset / sizeof(char)));

                    Vector256<byte> packedSource0 = LoadPacked256(ref searchSpace, 0);
                    Vector256<byte> packedSource1 = LoadPacked256(ref searchSpace, ch2ByteOffset);

                    if (typeof(TCaseSensitivity) != typeof(CaseSensitive))
                    {
                        packedSource0 &= Vector256.Create(CaseConversionMask);
                        packedSource1 &= Vector256.Create(CaseConversionMask);
                    }

                    Vector256<byte> result =
                        (Vector256.Equals(v0Ch1, packedSource0) & Vector256.Equals(v0Ch2, packedSource1)) |
                        (Vector256.Equals(v1Ch1, packedSource0) & Vector256.Equals(v1Ch2, packedSource1));

                    if (result != Vector256<byte>.Zero)
                    {
                        goto CandidateFound256;
                    }

                LoopFooter256:
                    searchSpace = ref Unsafe.Add(ref searchSpace, Vector256<byte>.Count);

                    if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpace))
                    {
                        if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpace, Vector256<byte>.Count)))
                        {
                            return -1;
                        }

                        searchSpace = ref lastSearchSpace;
                    }

                    continue;

                CandidateFound256:
                    if (TryMatch(ref searchSpaceStart, searchSpaceLength, ref searchSpace, PackedSpanHelpers.FixUpPackedVector256Result(result).ExtractMostSignificantBits(), out int offset))
                    {
                        return offset;
                    }
                    goto LoopFooter256;
                }
            }
            else if ((Sse2.IsSupported || AdvSimd.Arm64.IsSupported) && searchSpaceMinusValueTailLength - Vector128<byte>.Count >= 0)
            {
                Vector128<byte> v0Ch1 = Vector128.Create(_v0Ch1);
                Vector128<byte> v1Ch1 = Vector128.Create(_v1Ch1);
                Vector128<byte> v0Ch2 = Vector128.Create(_v0Ch2);
                Vector128<byte> v1Ch2 = Vector128.Create(_v1Ch2);

                ref char lastSearchSpace = ref Unsafe.Add(ref searchSpace, searchSpaceMinusValueTailLength - Vector128<byte>.Count);

                while (true)
                {
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector128<byte>.Count);
                    ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref searchSpace, Vector128<byte>.Count + (int)(ch2ByteOffset / sizeof(char)));

                    Vector128<byte> packedSource0 = LoadPacked128(ref searchSpace, 0);
                    Vector128<byte> packedSource1 = LoadPacked128(ref searchSpace, ch2ByteOffset);

                    if (typeof(TCaseSensitivity) != typeof(CaseSensitive))
                    {
                        packedSource0 &= Vector128.Create(CaseConversionMask);
                        packedSource1 &= Vector128.Create(CaseConversionMask);
                    }

                    Vector128<byte> result =
                        (Vector128.Equals(v0Ch1, packedSource0) & Vector128.Equals(v0Ch2, packedSource1)) |
                        (Vector128.Equals(v1Ch1, packedSource0) & Vector128.Equals(v1Ch2, packedSource1));

                    if (result != Vector128<byte>.Zero)
                    {
                        goto CandidateFound128;
                    }

                LoopFooter128:
                    searchSpace = ref Unsafe.Add(ref searchSpace, Vector128<byte>.Count);

                    if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpace))
                    {
                        if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpace, Vector128<byte>.Count)))
                        {
                            return -1;
                        }

                        searchSpace = ref lastSearchSpace;
                    }

                    continue;

                CandidateFound128:
                    if (TryMatch(ref searchSpaceStart, searchSpaceLength, ref searchSpace, result.ExtractMostSignificantBits(), out int offset))
                    {
                        return offset;
                    }
                    goto LoopFooter128;
                }
            }

            char value0Head = _value0State.Value.GetRawStringData();
            char value1Head = _value1State.Value.GetRawStringData();

            nint shortInputEnd = searchSpaceLength - _minValueLength + 1;
            for (nint i = 0; i < shortInputEnd; i++)
            {
                ref char cur = ref Unsafe.Add(ref searchSpace, i);
                char firstChar = TCaseSensitivity.TransformInput(cur);

                // value0 is the shorter one and we always have enough length to verify it.
                // value1 may be longer, so we need to check the remaining length first.
                if ((firstChar == value0Head && TCaseSensitivity.Equals<TValue0Length>(ref cur, in _value0State, checkedFirstChar: true)) ||
                    (firstChar == value1Head && searchSpaceLength - i >= _maxValueLength && TCaseSensitivity.Equals<TValue1Length>(ref cur, in _value1State, checkedFirstChar: true)))
                {
                    return (int)i;
                }
            }

            return -1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryMatch(ref char searchSpaceStart, int searchSpaceLength, ref char searchSpace, uint mask, out int offsetFromStart)
        {
            do
            {
                int bitPos = BitOperations.TrailingZeroCount(mask);

                ref char matchRef = ref Unsafe.Add(ref searchSpace, bitPos);

                ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref matchRef, _minValueLength);

                offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref searchSpaceStart, ref matchRef) / sizeof(char));

                // value0 is the shorter one and we always have enough length to verify it.
                // value1 may be longer, so we need to check the remaining length first.
                if (TCaseSensitivity.Equals<TValue0Length>(ref matchRef, in _value0State, checkedFirstChar: false) ||
                    (searchSpaceLength - offsetFromStart >= _maxValueLength && TCaseSensitivity.Equals<TValue1Length>(ref matchRef, in _value1State, checkedFirstChar: false)))
                {
                    return true;
                }

                mask = BitOperations.ResetLowestSetBit(mask);
            }
            while (mask != 0);

            offsetFromStart = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryMatch(ref char searchSpaceStart, int searchSpaceLength, ref char searchSpace, ulong mask, out int offsetFromStart)
        {
            do
            {
                int bitPos = BitOperations.TrailingZeroCount(mask);

                ref char matchRef = ref Unsafe.Add(ref searchSpace, bitPos);

                ValidateReadPosition(ref searchSpaceStart, searchSpaceLength, ref matchRef, _minValueLength);

                offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref searchSpaceStart, ref matchRef) / sizeof(char));

                // value0 is the shorter one and we always have enough length to verify it.
                // value1 may be longer, so we need to check the remaining length first.
                if (TCaseSensitivity.Equals<TValue0Length>(ref matchRef, in _value0State, checkedFirstChar: false) ||
                    (searchSpaceLength - offsetFromStart >= _maxValueLength && TCaseSensitivity.Equals<TValue1Length>(ref matchRef, in _value1State, checkedFirstChar: false)))
                {
                    return true;
                }

                mask = BitOperations.ResetLowestSetBit(mask);
            }
            while (mask != 0);

            offsetFromStart = 0;
            return false;
        }
    }
}
