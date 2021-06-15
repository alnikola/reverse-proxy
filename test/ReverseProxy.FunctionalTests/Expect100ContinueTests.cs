// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Primitives;
using Microsoft.Net.Http.Headers;
using Xunit;
using Yarp.ReverseProxy.Common;

namespace Yarp.ReverseProxy
{
    public class Expect100ContinueTests
    {
        [Theory]
        [InlineData(HttpProtocols.Http1, HttpProtocols.Http1, true, 200)]
        [InlineData(HttpProtocols.Http1, HttpProtocols.Http1, false, 200)]
        [InlineData(HttpProtocols.Http2, HttpProtocols.Http2, true, 200)]
        [InlineData(HttpProtocols.Http2, HttpProtocols.Http2, false, 200)]
        [InlineData(HttpProtocols.Http1, HttpProtocols.Http1, true, 400)]
        [InlineData(HttpProtocols.Http1, HttpProtocols.Http1, false, 400)]
        [InlineData(HttpProtocols.Http1, HttpProtocols.Http2, true, 400)]
        [InlineData(HttpProtocols.Http2, HttpProtocols.Http2, true, 400)]
        public async Task PostExpect100_BodyAlwaysUploaded(HttpProtocols proxyProtocol, HttpProtocols destProtocol, bool useContentLength, int destResponseCode)
        {
            var headerTcs = new TaskCompletionSource<StringValues>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bodyTcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);

            var contentString = new string('a', 1024 * 1024 * 10);
            var test = new TestEnvironment(
                async context =>
                {
                    if ((context.Request.Protocol == "HTTP/1.1" && destProtocol != HttpProtocols.Http1)
                        || (context.Request.Protocol == "HTTP/2.0" && destProtocol != HttpProtocols.Http2))
                    {
                        headerTcs.SetException(new Exception($"Unexpected request protocol {context.Request.Protocol}"));
                    }
                    else if (context.Request.Headers.TryGetValue(HeaderNames.Expect, out var expectHeader))
                    {
                        headerTcs.SetResult(expectHeader);
                    }
                    else
                    {
                        headerTcs.SetException(new Exception("Missing 'Expect' header in request"));
                    }

                    if (destResponseCode == 200)
                    {
                        // 100 response code is sent automatically on reading Body.
                        await ReadContent(context, bodyTcs, contentString);
                    }
                    context.Response.StatusCode = destResponseCode;
                    await context.Response.CompleteAsync();
                },
                proxyBuilder => { },
                proxyApp => { },
                proxyProtocol: proxyProtocol,
                useHttpsOnDestination: true,
                configTransformer: (c, r) =>
                {
                    c = c with
                    {
                        HttpRequest = new Forwarder.ForwarderRequestConfig
                        {
                            Version = destProtocol == HttpProtocols.Http2 ? HttpVersion.Version20 : HttpVersion.Version11,
#if NET
                            VersionPolicy = HttpVersionPolicy.RequestVersionExact
#endif
                        }
                    };
                    return (c, r);
                });

            await test.Invoke(async uri =>
            {
                await ProcessHttpRequest(new Uri(uri), proxyProtocol, contentString, useContentLength, destResponseCode);

                Assert.True(headerTcs.Task.IsCompleted);
                var expectHeader = await headerTcs.Task;
                var expectValue = Assert.Single(expectHeader);
                Assert.Equal("100-continue", expectValue);

                if (destResponseCode == 200)
                {
                    Assert.True(bodyTcs.Task.IsCompleted);
                    var actualString = await bodyTcs.Task;
                    Assert.Equal(contentString, actualString);
                }
                else
                {
                    Assert.False(bodyTcs.Task.IsCompleted);
                }
            });
        }

        private static async Task ReadContent(Microsoft.AspNetCore.Http.HttpContext context, TaskCompletionSource<string> bodyTcs, string contentString)
        {
            try
            {
                var buffer = new byte[Encoding.UTF8.GetByteCount(contentString)];
                var readCount = 0;
                var totalReadCount = 0;
                do
                {
                    readCount = await context.Request.Body.ReadAsync(buffer, totalReadCount, buffer.Length - totalReadCount);
                    totalReadCount += readCount;
                } while (readCount != 0);

                var actualString = Encoding.UTF8.GetString(buffer);
                bodyTcs.SetResult(actualString);
            }
            catch (Exception e)
            {
                bodyTcs.SetException(e);
            }
        }

        private async Task ProcessHttpRequest(Uri proxyHostUri, HttpProtocols protocol, string contentString, bool useContentLength, int expectedCode)
        {
            using var handler = new SocketsHttpHandler() { Expect100ContinueTimeout = TimeSpan.FromSeconds(60) };
            handler.UseProxy = false;
            handler.AllowAutoRedirect = false;
            using var client = new HttpClient(handler);
            using var message = new HttpRequestMessage(HttpMethod.Post, proxyHostUri);
            message.Version = protocol == HttpProtocols.Http2 ? HttpVersion.Version20 : HttpVersion.Version11;
#if NET
            message.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
#endif
            message.Headers.ExpectContinue = true;

            var content = Encoding.UTF8.GetBytes(contentString);
            using var contentStream = new MemoryStream(content);
            message.Content = new StreamContent(contentStream);
            if (useContentLength)
            {
                message.Content.Headers.ContentLength = content.Length;
            }
            else
            {
                message.Content.Headers.ContentEncoding.Add("chunked");
            }

            using var response = await client.SendAsync(message);

            Assert.Equal((int)response.StatusCode, expectedCode);
            if (expectedCode == 200)
            {
                Assert.Equal(content.Length, contentStream.Position);
            }
            else
            {
                Assert.Equal(0, contentStream.Position);
            }
        }
    }
}
