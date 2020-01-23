using Xunit;

namespace System.PrivateUri.Tests
{
    public class IsLoopbackTests
    {
        [Theory]
        [InlineData("file://server/filename.ext", false)]
        [InlineData("https://www.foo.bar", false)]
        [InlineData("https://127.0.0.1", true)]
        [InlineData("http://127.255.255.255", true)]
        [InlineData("https://128.0.0.1", false)]
        [InlineData("http://localhost/foo", true)]
        [InlineData("https://[0:0:0:0:0:0:0:1]", true)]
        [InlineData("http://[::1]", true)]
        [InlineData("https://[::2]", false)]
        [InlineData("http://[0::1]", true)]
        [InlineData("ftp://[::127.0.0.1]", true)]
        [InlineData("http://[::127.0.0.2]", false)]
        [InlineData("https://[1::127.0.0.1]", false)]
        [InlineData("http://[::FFFF:127.0.0.1]", true)]
        [InlineData("https://[::FFFE:127.0.0.1]", false)]
        public void Uri_IsLoopback(string uriString, bool expected)
        {
            Uri uri = new Uri(uriString, UriKind.RelativeOrAbsolute);
            Assert.Equal(expected, uri.IsLoopback);
        }

        [Theory]
        [InlineData("foo.bar")]
        [InlineData("[::1]")]
        [InlineData("127.0.0.1")]
        [InlineData("localhost")]
        public void Uri_IsLoopback_ThrowsForRelativeUris(string uriString)
        {
            Uri uri = new Uri(uriString, UriKind.Relative);
            Assert.Throws<InvalidOperationException>(() => uri.IsLoopback);
        }
    }
}
