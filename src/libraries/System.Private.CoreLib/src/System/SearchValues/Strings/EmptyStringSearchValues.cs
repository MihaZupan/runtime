// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;

namespace System.Buffers
{
    internal sealed class EmptyStringSearchValues : StringSearchValuesBase
    {
        public EmptyStringSearchValues(HashSet<string> uniqueValues) : base(uniqueValues) { }

        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) => 0;
    }
}
