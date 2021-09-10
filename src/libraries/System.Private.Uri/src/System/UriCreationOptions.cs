// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace System
{
    public readonly struct UriCreationOptions
    {
        internal readonly Uri.Flags _flags;
        private readonly UriKind _uriKind;

        public UriKind UriKind => _uriKind + 1; // _uriKind is offset by 1 so that default(UriCreationOptions) is UriKind.Absolute

        public bool DangerousUseRawTarget
        {
            get => (_flags & Uri.Flags.UseRawTarget) != 0;
            init
            {
                if (value)
                {
                    _flags |= Uri.Flags.UseRawTarget;
                }
                else
                {
                    _flags &= ~Uri.Flags.UseRawTarget;
                }
            }
        }

        public bool AllowImplicitFilePaths
        {
            get => (_flags & Uri.Flags.DisableImplicitFilePaths) == 0;
            init
            {
                if (value)
                {
                    _flags &= ~Uri.Flags.DisableImplicitFilePaths;
                }
                else
                {
                    _flags |= Uri.Flags.DisableImplicitFilePaths;
                }
            }
        }

        public UriCreationOptions(UriKind uriKind)
        {
            _flags = 0;
            _uriKind = uriKind - 1;

            if ((uint)uriKind > (uint)UriKind.Relative)
            {
                throw new ArgumentException(SR.Format(SR.net_uri_InvalidUriKind, uriKind), nameof(uriKind));
            }
        }

        internal UriCreationOptions(UriKind uriKind, bool dontEscape)
            : this(uriKind)
        {
            _flags = dontEscape ? Uri.Flags.UserEscaped : 0;
        }

        internal UriCreationOptions(Uri uri, UriKind uriKind)
            : this(uriKind)
        {
            _flags = uri._flags & Uri.Flags.CreationOptionsFlags;
        }
    }
}
