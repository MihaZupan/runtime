// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using static System.Buffers.TeddyHelper;

namespace System.Buffers
{
    internal static class IndexOfAnyStringValues
    {
        private static readonly IndexOfAnyValues<char> s_asciiLetters =
            IndexOfAnyValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

        public static IndexOfAnyValues<string> Create(ReadOnlySpan<string> values, bool ignoreCase)
        {
            if (values.Length == 0)
            {
                return new IndexOfAnyStringEmptyValues(new HashSet<string>());
            }

            var uniqueValues = new HashSet<string>(values.Length, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            bool allAscii = true;
            bool asciiLettersOnly = true;
            int minLength = int.MaxValue;

            foreach (string value in values)
            {
                ArgumentNullException.ThrowIfNull(value, nameof(values));

                uniqueValues.Add(value);
                allAscii = allAscii && Ascii.IsValid(value);
                asciiLettersOnly = asciiLettersOnly && value.AsSpan().IndexOfAnyExcept(s_asciiLetters) < 0;
                minLength = Math.Min(minLength, value.Length);
            }

            if (uniqueValues.Contains(string.Empty))
            {
                return new IndexOfAnyStringEmptyValues(uniqueValues);
            }

            string[] valuesCopy = new string[uniqueValues.Count];
            int i = 0;
            foreach (string value in uniqueValues)
            {
                valuesCopy[i++] = ignoreCase ? value.ToLowerInvariant() : value;
            }
            Debug.Assert(i == valuesCopy.Length);

            Array.Sort(valuesCopy, static (a, b) => a.Length.CompareTo(b.Length));

            // We may not end up choosing Aho-Corasick as the implementation, but it has a nice property of
            // finding all the unreachable values during the construction stage, so we build the trie early.
            List<string>? unreachableValues = null;
            var ahoCorasick = new AhoCorasick(valuesCopy, ignoreCase, ref unreachableValues, out bool asciiOnlyStartChars);

            if (unreachableValues is not null)
            {
                // Some values are exact prefixes of other values.
                // Exclude those values now to reduce the number of buckets and make verification steps cheaper during searching.
                string[] newValues = new string[valuesCopy.Length - unreachableValues.Count];
                Debug.Assert(newValues.Length > 0);

                // We've already normalized the values, so we can do ordinal comparisons here.
                var unreachableValuesSet = new HashSet<string>(unreachableValues, StringComparer.Ordinal);

                int newCount = 0;
                foreach (string value in valuesCopy)
                {
                    if (!unreachableValuesSet.Contains(value))
                    {
                        newValues[newCount++] = value;
                    }
                }
                Debug.Assert(newCount == newValues.Length);

                valuesCopy = newValues;
            }

            // TODO: Should this be supported anywhere else?
            // It may be too niche and too much code for WASM
            if ((Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) &&
                TryGetTeddyAcceleratedValues(valuesCopy, uniqueValues, allAscii, ignoreCase, asciiLettersOnly, minLength) is { } indexOfAnyValues)
            {
                return indexOfAnyValues;
            }

            // The Kelvin sign character maps to a lowercase ASCII 'k', so we may incorrectly consider all starting chars to be ASCII.
            // In such cases, we can't use the IndexOfAnyAsciiFastScan optimization, or we may skip over actual matching characters.
            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported && asciiOnlyStartChars && ignoreCase && !allAscii)
            {
                // Note that we are enumerating uniqueValues and not valuesCopy to get the original values (before ToLowerInvariant).
                foreach (string value in uniqueValues)
                {
                    if (!char.IsAscii(value[0]))
                    {
                        Debug.Assert(value[0] == '\u212A'); // Kelvin sign
                        asciiOnlyStartChars = false;
                        break;
                    }
                }
            }

            if (ignoreCase)
            {
                if (allAscii)
                {
                    return asciiLettersOnly
                        ? new IndexOfAnyStringValuesAhoCorasick<CaseInensitiveAsciiLetters, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                        : new IndexOfAnyStringValuesAhoCorasick<CaseInensitiveAscii, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues);
                }
                else
                {
                    return asciiOnlyStartChars
                        ? new IndexOfAnyStringValuesAhoCorasick<CaseInsensitiveUnicode, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                        : new IndexOfAnyStringValuesAhoCorasick<CaseInsensitiveUnicode, AhoCorasick.NoFastScan>(ahoCorasick, uniqueValues);
                }
            }

            return asciiOnlyStartChars
                ? new IndexOfAnyStringValuesAhoCorasick<CaseSensitive, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                : new IndexOfAnyStringValuesAhoCorasick<CaseSensitive, AhoCorasick.NoFastScan>(ahoCorasick, uniqueValues);
        }

        private static IndexOfAnyValues<string>? TryGetTeddyAcceleratedValues(
            string[] values,
            HashSet<string> uniqueValues,
            bool allAscii,
            bool ignoreCase,
            bool asciiLettersOnly,
            int minLength)
        {
            if (minLength == 1)
            {
                // An 'N=1' implementation is possible, but callers should likely
                // prefer using IndexOfAnyValues<char> instead in such cases.
                return null;
            }

            if (!allAscii)
            {
                // A vectorized implementation for non-ASCII values is possible.
                // It can be added if it turns out to be a common enough scenario.
                return null;
            }

            if (Ssse3.IsSupported)
            {
                foreach (string value in values)
                {
                    if (value.Contains('\0'))
                    {
                        // Supporting null chars would just complicate the vectorized implementation.
                        // We don't expect substrings to contain 0, so we don't bother.
                        return null;
                    }
                }
            }

            if (values.Length > RabinKarp.MaxValues)
            {
                // Rabin-Karp is likely to perform poorly with this many inputs.
                // If it turns out that this limit is commonly exceeded, we can tweak the number of buckets
                // in the implementation, or use different variants depending on input.
                return null;
            }

            var rabinKarp = new RabinKarp(values);

            if (rabinKarp.HasCatastrophicCollisionRate())
            {
                // The input doesn't lend itself well to the type of rolling hash used by our Rabin-Karp implementation.
                // It is likely that we would approach an O(n * m) average and we also assume that the vectorized
                // Teddy path would perform suboptimally due to the added overhead in the verification step.
                // Fallback to Aho-Corasick which has a guaranteed O(n) worst-case.
                return null;
            }

            if (ignoreCase)
            {
                return asciiLettersOnly
                    ? PickImplementation<CaseInensitiveAsciiLetters>(rabinKarp, values, uniqueValues, minLength)
                    : PickImplementation<CaseInensitiveAscii>(rabinKarp, values, uniqueValues, minLength);
            }

            return PickImplementation<CaseSensitive>(rabinKarp, values, uniqueValues, minLength);

            static IndexOfAnyValues<string> PickImplementation<TCaseSensitivity>(RabinKarp rabinKarp, string[] values, HashSet<string> uniqueValues, int minLength)
                where TCaseSensitivity : struct, ICaseSensitivity
            {
                Debug.Assert(values.Length > 0);
                Debug.Assert(minLength >= 2);

                if (values.Length > 8)
                {
                    // TODO: Should we bother with "Fat Teddy" (16 buckets)? It's limited to Avx2
                    string[][] buckets = Bucketize(values, bucketCount: 8, n: minLength == 2 ? 2 : 3);

                    // TODO: Should we bail if we encounter a bad bucket distributions?

                    // TODO: We don't have to pick the first N characters for the fingerprint.
                    // Would smarter offset selection help here to improve bucket distribution?

                    if (minLength == 2)
                    {
                        return Avx2.IsSupported
                            ? new IndexOfAnyAsciiStringValuesTeddy256BucketizedN2<TCaseSensitivity>(buckets, rabinKarp, uniqueValues)
                            : new IndexOfAnyAsciiStringValuesTeddy128BucketizedN2<TCaseSensitivity>(buckets, rabinKarp, uniqueValues);
                    }
                    else
                    {
                        return Avx2.IsSupported
                            ? new IndexOfAnyAsciiStringValuesTeddy256BucketizedN3<TCaseSensitivity>(buckets, rabinKarp, uniqueValues)
                            : new IndexOfAnyAsciiStringValuesTeddy128BucketizedN3<TCaseSensitivity>(buckets, rabinKarp, uniqueValues);
                    }
                }
                else
                {
                    if (minLength == 2)
                    {
                        return Avx2.IsSupported
                            ? new IndexOfAnyAsciiStringValuesTeddy256NonBucketizedN2<TCaseSensitivity>(values, rabinKarp, uniqueValues)
                            : new IndexOfAnyAsciiStringValuesTeddy128NonBucketizedN2<TCaseSensitivity>(values, rabinKarp, uniqueValues);
                    }
                    else
                    {
                        return Avx2.IsSupported
                            ? new IndexOfAnyAsciiStringValuesTeddy256NonBucketizedN3<TCaseSensitivity>(values, rabinKarp, uniqueValues)
                            : new IndexOfAnyAsciiStringValuesTeddy128NonBucketizedN3<TCaseSensitivity>(values, rabinKarp, uniqueValues);
                    }
                }
            }
        }
    }
}
