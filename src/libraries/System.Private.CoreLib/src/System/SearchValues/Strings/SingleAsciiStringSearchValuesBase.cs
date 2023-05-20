// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using static System.Buffers.TeddyHelper;

namespace System.Buffers
{
    internal class SingleAsciiStringSearchValuesBase<TLongString, TStartCaseSensitivity, TCaseSensitivity> : StringSearchValuesBase
        where TLongString : struct, SearchValues.IRuntimeConst
        where TStartCaseSensitivity : struct, ICaseSensitivity
        where TCaseSensitivity : struct, ICaseSensitivity
    {
        private const int MatchStartOffsetN2 = 1;
        private const int MatchStartOffsetN3 = 2;
        private const int CharsPerIterationAvx2 = 32;
        private const int CharsPerIterationVector128 = 16;

        private readonly string _value;
        private readonly byte _b0;
        private readonly byte _b1;
        private readonly byte _b2;
        private readonly int _minInputLengthVector128;
        private readonly int _minInputLengthAvx2;
        private readonly int _lastSearchSpaceOffsetVector128;
        private readonly int _lastSearchSpaceOffsetAvx2;

        public SingleAsciiStringSearchValuesBase(string value, HashSet<string> uniqueValues, int n) : base(uniqueValues)
        {
            Debug.Assert(TLongString.Value == value.Length >= 8);

            _value = value;

            Debug.Assert(char.IsAscii(value[0]));
            Debug.Assert(char.IsAscii(value[1]));
            _b0 = (byte)value[0];
            _b1 = (byte)value[1];

            if (n == 3)
            {
                Debug.Assert(char.IsAscii(value[2]));
                _b2 = (byte)value[2];
            }

            _lastSearchSpaceOffsetVector128 = -(CharsPerIterationVector128 + value.Length - n);
            _lastSearchSpaceOffsetAvx2 = -(CharsPerIterationAvx2 + value.Length - n);

            _minInputLengthVector128 = CharsPerIterationVector128 + value.Length - 1;
            _minInputLengthAvx2 = CharsPerIterationAvx2 + value.Length - 1;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd.Arm64))]
        protected int IndexOfAnyN2(ReadOnlySpan<char> span)
        {
#pragma warning disable IntrinsicsInSystemPrivateCoreLibAttributeNotSpecificEnough // The behavior of the rest of the function remains the same if Avx2.IsSupported is false
            if (Avx2.IsSupported && span.Length >= _minInputLengthAvx2)
#pragma warning disable IntrinsicsInSystemPrivateCoreLibAttributeNotSpecificEnough
            {
                return IndexOfAnyN2Avx2(span);
            }

            return IndexOfAnyN2Vector128(span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd.Arm64))]
        protected int IndexOfAnyN3(ReadOnlySpan<char> span)
        {
#pragma warning disable IntrinsicsInSystemPrivateCoreLibAttributeNotSpecificEnough // The behavior of the rest of the function remains the same if Avx2.IsSupported is false
            if (Avx2.IsSupported && span.Length >= _minInputLengthAvx2)
#pragma warning disable IntrinsicsInSystemPrivateCoreLibAttributeNotSpecificEnough
            {
                return IndexOfAnyN3Avx2(span);
            }

            return IndexOfAnyN3Vector128(span);
        }

        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd.Arm64))]
        private int IndexOfAnyN2Vector128(ReadOnlySpan<char> span)
        {
            if (span.Length < _minInputLengthVector128)
            {
                // Inputs shorter than 15 + value length (CharsPerIterationVector128 + _value.Length - 1)
                return typeof(TCaseSensitivity) == typeof(CaseSensitive)
                    ? span.IndexOf(_value)
                    : Ordinal.IndexOfOrdinalIgnoreCase(span, _value);
            }

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastSearchSpaceStart = ref Unsafe.Add(ref searchSpace, span.Length + _lastSearchSpaceOffsetVector128);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN2);

            Vector128<byte> test0 = Vector128.Create(_b0);
            Vector128<byte> test1 = Vector128.Create(_b1);
            Vector128<byte> prev0 = Vector128<byte>.AllBitsSet;

        Loop:
            Vector128<byte> input = TStartCaseSensitivity.TransformInput(LoadAndPack16AsciiChars(ref searchSpace));

            (Vector128<byte> result, prev0) = ProcessSingleInputN2(input, prev0, test0, test1);

            if (result != Vector128<byte>.Zero)
            {
                goto CandidateFound;
            }

        ContinueLoop:
            searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIterationVector128);

            if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpaceStart))
            {
                if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpaceStart, CharsPerIterationVector128)))
                {
                    return -1;
                }

                prev0 = Vector128<byte>.AllBitsSet;
                searchSpace = ref lastSearchSpaceStart;
            }
            goto Loop;

        CandidateFound:
            if (TryFindMatch(span, ref searchSpace, result.ExtractMostSignificantBits(), MatchStartOffsetN2, out int offset))
            {
                return offset;
            }
            goto ContinueLoop;
        }

        [CompExactlyDependsOn(typeof(Avx2))]
        private int IndexOfAnyN2Avx2(ReadOnlySpan<char> span)
        {
            Debug.Assert(span.Length >= _minInputLengthAvx2);

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastSearchSpaceStart = ref Unsafe.Add(ref searchSpace, span.Length + _lastSearchSpaceOffsetAvx2);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN2);

            Vector256<byte> test0 = Vector256.Create(_b0);
            Vector256<byte> test1 = Vector256.Create(_b1);
            Vector256<byte> prev0 = Vector256<byte>.AllBitsSet;

        Loop:
            Vector256<byte> input = TStartCaseSensitivity.TransformInput(LoadAndPack32AsciiChars(ref searchSpace));

            (Vector256<byte> result, prev0) = ProcessSingleInputN2(input, prev0, test0, test1);

            if (result != Vector256<byte>.Zero)
            {
                goto CandidateFound;
            }

        ContinueLoop:
            searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIterationAvx2);

            if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpaceStart))
            {
                if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpaceStart, CharsPerIterationAvx2)))
                {
                    return -1;
                }

                prev0 = Vector256<byte>.AllBitsSet;
                searchSpace = ref lastSearchSpaceStart;
            }
            goto Loop;

        CandidateFound:
            if (TryFindMatch(span, ref searchSpace, result.ExtractMostSignificantBits(), MatchStartOffsetN2, out int offset))
            {
                return offset;
            }
            goto ContinueLoop;
        }

        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd.Arm64))]
        private int IndexOfAnyN3Vector128(ReadOnlySpan<char> span)
        {
            if (span.Length < _minInputLengthVector128)
            {
                // Inputs shorter than 15 + value length (CharsPerIterationVector128 + _value.Length - 1)
                return typeof(TCaseSensitivity) == typeof(CaseSensitive)
                    ? span.IndexOf(_value)
                    : Ordinal.IndexOfOrdinalIgnoreCase(span, _value);
            }

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastSearchSpaceStart = ref Unsafe.Add(ref searchSpace, span.Length + _lastSearchSpaceOffsetVector128);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN3);

            Vector128<byte> test0 = Vector128.Create(_b0);
            Vector128<byte> test1 = Vector128.Create(_b1);
            Vector128<byte> test2 = Vector128.Create(_b2);
            Vector128<byte> prev0 = Vector128<byte>.AllBitsSet;
            Vector128<byte> prev1 = Vector128<byte>.AllBitsSet;

        Loop:
            Vector128<byte> input = TStartCaseSensitivity.TransformInput(LoadAndPack16AsciiChars(ref searchSpace));

            (Vector128<byte> result, prev0, prev1) = ProcessSingleInputN3(input, prev0, prev1, test0, test1, test2);

            if (result != Vector128<byte>.Zero)
            {
                goto CandidateFound;
            }

        ContinueLoop:
            searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIterationVector128);

            if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpaceStart))
            {
                if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpaceStart, CharsPerIterationVector128)))
                {
                    return -1;
                }

                prev0 = Vector128<byte>.AllBitsSet;
                prev1 = Vector128<byte>.AllBitsSet;
                searchSpace = ref lastSearchSpaceStart;
            }
            goto Loop;

        CandidateFound:
            if (TryFindMatch(span, ref searchSpace, result.ExtractMostSignificantBits(), MatchStartOffsetN3, out int offset))
            {
                return offset;
            }
            goto ContinueLoop;
        }

        [CompExactlyDependsOn(typeof(Avx2))]
        private int IndexOfAnyN3Avx2(ReadOnlySpan<char> span)
        {
            Debug.Assert(span.Length >= _minInputLengthAvx2);

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastSearchSpaceStart = ref Unsafe.Add(ref searchSpace, span.Length + _lastSearchSpaceOffsetAvx2);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN3);

            Vector256<byte> test0 = Vector256.Create(_b0);
            Vector256<byte> test1 = Vector256.Create(_b1);
            Vector256<byte> test2 = Vector256.Create(_b2);
            Vector256<byte> prev0 = Vector256<byte>.AllBitsSet;
            Vector256<byte> prev1 = Vector256<byte>.AllBitsSet;

        Loop:
            Vector256<byte> input = TStartCaseSensitivity.TransformInput(LoadAndPack32AsciiChars(ref searchSpace));

            (Vector256<byte> result, prev0, prev1) = ProcessSingleInputN3(input, prev0, prev1, test0, test1, test2);

            if (result != Vector256<byte>.Zero)
            {
                goto CandidateFound;
            }

        ContinueLoop:
            searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIterationAvx2);

            if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpaceStart))
            {
                if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpaceStart, CharsPerIterationAvx2)))
                {
                    return -1;
                }

                prev0 = Vector256<byte>.AllBitsSet;
                prev1 = Vector256<byte>.AllBitsSet;
                searchSpace = ref lastSearchSpaceStart;
            }
            goto Loop;

        CandidateFound:
            if (TryFindMatch(span, ref searchSpace, result.ExtractMostSignificantBits(), MatchStartOffsetN3, out int offset))
            {
                return offset;
            }
            goto ContinueLoop;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFindMatch(ReadOnlySpan<char> span, ref char searchSpace, uint resultMask, int matchStartOffset, out int offsetFromStart)
        {
            int baseOffset = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref searchSpace) / 2) - matchStartOffset;

            do
            {
                int matchOffset = BitOperations.TrailingZeroCount(resultMask);

                ref char matchRef = ref Unsafe.Add(ref searchSpace, matchOffset - matchStartOffset);

                Debug.Assert(span.Length - (baseOffset + matchOffset) >= _value.Length);

                if (TLongString.Value
                    ? TCaseSensitivity.LongInputEquals(ref matchRef, _value)
                    : TCaseSensitivity.Equals(ref matchRef, _value))
                {
                    offsetFromStart = baseOffset + matchOffset;
                    return true;
                }

                resultMask = BitOperations.ResetLowestSetBit(resultMask);
            }
            while (resultMask != 0);

            offsetFromStart = 0;
            return false;
        }
    }
}
