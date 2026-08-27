using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

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
        public int TimeoutSeconds { get; set; } = 20;
        public string UserAgent { get; set; }
        public CancellationToken CancellationToken { get; set; } = CancellationToken.None;
    }

    public class WebEngineResponse
    {
        public string RequestId { get; set; }
        public bool IsSuccess { get; set; }
        public int StatusCode { get; set; }
        public string StatusText { get; set; }
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
    /// Web Engine v2: Hệ thống điều phối Web Request công nghiệp với kiến trúc Pipeline.
    /// Hỗ trợ Request Manager, CancellationToken, Download Stream, ErrorType Classification và Network Ping.
    /// </summary>
    public static class WebService
    {
        private static readonly HttpClient Client;
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> ActiveRequests = new ConcurrentDictionary<string, CancellationTokenSource>();

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
                Timeout = TimeSpan.FromSeconds(60) // Base timeout, individual requests control their own CTS
            };
        }

        /// <summary>
        /// Phương thức điều phối chính của Web Engine theo mô hình Pipeline.
        /// </summary>
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

            var responseResult = new WebEngineResponse
            {
                RequestId = request.RequestId
            };

            var sw = Stopwatch.StartNew();
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(
                request.CancellationToken,
                new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, request.TimeoutSeconds))).Token))
            {
                ActiveRequests[request.RequestId] = cts;

                try
                {
                    using (var reqMsg = new HttpRequestMessage(new HttpMethod(request.Method), request.Url))
                    {
                        // 1. Request Manager: Thiết lập User-Agent & Headers
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

                        if (request.Method.Equals("POST", StringComparison.OrdinalIgnoreCase) ||
                            request.Method.Equals("PUT", StringComparison.OrdinalIgnoreCase))
                        {
                            if (request.Body != null)
                            {
                                reqMsg.Content = new StringContent(request.Body, Encoding.UTF8, request.ContentType ?? "application/json");
                            }
                        }

                        // 2. HTTP Transport Layer
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
                            responseResult.ContentLength = responseResult.RawBytes?.LongLength ?? 0;
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
                    {
                        responseResult.ErrorType = WebEngineErrorType.DnsResolutionFailed;
                    }
                    else if (msg.Contains("ssl") || msg.Contains("tls") || msg.Contains("certificate") || msg.Contains("secure"))
                    {
                        responseResult.ErrorType = WebEngineErrorType.SslHandshakeFailed;
                    }
                    else if (msg.Contains("network") || msg.Contains("connection"))
                    {
                        responseResult.ErrorType = WebEngineErrorType.NetworkUnavailable;
                    }
                    else
                    {
                        responseResult.ErrorType = WebEngineErrorType.Unknown;
                    }
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    responseResult.ResponseTimeMs = sw.ElapsedMilliseconds;
                    responseResult.IsSuccess = false;
                    responseResult.ErrorType = WebEngineErrorType.Unknown;
                    responseResult.ErrorMessage = ex.Message;
                }
                finally
                {
                    ActiveRequests.TryRemove(request.RequestId, out _);
                }
            }

            return responseResult;
        }

        public static async Task<WebEngineResponse> GetAsync(string url, CancellationToken ct = default(CancellationToken), int timeoutSeconds = 20, string userAgent = null)
        {
            return await RequestAsync(new WebEngineRequest
            {
                Url = url,
                Method = "GET",
                CancellationToken = ct,
                TimeoutSeconds = timeoutSeconds,
                UserAgent = userAgent
            });
        }

        public static async Task<WebEngineResponse> PostAsync(string url, string data, string contentType = "application/json", CancellationToken ct = default(CancellationToken), int timeoutSeconds = 20)
        {
            return await RequestAsync(new WebEngineRequest
            {
                Url = url,
                Method = "POST",
                Body = data,
                ContentType = contentType,
                CancellationToken = ct,
                TimeoutSeconds = timeoutSeconds
            });
        }

        /// <summary>
        /// Tải file trực tiếp về bộ nhớ máy (StorageFile) kèm theo dõi % tiến độ.
        /// </summary>
        public static async Task<bool> DownloadAsync(string url, StorageFile targetFile, IProgress<double> progress = null, CancellationToken ct = default(CancellationToken))
        {
            if (string.IsNullOrWhiteSpace(url) || targetFile == null) return false;

            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Get, url))
                using (var resp = await Client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    var totalBytes = resp.Content.Headers.ContentLength ?? -1L;

                    using (var contentStream = await resp.Content.ReadAsStreamAsync())
                    using (var fileStream = await targetFile.OpenStreamForWriteAsync())
                    {
                        var buffer = new byte[8192];
                        long totalRead = 0;
                        int bytesRead;

                        while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, ct)) > 0)
                        {
                            await fileStream.WriteAsync(buffer, 0, bytesRead, ct);
                            totalRead += bytesRead;

                            if (totalBytes > 0 && progress != null)
                            {
                                double percent = (double)totalRead / totalBytes * 100.0;
                                progress.Report(percent);
                            }
                        }
                    }
                }
                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("DownloadAsync Error: " + ex.Message);
                return false;
            }
        }

        /// <summary>
        /// Kiểm tra độ trễ mạng thực tế (Ping Test).
        /// </summary>
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

        /// <summary>
        /// Hủy tức thì một request đang hoạt động.
        /// </summary>
        public static bool CancelRequest(string requestId)
        {
            if (!string.IsNullOrEmpty(requestId) && ActiveRequests.TryRemove(requestId, out var cts))
            {
                try
                {
                    cts.Cancel();
                    return true;
                }
                catch {}
            }
            return false;
        }

        /// <summary>
        /// Tương thích ngược: ExecuteRequestAsync cho các module cũ.
        /// </summary>
        public static async Task<WebEngineResponse> ExecuteRequestAsync(string url, string method = "GET", string postData = null, string userAgent = null)
        {
            return await RequestAsync(new WebEngineRequest
            {
                Url = url,
                Method = method,
                Body = postData,
                UserAgent = userAgent
            });
        }
    }
}
