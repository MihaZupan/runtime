// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers
{
    internal readonly struct RabinKarp
    {
        // Arbitrary upper bounds. These also affect when Teddy may be used.
        private const int MaxValuesPerBucket = 5;
        public const int MaxValues = 64;

        // This is a tradeoff between memory consumption and the number of false positives
        // we have to rule out during the verification step.
        private const nuint BucketCount = 64;
        private const nuint BucketFlagsCount = BucketCount * 8;

        // We first check that the hash has a corresponding bucket via _bucketFlags to further
        // reduce the number of false positives that make it to the verification step.
        // While we could achieve similar results by increasing the number of buckets,
        // storing a lot more bools takes up less memory, so we can afford to use more.
        private readonly bool[] _bucketFlags;
        private readonly string[][] _buckets;
        private readonly int _hashLength;
        private readonly nuint _hashUpdateMultiplier;

        public RabinKarp(ReadOnlySpan<string> values)
        {
            Debug.Assert(values.Length <= MaxValues);

            _bucketFlags = new bool[BucketFlagsCount];

            int minimumLength = int.MaxValue;
            foreach (string value in values)
            {
                minimumLength = Math.Min(minimumLength, value.Length);
            }

            Debug.Assert(minimumLength > 1);

            _hashLength = minimumLength;
            _hashUpdateMultiplier = (nuint)1 << (minimumLength - 1);

            var bucketLists = new List<string>?[BucketCount];

            foreach (string value in values)
            {
                nuint hash = 0;
                for (int i = 0; i < minimumLength; i++)
                {
                    hash = (hash << 1) + value[i];
                }

                nuint bucketFlag = hash % BucketFlagsCount;
                nuint bucket = hash % BucketCount;
                _bucketFlags[bucketFlag] = true;
                var bucketList = bucketLists[bucket] ??= new List<string>();
                bucketList.Add(value);
            }

            var buckets = new string[BucketCount][];
            for (int i = 0; i < bucketLists.Length; i++)
            {
                if (bucketLists[i] is List<string> list)
                {
                    buckets[i] = list.ToArray();
                }
            }

            _buckets = buckets;
        }

        public bool HasCatastrophicCollisionRate()
        {
            foreach (string[] bucket in _buckets)
            {
                if (bucket is not null)
                {
                    if (bucket.Length > MaxValuesPerBucket)
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public readonly int IndexOfAny<TCaseSensitivity>(ReadOnlySpan<char> span, ref char current)
            where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
        {
            Debug.Assert(typeof(TCaseSensitivity) != typeof(TeddyHelper.CaseInsensitiveUnicode));
            Debug.Assert(!Unsafe.IsAddressLessThan(ref current, ref MemoryMarshal.GetReference(span)));
            Debug.Assert(Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref current) / 2 <= span.Length);

            // TODO: Is this code safe from overflow issues, or is the fact that it's only being used for short inputs saving us?
            Debug.Assert(span.Length - (Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref current) / 2) < 34,
                "Teddy should have handled the start of the input.");

            int hashLength = _hashLength;
            ref bool bucketFlags = ref MemoryMarshal.GetArrayDataReference(_bucketFlags);

            if (span.Length >= hashLength)
            {
                ref char end = ref Unsafe.Add(ref MemoryMarshal.GetReference(span), (uint)(span.Length - hashLength));

                nuint hash = 0;
                for (uint i = 0; i < hashLength; i++)
                {
                    hash = (hash << 1) + TCaseSensitivity.TransformInput(Unsafe.Add(ref current, i));
                }

                while (true)
                {
                    if (Unsafe.Add(ref bucketFlags, hash % BucketFlagsCount))
                    {
                        string[] bucket = Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buckets), hash % BucketCount);
                        Debug.Assert(bucket is not null);

                        int startOffset = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref current) / sizeof(char));

                        if (TeddyHelper.StartsWith<TCaseSensitivity>(ref current, span.Length - startOffset, bucket))
                        {
                            return startOffset;
                        }
                    }

                    if (!Unsafe.IsAddressLessThan(ref current, ref end))
                    {
                        break;
                    }

                    char previous = TCaseSensitivity.TransformInput(current);
                    char next = TCaseSensitivity.TransformInput(Unsafe.Add(ref current, (uint)hashLength));

                    hash = ((hash - (previous * _hashUpdateMultiplier)) << 1) + next;
                    current = ref Unsafe.Add(ref current, 1);
                }
            }

            return -1;
        }
    }
}
