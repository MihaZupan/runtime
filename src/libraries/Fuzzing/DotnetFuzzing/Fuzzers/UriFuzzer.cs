// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using SharpFuzz;

namespace DotnetFuzzing.Fuzzers
{
    internal sealed class UriFuzzer : IFuzzer
    {
        public string[] TargetAssemblies => ["System.Private.Uri"];
        public string[] TargetCoreLibPrefixes => ["System.Globalization", "System.Text"];

        // There's nothing particularly interesting in Uri logic that would require longer inputs. Reduce the default (4096) to speed up fuzzing.
        public int MaxInputLength => 2048;

        private const int OperationCount = 20;
        private const int MaxOperationTypes = 1024; // 256 regular operations + (3 * 256) GetComponents combinations

        private readonly ushort[] _operations = new ushort[OperationCount];
        private readonly string[] _results = new string[MaxOperationTypes];

        public void FuzzTarget(ReadOnlySpan<byte> bytes)
        {
            const int OptionsBytes = 4;

            if (bytes.Length < OptionsBytes + (OperationCount * sizeof(ushort)) ||
                bytes[0] > 2) // Invalid UriKind
            {
                return;
            }

            UriKind kind = (UriKind)bytes[0];
            var options = new UriCreationOptions
            {
                DangerousDisablePathAndQueryCanonicalization = bytes[1] != 0
            };
            bool userEscaped = bytes[2] != 0;

            // We're only using 3 bytes above, but we want chars to be 2-byte aligned.
            bytes = bytes.Slice(4);

            MemoryMarshal.Cast<byte, ushort>(bytes).Slice(0, OperationCount).CopyTo(_operations);
            bytes = bytes.Slice(OperationCount * sizeof(ushort));

            foreach (ushort op in _operations)
            {
                if (op >= MaxOperationTypes)
                {
                    return;
                }
            }

            Array.Clear(_results);

            var chars = new string(MemoryMarshal.Cast<byte, char>(bytes));

            //Debugger.Launch();

            if (userEscaped && kind == UriKind.Absolute)
            {
#pragma warning disable CS0618 // Type or member is obsolete
                Uri uri1;
                try
                {
                    uri1 = new Uri(chars, userEscaped);
                }
                catch (UriFormatException)
                {
                    return;
                }

                Uri uri2 = new Uri(chars, userEscaped);
#pragma warning restore CS0618

                AssertSameInternals(uri1, uri2);

                TestOperations(uri1, uri2, absolute: true, options: default);

                AssertSameInternals(uri1, uri2);
            }
            else if (TryCreate(chars, options, kind, out Uri? uri1))
            {
                Uri uri2 = Create(chars, options, kind);

                AssertSameInternals(uri1, uri2);

                if (uri1.IsAbsoluteUri) // TEMP
                {
                    TestOperations(uri1, uri2, uri1.IsAbsoluteUri, options);
                }

                AssertSameInternals(uri1, uri2);
            }
            else
            {
                Assert.Throws<UriFormatException>(() => Create(chars, options, kind));
            }

            static bool TryCreate(string uriString, UriCreationOptions options, UriKind kind, [NotNullWhen(true)] out Uri? uri)
            {
                return kind == UriKind.Absolute
                    ? Uri.TryCreate(uriString, options, out uri)
                    : Uri.TryCreate(uriString, kind, out uri);
            }

            static Uri Create(string uriString, UriCreationOptions options, UriKind kind)
            {
                return kind == UriKind.Absolute
                    ? new Uri(uriString, options)
                    : new Uri(uriString, kind);
            }
        }

        private void TestOperations(Uri uri1, Uri uri2, bool absolute, UriCreationOptions options)
        {
            Assert.Equal(absolute, uri1.IsAbsoluteUri);
            Assert.Equal(absolute, uri2.IsAbsoluteUri);

            foreach (ushort op in _operations)
            {
                string result = PickOperation(op, absolute, options)(uri1);

                if (_results[op] == null)
                {
                    _results[op] = result;
                }
                else
                {
                    Assert.Equal(_results[op], result);
                }
            }

            new Random(42).Shuffle(_operations);

            foreach (ushort op in _operations)
            {
                string result = PickOperation(op, absolute, options)(uri2);
                Assert.Equal(_results[op], result);
            }
        }

        private static void AssertSameInternals(Uri uri1, Uri uri2)
        {
            //Assert.Equal(UriInternals.Flags(uri1), UriInternals.Flags(uri2));
            Assert.Same(UriInternals.Syntax(uri1), UriInternals.Syntax(uri2));
            //Assert.Equal(UriInternals.String(uri1), UriInternals.String(uri2));
            Assert.Same(uri1.OriginalString, uri2.OriginalString);
        }

        private static Func<Uri, string> PickOperation(ushort op, bool absolute, UriCreationOptions options)
        {
            if (!absolute)
            {
                return op switch
                {
                    0 => uri => uri.ToString(),
                    1 => uri => uri.GetHashCode().ToString(),
                    2 => uri => uri.IsWellFormedOriginalString().ToString(),
                    3 => uri => uri.GetComponents(UriComponents.SerializationInfoString, UriFormat.Unescaped),
                    4 => uri => uri.GetComponents(UriComponents.SerializationInfoString, UriFormat.SafeUnescaped),
                    5 => uri => uri.GetComponents(UriComponents.SerializationInfoString, UriFormat.UriEscaped),
                    _ => _ => string.Empty
                };
            }

            if (op >= 256)
            {
                return GetComponents(op, options);
            }

            return op switch
            {
                0 => uri => uri.AbsoluteUri,
                1 => uri => uri.AbsolutePath,
                2 => uri => uri.Scheme,
                3 => uri => uri.UserInfo,
                4 => uri => uri.Host,
                5 => uri => uri.DnsSafeHost,
                6 => uri =>
                {
                    try
                    {
                        return uri.IdnHost;
                    }
                    catch (Exception ex) when (ex.Message == "An invalid Unicode character by IDN standards was specified in the host.")
                    {
                        return "<idn error>";
                    }
                },
                7 => uri => uri.Port.ToString(),
                8 => uri => uri.IsDefaultPort.ToString(),
                9 => uri => uri.Authority,
                10 => uri => uri.LocalPath,
                11 => uri => uri.PathAndQuery,
                12 => uri => uri.Query,
                13 => uri => uri.Fragment,
                14 => uri => uri.GetHashCode().ToString(),
                15 => uri => uri.ToString(),
                16 => uri => uri.IsFile.ToString(),
                17 => uri => uri.IsUnc.ToString(),
                18 => uri => uri.IsLoopback.ToString(),
                19 => uri => uri.IsWellFormedOriginalString().ToString(),
                20 => uri => string.Join('a', uri.Segments),
                21 => uri => uri.GetComponents(UriComponents.SerializationInfoString, UriFormat.Unescaped),
                22 => uri => uri.GetComponents(UriComponents.SerializationInfoString, UriFormat.SafeUnescaped),
                23 => uri => uri.GetComponents(UriComponents.SerializationInfoString, UriFormat.UriEscaped),
                _ => _ => string.Empty
            };

            static Func<Uri, string> GetComponents(ushort op, UriCreationOptions options)
            {
                Assert.True(op is >= 256 and < MaxOperationTypes);
                op -= 256;

                UriFormat format = (UriFormat)((op % 3) + 1); // UriFormat enum has values 1, 2, 3
                op /= 3;

                Assert.True(op is >= 0 and < 256);

                // There are 7 different component types of UriComponents. We use the 8th bit to encode the KeepDelimiter flag.
                UriComponents components = (UriComponents)op;

                if ((op & 128) != 0)
                {
                    components &= ~(UriComponents)128;
                    components |= UriComponents.KeepDelimiter;
                }

                if (options.DangerousDisablePathAndQueryCanonicalization)
                {
                    components &= ~(UriComponents.Path | UriComponents.Query);
                }

                return uri => uri.GetComponents(components, format);
            }
        }

        private static class UriInternals
        {
            private static readonly FieldInfo s_flagsField = typeof(Uri).GetField("_flags", BindingFlags.Instance | BindingFlags.NonPublic)!;
            private static readonly FieldInfo s_syntaxField = typeof(Uri).GetField("_syntax", BindingFlags.Instance | BindingFlags.NonPublic)!;
            private static readonly FieldInfo s_stringField = typeof(Uri).GetField("_string", BindingFlags.Instance | BindingFlags.NonPublic)!;

            public static ulong Flags(Uri uri) => (ulong)s_flagsField.GetValue(uri)!;
            public static object? Syntax(Uri uri) => s_syntaxField.GetValue(uri);
            public static string String(Uri uri) => (string)s_stringField.GetValue(uri)!;
        }
    }
}
