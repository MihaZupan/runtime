// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using static System.Buffers.TeddyHelper;

namespace System.Buffers
{
    internal sealed class IndexOfAnyAsciiStringValuesTeddy128NonBucketizedN2<TCaseSensitivity> : IndexOfAnyStringValuesRabinKarp<TCaseSensitivity>
        where TCaseSensitivity : struct, ICaseSensitivity
    {
        private const int MatchStartOffset = 1;
        private const int CharsPerIteration = 16;

        private readonly Vector128<byte>
            _n0Low, _n0High,
            _n1Low, _n1High;

        private readonly EightPackedReferences<string> _values;

        public IndexOfAnyAsciiStringValuesTeddy128NonBucketizedN2(string[] values, RabinKarp rabinKarp, HashSet<string> uniqueValues) : base(rabinKarp, uniqueValues)
        {
            _values = new EightPackedReferences<string>(values);

            (_n0Low, _n0High) = GenerateNonBucketizedFingerprint(values, offset: 0);
            (_n1Low, _n1High) = GenerateNonBucketizedFingerprint(values, offset: 1);
        }

        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span)
        {
            ref char searchSpace = ref MemoryMarshal.GetReference(span);

            if (span.Length >= CharsPerIteration + MatchStartOffset)
            {
                ref char lastVectorizedSearchSpace = ref Unsafe.Add(ref searchSpace, span.Length - CharsPerIteration);

                // The first MatchStartOffset characters are assumed to have matched in the previous round.
                // This may result in a false positive in the first loop iteration that the verification step will rule out.
                searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffset);

                Vector128<byte> n0Low = _n0Low, n0High = _n0High;
                Vector128<byte> n1Low = _n1Low, n1High = _n1High;
                Vector128<byte> prev0 = Vector128<byte>.AllBitsSet;

                while (true)
                {
                    if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastVectorizedSearchSpace))
                    {
                        break;
                    }

                    Vector128<byte> input = LoadAndPack16AsciiChars(ref searchSpace);
                    input = TCaseSensitivity.TransformInput(input);

                    (Vector128<byte> result, prev0) = ProcessInputN2(input, prev0, n0Low, n0High, n1Low, n1High);

                    if (result == Vector128<byte>.Zero)
                    {
                        searchSpace = ref Unsafe.Add(ref searchSpace, CharsPerIteration);
                        continue;
                    }

                    uint resultMask = (~Vector128.Equals(result, Vector128<byte>.Zero)).ExtractMostSignificantBits();

                    do
                    {
                        int matchOffset = BitOperations.TrailingZeroCount(resultMask);
                        resultMask = BitOperations.ResetLowestSetBit(resultMask);

                        ref char matchRef = ref Unsafe.Add(ref searchSpace, matchOffset - MatchStartOffset);
                        int offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref matchRef) / 2);
                        int lengthRemaining = span.Length - offsetFromStart;

                        uint candidateMask = result.GetElement(matchOffset);

                        do
                        {
                            int candidateOffset = BitOperations.TrailingZeroCount(candidateMask);
                            candidateMask = BitOperations.ResetLowestSetBit(candidateMask);

                            Debug.Assert(candidateOffset is >= 0 and < 8);
                            string candidate = _values[candidateOffset];

                            if (StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, candidate))
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

            return ShortInputRabinKarpFallback(span, ref searchSpace);
        }
    }
}
