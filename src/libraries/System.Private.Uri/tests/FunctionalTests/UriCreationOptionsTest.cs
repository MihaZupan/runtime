// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace System.PrivateUri.Tests
{
    public class UriCreationOptionsTest
    {
        [Fact]
        public void UriCreationOptions_HasReasonableDefaults()
        {
            UriCreationOptions options = default;

            Assert.Equal(UriKind.Absolute, options.UriKind);
            Assert.True(options.AllowImplicitFilePaths);
            Assert.False(options.DangerousUseRawTarget);
        }

        [Fact]
        public void UriCreationOptions_StoresCorrectValues()
        {
            var options = new UriCreationOptions(UriKind.Absolute);
            Assert.Equal(UriKind.Absolute, options.UriKind);

            options = new UriCreationOptions(UriKind.Relative);
            Assert.Equal(UriKind.Relative, options.UriKind);

            options = new UriCreationOptions(UriKind.RelativeOrAbsolute);
            Assert.Equal(UriKind.RelativeOrAbsolute, options.UriKind);

            options = new UriCreationOptions { AllowImplicitFilePaths = true };
            Assert.True(options.AllowImplicitFilePaths);

            options = new UriCreationOptions { AllowImplicitFilePaths = false };
            Assert.False(options.AllowImplicitFilePaths);

            options = new UriCreationOptions { DangerousUseRawTarget = true };
            Assert.True(options.DangerousUseRawTarget);

            options = new UriCreationOptions { DangerousUseRawTarget = false };
            Assert.False(options.DangerousUseRawTarget);
        }

        [Fact]
        public void UriCreationOptions_ValidatesInput()
        {
            Assert.Throws<ArgumentException>("uriKind", () => new UriCreationOptions((UriKind)0x42));
        }

        public static IEnumerable<object[]> UseRawTarget_TestData()
        {
            var schemes = new string[] { "http", "hTTp", " http", "https" };
            var hosts = new string[] { "foo", "f\u00F6\u00F6.com" };
            var ports = new string[] { ":80", ":443", ":0123", ":", "" };

            var pathAndQueries = new string[]
            {
                "",
                " ",
                "a b",
                "a%20b",
                "?a b",
                "?a%20b",
                "foo/./",
                "foo/../",
                "//\\//",
                "%41",
                "A?%41=%42",
                "?%41=%42",
                "? ",
            };

            var fragments = new string[] { "", "#", "#/foo ? %20%41/..//\\a" };
            var unicodeInPathModes = new int[] { 0, 1, 2, 3 };
            var pathDelimiters = new string[] { "", "/" };

            // Get various combinations of paths with unicode characters and delimiters
            string[] rawTargets = pathAndQueries
                .SelectMany(pq => fragments.Select(fragment => pq + fragment))
                .SelectMany(pqf => unicodeInPathModes.Select(unicodeMode => unicodeMode switch
                {
                    0 => pqf,
                    1 => "\u00F6" + pqf,
                    2 => pqf + "\u00F6",
                    _ => pqf.Insert(pqf.Length / 2, "\u00F6")
                }))
                .ToHashSet()
                .SelectMany(pqf => pathDelimiters.Select(delimiter => delimiter + pqf))
                .Where(target => target.StartsWith('/') || target.StartsWith('?')) // Can't see where the authority ends and the path starts otherwise
                .ToArray();

            foreach (string scheme in schemes)
            {
                foreach (string host in hosts)
                {
                    foreach (string port in ports)
                    {
                        foreach (string rawTarget in rawTargets)
                        {
                            string uriString = $"{scheme}://{host}{port}{rawTarget}";

                            int expectedPort = port.Length > 1 ? int.Parse(port.AsSpan(1)) : new Uri($"{scheme}://foo").Port;

                            string expectedQuery = rawTarget.Contains('?') ? rawTarget.Substring(rawTarget.IndexOf('?')) : "";

                            string expectedPath = rawTarget.Substring(0, rawTarget.Length - expectedQuery.Length);

                            yield return new object[] { uriString, host, expectedPort, expectedPath, expectedQuery };
                        }
                    }
                }
            }
        }

        [Theory]
        [MemberData(nameof(UseRawTarget_TestData))]
        public void UseRawTarget_IsRespected(string uriString, string expectedHost, int expectedPort, string expectedPath, string expectedQuery)
        {
            var options = new UriCreationOptions { DangerousUseRawTarget = true };

            var uri = new Uri(uriString, options);
            DoAsserts(uri);

            Assert.True(Uri.TryCreate(uriString, options, out uri));
            DoAsserts(uri);

            void DoAsserts(Uri uri)
            {
                Assert.Equal(new Uri($"http://{expectedHost}").Host, uri.Host);
                Assert.Equal(new Uri($"http://{expectedHost}").IdnHost, uri.IdnHost);

                Assert.Equal(expectedPort, uri.Port);

                Assert.Same(uri.AbsolutePath, uri.AbsolutePath);
                Assert.Equal(expectedPath, uri.AbsolutePath);
                TestComponent(uri, expectedPath, UriComponents.Path | UriComponents.KeepDelimiter);
                TestComponent(uri, expectedPath.StartsWith('/') ? expectedPath.Substring(1) : expectedPath, UriComponents.Path);

                Assert.Same(uri.Query, uri.Query);
                Assert.Equal(expectedQuery, uri.Query);
                TestComponent(uri, expectedQuery, UriComponents.Query | UriComponents.KeepDelimiter);
                TestComponent(uri, expectedQuery.StartsWith('?') ? expectedQuery.Substring(1) : expectedQuery, UriComponents.Query);

                string expectedPathAndQuery = expectedPath + expectedQuery;
                Assert.Same(uri.PathAndQuery, uri.PathAndQuery);
                Assert.Equal(expectedPathAndQuery, uri.PathAndQuery);
                TestComponent(uri, expectedPathAndQuery, UriComponents.PathAndQuery);

                Assert.Same(uri.Fragment, uri.Fragment);
                Assert.Empty(uri.Fragment); // Fragment is always empty in RawTarget mode
            }

            static void TestComponent(Uri uri, string expected, UriComponents components)
            {
                // UriFormat is ignored for PathAndQuery if UseRawTarget is set
                Assert.Equal(expected, uri.GetComponents(components, UriFormat.UriEscaped));
                Assert.Equal(expected, uri.GetComponents(components, UriFormat.Unescaped));
                Assert.Equal(expected, uri.GetComponents(components, UriFormat.SafeUnescaped));
            }
        }

        [Fact]
        public void UseRawTarget_AffectsRelativeUris()
        {
            const string RelativeUri = "f%6F%6F";

            var uri = new Uri(RelativeUri, new UriCreationOptions(UriKind.Relative) { DangerousUseRawTarget = false });
            Assert.Equal("foo", uri.ToString());

            var rawUri = new Uri(RelativeUri, new UriCreationOptions(UriKind.Relative) { DangerousUseRawTarget = true });
            Assert.Equal(RelativeUri, rawUri.ToString());
        }

        [Fact]
        public void UseRawTarget_IsPropagatedWhenCombining()
        {
            const string AbsolutePath = "/foo/bar/../ %41?B=%43";
            const string EscapedAbsolutePath = "/foo/%20A?B=C";

            var absolute = new Uri($"http://host{AbsolutePath}");
            Assert.Equal(EscapedAbsolutePath, absolute.PathAndQuery);

            var relative = new Uri(AbsolutePath, new UriCreationOptions(UriKind.Relative) { DangerousUseRawTarget = true });

            // Since relative starts with /, it replaces the path completely
            var combined = new Uri(absolute, relative);
            Assert.Equal(AbsolutePath, combined.PathAndQuery);


            relative = new Uri(AbsolutePath.Substring(1), new UriCreationOptions(UriKind.Relative) { DangerousUseRawTarget = true });

            // Since relative doesn't start with with /, the paths are combined, which includes compression (removing dot segments)
            combined = new Uri(absolute, relative);
            Assert.Equal("/foo/foo/ %41?B=%43", combined.PathAndQuery);


            absolute = new Uri($"http://host{AbsolutePath}", new UriCreationOptions { DangerousUseRawTarget = true });
            Assert.Equal(AbsolutePath, absolute.PathAndQuery);

            relative = new Uri(AbsolutePath.Substring(1), UriKind.Relative);

            // RawTarget from the base uri does not flow into the combined one
            combined = new Uri(absolute, relative);
            Assert.Equal($"/foo{EscapedAbsolutePath}", combined.PathAndQuery);

            combined = new Uri(absolute, AbsolutePath.Substring(1));
            Assert.Equal($"/foo{EscapedAbsolutePath}", combined.PathAndQuery);
        }

        [Fact]
        public void UseRawTarget_OnlyEqualToOtherRawTargetUris()
        {
            const string AbsoluteUri = "http://host";
            const string Path = "/foo";

            var relative = new Uri(Path, new UriCreationOptions(UriKind.Relative) { DangerousUseRawTarget = false });
            var relativeRaw = new Uri(Path, new UriCreationOptions(UriKind.Relative) { DangerousUseRawTarget = true });
            NotEqual(relative, relativeRaw);
            Equal(relative, relative);
            Equal(relativeRaw, relativeRaw);

            var absolute = new Uri(AbsoluteUri + Path, new UriCreationOptions(UriKind.Absolute) { DangerousUseRawTarget = false });
            var absoluteRaw = new Uri(AbsoluteUri + Path, new UriCreationOptions(UriKind.Absolute) { DangerousUseRawTarget = true });
            NotEqual(absolute, absoluteRaw);
            Equal(absolute, absolute);
            Equal(absoluteRaw, absoluteRaw);

            var absoluteRawCopy = new Uri(AbsoluteUri + Path, new UriCreationOptions(UriKind.Absolute) { DangerousUseRawTarget = true });
            Equal(absoluteRaw, absoluteRawCopy);

            var absoluteRawDifferentPath = new Uri(AbsoluteUri + "/bar", new UriCreationOptions(UriKind.Absolute) { DangerousUseRawTarget = true });
            NotEqual(absoluteRaw, absoluteRawDifferentPath);

            var absoluteRawSameAuthority = new Uri(AbsoluteUri + ":80" + Path, new UriCreationOptions(UriKind.Absolute) { DangerousUseRawTarget = true });
            Equal(absoluteRaw, absoluteRawSameAuthority);

            static void Equal(Uri left, Uri right)
            {
                Assert.True(left.Equals(right));
                Assert.True(right.Equals(left));
                Assert.Equal(left.GetHashCode(), right.GetHashCode());
            }

            static void NotEqual(Uri left, Uri right)
            {
                Assert.False(left.Equals(right));
                Assert.False(right.Equals(left));
            }
        }

        private const string FilePathRawData = "//\\A%41 %20\u00F6/.././%5C%2F#%42?%43#%44";

        public static IEnumerable<object[]> ImplicitFilePaths_TestData()
        {
            yield return Entry("C:/");
            yield return Entry("C|/");

            yield return Entry(@"//foo");
            yield return Entry(@"\/foo");
            yield return Entry(@"/\foo");
            yield return Entry(@"\\foo");

            if (!PlatformDetection.IsWindows)
            {
                yield return Entry("/foo");
            }

            static object[] Entry(string filePath) => new object[] { $"{filePath}/{FilePathRawData}" };
        }

        [Theory]
        [MemberData(nameof(ImplicitFilePaths_TestData))]
        public void UseRawTarget_WorksWithFileUris(string implicitFilePath)
        {
            var options = new UriCreationOptions { DangerousUseRawTarget = true };

            var uri = new Uri(implicitFilePath, options);
            DoAsserts(uri);

            Assert.True(Uri.TryCreate(implicitFilePath, options, out uri));
            DoAsserts(uri);

            static void DoAsserts(Uri uri)
            {
                Assert.True(uri.IsAbsoluteUri);
                Assert.True(uri.IsFile);
                Assert.Contains(FilePathRawData, uri.AbsolutePath);
                Assert.Contains(FilePathRawData, uri.AbsoluteUri);
                Assert.Contains(FilePathRawData, uri.ToString());
            }
        }

        [Theory]
        [MemberData(nameof(ImplicitFilePaths_TestData))]
        public void AllowImplicitFilePaths_IsRespected(string implicitFilePath)
        {
            var implicitUri = new Uri(implicitFilePath);
            Assert.True(implicitUri.IsAbsoluteUri);
            Assert.True(implicitUri.IsFile);

            var relativeOrAbsolute = new Uri(implicitFilePath, new UriCreationOptions(UriKind.RelativeOrAbsolute) { AllowImplicitFilePaths = false });
            Assert.False(relativeOrAbsolute.IsAbsoluteUri);

            Assert.True(Uri.TryCreate(implicitFilePath, new UriCreationOptions(UriKind.RelativeOrAbsolute) { AllowImplicitFilePaths = false }, out relativeOrAbsolute));
            Assert.False(relativeOrAbsolute.IsAbsoluteUri);

            Assert.Throws<UriFormatException>(() => new Uri(implicitFilePath, new UriCreationOptions(UriKind.Absolute) { AllowImplicitFilePaths = false }));

            Assert.False(Uri.TryCreate(implicitFilePath, new UriCreationOptions(UriKind.Absolute) { AllowImplicitFilePaths = false }, out _));

            var explicitAbsolute = new Uri("file://" + implicitFilePath, new UriCreationOptions(UriKind.Absolute) { AllowImplicitFilePaths = false });
            Assert.True(explicitAbsolute.IsAbsoluteUri);
            Assert.True(explicitAbsolute.IsFile);

            Assert.True(Uri.TryCreate("file://" + implicitFilePath, new UriCreationOptions(UriKind.Absolute) { AllowImplicitFilePaths = false }, out explicitAbsolute));
            Assert.True(explicitAbsolute.IsAbsoluteUri);
            Assert.True(explicitAbsolute.IsFile);
        }
    }
}
