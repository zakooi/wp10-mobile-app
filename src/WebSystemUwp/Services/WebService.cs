using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebSystemUwp.Services
{
    public enum WebEngineErrorType
    {
        None,
        Timeout,
        NetworkUnavailable,
        DnsResolutionFailed,
        SslHandshakeFailed,
        HttpError,
        Cancelled,
        Unknown
    }

    public class WebEngineRequest
    {
        public string RequestId { get; set; } = Guid.NewGuid().ToString();
        public string Url { get; set; }
        public string Method { get; set; } = "GET";
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public string Body { get; set; }
        public string ContentType { get; set; } = "application/json";
        public string Referer { get; set; }
        public string Accept { get; set; }
        public int TimeoutSeconds { get; set; } = 20;
        public int MaxResponseBytes { get; set; } = 0;
        public string UserAgent { get; set; }
        public bool UseRustTlsEngine { get; set; } = true;
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
    }

    public class WebEngineResponse
    {
        public string RequestId { get; set; }
        public bool IsSuccess { get; set; }
        public int StatusCode { get; set; }
        public string StatusText { get; set; }
        public string FinalUrl { get; set; }
        public string TransportInfo { get; set; }
        public Dictionary<string, string> Headers { get; set; } = new Dictionary<string, string>();
        public string ContentType { get; set; }
        public long ContentLength { get; set; }
        public long ResponseTimeMs { get; set; }
        public string Body { get; set; }
        public byte[] RawBytes { get; set; }
        public WebEngineErrorType ErrorType { get; set; } = WebEngineErrorType.None;
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Web Engine v2: Hệ thống điều phối Web Request qua Rust TLS hoặc HttpClient (Schannel).
    /// </summary>
    public static class WebService
    {
        private static readonly HttpClient Client;

        private const string DefaultUserAgent = "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Mobile Safari/537.36";

        static WebService()
        {
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = true,
                MaxAutomaticRedirections = 10,
                UseCookies = true
            };

            Client = new HttpClient(handler)
            {
                Timeout = TimeSpan.FromSeconds(60)
            };
        }

        public static async Task<WebEngineResponse> RequestAsync(WebEngineRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (string.IsNullOrWhiteSpace(request.Url))
            {
                return new WebEngineResponse
                {
                    RequestId = request.RequestId,
                    IsSuccess = false,
                    ErrorType = WebEngineErrorType.Unknown,
                    ErrorMessage = "URL yêu cầu không được để trống."
                };
            }

            if (request.CancellationToken.IsCancellationRequested)
            {
                return new WebEngineResponse
                {
                    RequestId = request.RequestId,
                    IsSuccess = false,
                    ErrorType = WebEngineErrorType.Cancelled,
                    ErrorMessage = "Yêu cầu đã bị hủy bởi người dùng."
                };
            }

            if (request.UseRustTlsEngine && (request.Headers == null || request.Headers.Count == 0) && RustEngineBridge.IsAvailable)
            {
                return await RustEngineBridge.SendHttpRequestAsync(request);
            }

            var responseResult = new WebEngineResponse
            {
                RequestId = request.RequestId,
                TransportInfo = "HttpClient (Schannel)"
            };

            var sw = Stopwatch.StartNew();
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, request.TimeoutSeconds))))
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(request.CancellationToken, timeoutCts.Token))
            {
                try
                {
                    using (var reqMsg = new HttpRequestMessage(new HttpMethod(request.Method), request.Url))
                    {
                        string ua = string.IsNullOrWhiteSpace(request.UserAgent) ? DefaultUserAgent : request.UserAgent;
                        reqMsg.Headers.TryAddWithoutValidation("User-Agent", ua);
                        reqMsg.Headers.TryAddWithoutValidation("Accept", "*/*");

                        if (request.Headers != null)
                        {
                            foreach (var h in request.Headers)
                            {
                                reqMsg.Headers.TryAddWithoutValidation(h.Key, h.Value);
                            }
                        }

                        if ((request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                             request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase)) &&
                            request.Body != null)
                        {
                            reqMsg.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType ?? "application/json");
                        }

                        using (var httpResp = await Client.SendAsync(reqMsg, HttpCompletionOption.ResponseContentRead, cts.Token))
                        {
                            sw.Stop();
                            responseResult.ResponseTimeMs = sw.ElapsedMilliseconds;
                            responseResult.StatusCode = (int)httpResp.StatusCode;
                            responseResult.StatusText = httpResp.ReasonPhrase;
                            responseResult.IsSuccess = httpResp.IsSuccessStatusCode;
                            responseResult.ContentType = httpResp.Content.Headers.ContentType?.ToString() ?? "text/plain";

                            foreach (var header in httpResp.Headers)
                            {
                                responseResult.Headers[header.Key] = string.Join(", ", header.Value);
                            }

                            responseResult.RawBytes = await httpResp.Content.ReadAsByteArrayAsync();
                            responseResult.ContentLength = responseResult.RawBytes != null ? (long)responseResult.RawBytes.Length : 0L;
                            responseResult.Body = Encoding.UTF8.GetString(responseResult.RawBytes ?? new byte[0]);

                            if (!httpResp.IsSuccessStatusCode)
                            {
                                responseResult.ErrorType = WebEngineErrorType.HttpError;
                                responseResult.ErrorMessage = $"HTTP {responseResult.StatusCode}: {responseResult.StatusText}";
                            }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    sw.Stop();
                    responseResult.ResponseTimeMs = sw.ElapsedMilliseconds;
                    responseResult.IsSuccess = false;
                    if (request.CancellationToken.IsCancellationRequested)
                    {
                        responseResult.ErrorType = WebEngineErrorType.Cancelled;
                        responseResult.ErrorMessage = "Yêu cầu đã bị hủy bởi người dùng.";
                    }
                    else
                    {
                        responseResult.ErrorType = WebEngineErrorType.Timeout;
                        responseResult.ErrorMessage = $"Hết thời gian chờ phản hồi ({request.TimeoutSeconds}s).";
                    }
                }
                catch (HttpRequestException ex)
                {
                    sw.Stop();
                    responseResult.ResponseTimeMs = sw.ElapsedMilliseconds;
                    responseResult.IsSuccess = false;
                    responseResult.ErrorMessage = ex.Message;

                    string msg = ex.Message.ToLowerInvariant();
                    if (msg.Contains("dns") || msg.Contains("name resolution") || msg.Contains("getaddrinfo"))
                        responseResult.ErrorType = WebEngineErrorType.DnsResolutionFailed;
                    else if (msg.Contains("ssl") || msg.Contains("tls") || msg.Contains("certificate") || msg.Contains("secure"))
                        responseResult.ErrorType = WebEngineErrorType.SslHandshakeFailed;
                    else if (msg.Contains("network") || msg.Contains("connection"))
                        responseResult.ErrorType = WebEngineErrorType.NetworkUnavailable;
                    else
                        responseResult.ErrorType = WebEngineErrorType.Unknown;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    responseResult.ResponseTimeMs = sw.ElapsedMilliseconds;
                    responseResult.IsSuccess = false;
                    responseResult.ErrorType = WebEngineErrorType.Unknown;
                    responseResult.ErrorMessage = ex.Message;
                }
            }

            return responseResult;
        }

        public static async Task<long> CheckConnectionAsync(string testUrl = "https://www.google.com")
        {
            var sw = Stopwatch.StartNew();
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8)))
                using (var req = new HttpRequestMessage(HttpMethod.Head, testUrl))
                {
                    var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token);
                    sw.Stop();
                    return resp.IsSuccessStatusCode ? sw.ElapsedMilliseconds : -1;
                }
            }
            catch
            {
                return -1;
            }
        }
    }
}
