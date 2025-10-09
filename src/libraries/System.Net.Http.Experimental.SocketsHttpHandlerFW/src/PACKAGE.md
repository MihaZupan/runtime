## About

<!-- A description of the package and where one can find more documentation -->

A partial experimental port of `SocketsHttpHandler` to .NET Framework for HTTP/2 testing.

Does **NOT** support:
- HTTP/1 and HTTP/3
- Proxies
- Request & connection auth
- SslOptions (besides remote cert validation and local cert selection)
- Auto decompression
- Metrics, distributed tracing
- Probably something else :shrug:

Only supports HTTP/2!
- H2C (HTTP/2 over cleartext) works fine.
- ALPN is supported via a Lib.Harmony runtime patch of SslStream internals.

## Key Features

<!-- The key features of this package -->

* Partial SocketsHttpHandler support on .NET Framework.

## How to Use

<!-- A compelling example on how to use this package with code, as well as any specific guidelines for when to use the package -->

```csharp
using System.Net.Http.DoNotUseInProduction.TestingOnly;

var handler = new SocketsHttpHandler();
using var client = new HttpClient(handler);

var request = new HttpRequestMessage(HttpMethod.Get, "https://localhost:8081/");
request.Version = new Version(2, 0);

using (HttpResponseMessage response = await client.SendAsync(request))
{
    Console.WriteLine(await response.Content.ReadAsStringAsync());
}
```

## Main Types

<!-- The main types provided in this library -->

The main types provided by this library are:

* `System.Net.Http.DoNotUseInProduction.TestingOnly.SocketsHttpHandler`

## Feedback & Contributing

<!-- How to provide feedback on this package and contribute to it -->

System.Net.ServerSentEvents is released as open source under the [MIT license](https://licenses.nuget.org/MIT). Bug reports and contributions are welcome at [the GitHub repository](https://github.com/dotnet/runtime).
