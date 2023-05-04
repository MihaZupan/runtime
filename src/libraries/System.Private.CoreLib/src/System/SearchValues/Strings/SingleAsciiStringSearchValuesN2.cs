// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace System.Buffers
{
    internal sealed class SingleAsciiStringSearchValuesN2<TLongString, TStartCaseSensitivity, TCaseSensitivity> : SingleAsciiStringSearchValuesBase<TLongString, TStartCaseSensitivity, TCaseSensitivity>
        where TLongString : struct, SearchValues.IRuntimeConst
        where TStartCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
        where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
    {
        public SingleAsciiStringSearchValuesN2(string value, HashSet<string> uniqueValues) : base(value, uniqueValues, n: 2) { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) => IndexOfAnyN2(span);
    }
}
