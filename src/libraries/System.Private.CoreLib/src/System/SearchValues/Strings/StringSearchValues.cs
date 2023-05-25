// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using static System.Buffers.TeddyHelper;

namespace System.Buffers
{
    internal static class StringSearchValues
    {
        private static readonly SearchValues<char> s_asciiLetters =
            SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

        public static SearchValues<string> Create(ReadOnlySpan<string> values, bool ignoreCase)
        {
            if (values.Length == 0)
            {
                return new EmptySearchValues<string>();
            }

            var uniqueValues = new HashSet<string>(values.Length, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (string value in values)
            {
                ArgumentNullException.ThrowIfNull(value, nameof(values));

                uniqueValues.Add(value);
            }

            if (uniqueValues.Contains(string.Empty))
            {
                return new EmptyStringSearchValues(uniqueValues);
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
            var ahoCorasickBuilder = new AhoCorasickBuilder(normalizedValues, ignoreCase, ref unreachableValues);

            if (unreachableValues is not null)
            {
                // Some values are exact prefixes of other values.
                // Exclude those values now to reduce the number of buckets and make verification steps cheaper during searching.
                normalizedValues = RemoveUnreachableValues(normalizedValues, unreachableValues);
            }

            SearchValues<string> searchValues = CreateFromNormalizedValues(normalizedValues, uniqueValues, ignoreCase, ref ahoCorasickBuilder);
            ahoCorasickBuilder.Dispose();
            return searchValues;

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

        private static SearchValues<string> CreateFromNormalizedValues(
            ReadOnlySpan<string> values,
            HashSet<string> uniqueValues,
            bool ignoreCase,
            ref AhoCorasickBuilder ahoCorasickBuilder)
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

            if (values.Length == 1)
            {
                return CreateForSingleValue(values[0], uniqueValues, ignoreCase, allAscii, asciiLettersOnly);
            }

            // TODO: Should this be supported anywhere else?
            // It may be too niche and too much code for WASM, but AOT with just Vector128 may be interesting.
            if ((Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) &&
                TryGetTeddyAcceleratedValues(values, uniqueValues, ignoreCase, allAscii, asciiLettersOnly, nonAsciiAffectedByCaseConversion, minLength) is { } searchValues)
            {
                return searchValues;
            }

            AhoCorasick ahoCorasick = ahoCorasickBuilder.Build();

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
                        ? new StringSearchValuesAhoCorasick<CaseInsensitiveUnicode, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                        : new StringSearchValuesAhoCorasick<CaseInsensitiveUnicode, AhoCorasick.NoFastScan>(ahoCorasick, uniqueValues);
                }
                else
                {
                    return asciiLettersOnly
                        ? new StringSearchValuesAhoCorasick<CaseInensitiveAsciiLetters, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                        : new StringSearchValuesAhoCorasick<CaseInensitiveAscii, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues);
                }
            }

            return asciiOnlyStartChars
                ? new StringSearchValuesAhoCorasick<CaseSensitive, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                : new StringSearchValuesAhoCorasick<CaseSensitive, AhoCorasick.NoFastScan>(ahoCorasick, uniqueValues);
        }

        private static SearchValues<string>? TryGetTeddyAcceleratedValues(
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
                // consider using SearchValues<char> instead in such cases.
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

            if (!ignoreCase)
            {
                return PickTeddyImplementation<CaseSensitive, CaseSensitive>(values, uniqueValues, n);
            }

            if (asciiLettersOnly)
            {
                return PickTeddyImplementation<CaseInensitiveAsciiLetters, CaseInensitiveAsciiLetters>(values, uniqueValues, n);
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
                    ? PickTeddyImplementation<CaseSensitive, CaseInsensitiveUnicode>(values, uniqueValues, n)
                    : PickTeddyImplementation<CaseSensitive, CaseInensitiveAscii>(values, uniqueValues, n);
            }

            if (nonAsciiAffectedByCaseConversion)
            {
                return asciiStartLettersOnly
                    ? PickTeddyImplementation<CaseInensitiveAsciiLetters, CaseInsensitiveUnicode>(values, uniqueValues, n)
                    : PickTeddyImplementation<CaseInensitiveAscii, CaseInsensitiveUnicode>(values, uniqueValues, n);
            }

            return asciiStartLettersOnly
                ? PickTeddyImplementation<CaseInensitiveAsciiLetters, CaseInensitiveAscii>(values, uniqueValues, n)
                : PickTeddyImplementation<CaseInensitiveAscii, CaseInensitiveAscii>(values, uniqueValues, n);
        }

        private static SearchValues<string> PickTeddyImplementation<TStartCaseSensitivity, TCaseSensitivity>(
            ReadOnlySpan<string> values,
            HashSet<string> uniqueValues,
            int n)
            where TStartCaseSensitivity : struct, ICaseSensitivity
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            Debug.Assert(typeof(TStartCaseSensitivity) != typeof(CaseInsensitiveUnicode));
            Debug.Assert(values.Length > 1);
            Debug.Assert(n is 2 or 3);

            if (values.Length > 8)
            {
                string[][] buckets = TeddyBucketizer.Bucketize(values, bucketCount: 8, n);

                // TODO: Should we bail if we encounter a bad bucket distributions?

                // TODO: We don't have to pick the first N characters for the fingerprint.
                // Different offset selection can noticeably improve throughput (e.g. 2x).

                return n == 2
                    ? new AsciiStringSearchValuesTeddyBucketizedN2<TStartCaseSensitivity, TCaseSensitivity>(buckets, values, uniqueValues)
                    : new AsciiStringSearchValuesTeddyBucketizedN3<TStartCaseSensitivity, TCaseSensitivity>(buckets, values, uniqueValues);
            }
            else
            {
                return n == 2
                    ? new AsciiStringSearchValuesTeddyNonBucketizedN2<TStartCaseSensitivity, TCaseSensitivity>(values, uniqueValues)
                    : new AsciiStringSearchValuesTeddyNonBucketizedN3<TStartCaseSensitivity, TCaseSensitivity>(values, uniqueValues);
            }
        }

        private static SearchValues<string> CreateForSingleValue(
            string value,
            HashSet<string> uniqueValues,
            bool ignoreCase,
            bool allAscii,
            bool asciiLettersOnly)
        {
            // We make use of optimizations that may overflow on 32bit systems for long values.
            int maxLength = IntPtr.Size == 4 ? 1_000_000_000 : int.MaxValue;

            // TODO: Double-check R2R isn't messing up this IsHardwareAccelerated check
            if (Vector128.IsHardwareAccelerated && value.Length > 1 && value.Length < maxLength)
            {
                SearchValues<string>? searchValues = value.Length switch
                {
                    < 4 => TryCreateSingleValuesMultiChars<ValueLengthLessThan4>(value, uniqueValues, ignoreCase, allAscii, asciiLettersOnly),
                    < 8 => TryCreateSingleValuesMultiChars<ValueLength4To7>(value, uniqueValues, ignoreCase, allAscii, asciiLettersOnly),
                    _ => TryCreateSingleValuesMultiChars<ValueLength8OrLonger>(value, uniqueValues, ignoreCase, allAscii, asciiLettersOnly),
                };

                if (searchValues is not null)
                {
                    return searchValues;
                }
            }

            return ignoreCase
                ? new SingleStringSearchValuesFallback<SearchValues.TrueConst>(value, uniqueValues)
                : new SingleStringSearchValuesFallback<SearchValues.FalseConst>(value, uniqueValues);
        }

        private static SearchValues<string>? TryCreateSingleValuesMultiChars<TValueLength>(
            string value,
            HashSet<string> uniqueValues,
            bool ignoreCase,
            bool allAscii,
            bool asciiLettersOnly)
            where TValueLength : struct, IValueLength
        {
            if (!ignoreCase)
            {
                return TryCreateSingleValuesMultiChars<TValueLength, CaseSensitive>(value, uniqueValues);
            }

            if (asciiLettersOnly)
            {
                return TryCreateSingleValuesMultiChars<TValueLength, CaseInensitiveAsciiLetters>(value, uniqueValues);
            }

            if (allAscii)
            {
                return TryCreateSingleValuesMultiChars<TValueLength, CaseInensitiveAscii>(value, uniqueValues);
            }

            return TryCreateSingleValuesMultiChars<TValueLength, CaseInsensitiveUnicode>(value, uniqueValues);
        }

        private static SearchValues<string>? TryCreateSingleValuesMultiChars<TValueLength, TCaseSensitivity>(string value, HashSet<string> uniqueValues)
            where TValueLength : struct, IValueLength
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            if (!CharacterFrequencyHelper.TryGetSingleStringMultiCharacterOffsets(
                value,
                ignoreCase: typeof(TCaseSensitivity) != typeof(CaseSensitive),
                out int ch2Offset,
                out int? ch3Offset))
            {
                return null;
            }

            return ch3Offset is null
                ? new SingleStringSearchValuesMultiCharsN2<TValueLength, TCaseSensitivity>(value, uniqueValues, ch2Offset)
                : new SingleStringSearchValuesMultiCharsN3<TValueLength, TCaseSensitivity>(value, uniqueValues, ch2Offset, ch3Offset.Value);
        }
    }
}
