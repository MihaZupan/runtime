// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace System.Buffers
{
    internal sealed class IndexOfAnySingleAsciiStringValueN3<TLongString, TStartCaseSensitivity, TCaseSensitivity> : IndexOfAnySingleAsciiStringValueBase<TLongString, TStartCaseSensitivity, TCaseSensitivity>
        where TLongString : struct, IndexOfAnyValues.IRuntimeConst
        where TStartCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
        where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
    {
        public IndexOfAnySingleAsciiStringValueN3(string value, HashSet<string> uniqueValues) : base(value, uniqueValues, n: 3) { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) => IndexOfAnyN3(span);
    }
}
