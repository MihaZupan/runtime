// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace System.Buffers
{
    internal sealed class IndexOfAnyStringEmptyValues : IndexOfAnyStringValuesBase
    {
        public IndexOfAnyStringEmptyValues(HashSet<string> values) : base(values) { }

        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span)
        {
            if (UniqueValues.Count == 0 || span.IsEmpty)
            {
                return -1;
            }

            return 0;
        }
    }
}
