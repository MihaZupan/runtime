// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics.CodeAnalysis;
using System.Net.Http.HPack;

namespace System.Net.Http.Headers
{
    internal sealed partial class KnownHeader
    {
        [MemberNotNull(nameof(Http2EncodedName))]
        partial void Initialize(int? http2StaticTableIndex, int? http3StaticTableIndex)
        {
            Http2EncodedName = http2StaticTableIndex.HasValue ?
                HPackEncoder.EncodeLiteralHeaderFieldWithoutIndexingToAllocatedArray(http2StaticTableIndex.GetValueOrDefault()) :
                HPackEncoder.EncodeLiteralHeaderFieldWithoutIndexingNewNameToAllocatedArray(Name);
        }

        public byte[] Http2EncodedName { get; private set; }
    }
}
