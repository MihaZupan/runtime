// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace System.Buffers
{
    internal sealed class MultiStringSearchValuesFallback : StringSearchValuesBase
    {
        private readonly string[] _values;
        private readonly bool _ignoreCase;

        public MultiStringSearchValuesFallback(HashSet<string> uniqueValues, bool ignoreCase) : base(uniqueValues)
        {
            _values = new string[uniqueValues.Count];
            uniqueValues.CopyTo(_values);

            _ignoreCase = ignoreCase;

            // Sort longest first so that when multiple values match at the same position, we check and report the longest one first.
            Array.Sort(_values, static (a, b) => b.Length.CompareTo(a.Length));
        }

        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) =>
            IndexOfAnyMultiString(span, out _);

        /// <summary>
        /// This method is intentionally implemented in a way that checks haystack positions one at a time.
        /// See the description in <see cref="SpanHelpers.IndexOfAny{T}(ref T, int, ref T, int)"/>.
        /// </summary>
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span, out string? matchedValue)
        {
            string[] values = _values;
            StringComparison comparisonType = _ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

            // The condition is intentionally "<= Length" instead of "<" to ensure that an empty string value matches any input.
            for (int i = 0; i <= span.Length; i++)
            {
                ReadOnlySpan<char> remaining = span.Slice(i);

                foreach (string value in values)
                {
                    if (remaining.StartsWith(value, comparisonType))
                    {
                        matchedValue = value;
                        return i;
                    }
                }
            }

            matchedValue = null;
            return -1;
        }
    }
}
