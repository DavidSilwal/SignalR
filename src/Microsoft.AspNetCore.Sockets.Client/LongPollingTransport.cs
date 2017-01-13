// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System;
using System.IO;
using System.IO.Pipelines;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Channels;
using Microsoft.AspNetCore.Sockets.Internal;
using Microsoft.Extensions.Internal;
using Microsoft.Extensions.Logging;

namespace Microsoft.AspNetCore.Sockets.Client
{
    public class LongPollingTransport : ITransport
    {
        private static readonly string DefaultUserAgent = "Microsoft.AspNetCore.SignalR.Client/0.0.0";
        private static readonly ProductInfoHeaderValue DefaultUserAgentHeader = ProductInfoHeaderValue.Parse(DefaultUserAgent);

        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly CancellationTokenSource _senderCts = new CancellationTokenSource();
        private readonly CancellationTokenSource _pollCts = new CancellationTokenSource();

        private IChannelConnection<Message> _application;

        private Task _sender;
        private Task _poller;

        public Task Running { get; private set; }

        public LongPollingTransport(HttpClient httpClient, ILoggerFactory loggerFactory)
        {
            _httpClient = httpClient;
            _logger = loggerFactory.CreateLogger<LongPollingTransport>();
        }

        public void Dispose()
        {
            _senderCts.Cancel();
            _pollCts.Cancel();
        }

        public Task StartAsync(Uri url, IChannelConnection<Message> application)
        {
            _application = application;

            //// Schedule shutdown of the poller when the output is closed
            //pipeline.Output.Writing.ContinueWith(_ =>
            //{
            //    _pollCts.Cancel();
            //    return TaskCache.CompletedTask;
            //});

            // Start sending and polling
            _poller = Poll(Utils.AppendPath(url, "poll"), _pollCts.Token);
            _sender = SendMessages(Utils.AppendPath(url, "send"), _senderCts.Token);
            Running = Task.WhenAll(_sender, _poller);

            return TaskCache.CompletedTask;
        }

        private async Task Poll(Uri pollUrl, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var request = new HttpRequestMessage(HttpMethod.Get, pollUrl);
                    request.Headers.UserAgent.Add(DefaultUserAgentHeader);

                    var response = await _httpClient.SendAsync(request, cancellationToken);
                    response.EnsureSuccessStatusCode();

                    if (response.StatusCode == HttpStatusCode.NoContent || cancellationToken.IsCancellationRequested)
                    {
                        // Transport closed or polling stopped, we're done
                        break;
                    }
                    else
                    {
                        var ms = new MemoryStream();
                        await response.Content.CopyToAsync(ms);
                        var message = new Message(ReadableBuffer.Create(ms.ToArray()).Preserve(), Format.Text, true);

                        // TODO: cancellation token
                        while (await _application.Output.WaitToWriteAsync())
                        {
                            if (_application.Output.TryWrite(message))
                            {
                                break;
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Shut down the output pipeline and log
                _logger.LogError("Error while polling '{0}': {1}", pollUrl, ex);
                // TODO: ?
                // _application.Input.TryComplete(ex);
                _application.Output.TryComplete(ex);
            }
        }

        private async Task SendMessages(Uri sendUrl, CancellationToken cancellationToken)
        {
            // TODO: ?
            // using (cancellationToken.Register(() => _application.Input.Re.Input.Complete()))
            {
                try
                {
                     while (await _application.Input.WaitToReadAsync(cancellationToken))
                     {
                        Message message;
                        if (! _application.Input.TryRead(out message))
                        {
                            continue;
                        }

                        var request = new HttpRequestMessage(HttpMethod.Post, sendUrl);
                        request.Headers.UserAgent.Add(DefaultUserAgentHeader);
                        // TODO: remove ReadableBufferContent?
                        request.Content = new ReadableBufferContent(message.Payload.Buffer);

                        var response = await _httpClient.SendAsync(request);
                        response.EnsureSuccessStatusCode();
                    }
                }
                catch (Exception ex)
                {
                    // Shut down the input pipeline and log
                    _logger.LogError("Error while sending to '{0}': {1}", sendUrl, ex);
                    // TODO: ?
                    // _application.Input.TryComplete(ex);
                }
            }
        }
    }
}
