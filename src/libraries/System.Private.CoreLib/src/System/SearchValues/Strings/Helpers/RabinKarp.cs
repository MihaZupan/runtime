﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers
{
    internal readonly struct RabinKarp
    {
        // Arbitrary upper bound. This also affects when Teddy may be used.
        public const int MaxValues = 64;

        // This is a tradeoff between memory consumption and the number of false positives
        // we have to rule out during the verification step.
        private const nuint BucketCount = 64;

        // 18 = Vector128<ushort>.Count + 2 (MatchStartOffset for N=3)
        // The logic in this class is not safe from overflows, but we avoid any issues by
        // only calling into it for inputs that are too short for Teddy to handle.
        private const int MaxInputLength = 18 - 1;

        // We're using nuint as the rolling hash, so we can spread the hash over more bits on 64bit.
        private static int HashShiftPerElement => IntPtr.Size == 8 ? 2 : 1;

        private readonly string[][] _buckets;
        private readonly int _hashLength;
        private readonly nuint _hashUpdateMultiplier;

        public RabinKarp(ReadOnlySpan<string> values)
        {
            Debug.Assert(values.Length <= MaxValues);

            int minimumLength = int.MaxValue;
            foreach (string value in values)
            {
                minimumLength = Math.Min(minimumLength, value.Length);
            }

            Debug.Assert(minimumLength > 1);

            _hashLength = minimumLength;
            _hashUpdateMultiplier = (nuint)1 << ((minimumLength - 1) * HashShiftPerElement);

            var bucketLists = new List<string>?[BucketCount];

            foreach (string value in values)
            {
                nuint hash = 0;
                for (int i = 0; i < minimumLength; i++)
                {
                    hash = (hash << HashShiftPerElement) + value[i];
                }

                nuint bucket = hash % BucketCount;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public readonly int IndexOfAny<TCaseSensitivity>(ReadOnlySpan<char> span)
            where TCaseSensitivity : struct, StringSearchValuesHelper.ICaseSensitivity =>
            typeof(TCaseSensitivity) == typeof(StringSearchValuesHelper.CaseInsensitiveUnicode)
                ? IndexOfAnyCaseInsensitiveUnicode(span)
                : IndexOfAnyCore<TCaseSensitivity>(span);

        private readonly int IndexOfAnyCore<TCaseSensitivity>(ReadOnlySpan<char> span)
            where TCaseSensitivity : struct, StringSearchValuesHelper.ICaseSensitivity
        {
            if (typeof(TCaseSensitivity) == typeof(StringSearchValuesHelper.CaseInsensitiveUnicode))
            {
                throw new UnreachableException();
            }

            Debug.Assert(span.Length <= MaxInputLength, "Teddy should have handled short inputs.");

            ref char current = ref MemoryMarshal.GetReference(span);

            int hashLength = _hashLength;

            if (span.Length >= hashLength)
            {
                ref char end = ref Unsafe.Add(ref MemoryMarshal.GetReference(span), (uint)(span.Length - hashLength));

                nuint hash = 0;
                for (uint i = 0; i < hashLength; i++)
                {
                    hash = (hash << HashShiftPerElement) + TCaseSensitivity.TransformInput(Unsafe.Add(ref current, i));
                }

                while (true)
                {
                    // TODO Should buckets be a local
                    if (Unsafe.Add(ref MemoryMarshal.GetArrayDataReference(_buckets), hash % BucketCount) is string[] bucket)
                    {
                        int startOffset = (int)((nuint)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(span), ref current) / sizeof(char));

                        if (StringSearchValuesHelper.StartsWith<TCaseSensitivity>(ref current, span.Length - startOffset, bucket))
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

                    // Update the hash by removing the previous character and adding the next one.
                    hash = ((hash - (previous * _hashUpdateMultiplier)) << HashShiftPerElement) + next;
                    current = ref Unsafe.Add(ref current, 1);
                }
            }

            return -1;
        }

        private readonly int IndexOfAnyCaseInsensitiveUnicode(ReadOnlySpan<char> span)
        {
            Debug.Assert(span.Length <= MaxInputLength, "Teddy should have handled long inputs.");

            Span<char> upperCase = stackalloc char[MaxInputLength].Slice(0, span.Length);

            int charsWritten = Ordinal.ToUpperOrdinal(span, upperCase);
            Debug.Assert(charsWritten == upperCase.Length);

            // CaseSensitive instead of CaseInsensitiveUnicode as we've already done the case conversion.
            return IndexOfAnyCore<StringSearchValuesHelper.CaseSensitive>(upperCase);
        }
    }
}
