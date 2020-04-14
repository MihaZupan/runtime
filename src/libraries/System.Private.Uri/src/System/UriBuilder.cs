// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Text;
using System.Diagnostics.CodeAnalysis;
using System.Diagnostics;

namespace System
{
    public class UriBuilder
    {
        [Flags]
        private enum Components : byte
        {
            None = 0,
            Scheme = 1,
            UserInfo = 2,
            Host = 4,
            Port = 8,
            Path = 16,
            Query = 32,
            Fragment = 64,

            All = Scheme | UserInfo | Host | Port | Path | Query | Fragment
        }

        // Cached => Up-to-date property is already stored, otherwise fetch it from _uri
        private Components _cached;

        // Regenerate _uri when Uri getter is invoked
        private bool _changed = true;

        private Uri _uri = null!; // initialized in ctor via helper

        private string _scheme = Uri.UriSchemeHttp;
        private string? _username = null;
        private string? _password = null;
        private string _host = "localhost";
        private int _port = -1;
        private string _path = "/";
        private string? _query = null;
        private string? _fragment = null;

        public UriBuilder()
        {
            _cached = Components.All;
            _changed = true;
        }

        public UriBuilder(string uri)
        {
            // setting allowRelative=true for a string like www.acme.org
            _uri = new Uri(uri, UriKind.RelativeOrAbsolute);

            if (!_uri.IsAbsoluteUri)
            {
                uri = Uri.UriSchemeHttp + Uri.SchemeDelimiter + uri;
                _uri = new Uri(uri);
            }

            _cached = Components.None;
            _changed = false;
        }

        public UriBuilder(Uri uri)
        {
            if (uri is null)
                throw new ArgumentNullException(nameof(uri));

            if (!uri.IsAbsoluteUri)
                throw new InvalidOperationException(SR.net_uri_NotAbsolute);

            _uri = uri;
            _cached = Components.None;
            _changed = false;
        }

        public UriBuilder(string? schemeName, string? hostName)
        {
            Scheme = schemeName;
            Host = hostName;
            _cached = Components.All;
        }

        public UriBuilder(string? scheme, string? host, int portNumber)
            : this(scheme, host)
        {
            Port = portNumber;
        }

        public UriBuilder(string? scheme, string? host, int port, string? pathValue)
            : this(scheme, host, port)
        {
            Path = pathValue;
        }

        public UriBuilder(string? scheme, string? host, int port, string? path, string? extraValue)
            : this(scheme, host, port, path)
        {
            // If the length of the query/fragment passed to the ctor is 1 (just ? or #), they are ignored
            if (!string.IsNullOrEmpty(extraValue))
            {
                if (extraValue[0] == '#')
                {
                    if (extraValue.Length != 1)
                    {
                        _fragment = extraValue;
                    }
                }
                else if (extraValue[0] == '?')
                {
                    int fragment = extraValue.IndexOf('#');
                    if (fragment == -1)
                    {
                        if (extraValue.Length != 1)
                        {
                            _query = extraValue;
                        }
                    }
                    else if (extraValue.Length != 2)
                    {
                        if (fragment == 1)
                        {
                            _fragment = extraValue.Substring(1);
                        }
                        else
                        {
                            _query = extraValue.Substring(0, fragment);
                            if (fragment != extraValue.Length - 1)
                            {
                                _fragment = extraValue.Substring(fragment);
                            }
                        }
                    }
                }
                else
                {
                    throw new ArgumentException(SR.Argument_ExtraNotValid, nameof(extraValue));
                }
            }
        }


        [AllowNull]
        public string Scheme
        {
            get
            {
                if ((_cached & Components.Scheme) == 0)
                {
                    _cached |= Components.Scheme;
                    _scheme = _uri.Scheme;
                }
                return _scheme;
            }
            set
            {
                value ??= string.Empty;

                if (!ReferenceEquals(value, _scheme))
                {
                    int index = value.IndexOf(':');
                    if (index != -1)
                    {
                        ReadOnlySpan<char> scheme = value.AsSpan(0, index);
                        if (scheme.Equals(_scheme, StringComparison.OrdinalIgnoreCase))
                        {
                            _cached |= Components.Scheme;
                            return;
                        }

                        // Avoid allocating in the common case
                        if (scheme.Length == 5)
                        {
                            if (scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
                            {
                                _scheme = Uri.UriSchemeHttps;
                                _cached |= Components.Scheme;
                                _changed = true;
                                return;
                            }
                        }
                        else if (scheme.Length == 4)
                        {
                            if (scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))
                            {
                                _scheme = Uri.UriSchemeHttp;
                                _cached |= Components.Scheme;
                                _changed = true;
                                return;
                            }
                        }

                        value = value.Substring(0, index);
                    }
                    else if (value.Equals(_scheme, StringComparison.OrdinalIgnoreCase))
                    {
                        _cached |= Components.Scheme;
                        return;
                    }

                    if (value.Length != 0)
                    {
                        if (!Uri.CheckSchemeName(value))
                        {
                            throw new ArgumentException(SR.net_uri_BadScheme, nameof(value));
                        }
                        value = value.ToLowerInvariant();
                    }

                    _scheme = value;
                    _changed = true;
                }

                _cached |= Components.Scheme;
            }
        }

        [AllowNull]
        public string UserName
        {
            get
            {
                if ((_cached & Components.UserInfo) == 0)
                {
                    SetUserInfoFromUri();
                }
                return _username ?? string.Empty;
            }
            set
            {
                if ((_cached & Components.UserInfo) == 0)
                {
                    SetUserInfoFromUri();
                }

                if (string.IsNullOrEmpty(value))
                {
                    if (_username != null)
                    {
                        _username = null;
                        _changed = true;
                    }
                }
                else if (_changed || !value.Equals(_username))
                {
                    _username = value;
                    _changed = true;
                }
            }
        }

        [AllowNull]
        public string Password
        {
            get
            {
                if ((_cached & Components.UserInfo) == 0)
                {
                    SetUserInfoFromUri();
                }
                return _password ?? string.Empty;
            }
            set
            {
                if ((_cached & Components.UserInfo) == 0)
                {
                    SetUserInfoFromUri();
                }

                if (string.IsNullOrEmpty(value))
                {
                    if (_password != null)
                    {
                        _password = null;
                        _changed = true;
                    }
                }
                else if (_changed || !value.Equals(_password))
                {
                    _password = value;
                    _changed = true;
                }
            }
        }

        [AllowNull]
        public string Host
        {
            get
            {
                if ((_cached & Components.Host) == 0)
                {
                    _cached |= Components.Host;
                    _host = _uri.Host;
                }
                return _host;
            }
            set
            {
                _cached |= Components.Host;

                value ??= string.Empty;

                if (!ReferenceEquals(value, _host))
                {
                    //probable ipv6 address - Note: this is only supported for cases where the authority is inet-based.
                    if (value.Contains(':'))
                    {
                        //set brackets
                        if (value[0] != '[')
                        {
                            if (value.Length + 2 == _host.Length && _host.AsSpan(1, _host.Length - 2).SequenceEqual(value))
                                return;

                            value = "[" + value + "]";
                        }
                    }
                    else if (!_changed && value.Equals(_host))
                    {
                        return;
                    }

                    _host = value;
                    _changed = true;
                }
            }
        }

        public int Port
        {
            get
            {
                if ((_cached & Components.Port) == 0)
                {
                    _cached |= Components.Port;
                    _port = _uri.Port;
                }
                return _port;
            }
            set
            {
                if (value < -1 || value > 0xFFFF)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                _cached |= Components.Port;

                if (value != _port)
                {
                    _port = value;
                    _changed = true;
                }
            }
        }

        [AllowNull]
        public string Path
        {
            get
            {
                if ((_cached & Components.Path) == 0)
                {
                    _cached |= Components.Path;
                    _path = _uri.AbsolutePath;
                }
                return _path;
            }
            set
            {
                _cached |= Components.Path;

                if (string.IsNullOrEmpty(value))
                {
                    if (_path != "/")
                    {
                        _path = "/";
                        _changed = true;
                    }
                }
                else if (!ReferenceEquals(value, _path))
                {
                    value = Uri.InternalEscapeString(value.Replace('\\', '/'));

                    if (_changed || !value.Equals(_path))
                    {
                        _path = value;
                        _changed = true;
                    }
                }
            }
        }

        [AllowNull]
        public string Query
        {
            get
            {
                if ((_cached & Components.Query) == 0)
                {
                    _cached |= Components.Query;
                    string query = _uri.Query;
                    _query = query.Length == 0 ? null : query;
                }
                return _query ?? string.Empty;
            }
            set
            {
                _cached |= Components.Query;

                if (string.IsNullOrEmpty(value))
                {
                    if (_query != null)
                    {
                        _query = null;
                        _changed = true;
                    }
                }
                else if (!ReferenceEquals(value, _query))
                {
                    if (value[0] != '?')
                    {
                        if (_query is null || !_query.AsSpan(1).SequenceEqual(value))
                        {
                            _query = "?" + value;
                            _changed = true;
                        }
                    }
                    else if (_changed || !value.Equals(_query))
                    {
                        _query = value;
                        _changed = true;
                    }
                }
            }
        }

        [AllowNull]
        public string Fragment
        {
            get
            {
                if ((_cached & Components.Fragment) == 0)
                {
                    _cached |= Components.Fragment;
                    string fragment = _uri.Fragment;
                    _fragment = fragment.Length == 0 ? null : fragment;
                }
                return _fragment ?? string.Empty;
            }
            set
            {
                _cached |= Components.Fragment;

                if (string.IsNullOrEmpty(value))
                {
                    if (_fragment != null)
                    {
                        _fragment = null;
                        _changed = true;
                    }
                }
                else if (!ReferenceEquals(value, _fragment))
                {
                    if (value[0] != '#')
                    {
                        if (_fragment is null || !_fragment.AsSpan(1).SequenceEqual(value))
                        {
                            _fragment = "#" + value;
                            _changed = true;
                        }
                    }
                    else if (_changed || !value.Equals(_fragment))
                    {
                        _fragment = value;
                        _changed = true;
                    }
                }
            }
        }

        public Uri Uri
        {
            get
            {
                if (_changed)
                {
                    var uri = new Uri(ToString());

                    if (!uri.IsAbsoluteUri)
                        throw new InvalidOperationException(SR.net_uri_NotAbsolute);

                    _uri = uri;
                    _cached = Components.None;
                    _changed = false;
                }
                return _uri;
            }
        }

        private void SetUserInfoFromUri()
        {
            Debug.Assert((_cached & Components.UserInfo) == 0);

            _cached |= Components.UserInfo;

            string userInfo = _uri.UserInfo;
            if (userInfo.Length == 0)
            {
                _username = null;
                _password = null;
            }
            else
            {
                int index = userInfo.IndexOf(':');

                if (index != -1)
                {
                    ReadOnlySpan<char> username = userInfo.AsSpan(0, index);
                    if (_username is null || !username.SequenceEqual(_username))
                        _username = username.ToString();

                    ReadOnlySpan<char> password = userInfo.AsSpan(index + 1);
                    if (_password is null || !password.SequenceEqual(_password))
                        _password = password.ToString();
                }
                else
                {
                    _username = userInfo;
                    _password = null;
                }
            }
        }

        public override bool Equals(object? rparam)
        {
            if (rparam == null)
            {
                return false;
            }
            return Uri.Equals(rparam.ToString());
        }

        public override int GetHashCode()
        {
            return Uri.GetHashCode();
        }

        public override string ToString()
        {
            if (UserName.Length == 0 && _password != null)
            {
                throw new UriFormatException(SR.net_uri_BadUserPassword);
            }

            ValueStringBuilder dest = new ValueStringBuilder(stackalloc char[512]);

            string scheme = Scheme;
            string host = Host;

            if (scheme.Length != 0)
            {
                dest.Append(scheme);

                UriParser? syntax = UriParser.GetSyntax(scheme);

                bool authorityDelimiter = syntax is null
                    ? host.Length != 0
                    : syntax.InFact(UriSyntaxFlags.MustHaveAuthority) ||
                        (host.Length != 0 && syntax.NotAny(UriSyntaxFlags.MailToLikeUri) && syntax.InFact(UriSyntaxFlags.OptionalAuthority));

                dest.Append(authorityDelimiter ? Uri.SchemeDelimiter : ":");
            }

            if (!string.IsNullOrEmpty(_username))
            {
                dest.Append(_username);
                if (!string.IsNullOrEmpty(_password))
                {
                    dest.Append(':');
                    dest.Append(_password);
                }
                dest.Append('@');
            }

            if (_host.Length != 0)
            {
                dest.Append(_host);

                int port = Port;
                if (port != -1)
                {
                    dest.Append(':');
                    dest.AppendNumber((ushort)port);
                }
            }

            string path = Path;
            if (path.Length != 0)
            {
                if (_host.Length != 0 && path[0] != '/')
                    dest.Append('/');

                dest.Append(path);
            }

            dest.Append(Query);
            dest.Append(Fragment);

            Debug.Assert(_host.Length == 0 ? (_cached & ~Components.Port) == ~Components.Port : _cached == Components.All);
            return dest.ToString();
        }
    }
}
