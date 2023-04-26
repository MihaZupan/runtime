// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static System.Buffers.TeddyHelper;

namespace System.Buffers
{
    internal sealed class IndexOfAnyAsciiStringValuesTeddy256BucketizedN3<TStartCaseSensitivity, TCaseSensitivity> : IndexOfAnyStringValuesRabinKarp<TCaseSensitivity>
        where TStartCaseSensitivity : struct, ICaseSensitivity
        where TCaseSensitivity : struct, ICaseSensitivity
    {
        private const int MatchStartOffset = 2;
        private const int CharsPerIteration = 32;

        private readonly Vector256<byte>
            _n0Low, _n0High,
            _n1Low, _n1High,
            _n2Low, _n2High;

        private readonly EightPackedReferences<string[]> _valueBuckets;

        public IndexOfAnyAsciiStringValuesTeddy256BucketizedN3(string[][] buckets, RabinKarp rabinKarp, HashSet<string> uniqueValues) : base(rabinKarp, uniqueValues)
        {
            _valueBuckets = new EightPackedReferences<string[]>(buckets);

            (_n0Low, _n0High) = GenerateBucketizedFingerprint256(buckets, offset: 0);
            (_n1Low, _n1High) = GenerateBucketizedFingerprint256(buckets, offset: 1);
            (_n2Low, _n2High) = GenerateBucketizedFingerprint256(buckets, offset: 2);
        }

        [BypassReadyToRun]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span)
        {
            ref char searchSpace = ref MemoryMarshal.GetReference(span);

            if (span.Length >= CharsPerIteration + MatchStartOffset)
            {
                ref char lastVectorizedSearchSpace = ref Unsafe.Add(ref searchSpace, span.Length - CharsPerIteration);

                // The first MatchStartOffset characters are assumed to have matched in the previous round.
                // This may result in a false positive in the first loop iteration that the verification step will rule out.
                searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffset);

                Vector256<byte> n0Low = _n0Low, n0High = _n0High;
                Vector256<byte> n1Low = _n1Low, n1High = _n1High;
                Vector256<byte> n2Low = _n2Low, n2High = _n2High;
                Vector256<byte> prev0 = Vector256<byte>.AllBitsSet;
                Vector256<byte> prev1 = Vector256<byte>.AllBitsSet;

                while (true)
                {
                    if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastVectorizedSearchSpace))
                    {
                        break;
                    }

                    Vector256<byte> input = LoadAndPack32AsciiChars(ref searchSpace);
                    input = TStartCaseSensitivity.TransformInput(input);

                    (Vector256<byte> result, prev0, prev1) = ProcessInputN3(input, prev0, prev1, n0Low, n0High, n1Low, n1High, n2Low, n2High);

                    if (result == Vector256<byte>.Zero)
                    {
                        searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIteration);
                        continue;
                    }

                    uint resultMask = (~Vector256.Equals(result, Vector256<byte>.Zero)).ExtractMostSignificantBits();

                    do
                    {
                        int matchOffset = BitOperations.TrailingZeroCount(resultMask);
                        resultMask = BitOperations.ResetLowestSetBit(resultMask);

                        ref char matchRef = ref Unsafe.Add(ref searchSpace, matchOffset - MatchStartOffset);
                        int offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref matchRef) / 2);
                        int lengthRemaining = span.Length - offsetFromStart;

                        uint candidateMask = result.GetElementUnsafe(matchOffset);

                        do
                        {
                            int candidateOffset = BitOperations.TrailingZeroCount(candidateMask);
                            candidateMask = BitOperations.ResetLowestSetBit(candidateMask);

                            if (StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, _valueBuckets[candidateOffset]))
                            {
                                return offsetFromStart;
                            }
                        }
                        while (candidateMask != 0);
                    }
                    while (resultMask != 0);

                    searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIteration);
                }

                searchSpace = ref Unsafe.Subtract(ref searchSpace, MatchStartOffset);
            }

            return ShortInputFallback(span, ref searchSpace);
        }
    }
}
