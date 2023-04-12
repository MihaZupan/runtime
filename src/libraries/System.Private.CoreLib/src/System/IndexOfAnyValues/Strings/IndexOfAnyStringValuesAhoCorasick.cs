// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace System.Buffers
{
    internal sealed class IndexOfAnyStringValuesAhoCorasick<TCaseSensitivity, TFastScanVariant> : IndexOfAnyStringValuesBase
        where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
        where TFastScanVariant : struct, AhoCorasick.IFastScan
    {
        private readonly AhoCorasick _ahoCorasick;

        public IndexOfAnyStringValuesAhoCorasick(AhoCorasick ahoCorasick, HashSet<string> uniqueValues) : base(uniqueValues)
        {
            _ahoCorasick = ahoCorasick;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) =>
            _ahoCorasick.IndexOfAny<TCaseSensitivity, TFastScanVariant>(span);
    }
}
