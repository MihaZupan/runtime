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
using static System.Buffers.TeddyHelper;

namespace System.Buffers
{
    // This is an implementation of the "Teddy" vectorized multi-substring matching algorithm.
    //
    // We have several vectorized string searching approaches implemented as part of SearchValues, among them are:
    // - 'IndexOfAnyAsciiSearcher', which can quickly find the next position of any character in a set.
    // - 'SingleStringSearchValuesThreeChars', which can determine the likely positions where a value may start.
    // The fast scan for starting positions is followed by a verification step that rules out false positives.
    // To reduce the number of false positives, the initial scan looks for multiple characters at different positions,
    // and only considers candidates where all of those match at the same time.
    //
    // Teddy combines the two to search for multiple values at the same time.
    // Similar to 'SingleStringSearchValuesThreeChars', it employs the starting positions scan and verification steps.
    // To reduce the number of values we have to check during verification, it also checks multiple characters in the initial scan.
    // We could implement that by just merging the two approaches: check for any of the value characters at position 0, 1, 2, then
    // AND those results together and verify potential matches. The issue with this approach is that we would always have to check
    // all values in the verification step, and we would be hitting many false positives as the number of values increased.
    // For example, if you are searching for "Teddy" and "Bear", position 0 could be either 'T' or 'B', position 1 could be 'e',
    // and position 2 could be 'd' or 'a'. We would do separate comparisons for each of those positions and then AND together the result.
    // Because there is no correlation between the values, we would get false positives for inputs like "Bed" and "Tea",
    // and we wouldn't know whether the match location was because of "Teddy" or "Bear", and thus which to proceed to verify.
    //
    // What is special about Teddy is how we perform that initial scan to not only determine the possible starting locations,
    // but also which values are the potential matches at each of those offsets.
    // Instead of encoding all starting characters at a given position into a bitmap that can only answer yes/no whether a given
    // character is present in the set, we want to encode both the character and the values in which it appears.
    // We only have 128* bits to work with, so we do this by encoding 8 bits of information for each nibble (half byte).
    // Those 8 bits represent a bitmask of values that contain that nibble at that location.
    // If we compare the input against two such bitmaps and AND the results together, we can determine which positions in the input
    // contained a matching character, and which of our values matched said character at that position.
    // We repeat this a few more times (checking 3 bytes or 6 nibbles for N=3) at different offsets to reduce the number of false positives.
    // See 'TeddyBucketizer.GenerateNonBucketizedFingerprint' for details around how such a bitmap is constructed.
    //
    // For example if we are searching for strings "Teddy" and "Bear", we will look for 'T' or 'B' at position 0, 'e' at position 1, ...
    // To look for 'T' (0x54) or 'B' (0x42), we will check for a high nibble of 5 or 4, and lower nibble of 4 or 2.
    // Each value's presence is indicated by 1 bit. We will use 1 (0b00000001) for the first value ("Teddy") and 2 (0b00000010) for "Bear".
    // Our bitmaps will look like so (1 is set for high 5 and low 4, 2 is set for high 4 and low 2):
    // bitmapHigh: [0, 0, 0, 0, 2, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
    // bitmapLow:  [0, 0, 2, 0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]
    //              ^     ^  ^  ^
    //
    // To map an input nibble to its corresponding bitmask, we use 'Shuffle(bitmap, nibble)'.
    // For an input like "TeddyBearFactory", our result will be
    // input:      [T, e, d, d, y, B, e, a, r, F, a, c, t, o, r, y]
    // inputHigh:  [5, 6, 6, 6, 7, 4, 6, 6, 7, 4, 6, 6, 7, 6, 7, 7] (values in hex)
    // inputLow:   [4, 5, 4, 4, 9, 2, 5, 1, 2, 6, 1, 3, 4, F, 2, 9] (values in hex)
    // resultHigh: [1, 0, 0, 0, 0, 2, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0]
    // resultLow:  [1, 0, 1, 1, 0, 2, 0, 0, 2, 0, 0, 0, 1, 0, 2, 0]
    // result:     [1, 0, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0] (resultHigh & resultLow)
    //              ^              ^
    // Note how we had quite a few false positives for individual nibbles that we ruled away after checking both nibbles.
    // See 'TeddyHelper.ProcessInputN3' for details about how we combine results for multiple characters at different offsets.
    //
    // The description above states that we can only encode the information about 8 values. To get around that limitation
    // we group multiple values together into buckets. Instead of looking for positions where a single value may match,
    // we look for positions where any value from a given bucket may match.
    // When creating the bitmap we don't set the bit for just one nibble value, but for each of the values in that bucket.
    // For example if "Teddy" and "Bear" were both in the same bucket, the high nibble bitmap would map both 5 and 4 to the same bucket.
    // We may see more false positives ('R' (0x52) and 'D' (0x44) would now also map to the same bucket), but we get to search for
    // many more values at the same time. Instead of 8 values, we are now capable of looking for 8 buckets of values at the same time.
    // See 'TeddyBucketizer.Bucketize' for details about how values are grouped into buckets.
    // See 'TeddyBucketizer.GenerateBucketizedFingerprint' for details around how such a bitmap is constructed.
    //
    // Teddy works in terms of bytes, but .NET chars represent UTF-16 code units.
    // We currently only use Teddy if the 2 or 3 starting characters are all ASCII. This limitation could be lifted in the future if needed.
    // Since we know that all of the characters we are looking for are ASCII, we also know that only other ASCII characters will match against them.
    // Making use of that fact, we narrow UTF-16 code units into bytes when reading the input (see 'TeddyHelper.LoadAndPack16AsciiChars').
    // While such narrowing does corrupt non-ASCII values, they are all mapped to values outside of ASCII, so they won't match anyway.
    // ASCII values remain unaffected since their high byte in UTF-16 representation is 0.
    //
    // To handle case-insensitive matching, all values are normalized to their uppercase equivalents ahead of time and the bitmaps are
    // generated as if all characters were uppercase. During the search, the input is also transformed into uppercase before being compared.
    //
    // * With wider vectors (256- and 512-bit), we have more bits available, but we currently only duplicate the original 128 bits
    // and perform the search on more characters at a time. We could instead choose to encode more information per nibble to trade
    // the number of characters we check per loop iteration for fewer false positives we then have to rule out during the verification step.
    //
    // For an alternative description of the algorithm, see
    // https://github.com/BurntSushi/aho-corasick/blob/8d735471fc12f0ca570cead8e17342274fae6331/src/packed/teddy/README.md
    // Has an O(i * m) worst-case, with the expected time closer to O(n) for good bucket distributions.
    internal abstract class AsciiStringSearchValuesTeddyBase<TBucketized, TStartCaseSensitivity, TCaseSensitivity> : StringSearchValuesRabinKarp<TCaseSensitivity>
        where TBucketized : struct, SearchValues.IRuntimeConst
        where TStartCaseSensitivity : struct, ICaseSensitivity  // Refers to the characters being matched by Teddy
        where TCaseSensitivity : struct, ICaseSensitivity       // Refers to the rest of the value for the verification step
    {
        // We may be using N2 or N3 mode depending on whether we're checking 2 or 3 starting bytes for each bucket.
        // The result of ProcessInputN2 and ProcessInputN3 are offset by 1 and 2 positions respectively (MatchStartOffsetN2 and MatchStartOffsetN3).
        // See the full description of TeddyHelper.ProcessInputN3 for more details about why these constants exist.
        private const int MatchStartOffsetN2 = 1;
        private const int MatchStartOffsetN3 = 2;

        // We may have up to 8 buckets.
        // If we have <= 8 strings, the buckets will be the strings themselves, and TBucketized.Value will be false.
        // If we have more than 8, the buckets will be string[], and TBucketized.Value will be true.
        private readonly EightPackedReferences _buckets;

        private Vector512<byte>
            _n0Low, _n0High,
            _n1Low, _n1High,
            _n2Low, _n2High;

        protected AsciiStringSearchValuesTeddyBase(ReadOnlySpan<string> values, HashSet<string> uniqueValues, int n) : base(values, uniqueValues)
        {
            Debug.Assert(!TBucketized.Value);
            Debug.Assert(n is 2 or 3);

            _buckets = new EightPackedReferences(MemoryMarshal.CreateReadOnlySpan(
                ref Unsafe.As<string, object>(ref MemoryMarshal.GetReference(values)),
                values.Length));

            (_n0Low, _n0High) = TeddyBucketizer.GenerateNonBucketizedFingerprint(values, offset: 0);
            (_n1Low, _n1High) = TeddyBucketizer.GenerateNonBucketizedFingerprint(values, offset: 1);

            if (n == 3)
            {
                (_n2Low, _n2High) = TeddyBucketizer.GenerateNonBucketizedFingerprint(values, offset: 2);
            }
        }

        protected AsciiStringSearchValuesTeddyBase(string[][] buckets, ReadOnlySpan<string> values, HashSet<string> uniqueValues, int n) : base(values, uniqueValues)
        {
            Debug.Assert(TBucketized.Value);
            Debug.Assert(n is 2 or 3);

            _buckets = new EightPackedReferences(buckets);

            (_n0Low, _n0High) = TeddyBucketizer.GenerateBucketizedFingerprint(buckets, offset: 0);
            (_n1Low, _n1High) = TeddyBucketizer.GenerateBucketizedFingerprint(buckets, offset: 1);

            if (n == 3)
            {
                (_n2Low, _n2High) = TeddyBucketizer.GenerateBucketizedFingerprint(buckets, offset: 2);
            }
        }

        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd.Arm64))]
        protected int IndexOfAnyN2(ReadOnlySpan<char> span)
        {
            // The behavior of the rest of the function remains the same if Avx2 or Avx512BW aren't supported
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported && span.Length >= Vector512<byte>.Count + MatchStartOffsetN2)
            {
                return IndexOfAnyN2Core<Vector512<byte>>(span);
            }

            if (Avx2.IsSupported && span.Length >= Vector256<byte>.Count + MatchStartOffsetN2)
            {
                return IndexOfAnyN2Core<Vector256<byte>>(span);
            }

            return IndexOfAnyN2Core<Vector128<byte>>(span);
        }

        [CompExactlyDependsOn(typeof(Ssse3))]
        [CompExactlyDependsOn(typeof(AdvSimd.Arm64))]
        protected int IndexOfAnyN3(ReadOnlySpan<char> span)
        {
            // The behavior of the rest of the function remains the same if Avx2 or Avx512BW aren't supported
            if (Vector512.IsHardwareAccelerated && Avx512BW.IsSupported && span.Length >= Vector512<byte>.Count + MatchStartOffsetN3)
            {
                return IndexOfAnyN3Core<Vector512<byte>>(span);
            }

            if (Avx2.IsSupported && span.Length >= Vector256<byte>.Count + MatchStartOffsetN3)
            {
                return IndexOfAnyN3Core<Vector256<byte>>(span);
            }

            return IndexOfAnyN3Core<Vector128<byte>>(span);
        }

        private int IndexOfAnyN2Core<TVector>(ReadOnlySpan<char> span)
            where TVector : struct, ISimdVector<TVector, byte>
        {
            // See comments in 'IndexOfAnyN3Vector128' below.
            // This method is the same, but compares 2 starting chars instead of 3.
            if (typeof(TVector) == typeof(Vector128<byte>) && span.Length < Vector128<byte>.Count + MatchStartOffsetN2)
            {
                return ShortInputFallback(span);
            }

            Debug.Assert(span.Length >= TVector.Count + MatchStartOffsetN2);

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastSearchSpaceStart = ref Unsafe.Add(ref searchSpace, span.Length - TVector.Count);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN2);

            TVector n0Low = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n0Low));
            TVector n0High = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n0High));
            TVector n1Low = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n1Low));
            TVector n1High = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n1High));
            TVector prev0 = TVector.AllBitsSet;

        Loop:
            ValidateReadPosition(span, ref searchSpace);
            TVector input = TStartCaseSensitivity.TransformInput(LoadAndPackAsciiChars<TVector>(ref searchSpace));

            (TVector result, prev0) = ProcessInputN2(input, prev0, n0Low, n0High, n1Low, n1High);

            if (result != TVector.Zero)
            {
                goto CandidateFound;
            }

        ContinueLoop:
            searchSpace = ref Unsafe.Add(ref searchSpace, TVector.Count);

            if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpaceStart))
            {
                if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpaceStart, TVector.Count)))
                {
                    return -1;
                }

                // We're switching which characters we will process in the next iteration.
                // prev0 no longer points to the characters just before the current input, so we must reset it.
                prev0 = TVector.AllBitsSet;
                searchSpace = ref lastSearchSpaceStart;
            }
            goto Loop;

        CandidateFound:
            if (TryFindMatch(span, ref searchSpace, result, MatchStartOffsetN2, out int offset))
            {
                return offset;
            }
            goto ContinueLoop;
        }

        private int IndexOfAnyN3Core<TVector>(ReadOnlySpan<char> span)
            where TVector : struct, ISimdVector<TVector, byte>
        {
            if (typeof(TVector) == typeof(Vector128<byte>) && span.Length < Vector128<byte>.Count + MatchStartOffsetN3)
            {
                return ShortInputFallback(span);
            }

            Debug.Assert(span.Length >= TVector.Count + MatchStartOffsetN3);

            ref char searchSpace = ref MemoryMarshal.GetReference(span);
            ref char lastSearchSpaceStart = ref Unsafe.Add(ref searchSpace, span.Length - TVector.Count);

            searchSpace = ref Unsafe.Add(ref searchSpace, MatchStartOffsetN3);

            TVector n0Low = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n0Low));
            TVector n0High = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n0High));
            TVector n1Low = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n1Low));
            TVector n1High = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n1High));
            TVector n2Low = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n2Low));
            TVector n2High = TVector.LoadUnsafe(ref Unsafe.As<Vector512<byte>, byte>(ref _n2High));

            // As matching is offset by 2 positions (MatchStartOffsetN3), we must remember the result of the previous loop iteration.
            // See the full description of TeddyHelper.ProcessInputN3 for more details about why these exist.
            // When doing the first loop iteration, there is no previous iteration, so we have to assume that the input did match (AllBitsSet)
            // for those positions. This makes it more likely to hit a false-positive at the very beginning, but TryFindMatch will discard them.
            TVector prev0 = TVector.AllBitsSet;
            TVector prev1 = TVector.AllBitsSet;

        Loop:
            // Load the input characters and normalize them to their uppercase variant if we're ignoring casing.
            // These characters may not be ASCII, but we know that the starting 3 characters of each value are.
            ValidateReadPosition(span, ref searchSpace);
            TVector input = TStartCaseSensitivity.TransformInput(LoadAndPackAsciiChars<TVector>(ref searchSpace));

            // Find which buckets contain potential matches for each input position.
            // For a bucket to be marked as a potential match, its fingerprint must match for all 3 starting characters (all 6 nibbles).
            (TVector result, prev0, prev1) = ProcessInputN3(input, prev0, prev1, n0Low, n0High, n1Low, n1High, n2Low, n2High);

            if (result != TVector.Zero)
            {
                goto CandidateFound;
            }

        ContinueLoop:
            // We haven't found a match. Update the input position and check if we've reached the end.
            searchSpace = ref Unsafe.Add(ref searchSpace, TVector.Count);

            if (Unsafe.IsAddressGreaterThan(ref searchSpace, ref lastSearchSpaceStart))
            {
                if (Unsafe.AreSame(ref searchSpace, ref Unsafe.Add(ref lastSearchSpaceStart, TVector.Count)))
                {
                    return -1;
                }

                // We're switching which characters we will process in the next iteration.
                // prev0 and prev1 no longer point to the characters just before the current input, so we must reset them.
                // Just like with the first iteration, we must assume that these positions did match (AllBitsSet).
                prev0 = TVector.AllBitsSet;
                prev1 = TVector.AllBitsSet;
                searchSpace = ref lastSearchSpaceStart;
            }
            goto Loop;

        CandidateFound:
            // We found potential matches, but they may be false-positives, so we must verify each one.
            if (TryFindMatch(span, ref searchSpace, result, MatchStartOffsetN3, out int offset))
            {
                return offset;
            }
            goto ContinueLoop;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFindMatch<TVector>(ReadOnlySpan<char> span, ref char searchSpace, TVector result, int matchStartOffset, out int offsetFromStart)
            where TVector : struct, ISimdVector<TVector, byte>
        {
            if (typeof(TVector) == typeof(Vector512<byte>))
            {
                return TryFindMatch(span, ref searchSpace, Unsafe.BitCast<TVector, Vector512<byte>>(result), matchStartOffset, out offsetFromStart);
            }

            // 'resultMask' encodes the input positions where at least one bucket may contain a match.
            // These positions are offset by 'matchStartOffset' places.
            result = ~TVector.Equals(result, TVector.Zero);

            uint resultMask = typeof(TVector) == typeof(Vector128<byte>)
                ? Unsafe.BitCast<TVector, Vector128<byte>>(result).ExtractMostSignificantBits()
                : Unsafe.BitCast<TVector, Vector256<byte>>(result).ExtractMostSignificantBits();

            do
            {
                int matchOffset = BitOperations.TrailingZeroCount(resultMask);

                // Calculate where in the input span this potential match begins.
                ref char matchRef = ref Unsafe.Add(ref searchSpace, matchOffset - matchStartOffset);
                offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref matchRef) / 2);
                int lengthRemaining = span.Length - offsetFromStart;

                ValidateReadPosition(span, ref matchRef, lengthRemaining);

                // 'candidateMask' encodes which buckets contain potential matches, starting at 'matchRef'.
                uint candidateMask = typeof(TVector) == typeof(Vector128<byte>)
                    ? Unsafe.BitCast<TVector, Vector128<byte>>(result).GetElementUnsafe(matchOffset)
                    : Unsafe.BitCast<TVector, Vector256<byte>>(result).GetElementUnsafe(matchOffset);

                do
                {
                    // Verify each bucket to see if we've found a match.
                    int candidateOffset = BitOperations.TrailingZeroCount(candidateMask);

                    object? bucket = _buckets[candidateOffset];
                    Debug.Assert(bucket is not null);

                    if (TBucketized.Value
                        ? StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, Unsafe.As<string[]>(bucket))
                        : StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, Unsafe.As<string>(bucket)))
                    {
                        return true;
                    }

                    candidateMask = BitOperations.ResetLowestSetBit(candidateMask);
                }
                while (candidateMask != 0);

                resultMask = BitOperations.ResetLowestSetBit(resultMask);
            }
            while (resultMask != 0);

            offsetFromStart = 0;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private bool TryFindMatch(ReadOnlySpan<char> span, ref char searchSpace, Vector512<byte> result, int matchStartOffset, out int offsetFromStart)
        {
            // See comments in 'TryFindMatch' for Vector128<byte> above.
            // This method is the same, but checks the potential matches for 64 input positions.
            ulong resultMask = (~Vector512.Equals(result, Vector512<byte>.Zero)).ExtractMostSignificantBits();

            do
            {
                int matchOffset = BitOperations.TrailingZeroCount(resultMask);

                ref char matchRef = ref Unsafe.Add(ref searchSpace, matchOffset - matchStartOffset);
                offsetFromStart = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref matchRef) / 2);
                int lengthRemaining = span.Length - offsetFromStart;

                ValidateReadPosition(span, ref matchRef, lengthRemaining);

                uint candidateMask = result.GetElementUnsafe(matchOffset);

                do
                {
                    int candidateOffset = BitOperations.TrailingZeroCount(candidateMask);

                    object? bucket = _buckets[candidateOffset];
                    Debug.Assert(bucket is not null);

                    if (TBucketized.Value
                        ? StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, Unsafe.As<string[]>(bucket))
                        : StartsWith<TCaseSensitivity>(ref matchRef, lengthRemaining, Unsafe.As<string>(bucket)))
                    {
                        return true;
                    }

                    candidateMask = BitOperations.ResetLowestSetBit(candidateMask);
                }
                while (candidateMask != 0);

                resultMask = BitOperations.ResetLowestSetBit(resultMask);
            }
            while (resultMask != 0);

            offsetFromStart = 0;
            return false;
        }
    }
}
