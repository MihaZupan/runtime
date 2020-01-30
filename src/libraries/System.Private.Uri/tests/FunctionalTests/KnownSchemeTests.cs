using Xunit;

namespace System.PrivateUri.Functional.Tests
{
    public static class KnownSchemeTests
    {
        [Theory]
        [InlineData("https")]
        [InlineData("ws")]
        [InlineData("wss")]
        [InlineData("ftp")]
        [InlineData("file")]
        [InlineData("gopher")]
        [InlineData("nntp")]
        [InlineData("news")]
        [InlineData("mailto")]
        [InlineData("uuid")]
        [InlineData("telnet")]
        [InlineData("ldap")]
        [InlineData("net.tcp")]
        [InlineData("net.pipe")]
        [InlineData("vsmacros")]
        public static void KnownSchemes_DoNotAllocateForSchemeLookup(string scheme)
        {
            string uriString = scheme + "://foo.bar";

            // Cache this to save on test execution time
            double allocatedForHttp = s_allocatedForHttp ??= MeasureAllocations(() => new Uri("http://foo.bar"));

            double allocatedForTestScheme = MeasureAllocations(() => new Uri(uriString));

            Assert.InRange(allocatedForTestScheme, allocatedForHttp - 4, allocatedForHttp + 4);
        }
        private static double? s_allocatedForHttp = null;

        private static double MeasureAllocations(Action action)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            const long iterations = 10_000;

            long before = GC.GetAllocatedBytesForCurrentThread();

            for (long i = 0; i < iterations; i++)
                action();

            long after = GC.GetAllocatedBytesForCurrentThread();

            return (after - before) / (double)iterations;
        }
    }
}
