// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Globalization;

namespace System.Buffers
{
    internal sealed class SingleStringSearchValuesFallback<TIgnoreCase> : StringSearchValuesBase
        where TIgnoreCase : struct, SearchValues.IRuntimeConst
    {
        private readonly string _value;

        public SingleStringSearchValuesFallback(string value, HashSet<string> uniqueValues) : base(uniqueValues)
        {
            _value = value;
        }

        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) =>
            TIgnoreCase.Value
                ? Ordinal.IndexOfOrdinalIgnoreCase(span, _value)
                : span.IndexOf(_value);
    }
}
