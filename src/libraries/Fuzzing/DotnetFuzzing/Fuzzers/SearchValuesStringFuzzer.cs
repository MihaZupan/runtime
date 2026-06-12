// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.InteropServices;

namespace DotnetFuzzing.Fuzzers;

internal sealed class SearchValuesStringFuzzer : IFuzzer
{
    public string[] TargetAssemblies => [];
    public string[] TargetCoreLibPrefixes { get; } = ["System.Buffers", "System.Globalization"];

    public void FuzzTarget(ReadOnlySpan<byte> bytes)
    {
        ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(bytes);

        int newLine = chars.IndexOf('\n');
        if (newLine < 0)
        {
            return;
        }

        ReadOnlySpan<char> haystack = chars.Slice(newLine + 1);
        string[] needles = chars.Slice(0, newLine).ToString().Split(',');

        using var haystack0 = PooledBoundedMemory<char>.Rent(haystack, PoisonPagePlacement.Before);
        using var haystack1 = PooledBoundedMemory<char>.Rent(haystack, PoisonPagePlacement.After);

        Test(haystack0.Span, haystack1.Span, needles, StringComparison.Ordinal);
        Test(haystack0.Span, haystack1.Span, needles, StringComparison.OrdinalIgnoreCase);
    }

    private static void Test(ReadOnlySpan<char> haystack, ReadOnlySpan<char> haystackCopy, string[] needles, StringComparison comparisonType)
    {
        SearchValues<string> searchValues = SearchValues.Create(needles, comparisonType);

        (int expectedIndex, string? expectedMatch) = IndexOfAnyReferenceImpl(haystack, needles, comparisonType);

        AssertEqual(expectedIndex, haystack.IndexOfAny(searchValues), searchValues);
        AssertEqual(expectedIndex, haystack.IndexOfAny(searchValues, out string? actualMatch), searchValues);
        AssertEqual(expectedMatch, actualMatch, comparisonType, searchValues);

        AssertEqual(expectedIndex, haystackCopy.IndexOfAny(searchValues), searchValues);
        AssertEqual(expectedIndex, haystackCopy.IndexOfAny(searchValues, out actualMatch), searchValues);
        AssertEqual(expectedMatch, actualMatch, comparisonType, searchValues);
    }

    private static (int Index, string? Match) IndexOfAnyReferenceImpl(ReadOnlySpan<char> searchSpace, ReadOnlySpan<string> values, StringComparison comparisonType)
    {
        int minIndex = -1;
        string? match = null;

        foreach (string value in values)
        {
            int i = searchSpace.IndexOf(value, comparisonType);

            if (i < 0 || (uint)i > (uint)minIndex)
            {
                // No match, or we already have an earlier one.
                continue;
            }

            if (i != minIndex || match!.Length < value.Length)
            {
                // Either at a lower index, or the same index but a longer match.
                // This is the new best match.
                match = value;
            }

            minIndex = i;
        }

        return (minIndex, match);
    }

    private static void AssertEqual(int expected, int actual, SearchValues<string> searchValues)
    {
        if (expected != actual)
        {
            AssertionFailed($"Expected index {expected}, got {actual}", searchValues);
        }
    }

    private static void AssertEqual(string? expected, string? actual, StringComparison comparisonType, SearchValues<string> searchValues)
    {
        if (!string.Equals(expected, actual, comparisonType))
        {
            AssertionFailed($"Expected match '{expected}', got '{actual}'", searchValues);
        }
    }

    private static void AssertionFailed(string error, SearchValues<string> searchValues)
    {
        Type implType = searchValues.GetType();
        string impl = $"{implType.Name} [{string.Join(", ", implType.GenericTypeArguments.Select(t => t.Name))}]";

        throw new Exception($"{error} for impl='{impl}'");
    }
}
