// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace System.Buffers
{
    internal sealed class SingleStringSearchValuesMultiCharsN3<TValueLength, TCaseSensitivity> : SingleStringSearchValuesMultiCharsBase<TValueLength, TCaseSensitivity>
        where TValueLength : struct, TeddyHelper.IValueLength
        where TCaseSensitivity : struct, TeddyHelper.ICaseSensitivity
    {
        public SingleStringSearchValuesMultiCharsN3(string value, HashSet<string> uniqueValues, int ch2Offset, int ch3Offset)
            : base(value, uniqueValues, ch2Offset, ch3Offset)
        { }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal override int IndexOfAnyMultiString(ReadOnlySpan<char> span) =>
            IndexOfN3(ref MemoryMarshal.GetReference(span), span.Length);
    }
}
