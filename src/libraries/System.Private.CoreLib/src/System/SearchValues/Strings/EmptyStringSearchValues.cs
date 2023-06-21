// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Diagnostics;

namespace System.Buffers
{
    internal sealed class EmptyStringSearchValues : StringSearchValuesBase
    {
        public EmptyStringSearchValues(HashSet<string> uniqueValues) : base(uniqueValues)
        {
            Debug.Assert(uniqueValues.Count == 1);
            Debug.Assert(uniqueValues.Contains(string.Empty));
        }

        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) => 0;
    }
}
