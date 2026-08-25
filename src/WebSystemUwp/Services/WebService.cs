using System;
using System.Diagnostics;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace WebSystemUwp.Services
{
    public class FetchResult
    {
        public bool IsSuccess { get; set; }
        public int StatusCode { get; set; }
        public string StatusText { get; set; }
        public long ElapsedMilliseconds { get; set; }
        public string ContentType { get; set; }
        public long ContentLength { get; set; }
        public string Body { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Module HTTP Client nâng cao trên nền Schannel UWP (TLS 1.2/1.3, HTTP/2).
    /// </summary>
    public static class WebService
    {
        private static readonly HttpClient Client = new HttpClient();

        static WebService()
        {
            Client.Timeout = TimeSpan.FromSeconds(30);
        }

        /// <summary>
        /// Gửi yêu cầu HTTP GET/POST và trả về kết quả chi tiết kèm timing & headers.
        /// </summary>
        public static async Task<FetchResult> ExecuteRequestAsync(string url, string method = "GET", string postData = null, string userAgent = null)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL không được rỗng.", nameof(url));

            var result = new FetchResult();
            var sw = Stopwatch.StartNew();

            using (var request = new HttpRequestMessage(new HttpMethod(method), url))
            {
                // Custom User-Agent
                string ua = string.IsNullOrWhiteSpace(userAgent)
                    ? "Mozilla/5.0 (Windows Phone 10.0; Android 6.0.1; Microsoft; Lumia 950) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/52.0.2743.116 Mobile Safari/537.36 Edge/15.15254"
                    : userAgent;

                request.Headers.TryAddWithoutValidation("User-Agent", ua);
                request.Headers.TryAddWithoutValidation("Accept", "*/*");

                if (method.Equals("POST", StringComparison.OrdinalIgnoreCase) && postData != null)
                {
                    request.Content = new StringContent(postData, Encoding.UTF8, "application/json");
                }

                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    try
                    {
                        using (var response = await Client.SendAsync(request, cts.Token))
                        {
                            sw.Stop();
                            result.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                            result.StatusCode = (int)response.StatusCode;
                            result.StatusText = response.ReasonPhrase;
                            result.IsSuccess = response.IsSuccessStatusCode;
                            result.ContentType = response.Content.Headers.ContentType?.ToString() ?? "text/plain";
                            result.Body = await response.Content.ReadAsStringAsync();
                            result.ContentLength = result.Body.Length;
                        }
                    }
                    catch (Exception ex)
                    {
                        sw.Stop();
                        result.ElapsedMilliseconds = sw.ElapsedMilliseconds;
                        result.IsSuccess = false;
                        result.ErrorMessage = ex.Message;
                    }
                }
            }

            return result;
        }

        public static async Task<string> FetchAsync(string url, int timeoutSeconds = 25)
        {
            var res = await ExecuteRequestAsync(url);
            if (!res.IsSuccess)
                throw new HttpRequestException(res.ErrorMessage ?? $"HTTP Error {res.StatusCode}: {res.StatusText}");
            return res.Body;
        }
    }
}
