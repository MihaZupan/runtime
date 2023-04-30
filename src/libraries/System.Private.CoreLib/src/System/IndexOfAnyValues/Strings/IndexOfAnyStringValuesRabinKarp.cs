// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace System.Buffers
{
    internal abstract class IndexOfAnyStringValuesRabinKarp<TCaseSensitivity> : IndexOfAnyStringValuesBase
        where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
    {
        private readonly RabinKarp _rabinKarp;

        public IndexOfAnyStringValuesRabinKarp(RabinKarp rabinKarp, HashSet<string> uniqueValues) : base(uniqueValues) =>
            _rabinKarp = rabinKarp;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        protected int ShortInputFallback(ReadOnlySpan<char> span) =>
            _rabinKarp.IndexOfAny<TCaseSensitivity>(span);
    }
}
