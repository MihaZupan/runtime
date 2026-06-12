// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Runtime.CompilerServices;

namespace System.Buffers
{
    internal sealed class SingleStringSearchValuesFallback<TIgnoreCase> : StringSearchValuesBase
        where TIgnoreCase : struct, SearchValues.IRuntimeConst
    {
        private readonly string _value;

        public SingleStringSearchValuesFallback(string value) : base(uniqueValues: null)
        {
            _value = value;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) =>
            TIgnoreCase.Value
                ? Ordinal.IndexOfOrdinalIgnoreCase(span, _value)
                : span.IndexOf(_value);

        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span, out string? matchedValue)
        {
            int index = IndexOfAnyMultiString(span);
            matchedValue = index >= 0 ? _value : null;
            return index;
        }

        internal override bool ContainsCore(string value) =>
            _value.Equals(value, TIgnoreCase.Value ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

        internal override string[] GetValues() =>
            [_value];
    }
}
