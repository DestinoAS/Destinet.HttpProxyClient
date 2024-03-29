// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Extensions;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core.Features;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Destinet
{
    public class HttpProxyClient : IHttpProxyClient, IDisposable
    {

        /// <summary>
        /// Proxies the incoming request to the upstream server, and the response back to our client.
        /// </summary>
        /// <remarks>
        /// In what follows, as well as throughout in Reverse Proxy, we consider
        /// the following picture as illustrative of the Proxy.
        /// <code>
        ///      +-------------------+
        ///      |  Upstream server  +
        ///      +-------------------+
        ///            ▲       |
        ///        (b) |       | (c)
        ///            |       ▼
        ///      +-------------------+
        ///      |      Proxy        +
        ///      +-------------------+
        ///            ▲       |
        ///        (a) |       | (d)
        ///            |       ▼
        ///      +-------------------+
        ///      | Downstream client +
        ///      +-------------------+
        /// </code>
        ///
        /// (a) and (b) show the *request* path, going *upstream* from the client to the target.
        /// (c) and (d) show the *response* path, going *downstream* from the target back to the client.
        /// </remarks>
        /// <param name="longCancellation">This should be linked to a client disconnect notification like <see cref="HttpContext.RequestAborted"/>
        /// to avoid leaking long running requests.</param>
        public Task ProxyAsync(
            HttpContext context,
            string destinationPrefix,
            string customHost = null,
            Action<HttpRequestMessage> requestAction = null)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 1: Create outgoing HttpRequestMessage
            var upgradeFeature = context.Features.Get<IHttpUpgradeFeature>();
            var isUpgrade = (upgradeFeature?.IsUpgradableRequest ?? false)
                // Mitigate https://github.com/microsoft/reverse-proxy/issues/255, IIS considers all requests upgradeable.
                && string.Equals("WebSocket", context.Request.Headers[HeaderNames.Upgrade], StringComparison.OrdinalIgnoreCase);
            // Default to HTTP/1.1 for proxying upgradeable requests. This is already the default as of .NET Core 3.1
            // Otherwise request HTTP/2 and let HttpClient fallback to HTTP/1.1 if it cannot establish HTTP/2 with the target.
            // This is done without extra round-trips thanks to ALPN. We can detect a downgrade after calling HttpClient.SendAsync
            // (see Step 3 below). TBD how this will change when HTTP/3 is supported.
            var version = isUpgrade ? ProtocolHelper.Http11Version : ProtocolHelper.Http2Version;

            var request = CreateRequestMessage(context, destinationPrefix, version);

            using (var shortCts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted))
            {
                shortCts.CancelAfter(TimeSpan.FromSeconds(30));
                var longCancellation = context.RequestAborted;
                var shortCancellation = shortCts.Token;

                if (isUpgrade)
                {
                    return UpgradableProxyAsync(context, upgradeFeature, request, CreateClient(), shortCancellation, longCancellation, customHost, requestAction);
                }
                else
                {
                    return NormalProxyAsync(context, request, CreateClient(), shortCancellation, longCancellation, customHost, requestAction);
                }

            }

        }

        /// <summary>
        /// Proxies a normal (i.e. non-upgradable) request to the upstream server, and the response back to our client.
        /// </summary>
        /// <remarks>
        /// Normal proxying comprises the following steps:
        ///    (0) Disable ASP .NET Core limits for streaming requests
        ///    (1) Create outgoing HttpRequestMessage
        ///    (2) Setup copy of request body (background)             Downstream --► Proxy --► Upstream
        ///    (3) Copy request headers                                Downstream --► Proxy --► Upstream
        ///    (4) Send the outgoing request using HttpMessageInvoker  Downstream --► Proxy --► Upstream
        ///    (5) Copy response status line                           Downstream ◄-- Proxy ◄-- Upstream
        ///    (6) Copy response headers                               Downstream ◄-- Proxy ◄-- Upstream
        ///    (7) Copy response body                                  Downstream ◄-- Proxy ◄-- Upstream
        ///    (8) Copy response trailer headers and finish response   Downstream ◄-- Proxy ◄-- Upstream
        ///    (9) Wait for completion of step 2: copying request body Downstream --► Proxy --► Upstream
        ///
        /// ASP .NET Core (Kestrel) will finally send response trailers (if any)
        /// after we complete the steps above and relinquish control.
        /// </remarks>
        private async Task NormalProxyAsync(
            HttpContext context,
            HttpRequestMessage upstreamRequest,
            HttpMessageInvoker httpClient,
            CancellationToken shortCancellation,
            CancellationToken longCancellation,
            string customHost,
            Action<HttpRequestMessage> requestAction = null)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _ = upstreamRequest ?? throw new ArgumentNullException(nameof(upstreamRequest));
            _ = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 0: Disable ASP .NET Core limits for streaming requests
            var isIncomingHttp2 = ProtocolHelper.IsHttp2(context.Request.Protocol);

            // NOTE: We heuristically assume gRPC-looking requests may require streaming semantics.
            // See https://github.com/microsoft/reverse-proxy/issues/118 for design discussion.
            var isStreamingRequest = isIncomingHttp2 && ProtocolHelper.IsGrpcContentType(context.Request.ContentType);
            if (isStreamingRequest)
            {
                DisableMinRequestBodyDataRateAndMaxRequestBodySize(context);
            }

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 2: Setup copy of request body (background) Downstream --► Proxy --► Upstream
            // Note that we must do this before step (3) because step (3) may also add headers to the HttpContent that we set up here.
            var bodyToUpstreamContent = SetupCopyBodyUpstream(context.Request, upstreamRequest, isStreamingRequest, longCancellation);

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 3: Copy request headers Downstream --► Proxy --► Upstream
            CopyHeadersToUpstream(context, upstreamRequest, customHost);

            if (requestAction != null)
                requestAction(upstreamRequest);

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 4: Send the outgoing request using HttpClient
            ////this.logger.LogInformation($"   Starting Proxy --> upstream request");
            var upstreamResponse = await httpClient.SendAsync(upstreamRequest, shortCancellation);

            // Detect connection downgrade, which may be problematic for e.g. gRPC.
            if (isIncomingHttp2 && upstreamResponse.Version.Major != 2)
            {
                // TODO: Do something on connection downgrade...
                //Log.HttpDowngradeDetected(_logger);
            }

            // Assert that, if we are proxying content upstream, it must have started by now
            // (since HttpClient.SendAsync has already completed asynchronously).
            // If this check fails, there is a coding defect which would otherwise
            // cause us to wait forever in step 9, so fail fast here.
            if (bodyToUpstreamContent != null && !bodyToUpstreamContent.Started)
            {
                // TODO: bodyToUpstreamContent is never null. HttpClient might would not need to read the body in some scenarios, such as an early auth failure with Expect: 100-continue.
                throw new InvalidOperationException("Proxying the downstream request body to the upstream server hasn't started. This is a coding defect.");
            }

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 5: Copy response status line Downstream ◄-- Proxy ◄-- Upstream
            ////this.logger.LogInformation($"   Setting downstream <-- Proxy status: {(int)upstreamResponse.StatusCode} {upstreamResponse.ReasonPhrase}");
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            context.Features.Get<IHttpResponseFeature>().ReasonPhrase = upstreamResponse.ReasonPhrase;

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 6: Copy response headers Downstream ◄-- Proxy ◄-- Upstream
            CopyHeadersToDownstream(upstreamResponse, context);

            // NOTE: it may *seem* wise to call `context.Response.StartAsync()` at this point
            // since it looks like we are ready to send back response headers
            // (and this might help reduce extra delays while we wait to receive the body from upstream).
            // HOWEVER, this would produce the wrong result if it turns out that there is no content
            // from the upstream -- instead of sending headers and terminating the stream at once,
            // we would send headers thinking a body may be coming, and there is none.
            // This is problematic on gRPC connections when the upstream server encounters an error,
            // in which case it immediately returns the response headers and trailing headers, but no content,
            // and clients misbehave if the initial headers response does not indicate stream end.

            // TODO: Some of the tasks in steps (7) - (9) may go unobserved depending on what fails first. Needs more consideration.

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 7: Copy response body Downstream ◄-- Proxy ◄-- Upstream
            await CopyBodyDownstreamAsync(upstreamResponse.Content, context.Response.Body, longCancellation);

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 8: Copy response trailer headers and finish response Downstream ◄-- Proxy ◄-- Upstream
            CopyTrailingHeadersToDownstream(upstreamResponse, context);

            if (isStreamingRequest)
            {
                // NOTE: We must call `CompleteAsync` so that Kestrel will flush all bytes to the client.
                // In the case where there was no response body,
                // this is also when headers and trailing headers are sent to the client.
                // Without this, the client might wait forever waiting for response bytes,
                // while we might wait forever waiting for request bytes,
                // leading to a stuck connection and no way to make progress.
                await context.Response.CompleteAsync();
            }

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 9: Wait for completion of step 2: copying request body Downstream --► Proxy --► Upstream
            if (bodyToUpstreamContent != null)
            {
                ////this.logger.LogInformation($"   Waiting for downstream --> Proxy --> upstream body proxying to complete");
                await bodyToUpstreamContent.ConsumptionTask;
            }
        }

        /// <summary>
        /// Proxies an upgradable request to the upstream server, treating the upgraded stream as an opaque duplex channel.
        /// </summary>
        /// <remarks>
        /// Upgradable request proxying comprises the following steps:
        ///    (1)  Create outgoing HttpRequestMessage
        ///    (2)  Copy request headers                                              Downstream ---► Proxy ---► Upstream
        ///    (3)  Send the outgoing request using HttpMessageInvoker                Downstream ---► Proxy ---► Upstream
        ///    (4)  Copy response status line                                         Downstream ◄--- Proxy ◄--- Upstream
        ///    (5)  Copy response headers                                             Downstream ◄--- Proxy ◄--- Upstream
        ///       Scenario A: upgrade with upstream worked (got 101 response)
        ///          (A-6)  Upgrade downstream channel (also sends response headers)  Downstream ◄--- Proxy ◄--- Upstream
        ///          (A-7)  Copy duplex streams                                       Downstream ◄--► Proxy ◄--► Upstream
        ///       ---- or ----
        ///       Scenario B: upgrade with upstream failed (got non-101 response)
        ///          (B-6)  Send response headers                                     Downstream ◄--- Proxy ◄--- Upstream
        ///          (B-7)  Copy response body                                        Downstream ◄--- Proxy ◄--- Upstream
        ///
        /// This takes care of WebSockets as well as any other upgradable protocol.
        /// </remarks>
        private async Task UpgradableProxyAsync(
            HttpContext context,
            IHttpUpgradeFeature upgradeFeature,
            HttpRequestMessage upstreamRequest,
            HttpMessageInvoker httpClient,
            CancellationToken shortCancellation,
            CancellationToken longCancellation,
            string customHost,
            Action<HttpRequestMessage> requestAction = null)
        {
            _ = context ?? throw new ArgumentNullException(nameof(context));
            _ = upgradeFeature ?? throw new ArgumentNullException(nameof(upgradeFeature));
            _ = upstreamRequest ?? throw new ArgumentNullException(nameof(upstreamRequest));
            _ = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 2: Copy request headers Downstream --► Proxy --► Upstream
            CopyHeadersToUpstream(context, upstreamRequest, customHost);

            if (requestAction != null)
                requestAction(upstreamRequest);

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 3: Send the outgoing request using HttpMessageInvoker
            var upstreamResponse = await httpClient.SendAsync(upstreamRequest, shortCancellation);
            var upgraded = upstreamResponse.StatusCode == HttpStatusCode.SwitchingProtocols && upstreamResponse.Content != null;

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 4: Copy response status line Downstream ◄-- Proxy ◄-- Upstream
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            context.Features.Get<IHttpResponseFeature>().ReasonPhrase = upstreamResponse.ReasonPhrase;

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step 5: Copy response headers Downstream ◄-- Proxy ◄-- Upstream
            CopyHeadersToDownstream(upstreamResponse, context);

            if (!upgraded)
            {
                // :::::::::::::::::::::::::::::::::::::::::::::
                // :: Step B-6: Send response headers Downstream ◄-- Proxy ◄-- Upstream
                // This is important to avoid any extra delays in sending response headers
                // e.g. if the upstream server is slow to provide its response body.
                await context.Response.StartAsync(shortCancellation);

                // :::::::::::::::::::::::::::::::::::::::::::::
                // :: Step B-7: Copy response body Downstream ◄-- Proxy ◄-- Upstream
                await CopyBodyDownstreamAsync(upstreamResponse.Content, context.Response.Body, longCancellation);
                return;
            }

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step A-6: Upgrade the downstream channel. This will send all response headers too.
            using var downstreamStream = await upgradeFeature.UpgradeAsync();

            // :::::::::::::::::::::::::::::::::::::::::::::
            // :: Step A-7: Copy duplex streams
            var upstreamStream = await upstreamResponse.Content.ReadAsStreamAsync();

            var upstreamCopier = new StreamCopier();
            var upstreamTask = upstreamCopier.CopyAsync(downstreamStream, upstreamStream, longCancellation);

            var downstreamCopier = new StreamCopier();
            var downstreamTask = downstreamCopier.CopyAsync(upstreamStream, downstreamStream, longCancellation);

            await Task.WhenAll(upstreamTask, downstreamTask);
        }

        private HttpRequestMessage CreateRequestMessage(HttpContext context,
            string destinationAddress,
            Version httpVersion)
        {
            // "http://a".Length = 8
            if (destinationAddress == null || destinationAddress.Length < 8)
            {
                throw new ArgumentException(nameof(destinationAddress));
            }

            // TODO Perf: We could probably avoid splitting this and just append the final path and query
            UriHelper.FromAbsolute(destinationAddress, out var destinationScheme, out var destinationHost, out var destinationPathBase, out _, out _); // Query and Fragment are not supported here.

            var request = context.Request;
            var url = UriHelper.BuildAbsolute(destinationScheme, destinationHost, destinationPathBase, request.Path, request.QueryString);
            var uri = new Uri(url, UriKind.Absolute);
            return new HttpRequestMessage(GetHttpMethod(context.Request.Method), uri) { Version = httpVersion };
        }

        private StreamCopyHttpContent SetupCopyBodyUpstream(HttpRequest request, HttpRequestMessage upstreamRequest, bool isStreamingRequest, CancellationToken cancellation)
        {
            // If we generate an HttpContent without a Content-Length then for HTTP/1.1 HttpClient will add a Transfer-Encoding: chunked header
            // even if it's a GET request. Some servers reject requests containing a Transfer-Encoding header if they're not expecting a body.
            // Try to be as specific as possible about the client's intent to send a body. The one thing we don't want to do is to start
            // reading the body early because that has side-effects like 100-continue.
            var hasBody = true;
            var contentLength = request.Headers.ContentLength;
            var method = request.Method;
            // https://tools.ietf.org/html/rfc7231#section-4.3.8
            // A client MUST NOT send a message body in a TRACE request.
            if (HttpMethods.IsTrace(method))
            {
                hasBody = false;
            }
            // https://tools.ietf.org/html/rfc7230#section-3.3.3
            // All HTTP/1.1 requests should have Transfer-Encoding or Content-Length.
            // Http.Sys/IIS will even add a Transfer-Encoding header to HTTP/2 requests with bodies for back-compat.
            // HTTP/1.0 Connection: close bodies are only allowed on responses, not requests.
            // https://tools.ietf.org/html/rfc1945#section-7.2.2
            //
            // Transfer-Encoding overrides Content-Length per spec
            else if (request.Headers.TryGetValue(HeaderNames.TransferEncoding, out var transferEncoding)
                && transferEncoding.Count == 1
                && string.Equals("chunked", transferEncoding.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                hasBody = true;
            }
            else if (contentLength.HasValue)
            {
                hasBody = contentLength > 0;
            }
            // Kestrel HTTP/2: There are no required headers that indicate if there is a request body so we need to sniff other fields.
            else if (!ProtocolHelper.IsHttp2OrGreater(request.Protocol))
            {
                hasBody = false;
            }
            // https://tools.ietf.org/html/rfc7231#section-5.1.1
            // A client MUST NOT generate a 100-continue expectation in a request that does not include a message body.
            else if (request.Headers.TryGetValue(HeaderNames.Expect, out var expect)
                && expect.Count == 1
                && string.Equals("100-continue", expect.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                hasBody = true;
            }
            // https://tools.ietf.org/html/rfc7231#section-4.3.1
            // A payload within a GET/HEAD/DELETE/CONNECT request message has no defined semantics; sending a payload body on a
            // GET/HEAD/DELETE/CONNECT request might cause some existing implementations to reject the request.
            else if (HttpMethods.IsGet(method)
                || HttpMethods.IsHead(method)
                || HttpMethods.IsDelete(method)
                || HttpMethods.IsConnect(method))
            {
                hasBody = false;
            }
            // else hasBody defaults to true

            StreamCopyHttpContent contentToUpstream = null;
            if (hasBody)
            {
                ////this.logger.LogInformation($"   Setting up downstream --> Proxy --> upstream body proxying");

                // Note on `autoFlushHttpClientOutgoingStream: isStreamingRequest`:
                // The.NET Core HttpClient stack keeps its own buffers on top of the underlying outgoing connection socket.
                // We flush those buffers down to the socket on every write when this is set,
                // but it does NOT result in calls to flush on the underlying socket.
                // This is necessary because we proxy http2 transparently,
                // and we are deliberately unaware of packet structure used e.g. in gRPC duplex channels.
                // Because the sockets aren't flushed, the perf impact of this choice is expected to be small.
                // Future: It may be wise to set this to true for *all* http2 incoming requests,
                // but for now, out of an abundance of caution, we only do it for requests that look like gRPC.
                var streamCopier = new StreamCopier();
                contentToUpstream = new StreamCopyHttpContent(
                    source: request.Body,
                    streamCopier: streamCopier,
                    autoFlushHttpClientOutgoingStream: isStreamingRequest,
                    cancellation: cancellation);
                upstreamRequest.Content = contentToUpstream;
            }

            return contentToUpstream;
        }

        private void CopyHeadersToUpstream(HttpContext context, HttpRequestMessage destination, string customHost)
        {
            foreach (var header in context.Request.Headers)
            {
                var headerName = header.Key;

                // Filter out HTTP/2 pseudo headers like ":method" and ":path", those go into other fields.
                if (headerName.Length > 0 && headerName[0] == ':')
                    continue;

                var value = header.Value;
                if (StringValues.IsNullOrEmpty(value))
                    continue;

                var sValue = value.ToString();

                if (headerName == "Host")
                {
                    if (!string.IsNullOrEmpty(customHost))
                    {
                        value = new StringValues(customHost);
                    }
                    else if (sValue.Contains(":"))
                        value = new StringValues(sValue.Split(':')[0]);
                }

                AddHeader(destination, headerName, value);
            }

            // Note: HttpClient.SendAsync will end up sending the union of
            // HttpRequestMessage.Headers and HttpRequestMessage.Content.Headers.
            // We don't really care where the proxied headers appear among those 2,
            // as long as they appear in one (and only one, otherwise they would be duplicated).
            static void AddHeader(HttpRequestMessage request, string headerName, StringValues value)
            {
                if (value.Count == 1)
                {
                    string headerValue = value;
                    if (!request.Headers.TryAddWithoutValidation(headerName, headerValue))
                    {
                        request.Content?.Headers.TryAddWithoutValidation(headerName, headerValue);
                    }
                }
                else
                {
                    // DESTINO: Fix for cookies
                    //if (headerName == "Cookie")
                    //    value = new StringValues(string.Join(";", value));
                    string[] headerValues = value;
                    if (!request.Headers.TryAddWithoutValidation(headerName, headerValues))
                    {
                        request.Content?.Headers.TryAddWithoutValidation(headerName, headerValues);
                    }
                }
            }
        }

        private void CopyHeadersToDownstream(HttpResponseMessage source, HttpContext context)
        {
            // Transforms that were run in the first pass.
            var responseHeaders = context.Response.Headers;
            CopyHeaders(source, source.Headers, context, responseHeaders);
            if (source.Content != null)
            {
                CopyHeaders(source, source.Content.Headers, context, responseHeaders);
            }
        }

        private async Task CopyBodyDownstreamAsync(HttpContent upstreamResponseContent, Stream destination, CancellationToken cancellation)
        {
            if (upstreamResponseContent != null)
            {
                var streamCopier = new StreamCopier();
                var upstreamResponseStream = await upstreamResponseContent.ReadAsStreamAsync();
                await streamCopier.CopyAsync(upstreamResponseStream, destination, cancellation);
            }
        }

        private void CopyTrailingHeadersToDownstream(HttpResponseMessage source, HttpContext context)
        {
            // NOTE: Deliberately not using `context.Response.SupportsTrailers()`, `context.Response.AppendTrailer(...)`
            // because they lookup `IHttpResponseTrailersFeature` for every call. Here we do it just once instead.
            var responseTrailersFeature = context.Features.Get<IHttpResponseTrailersFeature>();
            var outgoingTrailers = responseTrailersFeature?.Trailers;
            if (outgoingTrailers != null && !outgoingTrailers.IsReadOnly)
            {
                // Note that trailers, if any, should already have been declared in Proxy's response
                // by virtue of us having proxied all upstream response headers in step 6.
                CopyHeaders(source, source.TrailingHeaders, context, outgoingTrailers);
            }
        }

        private static void CopyHeaders(HttpResponseMessage response, HttpHeaders source, HttpContext context, IHeaderDictionary destination)
        {
            foreach (var header in source)
            {
                try
                {
                    var headerName = header.Key;
                    // TODO: this list only contains "Transfer-Encoding" because that messes up Kestrel. If we don't need to add any more here then it would be more efficient to
                    // check for the single value directly.
                    if (_headersToSkipGoingDownstream.Contains(headerName))
                    {
                        continue;
                    }
                    var headerValue = new StringValues(header.Value.ToArray());
                    if (!StringValues.IsNullOrEmpty(headerValue))
                    {
                        // DESTINO: Fix for location
                        if (headerName == "Location")
                        {
                            destination.Append(headerName, UriHelper.Encode(new Uri(headerValue)));
                        }
                        else
                        {
                            destination.Append(headerName, headerValue);
                        }
                    }
                }
                catch (Exception ex)
                {
                    var headerValue = new StringValues(header.Value.ToArray());
                    throw new Exception($"Error copying header '{header.Key}' with value '{headerValue}'", ex);
                }
            }
        }


        private static readonly HashSet<string> _headersToSkipGoingDownstream = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Transfer-Encoding",
        };

        private SocketsHttpHandler _handler = new SocketsHttpHandler
        {
            UseProxy = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            UseCookies = false,
            MaxConnectionsPerServer = int.MaxValue, // Proxy manages max connections
                                                    // NOTE: MaxResponseHeadersLength = 64, which means up to 64 KB of headers are allowed by default as of .NET Core 3.1.
        };


        /// <inheritdoc/>
        private HttpMessageInvoker CreateClient()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(typeof(HttpProxyClient).FullName);
            }

            return new HttpMessageInvoker(_handler, disposeHandler: false);
        }

        ///// <summary>
        ///// Handler for http requests.
        ///// </summary>
        //private readonly HttpMessageHandler _handler;

        private bool _disposed;

        /// <inheritdoc/>
        public void Dispose()
        {
            Dispose(true);
        }

        /// <summary>
        /// Disposes the current instance.
        /// </summary>
        /// <remarks>
        /// This will dispose the underlying <see cref="HttpClientHandler"/>,
        /// so it can only be called after it is no longer in use.
        /// </remarks>
        protected virtual void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                if (disposing)
                {
                    // TODO: This has high potential to cause coding defects. See if we can do better,
                    // perhaps do something like `Microsoft.Extensions.Http.LifetimeTrackingHttpMessageHandler`.
                    _handler.Dispose();
                }

                _disposed = true;
            }
        }



        /// <summary>
        /// Disable some ASP .NET Core server limits so that we can handle long-running gRPC requests unconstrained.
        /// Note that the gRPC server implementation on ASP .NET Core does the same for client-streaming and duplex methods.
        /// Since in Gateway we have no way to determine if the current request requires client-streaming or duplex comm,
        /// we do this for *all* incoming requests that look like they might be gRPC.
        /// </summary>
        /// <remarks>
        /// Inspired on
        /// <see href="https://github.com/grpc/grpc-dotnet/blob/3ce9b104524a4929f5014c13cd99ba9a1c2431d4/src/Grpc.AspNetCore.Server/Internal/CallHandlers/ServerCallHandlerBase.cs#L127"/>.
        /// </remarks>
        private void DisableMinRequestBodyDataRateAndMaxRequestBodySize(HttpContext httpContext)
        {
            var minRequestBodyDataRateFeature = httpContext.Features.Get<IHttpMinRequestBodyDataRateFeature>();
            if (minRequestBodyDataRateFeature != null)
            {
                minRequestBodyDataRateFeature.MinDataRate = null;
            }

            var maxRequestBodySizeFeature = httpContext.Features.Get<IHttpMaxRequestBodySizeFeature>();
            if (maxRequestBodySizeFeature != null)
            {
                if (!maxRequestBodySizeFeature.IsReadOnly)
                {
                    maxRequestBodySizeFeature.MaxRequestBodySize = null;
                }
                else
                {
                    // IsReadOnly could be true if middleware has already started reading the request body
                    // In that case we can't disable the max request body size for the request stream
                    //_logger.LogWarning("Unable to disable max request body size.");
                }
            }
        }


        /// <summary>
        /// Converts the given HTTP method (usually obtained from <see cref="HttpRequest.Method"/>)
        /// into the corresponding <see cref="HttpMethod"/> static instance.
        /// </summary>
        public static HttpMethod GetHttpMethod(string method) => method switch
        {
            string mth when HttpMethods.IsGet(mth) => HttpMethod.Get,
            string mth when HttpMethods.IsPost(mth) => HttpMethod.Post,
            string mth when HttpMethods.IsPut(mth) => HttpMethod.Put,
            string mth when HttpMethods.IsDelete(mth) => HttpMethod.Delete,
            string mth when HttpMethods.IsOptions(mth) => HttpMethod.Options,
            string mth when HttpMethods.IsHead(mth) => HttpMethod.Head,
            string mth when HttpMethods.IsPatch(mth) => HttpMethod.Patch,
            string mth when HttpMethods.IsTrace(mth) => HttpMethod.Trace,
            // NOTE: Proxying "CONNECT" is not supported (by design!)
            string mth when HttpMethods.IsConnect(mth) => throw new NotSupportedException($"Unsupported request method '{method}'."),
            _ => new HttpMethod(method)
        };

    }
}
