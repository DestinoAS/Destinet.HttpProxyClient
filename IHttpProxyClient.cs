﻿using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

namespace Destinet
{
    public interface IHttpProxyClient
    {
        Task ProxyAsync(HttpContext context, string destinationPrefix, string customHost = null, Action<HttpRequestMessage> requestAction = null);
    }
}
