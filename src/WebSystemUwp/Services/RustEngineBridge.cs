using System;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.UI;

namespace WebSystemUwp.Services
{
    public sealed class RustCoreStatus
    {
        public bool IsLoaded { get; set; }
        public string Version { get; set; }
        public string Info { get; set; }
        public string SmokeTestResult { get; set; }
        public string ErrorMessage { get; set; }
    }

    /// <summary>
    /// Cầu nối P/Invoke tới lõi trình duyệt Rust (websystem_core.dll).
    /// Giai đoạn 0-2: Kiểm chứng runtime, HTTP/TLS 1.3 (rustls) và HTML/CSS Layout (html5ever + taffy).
    /// </summary>
    public static class RustEngineBridge
    {
        private const string NativeDllName = "websystem_core.dll";
        private const int DefaultBufferSize = 1024;
        private const uint RenderMagicWsr1 = 0x57535231; // "WSR1"

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint ws_core_version();

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_core_get_info(byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_core_smoke_test(
            byte[] inputPtr,
            UIntPtr inputLen,
            byte[] outBuf,
            UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ws_http_request(
            byte[] methodPtr,
            UIntPtr methodLen,
            byte[] urlPtr,
            UIntPtr urlLen,
            byte[] uaPtr,
            UIntPtr uaLen,
            byte[] bodyPtr,
            UIntPtr bodyLen,
            uint timeoutSecs);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_status(IntPtr resp);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong ws_http_response_elapsed_ms(IntPtr resp);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_content_type(IntPtr resp, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_tls_info(IntPtr resp, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_error(IntPtr resp, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr ws_http_response_body_len(IntPtr resp);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_copy_body(IntPtr resp, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void ws_http_response_free(IntPtr resp);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ws_render_html(
            byte[] htmlPtr,
            UIntPtr htmlLen,
            byte[] baseUrlPtr,
            UIntPtr baseUrlLen,
            float viewportWidth,
            byte darkMode);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr ws_render_buffer_len(IntPtr buf);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_render_buffer_copy(IntPtr buf, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void ws_render_buffer_free(IntPtr buf);

        private static bool? _cachedAvailable;

        public static bool IsAvailable
        {
            get
            {
                if (!_cachedAvailable.HasValue)
                {
                    _cachedAvailable = ProbeCore().IsLoaded;
                }
                return _cachedAvailable.Value;
            }
        }

        public static RustCoreStatus ProbeCore()
        {
            try
            {
                uint packed = ws_core_version();
                uint major = (packed >> 16) & 0xFF;
                uint minor = (packed >> 8) & 0xFF;
                uint patch = packed & 0xFF;

                string info = ReadUtf8Buffer((buf, cap) => ws_core_get_info(buf, cap));

                byte[] sample = Encoding.UTF8.GetBytes("https://www.google.com");
                string smoke = ReadUtf8Buffer((buf, cap) =>
                    ws_core_smoke_test(sample, (UIntPtr)sample.Length, buf, cap));

                return new RustCoreStatus
                {
                    IsLoaded = true,
                    Version = $"{major}.{minor}.{patch}",
                    Info = info,
                    SmokeTestResult = smoke
                };
            }
            catch (DllNotFoundException)
            {
                return new RustCoreStatus
                {
                    IsLoaded = false,
                    ErrorMessage = "Chưa đóng gói websystem_core.dll cho kiến trúc hiện tại."
                };
            }
            catch (BadImageFormatException ex)
            {
                return new RustCoreStatus
                {
                    IsLoaded = false,
                    ErrorMessage = "Sai kiến trúc DLL (ARM/x86): " + ex.Message
                };
            }
            catch (Exception ex)
            {
                return new RustCoreStatus
                {
                    IsLoaded = false,
                    ErrorMessage = ex.GetType().Name + ": " + ex.Message
                };
            }
        }

        public static string GetCoreStatusSummary()
        {
            var status = ProbeCore();
            if (!status.IsLoaded)
            {
                return $"🦀 Rust Core: Chưa kích hoạt ({status.ErrorMessage})";
            }

            return $"🦀 Rust Core: Sẵn sàng (v{status.Version})\n" +
                   $"   • Info: {status.Info}\n" +
                   $"   • Smoke test: {status.SmokeTestResult}";
        }

        public static Task<WebEngineResponse> SendHttpRequestAsync(WebEngineRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            return Task.Run(() => ExecuteHttpSync(request));
        }

        public static Task<RustRenderPage> RenderHtmlAsync(
            string html,
            string baseUrl,
            float viewportWidth,
            bool darkMode)
        {
            return Task.Run(() => RenderHtmlSync(html ?? string.Empty, baseUrl ?? string.Empty, viewportWidth, darkMode));
        }

        public static async Task<RustRenderPage> FetchAndRenderAsync(
            string url,
            string userAgent,
            float viewportWidth,
            bool darkMode,
            int timeoutSecs = 20)
        {
            var httpResp = await SendHttpRequestAsync(new WebEngineRequest
            {
                Url = url,
                Method = "GET",
                UserAgent = userAgent,
                TimeoutSeconds = timeoutSecs,
                UseRustTlsEngine = true
            });

            string htmlToRender;
            if (httpResp.StatusCode <= 0)
            {
                string safeUrl = WebUtility.HtmlEncode(url ?? "");
                string safeErr = WebUtility.HtmlEncode(httpResp.ErrorMessage ?? "Lỗi kết nối mạng");
                htmlToRender =
                    $"<html><head><title>Lỗi tải trang</title></head><body>" +
                    $"<h2>Không thể tải trang qua Rust Engine</h2>" +
                    $"<p><b>URL:</b> {safeUrl}</p>" +
                    $"<p><b>Chi tiết:</b> {safeErr}</p>" +
                    $"<p>Bạn có thể tắt <i>Trình duyệt Rust Engine</i> trong Menu để mở bằng WebView hệ thống.</p>" +
                    $"</body></html>";
            }
            else if (!string.IsNullOrEmpty(httpResp.ContentType) &&
                     httpResp.ContentType.IndexOf("html", StringComparison.OrdinalIgnoreCase) < 0)
            {
                string safeText = WebUtility.HtmlEncode(httpResp.Body ?? string.Empty);
                htmlToRender =
                    $"<html><head><title>{WebUtility.HtmlEncode(url)}</title></head>" +
                    $"<body><h3>Nội dung ({WebUtility.HtmlEncode(httpResp.ContentType)})</h3>" +
                    $"<pre>{safeText}</pre></body></html>";
            }
            else
            {
                htmlToRender = httpResp.Body ?? string.Empty;
            }

            var page = await RenderHtmlAsync(htmlToRender, url, viewportWidth, darkMode);
            page.Url = url;
            page.SourceHtml = htmlToRender;
            page.TransportInfo = httpResp.TransportInfo;
            page.ElapsedMs = httpResp.ResponseTimeMs;
            return page;
        }

        private static WebEngineResponse ExecuteHttpSync(WebEngineRequest request)
        {
            var result = new WebEngineResponse
            {
                RequestId = request.RequestId
            };

            if (string.IsNullOrWhiteSpace(request.Url))
            {
                result.IsSuccess = false;
                result.ErrorType = WebEngineErrorType.Unknown;
                result.ErrorMessage = "URL yêu cầu không được để trống.";
                return result;
            }

            byte[] methodBytes = Encoding.UTF8.GetBytes(string.IsNullOrWhiteSpace(request.Method) ? "GET" : request.Method);
            byte[] urlBytes = Encoding.UTF8.GetBytes(request.Url);
            byte[] uaBytes = string.IsNullOrWhiteSpace(request.UserAgent)
                ? new byte[0]
                : Encoding.UTF8.GetBytes(request.UserAgent);
            byte[] bodyBytes = string.IsNullOrEmpty(request.Body)
                ? new byte[0]
                : Encoding.UTF8.GetBytes(request.Body);

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = ws_http_request(
                    methodBytes, (UIntPtr)methodBytes.Length,
                    urlBytes, (UIntPtr)urlBytes.Length,
                    uaBytes, (UIntPtr)uaBytes.Length,
                    bodyBytes, (UIntPtr)bodyBytes.Length,
                    (uint)Math.Max(5, request.TimeoutSeconds));

                if (handle == IntPtr.Zero)
                {
                    result.IsSuccess = false;
                    result.ErrorType = WebEngineErrorType.Unknown;
                    result.ErrorMessage = "ws_http_request trả về handle rỗng.";
                    return result;
                }

                int status = ws_http_response_status(handle);
                ulong elapsed = ws_http_response_elapsed_ms(handle);
                string contentType = ReadOptionalUtf8Buffer((buf, cap) => ws_http_response_content_type(handle, buf, cap));
                string tlsInfo = ReadOptionalUtf8Buffer((buf, cap) => ws_http_response_tls_info(handle, buf, cap));
                string errMsg = ReadOptionalUtf8Buffer((buf, cap) => ws_http_response_error(handle, buf, cap));

                result.StatusCode = status;
                result.ResponseTimeMs = (long)elapsed;
                result.ContentType = contentType;
                result.TransportInfo = string.IsNullOrEmpty(tlsInfo) ? "rustls" : tlsInfo;

                if (status > 0 && string.IsNullOrEmpty(errMsg))
                {
                    ulong bodyLen = ws_http_response_body_len(handle).ToUInt64();
                    if (bodyLen > 0 && bodyLen <= int.MaxValue)
                    {
                        var raw = new byte[(int)bodyLen];
                        int copied = ws_http_response_copy_body(handle, raw, (UIntPtr)raw.Length);
                        if (copied > 0)
                        {
                            result.RawBytes = raw;
                            result.ContentLength = copied;
                            result.Body = Encoding.UTF8.GetString(raw, 0, Math.Min(copied, raw.Length));
                        }
                    }
                    else
                    {
                        result.RawBytes = new byte[0];
                        result.Body = string.Empty;
                    }

                    result.IsSuccess = status >= 200 && status < 400;
                    result.StatusText = result.IsSuccess ? "OK" : $"HTTP {status}";
                    if (!result.IsSuccess)
                    {
                        result.ErrorType = WebEngineErrorType.HttpError;
                        result.ErrorMessage = $"HTTP {status}";
                    }
                }
                else
                {
                    result.IsSuccess = false;
                    result.ErrorType = MapErrorKind(tlsInfo);
                    result.ErrorMessage = string.IsNullOrEmpty(errMsg) ? "Lỗi kết nối Rust TLS." : errMsg;
                }

                return result;
            }
            catch (Exception ex)
            {
                result.IsSuccess = false;
                result.ErrorType = WebEngineErrorType.Unknown;
                result.ErrorMessage = ex.Message;
                result.TransportInfo = "RustEngineBridge Error";
                return result;
            }
            finally
            {
                if (handle != IntPtr.Zero)
                {
                    ws_http_response_free(handle);
                }
            }
        }

        private static RustRenderPage RenderHtmlSync(string html, string baseUrl, float viewportWidth, bool darkMode)
        {
            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
            byte[] urlBytes = Encoding.UTF8.GetBytes(baseUrl);
            IntPtr bufHandle = IntPtr.Zero;

            try
            {
                bufHandle = ws_render_html(
                    htmlBytes, (UIntPtr)htmlBytes.Length,
                    urlBytes, (UIntPtr)urlBytes.Length,
                    viewportWidth,
                    darkMode ? (byte)1 : (byte)0);

                if (bufHandle == IntPtr.Zero)
                {
                    return new RustRenderPage { Url = baseUrl, Title = baseUrl, ContentWidth = viewportWidth, ContentHeight = 200 };
                }

                ulong len = ws_render_buffer_len(bufHandle).ToUInt64();
                if (len == 0 || len > int.MaxValue)
                {
                    return new RustRenderPage { Url = baseUrl, Title = baseUrl, ContentWidth = viewportWidth, ContentHeight = 200 };
                }

                var raw = new byte[(int)len];
                int copied = ws_render_buffer_copy(bufHandle, raw, (UIntPtr)raw.Length);
                if (copied <= 0)
                {
                    return new RustRenderPage { Url = baseUrl, Title = baseUrl, ContentWidth = viewportWidth, ContentHeight = 200 };
                }

                return DecodeDisplayList(raw, copied, baseUrl);
            }
            finally
            {
                if (bufHandle != IntPtr.Zero)
                {
                    ws_render_buffer_free(bufHandle);
                }
            }
        }

        private static RustRenderPage DecodeDisplayList(byte[] data, int length, string baseUrl)
        {
            var page = new RustRenderPage { Url = baseUrl };
            using (var ms = new MemoryStream(data, 0, length))
            using (var reader = new BinaryReader(ms, Encoding.UTF8))
            {
                uint magic = reader.ReadUInt32();
                if (magic != RenderMagicWsr1)
                {
                    page.Title = baseUrl;
                    return page;
                }

                page.ContentWidth = reader.ReadSingle();
                page.ContentHeight = reader.ReadSingle();
                page.Title = ReadLengthPrefixedUtf8(reader);

                uint count = reader.ReadUInt32();
                for (uint i = 0; i < count; i++)
                {
                    byte kind = reader.ReadByte();
                    byte flags = reader.ReadByte();
                    float x = reader.ReadSingle();
                    float y = reader.ReadSingle();
                    float w = reader.ReadSingle();
                    float h = reader.ReadSingle();
                    float fontSize = reader.ReadSingle();
                    uint fg = reader.ReadUInt32();
                    uint bg = reader.ReadUInt32();
                    uint border = reader.ReadUInt32();
                    string text = ReadLengthPrefixedUtf8(reader);
                    string href = ReadLengthPrefixedUtf8(reader);

                    page.Items.Add(new RenderDisplayItem
                    {
                        Kind = (RenderItemKind)kind,
                        Bold = (flags & 1) != 0,
                        Italic = (flags & 2) != 0,
                        Underline = (flags & 4) != 0,
                        X = x,
                        Y = y,
                        Width = w,
                        Height = h,
                        FontSize = fontSize,
                        ForegroundColor = ArgbToColor(fg),
                        BackgroundColor = ArgbToColor(bg),
                        BorderColor = ArgbToColor(border),
                        Text = text,
                        Href = href
                    });
                }
            }

            return page;
        }

        private static string ReadLengthPrefixedUtf8(BinaryReader reader)
        {
            int len = (int)reader.ReadUInt32();
            if (len <= 0) return string.Empty;
            byte[] bytes = reader.ReadBytes(len);
            return Encoding.UTF8.GetString(bytes);
        }

        private static Color ArgbToColor(uint argb)
        {
            return Color.FromArgb(
                (byte)((argb >> 24) & 0xFF),
                (byte)((argb >> 16) & 0xFF),
                (byte)((argb >> 8) & 0xFF),
                (byte)(argb & 0xFF));
        }

        private static WebEngineErrorType MapErrorKind(string kind)
        {
            switch (kind)
            {
                case "Timeout":
                    return WebEngineErrorType.Timeout;
                case "DnsResolutionFailed":
                    return WebEngineErrorType.DnsResolutionFailed;
                case "SslHandshakeFailed":
                    return WebEngineErrorType.SslHandshakeFailed;
                case "NetworkUnavailable":
                    return WebEngineErrorType.NetworkUnavailable;
                case "HttpError":
                    return WebEngineErrorType.HttpError;
                default:
                    return WebEngineErrorType.Unknown;
            }
        }

        private static string ReadUtf8Buffer(Func<byte[], UIntPtr, int> nativeCall)
        {
            var buffer = new byte[DefaultBufferSize];
            int written = nativeCall(buffer, (UIntPtr)buffer.Length);
            if (written <= 0)
            {
                return $"ERR({written})";
            }

            return Encoding.UTF8.GetString(buffer, 0, Math.Min(written, buffer.Length));
        }

        private static string ReadOptionalUtf8Buffer(Func<byte[], UIntPtr, int> nativeCall)
        {
            var buffer = new byte[DefaultBufferSize];
            int written = nativeCall(buffer, (UIntPtr)buffer.Length);
            if (written <= 0)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(buffer, 0, Math.Min(written, buffer.Length));
        }
    }
}
