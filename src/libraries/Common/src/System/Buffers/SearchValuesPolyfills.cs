// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace System.Buffers
{
    internal static class SearchValues
    {
        public static SearchValues<char> Create(string values) =>
            Create(values.AsSpan());

        public static SearchValues<char> Create(ReadOnlySpan<char> values) =>
            new PreNet8CompatAsciiSearchValues(values);

        public static int IndexOfAny(this ReadOnlySpan<char> span, SearchValues<char> values) =>
            values.IndexOfAny(span);

        public static int IndexOfAnyExcept(this ReadOnlySpan<char> span, SearchValues<char> values) =>
            values.IndexOfAnyExcept(span);

        public static bool ContainsAny(this ReadOnlySpan<char> span, SearchValues<char> values) =>
            span.IndexOfAny(values) >= 0;

        public static bool ContainsAnyExcept(this ReadOnlySpan<char> span, SearchValues<char> values) =>
            span.IndexOfAnyExcept(values) >= 0;
    }

    internal abstract class SearchValues<T>
    {
        public abstract bool Contains(T value);

        public abstract int IndexOfAny(ReadOnlySpan<char> span);

        public abstract int IndexOfAnyExcept(ReadOnlySpan<char> span);
    }

    internal sealed class PreNet8CompatAsciiSearchValues : SearchValues<char>
    {
        private readonly BoolVector128 _ascii;

        public PreNet8CompatAsciiSearchValues(ReadOnlySpan<char> values)
        {
            Debug.Assert(Ascii.IsValid(values));

            foreach (char c in values)
            {
                _ascii.Set(c);
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public override bool Contains(char value) =>
            value < 128 && _ascii[value];

        public override int IndexOfAny(ReadOnlySpan<char> span)
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = span[i];

                if (c < 128 && _ascii[c])
                {
                    return i;
                }
            }

            return -1;
        }

        public override int IndexOfAnyExcept(ReadOnlySpan<char> span)
        {
            for (int i = 0; i < span.Length; i++)
            {
                char c = span[i];

                if (c >= 128 || !_ascii[c])
                {
                    return i;
                }
            }

            return -1;
        }

        private unsafe struct BoolVector128
        {
            private fixed bool _values[128];

            public void Set(char c)
            {
                Debug.Assert(c < 128);
                _values[c] = true;
            }

            public readonly bool this[uint c]
            {
                [MethodImpl(MethodImplOptions.AggressiveInlining)]
                get
                {
                    Debug.Assert(c < 128);
                    return _values[c];
                }
            }
        }
    }
}
