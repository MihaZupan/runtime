// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace System
{
    public class UriBuilder
    {
        private static readonly Uri _defaultUri = new("http://localhost");

        [Flags]
        private enum Components : byte
        {
            None = 0,
            Scheme = 1 << 0,
            Username = 1 << 1,
            Password = 1 << 2,
            Host = 1 << 3,
            Port = 1 << 4,
            Path = 1 << 5,
            Query = 1 << 6,
            Fragment = 1 << 7
        }

        private Components _changed;

        private string? _scheme;
        private string? _username;
        private string? _password;
        private string? _host;
        private ushort? _port;
        private string? _path;
        private string? _query;
        private string? _fragment;

        private Uri _uri;

        private static void SplitIfNull(string source, char delimiter, ref string? left, ref string? right, bool includeDelimiter = true)
        {
            Debug.Assert(left is null || right is null);

            int index = source.IndexOf(delimiter);
            if (index == -1)
            {
                left ??= source;
                right ??= string.Empty;
            }
            else
            {
                left ??= source.Substring(0, index);
                right ??= source.Substring(index + (includeDelimiter ? 0 : 1));
            }
        }

        [AllowNull]
        public string Scheme
        {
            get => _scheme ??= _uri.Scheme;
            set
            {
                value ??= string.Empty;

                if (value.Length != 0)
                {
                    if (!Uri.CheckSchemeName(value))
                    {
                        int index = value.IndexOf(':');
                        if (index != -1)
                        {
                            value = value.Substring(0, index);
                        }

                        if (!Uri.CheckSchemeName(value))
                        {
                            throw new ArgumentException(SR.net_uri_BadScheme, nameof(value));
                        }
                    }
                }

                _scheme = value.ToLowerInvariant();
                _changed |= Components.Scheme;
            }
        }

        [AllowNull]
        public string UserName
        {
            get
            {
                if (_username is null)
                {
                    SplitIfNull(_uri.UserInfo, ':', ref _username, ref _password, includeDelimiter: false);
                    Debug.Assert(_username is not null);
                }
                return _username;
            }
            set
            {
                _username = value ?? string.Empty;
                _changed |= Components.Username;
            }
        }

        [AllowNull]
        public string Password
        {
            get
            {
                if (_password is null)
                {
                    SplitIfNull(_uri.UserInfo, ':', ref _username, ref _password, includeDelimiter: false);
                    Debug.Assert(_password is not null);
                }
                return _password;
            }
            set
            {
                _password = value ?? string.Empty;
                _changed |= Components.Password;
            }
        }

        [AllowNull]
        public string Host
        {
            get => _host ??= _uri.Host;
            set
            {
                if (!string.IsNullOrEmpty(value) && value.Contains(':') && value[0] != '[')
                {
                    //probable ipv6 address - Note: this is only supported for cases where the authority is inet-based.
                    value = "[" + value + "]";
                }

                _host = value ?? string.Empty;
                _changed |= Components.Host;
            }
        }

        public int Port
        {
            get => _port ?? -1;
            set
            {
                if (value < -1 || value > 0xFFFF)
                {
                    throw new ArgumentOutOfRangeException(nameof(value));
                }

                ushort? newValue = value == -1 ? null : (ushort)value;
                if (_port != newValue)
                {
                    _port = newValue;
                    _changed |= Components.Port;
                }
            }
        }

        [AllowNull]
        public string Path
        {
            get
            {
                if (_path is null)
                {
                    SplitIfNull(_uri.PathAndQuery, '?', ref _path, ref _query);
                    Debug.Assert(_path is not null);
                }
                return _path;
            }
            set
            {
                _path = string.IsNullOrEmpty(value)
                    ? "/"
                    : Uri.InternalEscapeString(value.Replace('\\', '/'));

                _changed |= Components.Path;
            }
        }

        [AllowNull]
        public string Query
        {
            get
            {
                if (_query is null)
                {
                    SplitIfNull(_uri.PathAndQuery, '?', ref _path, ref _query);
                    Debug.Assert(_query is not null);
                }
                return _query;
            }
            set
            {
                if (!string.IsNullOrEmpty(value) && value[0] != '?')
                {
                    value = '?' + value;
                }

                _query = value ?? string.Empty;
                _changed |= Components.Query;
            }
        }

        [AllowNull]
        public string Fragment
        {
            get => _fragment ??= _uri.Fragment;
            set
            {
                if (!string.IsNullOrEmpty(value) && value[0] != '#')
                {
                    value = '#' + value;
                }

                _fragment = value ?? string.Empty;
                _changed |= Components.Fragment;
            }
        }

        public UriBuilder()
        {
            _uri = _defaultUri;
        }

        public UriBuilder(string uri)
        {
            // setting allowRelative=true for a string like www.acme.org
            _uri = new Uri(uri, UriKind.RelativeOrAbsolute);

            if (!_uri.IsAbsoluteUri)
            {
                _uri = new Uri(Uri.UriSchemeHttp + Uri.SchemeDelimiter + uri);
            }

            Port = _uri.Port;
            _changed = Components.None;
        }

        public UriBuilder(Uri uri)
        {
            if (uri is null)
                throw new ArgumentNullException(nameof(uri));

            _uri = uri;
            Port = _uri.Port;
            _changed = Components.None;
        }

        public UriBuilder(string? schemeName, string? hostName)
            : this()
        {
            Scheme = schemeName;
            Host = hostName;
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
            if (string.IsNullOrEmpty(extraValue))
            {
                _query = string.Empty;
                _fragment = string.Empty;
            }
            else if (extraValue[0] == '#')
            {
                _query = string.Empty;
                _fragment = extraValue;
            }
            else if (extraValue[0] == '?')
            {
                SplitIfNull(extraValue, '#', ref _query, ref _fragment);
                Debug.Assert(_query is not null && _fragment is not null);
            }
            else
            {
                throw new ArgumentException(SR.Argument_ExtraNotValid, nameof(extraValue));
            }

            if (_query.Length == 1)
            {
                _query = string.Empty;
            }

            if (_fragment.Length == 1)
            {
                _fragment = string.Empty;
            }

            _changed |= Components.Query | Components.Fragment;
        }

        public Uri Uri
        {
            get
            {
                if (_changed != Components.None)
                {
                    _uri = new Uri(ToString());
                    Port = _uri.Port;

                    _changed = Components.None;
                    _scheme = null;
                    _username = null;
                    _password = null;
                    _host = null;
                    _path = null;
                    _query = null;
                    _fragment = null;
                }
                return _uri;
            }
        }

        public override string ToString()
        {
            if (UserName.Length == 0 && Password.Length != 0)
            {
                throw new UriFormatException(SR.net_uri_BadUserPassword);
            }

            var vsb = new ValueStringBuilder(stackalloc char[Uri.StackallocThreshold]);

            string scheme = Scheme;
            string host = Host;

            if (scheme.Length != 0)
            {
                UriParser? syntax = UriParser.GetSyntax(scheme);
                string schemeDelimiter;
                if (syntax is null)
                {
                    schemeDelimiter = host.Length == 0 ? ":" : Uri.SchemeDelimiter;
                }
                else
                {
                    schemeDelimiter = syntax.InFact(UriSyntaxFlags.MustHaveAuthority)
                        || (host.Length != 0 && syntax.NotAny(UriSyntaxFlags.MailToLikeUri) && syntax.InFact(UriSyntaxFlags.OptionalAuthority))
                            ? Uri.SchemeDelimiter
                            : ":";
                }

                vsb.Append(scheme);
                vsb.Append(schemeDelimiter);
            }

            string username = UserName;
            if (username.Length != 0)
            {
                vsb.Append(username);

                string password = Password;
                if (password.Length != 0)
                {
                    vsb.Append(':');
                    vsb.Append(password);
                }

                vsb.Append('@');
            }

            if (host.Length != 0)
            {
                vsb.Append(host);

                if (_port.HasValue)
                {
                    vsb.Append(':');

                    const int MaxUshortLength = 5;
                    bool success = _port.Value.TryFormat(vsb.AppendSpan(MaxUshortLength), out int charsWritten);
                    vsb.Length -= MaxUshortLength - charsWritten;
                }
            }

            if ((_changed & (Components.Path | Components.Query)) == 0)
            {
                // If path and query haven't been changed, avoid allocating substrings
                vsb.Append(_uri.PathAndQuery);
            }
            else
            {
                var path = Path;
                if (path.Length != 0)
                {
                    if (!path.StartsWith('/') && host.Length != 0)
                    {
                        vsb.Append('/');
                    }

                    vsb.Append(path);
                }

                vsb.Append(Query);
            }

            vsb.Append(Fragment);

            return vsb.ToString();
        }

        public override bool Equals([NotNullWhen(true)] object? obj) => obj is not null && Uri.Equals(obj.ToString());

        public override int GetHashCode() => Uri.GetHashCode();
    }
}
