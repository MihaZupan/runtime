// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime;
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

            foreach (string value in values)
            {
                ArgumentNullException.ThrowIfNull(value, nameof(values));

                uniqueValues.Add(value);
            }

            if (uniqueValues.Contains(string.Empty))
            {
                return new IndexOfAnyStringEmptyValues(uniqueValues);
            }

            Span<string> normalizedValues = new string[uniqueValues.Count];
            int i = 0;
            foreach (string value in uniqueValues)
            {
                string normalized = value;

                if (ignoreCase && (value.AsSpan().IndexOfAnyInRange('a', 'z') >= 0 || !Ascii.IsValid(value)))
                {
                    string upperCase = string.FastAllocateString(value.Length);
                    int charsWritten = Ordinal.ToUpperOrdinal(value, new Span<char>(ref upperCase.GetRawStringData(), upperCase.Length));
                    Debug.Assert(charsWritten == upperCase.Length);
                    normalized = upperCase;
                }

                normalizedValues[i++] = normalized;
            }
            Debug.Assert(i == normalizedValues.Length);

            // Aho-Corasick's ctor expects values to be sorted by length.
            normalizedValues.Sort(static (a, b) => a.Length.CompareTo(b.Length));

            // We may not end up choosing Aho-Corasick as the implementation, but it has a nice property of
            // finding all the unreachable values during the construction stage, so we build the trie early.
            List<string>? unreachableValues = null;
            var ahoCorasick = new AhoCorasick(normalizedValues, ignoreCase, ref unreachableValues);

            if (unreachableValues is not null)
            {
                // Some values are exact prefixes of other values.
                // Exclude those values now to reduce the number of buckets and make verification steps cheaper during searching.
                normalizedValues = RemoveUnreachableValues(normalizedValues, unreachableValues);
            }

            return CreateFromNormalizedValues(normalizedValues, uniqueValues, ignoreCase, ahoCorasick);

            static Span<string> RemoveUnreachableValues(Span<string> values, List<string> unreachableValues)
            {
                // We've already normalized the values, so we can do ordinal comparisons here.
                var unreachableValuesSet = new HashSet<string>(unreachableValues, StringComparer.Ordinal);

                int newCount = 0;
                foreach (string value in values)
                {
                    if (!unreachableValuesSet.Contains(value))
                    {
                        values[newCount++] = value;
                    }
                }

                Debug.Assert(newCount == values.Length - unreachableValues.Count);
                Debug.Assert(newCount > 0);

                return values.Slice(0, newCount);
            }
        }

        private static IndexOfAnyValues<string> CreateFromNormalizedValues(
            ReadOnlySpan<string> values,
            HashSet<string> uniqueValues,
            bool ignoreCase,
            AhoCorasick ahoCorasick)
        {
            bool allAscii = true;
            bool asciiLettersOnly = true;
            int minLength = int.MaxValue;

            foreach (string value in values)
            {
                allAscii = allAscii && Ascii.IsValid(value);
                asciiLettersOnly = asciiLettersOnly && value.AsSpan().IndexOfAnyExcept(s_asciiLetters) < 0;
                minLength = Math.Min(minLength, value.Length);
            }

            // TODO: Not all characters participate in Unicode case conversion.
            // If we can determine that none of the non-ASCII characters do, we can make searching faster
            // by using the same paths as we do for ASCII-only values.
            // We should be able to get that answer as long as we're not using NLS.
            bool nonAsciiAffectedByCaseConversion = ignoreCase && !allAscii;

            // If all the characters in values are unaffected by casing, we can avoid the ignoreCase overhead.
            if (ignoreCase && !nonAsciiAffectedByCaseConversion)
            {
                ignoreCase = false;

                foreach (string value in values)
                {
                    if (value.AsSpan().IndexOfAny(s_asciiLetters) >= 0)
                    {
                        ignoreCase = true;
                        break;
                    }
                }
            }

            // TODO: Should this be supported anywhere else?
            // It may be too niche and too much code for WASM, but AOT with just Vector128 may be interesting.
            if ((Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) &&
                TryGetTeddyAcceleratedValues(values, uniqueValues, ignoreCase, allAscii, asciiLettersOnly, nonAsciiAffectedByCaseConversion, minLength) is { } indexOfAnyValues)
            {
                return indexOfAnyValues;
            }

            bool asciiOnlyStartChars = true;

            if (IndexOfAnyAsciiSearcher.IsVectorizationSupported && !allAscii)
            {
                foreach (string value in values)
                {
                    if (!char.IsAscii(value[0]))
                    {
                        asciiOnlyStartChars = false;
                        break;
                    }
                }
            }

            if (ignoreCase)
            {
                if (nonAsciiAffectedByCaseConversion)
                {
                    return asciiOnlyStartChars
                        ? new IndexOfAnyStringValuesAhoCorasick<CaseInsensitiveUnicode, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                        : new IndexOfAnyStringValuesAhoCorasick<CaseInsensitiveUnicode, AhoCorasick.NoFastScan>(ahoCorasick, uniqueValues);
                }
                else
                {
                    return asciiLettersOnly
                        ? new IndexOfAnyStringValuesAhoCorasick<CaseInensitiveAsciiLetters, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                        : new IndexOfAnyStringValuesAhoCorasick<CaseInensitiveAscii, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues);
                }
            }

            return asciiOnlyStartChars
                ? new IndexOfAnyStringValuesAhoCorasick<CaseSensitive, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                : new IndexOfAnyStringValuesAhoCorasick<CaseSensitive, AhoCorasick.NoFastScan>(ahoCorasick, uniqueValues);
        }

        private static IndexOfAnyValues<string>? TryGetTeddyAcceleratedValues(
            ReadOnlySpan<string> values,
            HashSet<string> uniqueValues,
            bool ignoreCase,
            bool allAscii,
            bool asciiLettersOnly,
            bool nonAsciiAffectedByCaseConversion,
            int minLength)
        {
            if (minLength == 1)
            {
                // An 'N=1' implementation is possible, but callers should
                // consider using IndexOfAnyValues<char> instead in such cases.
                // It can be added if Regex ends up running into this case.
                return null;
            }

            if (values.Length > RabinKarp.MaxValues)
            {
                // Rabin-Karp is likely to perform poorly with this many inputs.
                // If it turns out that this limit is commonly exceeded, we can tweak the number of buckets
                // in the implementation, or use different variants depending on input.
                return null;
            }

            // TODO: We could pick N=2 even if minLength >= 3 to speed up the vectorized search
            // while increasing the time spent in the verification step.
            int n = minLength == 2 ? 2 : 3;

            if (Ssse3.IsSupported)
            {
                foreach (string value in values)
                {
                    if (value.AsSpan(0, n).Contains('\0'))
                    {
                        // If we let null chars through here, Teddy would still work correctly, but it
                        // would hit more false positives that the verification step would have to rule out.
                        // While we could flow a generic flag like Ssse3AndWasmHandleZeroInNeedle through,
                        // we expect such values to be rare enough that introducing more code is not worth it.
                        return null;
                    }
                }
            }

            // Even if the values contain non-ASCII chars, we may be able to use Teddy as long as the
            // first N characters are ASCII.
            if (!allAscii)
            {
                foreach (string value in values)
                {
                    if (!Ascii.IsValid(value.AsSpan(0, n)))
                    {
                        // A vectorized implementation for non-ASCII values is possible.
                        // It can be added if it turns out to be a common enough scenario.
                        return null;
                    }
                }
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

            if (!ignoreCase)
            {
                return PickTeddyImplementation<CaseSensitive, CaseSensitive>(values, uniqueValues, rabinKarp, minLength);
            }

            if (asciiLettersOnly)
            {
                return PickTeddyImplementation<CaseInensitiveAsciiLetters, CaseInensitiveAsciiLetters>(values, uniqueValues, rabinKarp, minLength);
            }

            // Even if the whole value isn't ASCII letters only, we can still use a faster approach
            // for the vectorized part as long as the first N characters are.
            bool asciiStartLettersOnly = true;
            bool asciiStartUnaffectedByCaseConversion = true;

            foreach (string value in values)
            {
                ReadOnlySpan<char> slice = value.AsSpan(0, n);
                asciiStartLettersOnly = asciiStartLettersOnly && slice.IndexOfAnyExcept(s_asciiLetters) < 0;
                asciiStartUnaffectedByCaseConversion = asciiStartUnaffectedByCaseConversion && slice.IndexOfAny(s_asciiLetters) < 0;
            }

            Debug.Assert(!(asciiStartLettersOnly && asciiStartUnaffectedByCaseConversion));

            if (asciiStartUnaffectedByCaseConversion)
            {
                return nonAsciiAffectedByCaseConversion
                    ? PickTeddyImplementation<CaseSensitive, CaseInsensitiveUnicode>(values, uniqueValues, rabinKarp, minLength)
                    : PickTeddyImplementation<CaseSensitive, CaseInensitiveAscii>(values, uniqueValues, rabinKarp, minLength);
            }

            if (nonAsciiAffectedByCaseConversion)
            {
                return asciiStartLettersOnly
                    ? PickTeddyImplementation<CaseInensitiveAsciiLetters, CaseInsensitiveUnicode>(values, uniqueValues, rabinKarp, minLength)
                    : PickTeddyImplementation<CaseInensitiveAscii, CaseInsensitiveUnicode>(values, uniqueValues, rabinKarp, minLength);
            }

            return asciiStartLettersOnly
                ? PickTeddyImplementation<CaseInensitiveAsciiLetters, CaseInensitiveAscii>(values, uniqueValues, rabinKarp, minLength)
                : PickTeddyImplementation<CaseInensitiveAscii, CaseInensitiveAscii>(values, uniqueValues, rabinKarp, minLength);
        }

        [BypassReadyToRun]
        private static IndexOfAnyValues<string> PickTeddyImplementation<TStartCaseSensitivity, TCaseSensitivity>(
            ReadOnlySpan<string> values,
            HashSet<string> uniqueValues,
            RabinKarp rabinKarp,
            int n)
            where TStartCaseSensitivity : struct, ICaseSensitivity
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            Debug.Assert(typeof(TStartCaseSensitivity) != typeof(CaseInsensitiveUnicode));
            Debug.Assert(values.Length > 0);
            Debug.Assert(n >= 2);

            if (values.Length > 8)
            {
                // TODO: Should we bother with "Fat Teddy" (16 buckets)? It's limited to Avx2
                string[][] buckets = Bucketize(values, bucketCount: 8, n);

                // TODO: Should we bail if we encounter a bad bucket distributions?

                // TODO: We don't have to pick the first N characters for the fingerprint.
                // Would smarter offset selection help here to improve bucket distribution?

                if (n == 2)
                {
                    return Avx2.IsSupported
                        ? new IndexOfAnyAsciiStringValuesTeddy256BucketizedN2<TStartCaseSensitivity, TCaseSensitivity>(buckets, rabinKarp, uniqueValues)
                        : new IndexOfAnyAsciiStringValuesTeddy128BucketizedN2<TStartCaseSensitivity, TCaseSensitivity>(buckets, rabinKarp, uniqueValues);
                }
                else
                {
                    return Avx2.IsSupported
                        ? new IndexOfAnyAsciiStringValuesTeddy256BucketizedN3<TStartCaseSensitivity, TCaseSensitivity>(buckets, rabinKarp, uniqueValues)
                        : new IndexOfAnyAsciiStringValuesTeddy128BucketizedN3<TStartCaseSensitivity, TCaseSensitivity>(buckets, rabinKarp, uniqueValues);
                }
            }
            else
            {
                if (n == 2)
                {
                    return Avx2.IsSupported
                        ? new IndexOfAnyAsciiStringValuesTeddy256NonBucketizedN2<TStartCaseSensitivity, TCaseSensitivity>(values, rabinKarp, uniqueValues)
                        : new IndexOfAnyAsciiStringValuesTeddy128NonBucketizedN2<TStartCaseSensitivity, TCaseSensitivity>(values, rabinKarp, uniqueValues);
                }
                else
                {
                    return Avx2.IsSupported
                        ? new IndexOfAnyAsciiStringValuesTeddy256NonBucketizedN3<TStartCaseSensitivity, TCaseSensitivity>(values, rabinKarp, uniqueValues)
                        : new IndexOfAnyAsciiStringValuesTeddy128NonBucketizedN3<TStartCaseSensitivity, TCaseSensitivity>(values, rabinKarp, uniqueValues);
                }
            }
        }
    }
}
