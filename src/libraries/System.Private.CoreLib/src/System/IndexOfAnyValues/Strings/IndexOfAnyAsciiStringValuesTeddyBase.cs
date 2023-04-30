// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;
using static System.Buffers.TeddyHelper;

namespace System.Buffers
{
    internal abstract class IndexOfAnyAsciiStringValuesTeddyBase<TBucketized, TStartCaseSensitivity, TCaseSensitivity> : IndexOfAnyStringValuesRabinKarp<TCaseSensitivity>
        where TBucketized : struct, IndexOfAnyValues.IRuntimeConst
        where TStartCaseSensitivity : struct, ICaseSensitivity
        where TCaseSensitivity : struct, ICaseSensitivity
    {
        private const int MatchStartOffsetN2 = 1;
        private const int MatchStartOffsetN3 = 2;
        private const int CharsPerIterationAvx2 = 32;
        private const int CharsPerIterationVector128 = 16;

        private readonly EightPackedReferences _buckets;

        private readonly Vector256<byte>
            _n0Low256, _n0High256,
            _n1Low256, _n1High256,
            _n2Low256, _n2High256;

        private readonly Vector128<byte>
            _n0Low, _n0High,
            _n1Low, _n1High,
            _n2Low, _n2High;

        protected IndexOfAnyAsciiStringValuesTeddyBase(ReadOnlySpan<string> values, RabinKarp rabinKarp, HashSet<string> uniqueValues, int n) : base(rabinKarp, uniqueValues)
        {
            Debug.Assert(!TBucketized.Value);

            _buckets = new EightPackedReferences(MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<string, object>(ref MemoryMarshal.GetReference(values)),
                values.Length));

            (_n0Low, _n0High) = GenerateNonBucketizedFingerprint(values, offset: 0);
            (_n1Low, _n1High) = GenerateNonBucketizedFingerprint(values, offset: 1);

            if (n == 3)
            {
                (_n2Low, _n2High) = GenerateNonBucketizedFingerprint(values, offset: 2);
            }

            (_n0Low256, _n0High256) = (Vector256.Create(_n0Low, _n0Low), Vector256.Create(_n0High, _n0High));
            (_n1Low256, _n1High256) = (Vector256.Create(_n1Low, _n1Low), Vector256.Create(_n1High, _n1High));
            (_n2Low256, _n2High256) = (Vector256.Create(_n2Low, _n2Low), Vector256.Create(_n2High, _n2High));
        }

        protected IndexOfAnyAsciiStringValuesTeddyBase(string[][] buckets, RabinKarp rabinKarp, HashSet<string> uniqueValues, int n) : base(rabinKarp, uniqueValues)
        {
            Debug.Assert(TBucketized.Value);

            _buckets = new EightPackedReferences(buckets);

            (_n0Low, _n0High) = GenerateBucketizedFingerprint(buckets, offset: 0);
            (_n1Low, _n1High) = GenerateBucketizedFingerprint(buckets, offset: 1);

            if (n == 3)
            {
                (_n2Low, _n2High) = GenerateBucketizedFingerprint(buckets, offset: 2);
            }

            (_n0Low256, _n0High256) = (Vector256.Create(_n0Low, _n0Low), Vector256.Create(_n0High, _n0High));
            (_n1Low256, _n1High256) = (Vector256.Create(_n1Low, _n1Low), Vector256.Create(_n1High, _n1High));
            (_n2Low256, _n2High256) = (Vector256.Create(_n2Low, _n2Low), Vector256.Create(_n2High, _n2High));
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected int IndexOfAnyN2(ReadOnlySpan<char> span)
        {
            if (Avx2.IsSupported && span.Length >= CharsPerIterationAvx2 + MatchStartOffsetN2)
            {
                return IndexOfAnyN2Avx2(span);
            }

            return IndexOfAnyN2Vector128(span);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected int IndexOfAnyN3(ReadOnlySpan<char> span)
        {
            if (Avx2.IsSupported && span.Length >= CharsPerIterationAvx2 + MatchStartOffsetN3)
            {
                return IndexOfAnyN3Avx2(span);
            }

            return IndexOfAnyN3Vector128(span);
        }

        private int IndexOfAnyN2Vector128(ReadOnlySpan<char> span)
        {
            if (span.Length < CharsPerIterationVector128 + MatchStartOffsetN2)
            {
                return ShortInputFallback(span);
            }

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastVectorizedSearchSpace = ref Unsafe.Add(ref searchSpace, span.Length - CharsPerIterationVector128);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN2);

            Vector128<byte> n0Low = _n0Low, n0High = _n0High;
            Vector128<byte> n1Low = _n1Low, n1High = _n1High;
            Vector128<byte> prev0 = Vector128<byte>.AllBitsSet;

            while (true)
            {
                Vector128<byte> input = TStartCaseSensitivity.TransformInput(LoadAndPack16AsciiChars(ref searchSpace));

                (Vector128<byte> result, prev0) = ProcessInputN2(input, prev0, n0Low, n0High, n1Low, n1High);

                if (TryFindMatch(span, ref searchSpace, result, MatchStartOffsetN2, out int offset))
                {
                    return offset;
                }

                searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIterationVector128);

                if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastVectorizedSearchSpace))
                {
                    if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastVectorizedSearchSpace, CharsPerIterationVector128)))
                    {
                        return -1;
                    }

                    prev0 = Vector128<byte>.AllBitsSet;
                    searchSpace = ref lastVectorizedSearchSpace;
                }
            }
        }

        [BypassReadyToRun]
        private int IndexOfAnyN2Avx2(ReadOnlySpan<char> span)
        {
            Debug.Assert(span.Length >= CharsPerIterationAvx2 + MatchStartOffsetN2);

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastVectorizedSearchSpace = ref Unsafe.Add(ref searchSpace, span.Length - CharsPerIterationAvx2);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN2);

            Vector256<byte> n0Low = _n0Low256, n0High = _n0High256;
            Vector256<byte> n1Low = _n1Low256, n1High = _n1High256;
            Vector256<byte> prev0 = Vector256<byte>.AllBitsSet;

            while (true)
            {
                Vector256<byte> input = TStartCaseSensitivity.TransformInput(LoadAndPack32AsciiChars(ref searchSpace));

                (Vector256<byte> result, prev0) = ProcessInputN2(input, prev0, n0Low, n0High, n1Low, n1High);

                if (TryFindMatch(span, ref searchSpace, result, MatchStartOffsetN2, out int offset))
                {
                    return offset;
                }

                searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIterationAvx2);

                if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastVectorizedSearchSpace))
                {
                    if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastVectorizedSearchSpace, CharsPerIterationAvx2)))
                    {
                        return -1;
                    }

                    prev0 = Vector256<byte>.AllBitsSet;
                    searchSpace = ref lastVectorizedSearchSpace;
                }
            }
        }

        private int IndexOfAnyN3Vector128(ReadOnlySpan<char> span)
        {
            if (span.Length < CharsPerIterationVector128 + MatchStartOffsetN3)
            {
                return ShortInputFallback(span);
            }

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastVectorizedSearchSpace = ref Unsafe.Add(ref searchSpace, span.Length - CharsPerIterationVector128);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN3);

            Vector128<byte> n0Low = _n0Low, n0High = _n0High;
            Vector128<byte> n1Low = _n1Low, n1High = _n1High;
            Vector128<byte> n2Low = _n2Low, n2High = _n2High;
            Vector128<byte> prev0 = Vector128<byte>.AllBitsSet;
            Vector128<byte> prev1 = Vector128<byte>.AllBitsSet;

            while (true)
            {
                Vector128<byte> input = TStartCaseSensitivity.TransformInput(LoadAndPack16AsciiChars(ref searchSpace));

                (Vector128<byte> result, prev0, prev1) = ProcessInputN3(input, prev0, prev1, n0Low, n0High, n1Low, n1High, n2Low, n2High);

                if (TryFindMatch(span, ref searchSpace, result, MatchStartOffsetN3, out int offset))
                {
                    return offset;
                }

                searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIterationVector128);

                if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastVectorizedSearchSpace))
                {
                    if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastVectorizedSearchSpace, CharsPerIterationVector128)))
                    {
                        return -1;
                    }

                    prev0 = Vector128<byte>.AllBitsSet;
                    prev1 = Vector128<byte>.AllBitsSet;
                    searchSpace = ref lastVectorizedSearchSpace;
                }
            }
        }

        [BypassReadyToRun]
        private int IndexOfAnyN3Avx2(ReadOnlySpan<char> span)
        {
            Debug.Assert(span.Length >= CharsPerIterationAvx2 + MatchStartOffsetN3);

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastVectorizedSearchSpace = ref Unsafe.Add(ref searchSpace, span.Length - CharsPerIterationAvx2);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN3);

            Vector256<byte> n0Low = _n0Low256, n0High = _n0High256;
            Vector256<byte> n1Low = _n1Low256, n1High = _n1High256;
            Vector256<byte> n2Low = _n2Low256, n2High = _n2High256;
            Vector256<byte> prev0 = Vector256<byte>.AllBitsSet;
            Vector256<byte> prev1 = Vector256<byte>.AllBitsSet;

            while (true)
            {
                Vector256<byte> input = TStartCaseSensitivity.TransformInput(LoadAndPack32AsciiChars(ref searchSpace));

                (Vector256<byte> result, prev0, prev1) = ProcessInputN3(input, prev0, prev1, n0Low, n0High, n1Low, n1High, n2Low, n2High);

                if (TryFindMatch(span, ref searchSpace, result, MatchStartOffsetN3, out int offset))
                {
                    return offset;
                }

                searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIterationAvx2);

                if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastVectorizedSearchSpace))
                {
                    if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastVectorizedSearchSpace, CharsPerIterationAvx2)))
                    {
                        return -1;
                    }

                    prev0 = Vector256<byte>.AllBitsSet;
                    prev1 = Vector256<byte>.AllBitsSet;
                    searchSpace = ref lastVectorizedSearchSpace;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryFindMatch(ReadOnlySpan<char> span, ref char searchSpace, Vector128<byte> result, int matchStartOffset, out int offsetFromStart)
        {
            if (result != Vector128<byte>.Zero)
            {
                uint resultMask = (~Vector128.Equals(result, Vector128<byte>.Zero)).ExtractMostSignificantBits();

                do
                {
                    int matchOffset = BitOperations.TrailingZeroCount(resultMask);
                    resultMask = BitOperations.ResetLowestSetBit(resultMask);

                    ref char matchRef = ref Unsafe.Add(ref searchSpace, matchOffset - matchStartOffset);
                    offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref matchRef) / 2);
                    int lengthRemaining = span.Length - offsetFromStart;

                    uint candidateMask = result.GetElementUnsafe(matchOffset);

                    do
                    {
                        int candidateOffset = BitOperations.TrailingZeroCount(candidateMask);
                        candidateMask = BitOperations.ResetLowestSetBit(candidateMask);

                        object bucket = _buckets[candidateOffset];

                        if (TBucketized.Value
                            ? StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, Unsafe.As<string[]>(bucket))
                            : StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, Unsafe.As<string>(bucket)))
                        {
                            return true;
                        }
                    }
                    while (candidateMask != 0);
                }
                while (resultMask != 0);
            }

            offsetFromStart = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected bool TryFindMatch(ReadOnlySpan<char> span, ref char searchSpace, Vector256<byte> result, int matchStartOffset, out int offsetFromStart)
        {
            if (result != Vector256<byte>.Zero)
            {
                uint resultMask = (~Vector256.Equals(result, Vector256<byte>.Zero)).ExtractMostSignificantBits();

                do
                {
                    int matchOffset = BitOperations.TrailingZeroCount(resultMask);
                    resultMask = BitOperations.ResetLowestSetBit(resultMask);

                    ref char matchRef = ref Unsafe.Add(ref searchSpace, matchOffset - matchStartOffset);
                    offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref matchRef) / 2);
                    int lengthRemaining = span.Length - offsetFromStart;

                    uint candidateMask = result.GetElementUnsafe(matchOffset);

                    do
                    {
                        int candidateOffset = BitOperations.TrailingZeroCount(candidateMask);
                        candidateMask = BitOperations.ResetLowestSetBit(candidateMask);

                        object bucket = _buckets[candidateOffset];

                        if (TBucketized.Value
                            ? StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, Unsafe.As<string[]>(bucket))
                            : StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, Unsafe.As<string>(bucket)))
                        {
                            return true;
                        }
                    }
                    while (candidateMask != 0);
                }
                while (resultMask != 0);
            }

            offsetFromStart = 0;
            return false;
        }
    }
}
