using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace WebSystemUwp.Services
{
    /// <summary>
    /// Truy cập web (HTTP/HTTPS) dùng <see cref="HttpClient"/> của UWP.
    ///
    /// Khác với WP8 (IE10, TLS 1.0/1.1), trên Windows 10 Mobile/UWP HttpClient chạy trên
    /// Schannel hiện đại — hỗ trợ TLS 1.2/1.3 và HTTP/2, nên kết nối website hiện đại tốt hơn nhiều.
    /// Lỗi mạng (hết giờ, offline, site từ chối) vẫn được ném rõ ràng.
    /// </summary>
    public static class WebService
    {
        private static readonly HttpClient Client = new HttpClient();

        static WebService()
        {
            // User-Agent để server không chặn và dễ chẩn đoán.
            if (Client.DefaultRequestHeaders.UserAgent.TryParseAdd("WebSystemUwp/1.0 (Windows 10 Mobile)"))
            {
                // ok
            }
            // Thời gian chờ mặc định cho mỗi yêu cầu.
            Client.Timeout = TimeSpan.FromSeconds(25);
        }

        /// <summary>
        /// Gửi GET tới <paramref name="url"/> và trả về nội dung chuỗi.
        /// </summary>
        /// <exception cref="HttpRequestException">Lỗi HTTP (status không thành công).</exception>
        /// <exception cref="TaskCanceledException">Quá thời gian chờ.</exception>
        public static async Task<string> FetchAsync(string url, int timeoutSeconds = 25)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL không được rỗng.", nameof(url));

            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds)))
            using (var response = await Client.GetAsync(url, cts.Token))
            {
                // Ném lỗi rõ ràng nếu status 4xx/5xx thay vì im lặng.
                response.EnsureSuccessStatusCode();
                return await response.Content.ReadAsStringAsync();
            }
        }
    }
}
