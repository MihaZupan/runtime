// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace System.Buffers
{
    internal sealed class AsciiStringSearchValuesTeddyBucketizedN3<TStartCaseSensitivity, TCaseSensitivity> : AsciiStringSearchValuesTeddyBase<SearchValues.TrueConst, TStartCaseSensitivity, TCaseSensitivity>
        where TStartCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
        where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
    {
        public AsciiStringSearchValuesTeddyBucketizedN3(string[][] buckets, ReadOnlySpan<string> values, HashSet<string> uniqueValues) : base(buckets, values, uniqueValues, n: 3) { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) => IndexOfAnyN3(span);
    }
}
