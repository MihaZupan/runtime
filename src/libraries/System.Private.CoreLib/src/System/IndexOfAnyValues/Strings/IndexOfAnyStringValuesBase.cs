// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace System.Buffers
{
    internal abstract class IndexOfAnyStringValuesBase : IndexOfAnyValues<string>
    {
        protected readonly HashSet<string> UniqueValues;

        public IndexOfAnyStringValuesBase(HashSet<string> uniqueValues) =>
            UniqueValues = uniqueValues;

        internal sealed override bool ContainsCore(string value) =>
            UniqueValues.Contains(value);

        internal sealed override string[] GetValues()
        {
            string[] values = new string[UniqueValues.Count];
            UniqueValues.CopyTo(values);
            return values;
        }

        internal sealed override int IndexOfAny(ReadOnlySpan<string> span) =>
            IndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(span);

        internal sealed override int IndexOfAnyExcept(ReadOnlySpan<string> span) =>
            IndexOfAny<IndexOfAnyAsciiSearcher.Negate>(span);

        internal sealed override int LastIndexOfAny(ReadOnlySpan<string> span) =>
            LastIndexOfAny<IndexOfAnyAsciiSearcher.DontNegate>(span);

        internal sealed override int LastIndexOfAnyExcept(ReadOnlySpan<string> span) =>
            LastIndexOfAny<IndexOfAnyAsciiSearcher.Negate>(span);

        private int IndexOfAny<TNegator>(ReadOnlySpan<string> span)
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            for (int i = 0; i < span.Length; i++)
            {
                if (TNegator.NegateIfNeeded(UniqueValues.Contains(span[i])))
                {
                    return i;
                }
            }

            return -1;
        }

        private int LastIndexOfAny<TNegator>(ReadOnlySpan<string> span)
            where TNegator : struct, IndexOfAnyAsciiSearcher.INegator
        {
            for (int i = span.Length - 1; i >= 0; i--)
            {
                if (TNegator.NegateIfNeeded(UniqueValues.Contains(span[i])))
                {
                    return i;
                }
            }

            return -1;
        }
    }
}
