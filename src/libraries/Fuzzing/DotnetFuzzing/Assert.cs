﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.InteropServices;
using System.Text;

namespace DotnetFuzzing;

internal static class Assert
{
    // Feel free to add any other helpers as needed.

    public static void True(bool actual) =>
        Equal(true, actual);

    public static void False(bool actual) =>
        Equal(false, actual);

    public static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            Throw(expected, actual);
        }

        static void Throw(T expected, T actual) =>
            throw new AssertException($"Expected={expected} Actual={actual}");
    }

    public static void SequenceEqual<T>(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual)
    {
        if (!expected.SequenceEqual(actual))
        {
            Throw(expected, actual);
        }

        static void Throw(ReadOnlySpan<T> expected, ReadOnlySpan<T> actual)
        {
            Equal(expected.Length, actual.Length);

            int diffIndex = expected.CommonPrefixLength(actual);

            throw new AssertException($"Expected={expected[diffIndex]} Actual={actual[diffIndex]} at index {diffIndex}");
        }
    }

    public static void SequenceEqual<T>(IEnumerable<T>? expected, IEnumerable<T>? actual)
    {
        Equal(expected is null, actual is null);

        if (expected is not null)
        {
            SequenceEqual<T>(expected.ToArray().AsSpan(), actual!.ToArray().AsSpan());
        }
    }

    public static void SequenceEqual<T>(List<T>? expected, List<T>? actual)
    {
        Equal(expected is null, actual is null);

        SequenceEqual<T>(CollectionsMarshal.AsSpan(expected), CollectionsMarshal.AsSpan(actual));
    }

    public static void SequenceEqual(ReadOnlySpan<char> expected, StringBuilder actual)
    {
        Equal(expected.Length, actual.Length);

        foreach (ReadOnlyMemory<char> chunk in actual.GetChunks())
        {
            SequenceEqual(expected.Slice(0, chunk.Length), chunk.Span);

            expected = expected.Slice(chunk.Length);
        }

        Equal(0, expected.Length);
    }

    private sealed class AssertException : Exception
    {
        public AssertException(string message) : base(message) { }
    }
}
