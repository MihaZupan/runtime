// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;

namespace System.Buffers
{
    internal static class TeddyHelper
    {
        // https://github.com/jneem/teddy/blob/9ab5e899ad6ef6911aecd3cf1033f1abe6e1f66c/src/x86/mask.rs#L215-L262
        // TODO: Rewrite this?
        // TODO: Does this actually work to produce a decent bucket distribution?
        private static class Bucketizer
        {
            private sealed record Fingerprint((uint High, uint Low)[] Nibbles)
            {
                public void AddString(string s)
                {
                    for (int i = 0; i < Nibbles.Length; i++)
                    {
                        Debug.Assert(char.IsAscii(s[i]));
                        byte b = (byte)s[i];

                        (uint high, uint low) = Nibbles[i];
                        Nibbles[i] = (high | (1u << (b >> 4)), low | (1u << (b & 0xF)));
                    }
                }

                public int Len()
                {
                    int product = 1;
                    foreach ((uint high, uint low) in Nibbles)
                    {
                        product *= BitOperations.PopCount(high);
                        product *= BitOperations.PopCount(low);
                    }
                    return product;
                }

                public int IntersectionSize(Fingerprint other)
                {
                    int product = 1;
                    for (int i = 0; i < Nibbles.Length; i++)
                    {
                        product *= BitOperations.PopCount(Nibbles[i].High & other.Nibbles[i].High);
                        product *= BitOperations.PopCount(Nibbles[i].Low & other.Nibbles[i].Low);
                    }
                    return product;
                }

                public int CoverSize(Fingerprint other)
                {
                    int product = 1;
                    for (int i = 0; i < Nibbles.Length; i++)
                    {
                        product *= BitOperations.PopCount(Nibbles[i].High | other.Nibbles[i].High);
                        product *= BitOperations.PopCount(Nibbles[i].Low | other.Nibbles[i].Low);
                    }
                    return product;
                }

                public void Include(Fingerprint other)
                {
                    for (int i = 0; i < Nibbles.Length; i++)
                    {
                        Nibbles[i] = (Nibbles[i].High | other.Nibbles[i].High, Nibbles[i].Low | other.Nibbles[i].Low);
                    }
                }
            }

            private sealed record Bucket(List<string> Values, Fingerprint Fingerprint)
            {
                public void AddString(string s)
                {
                    Values.Add(s);
                    Fingerprint.AddString(s);
                }

                public Penalty MergePenalty(Bucket other)
                {
                    int oldSize = Fingerprint.Len() + other.Fingerprint.Len() - Fingerprint.IntersectionSize(other.Fingerprint);
                    int newSize = Fingerprint.CoverSize(other.Fingerprint);
                    return new Penalty(newSize - oldSize, newSize);
                }

                public void Merge(Bucket other)
                {
                    Values.AddRange(other.Values);
                    Fingerprint.Include(other.Fingerprint);
                }
            }

            private record struct Penalty(int Difference, int NewSize);

            private static void MergeOneBucket(List<Bucket> buckets)
            {
                (int i, int j) bestPair = new(int.MaxValue, int.MaxValue);
                Penalty bestPenalty = new(int.MaxValue, int.MaxValue);

                for (int i = 0; i < buckets.Count; i++)
                {
                    for (int j = i + 1; j < buckets.Count; j++)
                    {
                        var penalty = buckets[i].MergePenalty(buckets[j]);

                        if (penalty.Difference < bestPenalty.Difference ||
                            (penalty.Difference == bestPenalty.Difference && penalty.NewSize < bestPenalty.NewSize))
                        {
                            bestPenalty = penalty;
                            bestPair = (i, j);
                        }
                    }
                }

                Bucket b2 = buckets[bestPair.j];
                buckets.RemoveAt(bestPair.j);
                buckets[bestPair.i].Merge(b2);
            }

            public static string[][] GatherBuckets(ReadOnlySpan<string> values, int bucketCount, int n)
            {
                Dictionary<long, List<string>> initialBuckets = new();

                foreach (string value in values)
                {
                    long fingerprint = 0;
                    for (int i = 0; i < n; i++)
                    {
                        fingerprint = (fingerprint << 16) | value[i];
                    }

                    List<string> valuesWithSharedPrefix = CollectionsMarshal.GetValueRefOrAddDefault(initialBuckets, fingerprint, out _) ??= new();
                    valuesWithSharedPrefix.Add(value);
                }

                List<Bucket> buckets = new(initialBuckets.Count);
                foreach (var b in initialBuckets)
                {
                    var fp = new Fingerprint(new (uint High, uint Low)[n]);

                    var newBucket = new Bucket(new List<string>(), fp);

                    foreach (string p in b.Value)
                    {
                        newBucket.AddString(p);
                    }

                    buckets.Add(newBucket);
                }

                while (buckets.Count > bucketCount)
                {
                    MergeOneBucket(buckets);
                }

                string[][] finalBuckets = new string[buckets.Count][];
                for (int i = 0; i < finalBuckets.Length; i++)
                {
                    finalBuckets[i] = buckets[i].Values.ToArray();
                }

                return finalBuckets;
            }
        }

        public static string[][] Bucketize(ReadOnlySpan<string> values, int bucketCount, int n)
        {
            Debug.Assert(bucketCount == 8, "This may change if we end up supporting the 'fat Teddy' variant.");
            Debug.Assert(values.Length > bucketCount, "Should be using a non-bucketized implementation.");

            string[][] buckets = Bucketizer.GatherBuckets(values, bucketCount, n);
            Debug.Assert(buckets.Length <= bucketCount);

            return buckets;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool StartsWith<TCaseSensitivity>(ref char matchStart, int lengthRemaining, string[] candidates)
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            foreach (string candidate in candidates)
            {
                if (StartsWith<TCaseSensitivity>(ref matchStart, lengthRemaining, candidate))
                {
                    return true;
                }
            }

            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool StartsWith<TCaseSensitivity>(ref char matchStart, int lengthRemaining, string candidate)
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            // TODO: For non-first iterations in the non-bucketized path, we don't actually have to check the first
            // 2-3 characters as they are guaranteed to match. Can we make use of that fact to improve this at all?

            if (lengthRemaining < candidate.Length)
            {
                return false;
            }

            return TCaseSensitivity.Equals(ref matchStart, candidate);
        }

        // TODO: When we're dealing with ASCII-only patterns, can we get any use out of the spare bits (e.g. more buckets)?
        public static (Vector128<byte> Low, Vector128<byte> High) GenerateNonBucketizedFingerprint(ReadOnlySpan<string> values, int offset)
        {
            Debug.Assert(values.Length <= 8);

            Vector128<byte> low = default;
            Vector128<byte> high = default;

            for (int i = 0; i < values.Length; i++)
            {
                string value = values[i];

                int bit = 1 << i;

                char c = value[offset];
                Debug.Assert(char.IsAscii(c));

                int lowNibble = c & 0xF;
                int highNibble = c >> 4;

                low.SetElementUnsafe(lowNibble, (byte)(low.GetElementUnsafe(lowNibble) | bit));
                high.SetElementUnsafe(highNibble, (byte)(high.GetElementUnsafe(highNibble) | bit));
            }

            return (low, high);
        }

        public static (Vector128<byte> Low, Vector128<byte> High) GenerateBucketizedFingerprint(string[][] valueBuckets, int offset)
        {
            Debug.Assert(valueBuckets.Length <= 8);

            Vector128<byte> low = default;
            Vector128<byte> high = default;

            for (int i = 0; i < valueBuckets.Length; i++)
            {
                int bit = 1 << i;

                foreach (string value in valueBuckets[i])
                {
                    char c = value[offset];
                    Debug.Assert(char.IsAscii(c));

                    int lowNibble = c & 0xF;
                    int highNibble = c >> 4;

                    low.SetElementUnsafe(lowNibble, (byte)(low.GetElementUnsafe(lowNibble) | bit));
                    high.SetElementUnsafe(highNibble, (byte)(high.GetElementUnsafe(highNibble) | bit));
                }
            }

            return (low, high);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (Vector128<byte> Result, Vector128<byte> Prev0) ProcessInputN2(
            Vector128<byte> input,
            Vector128<byte> prev0,
            Vector128<byte> n0Low, Vector128<byte> n0High,
            Vector128<byte> n1Low, Vector128<byte> n1High)
        {
            (Vector128<byte> low, Vector128<byte> high) = GetNibbles(input);

            Vector128<byte> match0 = Shuffle(n0Low, n0High, low, high);
            Vector128<byte> result1 = Shuffle(n1Low, n1High, low, high);

            Vector128<byte> result0 = RightShift1(prev0, match0);

            Vector128<byte> result = result0 & result1;

            return (result, match0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BypassReadyToRun]
        public static (Vector256<byte> Result, Vector256<byte> Prev0) ProcessInputN2(
            Vector256<byte> input,
            Vector256<byte> prev0,
            Vector256<byte> n0Low, Vector256<byte> n0High,
            Vector256<byte> n1Low, Vector256<byte> n1High)
        {
            (Vector256<byte> low, Vector256<byte> high) = GetNibbles(input);

            Vector256<byte> match0 = Shuffle(n0Low, n0High, low, high);
            Vector256<byte> result1 = Shuffle(n1Low, n1High, low, high);

            Vector256<byte> result0 = RightShift1(prev0, match0);

            Vector256<byte> result = result0 & result1;

            return (result, match0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (Vector128<byte> Result, Vector128<byte> Prev0, Vector128<byte> Prev1) ProcessInputN3(
            Vector128<byte> input,
            Vector128<byte> prev0, Vector128<byte> prev1,
            Vector128<byte> n0Low, Vector128<byte> n0High,
            Vector128<byte> n1Low, Vector128<byte> n1High,
            Vector128<byte> n2Low, Vector128<byte> n2High)
        {
            (Vector128<byte> low, Vector128<byte> high) = GetNibbles(input);

            Vector128<byte> match0 = Shuffle(n0Low, n0High, low, high);
            Vector128<byte> match1 = Shuffle(n1Low, n1High, low, high);
            Vector128<byte> result2 = Shuffle(n2Low, n2High, low, high);

            Vector128<byte> result0 = RightShift2(prev0, match0);
            Vector128<byte> result1 = RightShift1(prev1, match1);

            Vector128<byte> result = result0 & result1 & result2;

            return (result, match0, match1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BypassReadyToRun]
        public static (Vector256<byte> Result, Vector256<byte> Prev0, Vector256<byte> Prev1) ProcessInputN3(
            Vector256<byte> input,
            Vector256<byte> prev0, Vector256<byte> prev1,
            Vector256<byte> n0Low, Vector256<byte> n0High,
            Vector256<byte> n1Low, Vector256<byte> n1High,
            Vector256<byte> n2Low, Vector256<byte> n2High)
        {
            (Vector256<byte> low, Vector256<byte> high) = GetNibbles(input);

            Vector256<byte> match0 = Shuffle(n0Low, n0High, low, high);
            Vector256<byte> match1 = Shuffle(n1Low, n1High, low, high);
            Vector256<byte> result2 = Shuffle(n2Low, n2High, low, high);

            Vector256<byte> result0 = RightShift2(prev0, match0);
            Vector256<byte> result1 = RightShift1(prev1, match1);

            Vector256<byte> result = result0 & result1 & result2;

            return (result, match0, match1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (Vector128<byte> Result, Vector128<byte> Prev0) ProcessSingleInputN2(
            Vector128<byte> input, Vector128<byte> prev0,
            Vector128<byte> test0, Vector128<byte> test1)
        {
            Vector128<byte> match0 = Vector128.Equals(input, test0);
            Vector128<byte> result1 = Vector128.Equals(input, test1);

            Vector128<byte> result0 = RightShift1(prev0, match0);

            Vector128<byte> result = result0 & result1;

            return (result, match0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BypassReadyToRun]
        public static (Vector256<byte> Result, Vector256<byte> Prev0) ProcessSingleInputN2(
            Vector256<byte> input, Vector256<byte> prev0,
            Vector256<byte> test0, Vector256<byte> test1)
        {
            Vector256<byte> match0 = Vector256.Equals(input, test0);
            Vector256<byte> result1 = Vector256.Equals(input, test1);

            Vector256<byte> result0 = RightShift1(prev0, match0);

            Vector256<byte> result = result0 & result1;

            return (result, match0);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static (Vector128<byte> Result, Vector128<byte> Prev0, Vector128<byte> Prev1) ProcessSingleInputN3(
            Vector128<byte> input, Vector128<byte> prev0, Vector128<byte> prev1,
            Vector128<byte> test0, Vector128<byte> test1, Vector128<byte> test2)
        {
            Vector128<byte> match0 = Vector128.Equals(input, test0);
            Vector128<byte> match1 = Vector128.Equals(input, test1);
            Vector128<byte> result2 = Vector128.Equals(input, test2);

            Vector128<byte> result0 = RightShift2(prev0, match0);
            Vector128<byte> result1 = RightShift1(prev1, match1);

            Vector128<byte> result = result0 & result1 & result2;

            return (result, match0, match1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BypassReadyToRun]
        public static (Vector256<byte> Result, Vector256<byte> Prev0, Vector256<byte> Prev1) ProcessSingleInputN3(
            Vector256<byte> input, Vector256<byte> prev0, Vector256<byte> prev1,
            Vector256<byte> test0, Vector256<byte> test1, Vector256<byte> test2)
        {
            Vector256<byte> match0 = Vector256.Equals(input, test0);
            Vector256<byte> match1 = Vector256.Equals(input, test1);
            Vector256<byte> result2 = Vector256.Equals(input, test2);

            Vector256<byte> result0 = RightShift2(prev0, match0);
            Vector256<byte> result1 = RightShift1(prev1, match1);

            Vector256<byte> result = result0 & result1 & result2;

            return (result, match0, match1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Vector128<byte> LoadAndPack16AsciiChars(ref char source)
        {
            Vector128<ushort> source0 = Vector128.LoadUnsafe(ref source);
            Vector128<ushort> source1 = Vector128.LoadUnsafe(ref source, (nuint)Vector128<ushort>.Count);

            return Sse2.IsSupported
                ? Sse2.PackUnsignedSaturate(source0.AsInt16(), source1.AsInt16())
                : AdvSimd.ExtractNarrowingSaturateUpper(AdvSimd.ExtractNarrowingSaturateLower(source0), source1);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BypassReadyToRun]
        public static Vector256<byte> LoadAndPack32AsciiChars(ref char source)
        {
            Vector256<ushort> source0 = Vector256.LoadUnsafe(ref source);
            Vector256<ushort> source1 = Vector256.LoadUnsafe(ref source, (nuint)Vector256<ushort>.Count);

            Vector256<byte> packed = Avx2.PackUnsignedSaturate(source0.AsInt16(), source1.AsInt16());

            return Avx2.Permute4x64(packed.AsInt64(), 0b_11_01_10_00).AsByte();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static (Vector128<byte> Low, Vector128<byte> High) GetNibbles(Vector128<byte> input)
        {
            // 'low' is not strictly correct here, but we take advantage of Ssse3.Shuffle's behavior
            // of doing an implicit 'AND 0xF' in order to skip the redundant AND.
            Vector128<byte> low = Ssse3.IsSupported
                ? input
                : input & Vector128.Create((byte)0xF);

            // X86 doesn't have a logical right shift intrinsic for bytes: https://github.com/dotnet/runtime/issues/82564
            Vector128<byte> high = AdvSimd.IsSupported
                ? AdvSimd.ShiftRightLogical(input, 4)
                : (input.AsInt32() >>> 4).AsByte() & Vector128.Create((byte)0xF);

            return (low, high);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BypassReadyToRun]
        private static (Vector256<byte> Low, Vector256<byte> High) GetNibbles(Vector256<byte> input)
        {
            // 'low' is not strictly correct here, but we take advantage of Avx2.Shuffle's behavior
            // of doing an implicit 'AND 0xF' in order to skip the redundant AND.
            Vector256<byte> low = input;

            // X86 doesn't have a logical right shift intrinsic for bytes: https://github.com/dotnet/runtime/issues/82564
            Vector256<byte> high = (input.AsInt32() >>> 4).AsByte() & Vector256.Create((byte)0xF);

            return (low, high);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> Shuffle(Vector128<byte> maskLow, Vector128<byte> maskHigh, Vector128<byte> low, Vector128<byte> high)
        {
            return Vector128.ShuffleUnsafe(maskLow, low) & Vector128.ShuffleUnsafe(maskHigh, high);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BypassReadyToRun]
        private static Vector256<byte> Shuffle(Vector256<byte> maskLow, Vector256<byte> maskHigh, Vector256<byte> low, Vector256<byte> high)
        {
            return Avx2.Shuffle(maskLow, low) & Avx2.Shuffle(maskHigh, high);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> RightShift1(Vector128<byte> left, Vector128<byte> right)
        {
            // Given input vectors like
            // left:   [ 0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15]
            // right:  [16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31]
            // We want to shift the last element of left (15) to be the first element of the result
            // result: [15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30]

            if (Ssse3.IsSupported)
            {
                return Ssse3.AlignRight(right, left, 15);
            }
            else
            {
                // TODO: Can we do better?
                Vector128<byte> leftShifted = Vector128.Shuffle(left, Vector128.Create(15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).AsByte());
                return AdvSimd.Arm64.VectorTableLookupExtension(leftShifted, right, Vector128.Create(0xFF, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static Vector128<byte> RightShift2(Vector128<byte> left, Vector128<byte> right)
        {
            // Given input vectors like
            // left:   [ 0,  1,  2,  3,  4,  5,  6,  7,  8,  9, 10, 11, 12, 13, 14, 15]
            // right:  [16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29, 30, 31]
            // We want to shift the last two elements of left (14, 15) to be the first elements of the result
            // result: [14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26, 27, 28, 29]

            if (Ssse3.IsSupported)
            {
                return Ssse3.AlignRight(right, left, 14);
            }
            else
            {
                // TODO: Can we do better?
                Vector128<byte> leftShifted = Vector128.Shuffle(left, Vector128.Create(14, 15, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0).AsByte());
                return AdvSimd.Arm64.VectorTableLookupExtension(leftShifted, right, Vector128.Create(0xFF, 0xFF, 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13));
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BypassReadyToRun]
        private static Vector256<byte> RightShift1(Vector256<byte> left, Vector256<byte> right)
        {
            Vector256<byte> leftShifted = Avx2.Permute2x128(left, right, 33);
            return Avx2.AlignRight(right, leftShifted, 15);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        [BypassReadyToRun]
        private static Vector256<byte> RightShift2(Vector256<byte> left, Vector256<byte> right)
        {
            Vector256<byte> leftShifted = Avx2.Permute2x128(left, right, 33);
            return Avx2.AlignRight(right, leftShifted, 14);
        }


        public interface ICaseSensitivity
        {
            static abstract char TransformInput(char input);
            static abstract Vector128<byte> TransformInput(Vector128<byte> input);
            static abstract Vector256<byte> TransformInput(Vector256<byte> input);
            static abstract bool Equals(ref char matchStart, string candidate);
            static abstract bool LongInputEquals(ref char matchStart, string candidate);
        }

        public readonly struct CaseSensitive : ICaseSensitivity
        {
            public static char TransformInput(char input) => input;
            public static Vector128<byte> TransformInput(Vector128<byte> input) => input;
            public static Vector256<byte> TransformInput(Vector256<byte> input) => input;

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Equals(ref char matchStart, string candidate)
            {
                for (int i = 0; i < candidate.Length; i++)
                {
                    if (Unsafe.Add(ref matchStart, i) != candidate[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool LongInputEquals(ref char matchStart, string candidate)
            {
                return SpanHelpers.SequenceEqual(
                    ref Unsafe.As<char, byte>(ref matchStart),
                    ref Unsafe.As<char, byte>(ref candidate.GetRawStringData()),
                    (nuint)(uint)candidate.Length * 2);
            }
        }

        public readonly struct CaseInensitiveAsciiLetters : ICaseSensitivity
        {
            public static char TransformInput(char input) => (char)(input & ~0x20);
            public static Vector128<byte> TransformInput(Vector128<byte> input) => input & Vector128.Create(unchecked((byte)~0x20));
            [BypassReadyToRun]
            public static Vector256<byte> TransformInput(Vector256<byte> input) => input & Vector256.Create(unchecked((byte)~0x20));

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Equals(ref char matchStart, string candidate)
            {
                for (int i = 0; i < candidate.Length; i++)
                {
                    if ((Unsafe.Add(ref matchStart, i) & ~0x20) != candidate[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool LongInputEquals(ref char matchStart, string candidate)
            {
                // TODO: We can do better as we know the candidate is definitely normalized uppercase ASCII
                return Ordinal.EqualsIgnoreCase_Vector128(ref matchStart, ref candidate.GetRawStringData(), candidate.Length);
            }
        }

        public readonly struct CaseInensitiveAscii : ICaseSensitivity
        {
            public static char TransformInput(char input) => TextInfo.ToUpperAsciiInvariant(input);

            public static Vector128<byte> TransformInput(Vector128<byte> input)
            {
                // TODO: Can we save an instruction here?
                Vector128<byte> mask = Vector128.GreaterThan(input - Vector128.Create((byte)'a'), Vector128.Create((byte)('z' - 'a')));
                Vector128<byte> upperCase = input & Vector128.Create(unchecked((byte)~0x20));
                return Vector128.ConditionalSelect(mask, input, upperCase);
            }

            [BypassReadyToRun]
            public static Vector256<byte> TransformInput(Vector256<byte> input)
            {
                Vector256<byte> mask = Vector256.GreaterThan(input - Vector256.Create((byte)'a'), Vector256.Create((byte)('z' - 'a')));
                Vector256<byte> upperCase = input & Vector256.Create(unchecked((byte)~0x20));
                return Vector256.ConditionalSelect(mask, input, upperCase);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Equals(ref char matchStart, string candidate)
            {
                for (int i = 0; i < candidate.Length; i++)
                {
                    if (TextInfo.ToUpperAsciiInvariant(Unsafe.Add(ref matchStart, i)) != candidate[i])
                    {
                        return false;
                    }
                }

                return true;
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool LongInputEquals(ref char matchStart, string candidate)
            {
                // TODO: We can do better as we know the candidate is definitely normalized uppercase ASCII
                return Ordinal.EqualsIgnoreCase_Vector128(ref matchStart, ref candidate.GetRawStringData(), candidate.Length);
            }
        }

        public readonly struct CaseInsensitiveUnicode : ICaseSensitivity
        {
            public static char TransformInput(char input) => throw new UnreachableException();
            public static Vector128<byte> TransformInput(Vector128<byte> input) => throw new UnreachableException();
            public static Vector256<byte> TransformInput(Vector256<byte> input) => throw new UnreachableException();

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool Equals(ref char matchStart, string candidate)
            {
                // TODO: Would Ordinal.CompareStringIgnoreCaseNonAscii == 0 be better?
                return Ordinal.EqualsIgnoreCase(ref matchStart, ref candidate.GetRawStringData(), candidate.Length);
            }

            [MethodImpl(MethodImplOptions.AggressiveInlining)]
            public static bool LongInputEquals(ref char matchStart, string candidate)
            {
                return Ordinal.EqualsIgnoreCase_Vector128(ref matchStart, ref candidate.GetRawStringData(), candidate.Length);
            }
        }
    }
}
