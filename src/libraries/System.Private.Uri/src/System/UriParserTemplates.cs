// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System
{
    public class HttpStyleUriParser : UriParser
    {
        public HttpStyleUriParser() : base(HttpSyntaxFlags)
        {
        }
    }

    public class FtpStyleUriParser : UriParser
    {
        public FtpStyleUriParser() : base(FtpSyntaxFlags)
        {
        }
    }

    public class FileStyleUriParser : UriParser
    {
        public FileStyleUriParser() : base(FileSyntaxFlags)
        {
        }
    }

    public class NewsStyleUriParser : UriParser
    {
        public NewsStyleUriParser() : base(NewsSyntaxFlags)
        {
        }
    }

    public class GopherStyleUriParser : UriParser
    {
        public GopherStyleUriParser() : base(GopherSyntaxFlags)
        {
        }
    }

    public class LdapStyleUriParser : UriParser
    {
        public LdapStyleUriParser() : base(LdapSyntaxFlags)
        {
        }
    }

    public class NetPipeStyleUriParser : UriParser
    {
        public NetPipeStyleUriParser() : base(NetPipeSyntaxFlags)
        {
        }
    }

    public class NetTcpStyleUriParser : UriParser
    {
        public NetTcpStyleUriParser() : base(NetTcpSyntaxFlags)
        {
        }
    }
}
