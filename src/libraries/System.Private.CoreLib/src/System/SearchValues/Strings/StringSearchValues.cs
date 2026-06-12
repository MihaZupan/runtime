// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.Arm;
using System.Runtime.Intrinsics.X86;
using System.Text;
using System.Text.Unicode;
using static System.Buffers.StringSearchValuesHelper;

namespace System.Buffers
{
    internal static class StringSearchValues
    {
        private const int TeddyBucketCount = 8;

        private static readonly SearchValues<char> s_asciiLetters =
            SearchValues.Create("ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz");

        private static readonly SearchValues<char> s_allAsciiExceptLowercase =
            SearchValues.Create("\0\u0001\u0002\u0003\u0004\u0005\u0006\a\b\t\n\v\f\r\u000E\u000F\u0010\u0011\u0012\u0013\u0014\u0015\u0016\u0017\u0018\u0019\u001A\e\u001C\u001D\u001E\u001F !\"#$%&'()*+,-./0123456789:;<=>?@ABCDEFGHIJKLMNOPQRSTUVWXYZ[\\]^_`{|}~\u007F");

        public static SearchValues<string> Create(ReadOnlySpan<string> values, bool ignoreCase)
        {
            if (values.Length == 0)
            {
                return new EmptySearchValues<string>();
            }

            if (values.Length == 1)
            {
                // Avoid additional overheads for single-value inputs.
                string value = values[0];
                ArgumentNullException.ThrowIfNull(value, nameof(values));

                if (ignoreCase)
                {
                    NormalizeIfNeeded(ref value);
                }

                return CreateForSingleValue(value, ignoreCase);
            }

            var uniqueValues = new HashSet<string>(values.Length, ignoreCase ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

            foreach (string value in values)
            {
                ArgumentNullException.ThrowIfNull(value, nameof(values));

                uniqueValues.Add(value);
            }

            if (uniqueValues.Contains(string.Empty))
            {
                // An empty string value will always match at position 0.
                // This isn't expected to be a common scenario, so we simplify the implementation
                // by returning the slow fallback which will still guarantee O(i * m) complexity.
                return new MultiStringSearchValuesFallback(uniqueValues, ignoreCase);
            }

            string[] normalizedValues = new string[uniqueValues.Count];
            uniqueValues.CopyTo(normalizedValues);

            if (ignoreCase)
            {
                for (int i = 0; i < normalizedValues.Length; i++)
                {
                    NormalizeIfNeeded(ref normalizedValues[i]);
                }
            }

            if (normalizedValues.Length == 1)
            {
                // The input only had duplicate values.
                return CreateForSingleValue(normalizedValues[0], ignoreCase);
            }

            return CreateFromNormalizedValues(normalizedValues, uniqueValues, ignoreCase);

            static void NormalizeIfNeeded(ref string value)
            {
                if (value.AsSpan().ContainsAnyExcept(s_allAsciiExceptLowercase))
                {
                    string upperCase = string.FastAllocateString(value.Length);
                    int charsWritten = Ordinal.ToUpperOrdinal(value, new Span<char>(ref upperCase.GetRawStringData(), upperCase.Length));
                    Debug.Assert(charsWritten == upperCase.Length);
                    value = upperCase;
                }
            }
        }

        private static SearchValues<string> CreateFromNormalizedValues(
            Span<string> values,
            HashSet<string> uniqueValues,
            bool ignoreCase)
        {
            AnalyzeValues(values, ref ignoreCase, out bool allAscii, out bool asciiLettersOnly, out bool nonAsciiAffectedByCaseConversion, out int minLength);

            if ((Ssse3.IsSupported || AdvSimd.Arm64.IsSupported) &&
                TryGetTeddyAcceleratedValues(values, uniqueValues, ignoreCase, allAscii, asciiLettersOnly, nonAsciiAffectedByCaseConversion, minLength) is { } searchValues)
            {
                return searchValues;
            }

            // Fall back to Aho-Corasick for all other multi-value sets.
            AhoCorasick ahoCorasick = AhoCorasickBuilder.Build(values, ignoreCase);

            if (!ignoreCase)
            {
                return PickAhoCorasickImplementation<CaseSensitive>(ahoCorasick, uniqueValues);
            }

            if (nonAsciiAffectedByCaseConversion)
            {
                if (ContainsInvalidValues(values))
                {
                    // Aho-Corasick can't deal with the matching semantics of invalid values.
                    // We will use a slow but correct O(n * m) fallback implementation.
                    return new MultiStringSearchValuesFallback(uniqueValues, ignoreCase: true);
                }

                return PickAhoCorasickImplementation<CaseInsensitiveUnicode>(ahoCorasick, uniqueValues);
            }

            if (asciiLettersOnly)
            {
                return PickAhoCorasickImplementation<CaseInsensitiveAsciiLetters>(ahoCorasick, uniqueValues);
            }

            return PickAhoCorasickImplementation<CaseInsensitiveAscii>(ahoCorasick, uniqueValues);

            static SearchValues<string> PickAhoCorasickImplementation<TCaseSensitivity>(AhoCorasick ahoCorasick, HashSet<string> uniqueValues)
                where TCaseSensitivity : struct, ICaseSensitivity
            {
                return ahoCorasick.ShouldUseAsciiFastScan
                    ? new StringSearchValuesAhoCorasick<TCaseSensitivity, AhoCorasick.IndexOfAnyAsciiFastScan>(ahoCorasick, uniqueValues)
                    : new StringSearchValuesAhoCorasick<TCaseSensitivity, AhoCorasick.NoFastScan>(ahoCorasick, uniqueValues);
            }
        }

        private static SearchValues<string>? TryGetTeddyAcceleratedValues(
            Span<string> values,
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
                // The more values we have, the higher the chance of hash/fingerprint collisions.
                // To avoid spending too much time in verification steps, fallback to Aho-Corasick which guarantees O(n).
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
                return PickTeddyImplementation<CaseInsensitiveAsciiLetters, CaseInsensitiveAsciiLetters>(values, uniqueValues, n);
            }

            // Even if the whole value isn't ASCII letters only, we can still use a faster approach
            // for the vectorized part as long as the first N characters are.
            bool asciiStartLettersOnly = true;
            bool asciiStartUnaffectedByCaseConversion = true;

            foreach (string value in values)
            {
                ReadOnlySpan<char> slice = value.AsSpan(0, n);
                asciiStartLettersOnly = asciiStartLettersOnly && !slice.ContainsAnyExcept(s_asciiLetters);
                asciiStartUnaffectedByCaseConversion = asciiStartUnaffectedByCaseConversion && !slice.ContainsAny(s_asciiLetters);
            }

            Debug.Assert(!(asciiStartLettersOnly && asciiStartUnaffectedByCaseConversion));

            // If we still have empty buckets we could use and we're ignoring case, we may be able to
            // generate all possible permutations of the first N characters and switch to case-sensitive searching.
            // E.g. ["ab", "c!"] => ["ab", "Ab" "aB", "AB", "c!", "C!"].
            // This won't apply to inputs with many letters (e.g. "abc" => 8 permutations on its own).
            if (!asciiStartUnaffectedByCaseConversion &&
                values.Length < TeddyBucketCount &&
                TryGenerateAllCasePermutationsForPrefixes(values, n, TeddyBucketCount, out string[]? newValues))
            {
                asciiStartUnaffectedByCaseConversion = true;
                values = newValues;
            }

            if (asciiStartUnaffectedByCaseConversion)
            {
                return nonAsciiAffectedByCaseConversion
                    ? PickTeddyImplementation<CaseSensitive, CaseInsensitiveUnicode>(values, uniqueValues, n)
                    : PickTeddyImplementation<CaseSensitive, CaseInsensitiveAscii>(values, uniqueValues, n);
            }

            if (nonAsciiAffectedByCaseConversion)
            {
                return asciiStartLettersOnly
                    ? PickTeddyImplementation<CaseInsensitiveAsciiLetters, CaseInsensitiveUnicode>(values, uniqueValues, n)
                    : PickTeddyImplementation<CaseInsensitiveAscii, CaseInsensitiveUnicode>(values, uniqueValues, n);
            }

            return asciiStartLettersOnly
                ? PickTeddyImplementation<CaseInsensitiveAsciiLetters, CaseInsensitiveAscii>(values, uniqueValues, n)
                : PickTeddyImplementation<CaseInsensitiveAscii, CaseInsensitiveAscii>(values, uniqueValues, n);
        }

        private static SearchValues<string> PickTeddyImplementation<TStartCaseSensitivity, TCaseSensitivity>(
            Span<string> values,
            HashSet<string> uniqueValues,
            int n)
            where TStartCaseSensitivity : struct, ICaseSensitivity
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            Debug.Assert(typeof(TStartCaseSensitivity) != typeof(CaseInsensitiveUnicode));
            Debug.Assert(values.Length > 1);
            Debug.Assert(n is 2 or 3);

            // Sort longest first so that when multiple values match at the same position, we check and report the longest one first.
            values.Sort(static (a, b) => b.Length.CompareTo(a.Length));

            if (values.Length > TeddyBucketCount)
            {
                string[][] buckets = TeddyBucketizer.Bucketize(values, TeddyBucketCount, n);

                // Potential optimization: We don't have to pick the first N characters for the fingerprint.
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

        private static bool TryGenerateAllCasePermutationsForPrefixes(ReadOnlySpan<string> values, int n, int maxValues, [NotNullWhen(true)] out string[]? newValues)
        {
            Debug.Assert(n is 2 or 3);
            Debug.Assert(values.Length < maxValues);

            // Count how many possible permutations there are.
            int newValuesCount = 0;

            foreach (string value in values)
            {
                int permutations = 1;

                foreach (char c in value.AsSpan(0, n))
                {
                    Debug.Assert(char.IsAscii(c));

                    if (char.IsAsciiLetter(c))
                    {
                        permutations *= 2;
                    }
                }

                newValuesCount += permutations;
            }

            Debug.Assert(newValuesCount > values.Length, "Shouldn't have been called if there were no letters present");

            if (newValuesCount > maxValues)
            {
                newValues = null;
                return false;
            }

            // Generate the permutations.
            newValues = new string[newValuesCount];
            newValuesCount = 0;

            foreach (string value in values)
            {
                int start = newValuesCount;

                newValues[newValuesCount++] = value;

                for (int i = 0; i < n; i++)
                {
                    char c = value[i];

                    if (char.IsAsciiLetter(c))
                    {
                        // Copy all the previous permutations of this value but change the casing of the i-th character.
                        foreach (string previous in newValues.AsSpan(start, newValuesCount - start))
                        {
                            newValues[newValuesCount++] = $"{previous.AsSpan(0, i)}{(char)(c ^ 0x20)}{previous.AsSpan(i + 1)}";
                        }
                    }
                }
            }

            Debug.Assert(newValuesCount == newValues.Length);
            return true;
        }

        private static SearchValues<string> CreateForSingleValue(string value, bool ignoreCase)
        {
            AnalyzeValues(new ReadOnlySpan<string>(ref value), ref ignoreCase, out bool ascii, out bool asciiLettersOnly, out _, out _);

            // We make use of optimizations that may overflow on 32bit systems for long values.
            int maxLength = IntPtr.Size == 4 ? 1_000_000_000 : int.MaxValue;

            if (Vector128.IsHardwareAccelerated && value.Length > 1 && value.Length <= maxLength)
            {
                SearchValues<string>? searchValues = value.Length switch
                {
                    < 4 => TryCreateSingleValuesThreeChars<ValueLengthLessThan4>(value, ignoreCase, ascii, asciiLettersOnly),
                    <= 8 => TryCreateSingleValuesThreeChars<ValueLength4To8>(value, ignoreCase, ascii, asciiLettersOnly),
                    <= 16 => TryCreateSingleValuesThreeChars<ValueLength9To16>(value, ignoreCase, ascii, asciiLettersOnly),
                    _ => TryCreateSingleValuesThreeChars<ValueLengthLongOrUnknown>(value, ignoreCase, ascii, asciiLettersOnly),
                };

                if (searchValues is not null)
                {
                    return searchValues;
                }
            }

            return ignoreCase
                ? new SingleStringSearchValuesFallback<SearchValues.TrueConst>(value)
                : new SingleStringSearchValuesFallback<SearchValues.FalseConst>(value);
        }

        private static SearchValues<string>? TryCreateSingleValuesThreeChars<TValueLength>(
            string value,
            bool ignoreCase,
            bool allAscii,
            bool asciiLettersOnly)
            where TValueLength : struct, IValueLength
        {
            if (!ignoreCase)
            {
                return CreateSingleValuesThreeChars<TValueLength, CaseSensitive>(value);
            }

            if (asciiLettersOnly)
            {
                return CreateSingleValuesThreeChars<TValueLength, CaseInsensitiveAsciiLetters>(value);
            }

            if (allAscii)
            {
                return CreateSingleValuesThreeChars<TValueLength, CaseInsensitiveAscii>(value);
            }

            // SingleStringSearchValuesThreeChars doesn't have logic to handle non-ASCII case conversion, so we require that anchor characters are ASCII.
            // Right now we're always selecting the first character as one of the anchors, and we need at least two.
            if (char.IsAscii(value[0]) && value.AsSpan(1).ContainsAnyInRange((char)0, (char)127))
            {
                return CreateSingleValuesThreeChars<TValueLength, CaseInsensitiveUnicode>(value);
            }

            return null;
        }

        private static SearchValues<string> CreateSingleValuesThreeChars<TValueLength, TCaseSensitivity>(string value)
            where TValueLength : struct, IValueLength
            where TCaseSensitivity : struct, ICaseSensitivity
        {
            CharacterFrequencyHelper.GetSingleStringMultiCharacterOffsets(value, ignoreCase: typeof(TCaseSensitivity) != typeof(CaseSensitive), out int ch2Offset, out int ch3Offset);

            if (CanUsePackedImpl(value[0]) && CanUsePackedImpl(value[ch2Offset]) && CanUsePackedImpl(value[ch3Offset]))
            {
                return new SingleStringSearchValuesPackedThreeChars<TValueLength, TCaseSensitivity>(value, ch2Offset, ch3Offset);
            }

            return new SingleStringSearchValuesThreeChars<TValueLength, TCaseSensitivity>(value, ch2Offset, ch3Offset);

            // Unlike with PackedSpanHelpers (Sse2 only), we are also using this approach on ARM64.
            // We use PackUnsignedSaturate on X86 and UnzipEven on ARM, so the set of allowed characters differs slightly (we can't use it for \0 and \xFF on X86).
            static bool CanUsePackedImpl(char c) =>
                PackedSpanHelpers.PackedIndexOfIsSupported ? PackedSpanHelpers.CanUsePackedIndexOf(c) :
                (AdvSimd.Arm64.IsSupported && c <= byte.MaxValue);
        }

        private static void AnalyzeValues(
            ReadOnlySpan<string> values,
            ref bool ignoreCase,
            out bool allAscii,
            out bool asciiLettersOnly,
            out bool nonAsciiAffectedByCaseConversion,
            out int minLength)
        {
            allAscii = true;
            asciiLettersOnly = true;
            minLength = int.MaxValue;

            foreach (string value in values)
            {
                allAscii = allAscii && Ascii.IsValid(value);
                asciiLettersOnly = asciiLettersOnly && !value.AsSpan().ContainsAnyExcept(s_asciiLetters);
                minLength = Math.Min(minLength, value.Length);
            }

            // Potential optimization: Not all characters participate in Unicode case conversion.
            // If we can determine that none of the non-ASCII characters do, we can make searching faster
            // by using the same paths as we do for ASCII-only values.
            nonAsciiAffectedByCaseConversion = ignoreCase && !allAscii;

            // If all the characters in values are unaffected by casing, we can avoid the ignoreCase overhead.
            if (ignoreCase && !nonAsciiAffectedByCaseConversion && !asciiLettersOnly)
            {
                ignoreCase = false;

                foreach (string value in values)
                {
                    if (value.AsSpan().ContainsAny(s_asciiLetters))
                    {
                        ignoreCase = true;
                        break;
                    }
                }
            }
        }

        private static bool ContainsInvalidValues(ReadOnlySpan<string> values)
        {
            foreach (string value in values)
            {
                if (!Utf16.IsValid(value))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
