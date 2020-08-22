# HTTPProxyClient

Client based on the Microsoft Yarp project (https://github.com/microsoft/reverse-proxy). The Yarp project is intended to build a complete proxy server. If you however want to implement the proxy functionality into your own product this project is for you.

A NuGet package is available here: https://www.nuget.org/packages/Destinet.HttpProxyClient/

The client works by proxying the HttpContext.Request to the a server, then proxy the result back to the downstream client using HttpContext.Response.

<pre>
+-------------------+
|  Upstream server  +
+-------------------+
      ▲       |
  (b) |       | (c)
      |       ▼
+-------------------+
|      Proxy        +
+-------------------+
      ▲       |
  (a) |       | (d)
      |       ▼
+-------------------+
| Downstream client +
+-------------------+
</pre>

I recommend using the client by dependency injection:
```csharp
services.AddSingleton<IHttpProxyClient, HttpProxyClient>();
```

Use is rather simple:

```csharp
var upstreamUrl = "http://1.1.1.1";
await _httpProxyClient.ProxyAsync(
    context,
    upstreamUrl
);
```

There is also an option to adjust the request headers before sending to the upstream server:

```csharp
await _httpProxyClient.ProxyAsync(
    context,
    upstreamUrl,
    (requestMessage) =>
    {
        requestMessage.Headers.Add("X-Forwarded-For", context.Connection.RemoteIpAddress.ToString());
        requestMessage.Headers.Add("X-Forwarded-Proto", context.Request.Scheme);
    });
```
