// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System;
using System.Runtime.InteropServices;
using System.Text;

internal static partial class Interop
{
    internal static partial class Normaliz
    {
        [DllImport("Normaliz.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        internal static extern bool IsNormalizedString(NormalizationForm normForm, string source, int length);

        internal static int NormalizeString(NormalizationForm normForm, string source, Span<char> destination) =>
            NormalizeString(normForm, source, source.Length, ref MemoryMarshal.GetReference(destination), destination.Length);

        [DllImport("Normaliz.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int NormalizeString(
                                        NormalizationForm normForm,
                                        string source,
                                        int sourceLength,
                                        ref char destination,
                                        int destinationLength);
    }
}
