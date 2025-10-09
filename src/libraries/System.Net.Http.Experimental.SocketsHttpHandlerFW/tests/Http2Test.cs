// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Net.Http.DoNotUseInProduction.TestingOnly;
using System.Threading.Tasks;
using Xunit;

namespace System.Net.Http.Experimental.SocketsHttpHandlerFW.Tests
{
    public class Http2Test
    {
        [Fact]
        public static async Task TestAsync()
        {
            var handler = new SocketsHttpHandler();
            using var client = new HttpClient(handler);

            var request = new HttpRequestMessage(HttpMethod.Get, "https://microsoft.com");
            request.Version = new Version(2, 0);

            using (HttpResponseMessage response = await client.SendAsync(request))
            {
                Assert.Equal(2, response.Version.Major);
            }
        }
    }

}
