// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;

namespace System.Buffers
{
    // https://github.com/jneem/teddy/blob/9ab5e899ad6ef6911aecd3cf1033f1abe6e1f66c/src/x86/mask.rs#L215-L262
    // TODO: Rewrite this?
    // TODO: Does this actually work to produce a decent bucket distribution?
    internal static class TeddyBucketizer
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

        private static string[][] GatherBuckets(ReadOnlySpan<string> values, int bucketCount, int n)
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

        public static string[][] Bucketize(ReadOnlySpan<string> values, int bucketCount, int n)
        {
            Debug.Assert(bucketCount == 8, "This may change if we end up supporting the 'fat Teddy' variant.");
            Debug.Assert(values.Length > bucketCount, "Should be using a non-bucketized implementation.");

            string[][] buckets = GatherBuckets(values, bucketCount, n);
            Debug.Assert(buckets.Length <= bucketCount);

            return buckets;
        }
    }
}
