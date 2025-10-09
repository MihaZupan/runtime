// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace System.Net.Http.Headers
{
    internal sealed class HttpHeaderParser
    {
        public const string DefaultSeparator = ", ";
        public static readonly byte[] DefaultSeparatorBytes = new byte[] { (byte)',', (byte)' ' };

        public string Separator { get; }

        public byte[] SeparatorBytes { get; }

        public HttpHeaderParser()
        {
            Separator = DefaultSeparator;
            SeparatorBytes = DefaultSeparatorBytes;
        }

        public HttpHeaderParser(string separator) : this()
        {
            Debug.Assert(!string.IsNullOrEmpty(separator));
            Debug.Assert(Ascii.IsValid(separator));

            Separator = separator;
            SeparatorBytes = Encoding.ASCII.GetBytes(separator);
        }
    }
}
