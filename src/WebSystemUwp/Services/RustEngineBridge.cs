using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
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
    /// Giai đoạn 0-5: HTTP/TLS 1.3 + Cookies, External CSS, Boa JS ES6+ DOM Sandbox, HTML5/CSS Layout (WSR2).
    /// </summary>
    public static class RustEngineBridge
    {
        private const string NativeDllName = "websystem_core.dll";
        private const int DefaultBufferSize = 1024;
        private const int MaxExternalResources = 6;
        private const int MaxExternalCssBytes = 512 * 1024;
        private const int MaxExternalJsBytes = 256 * 1024;
        private const string ScriptChunkSeparator = "\n/*__WS_SCRIPT_SEP__*/\n";
        private const uint RenderMagicWsr2 = 0x57535232; // "WSR2"
        private static readonly SemaphoreSlim HttpGate = new SemaphoreSlim(4, 4);

        private delegate int NativeExtractUrlsDelegate(
            byte[] htmlPtr,
            UIntPtr htmlLen,
            byte[] baseUrlPtr,
            UIntPtr baseUrlLen,
            byte[] outBuf,
            UIntPtr outCap);

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
        private static extern IntPtr ws_http_request_v2(
            byte[] methodPtr,
            UIntPtr methodLen,
            byte[] urlPtr,
            UIntPtr urlLen,
            byte[] uaPtr,
            UIntPtr uaLen,
            byte[] ctPtr,
            UIntPtr ctLen,
            byte[] bodyPtr,
            UIntPtr bodyLen,
            uint timeoutSecs,
            uint maxBodyBytes);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_status(IntPtr resp);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern ulong ws_http_response_elapsed_ms(IntPtr resp);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_content_type(IntPtr resp, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_tls_info(IntPtr resp, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_final_url(IntPtr resp, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_error(IntPtr resp, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr ws_http_response_body_len(IntPtr resp);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_http_response_copy_body(IntPtr resp, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void ws_http_response_free(IntPtr resp);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ws_http_request_v3(
            byte[] methodPtr,
            UIntPtr methodLen,
            byte[] urlPtr,
            UIntPtr urlLen,
            byte[] uaPtr,
            UIntPtr uaLen,
            byte[] ctPtr,
            UIntPtr ctLen,
            byte[] bodyPtr,
            UIntPtr bodyLen,
            byte[] refererPtr,
            UIntPtr refererLen,
            byte[] acceptPtr,
            UIntPtr acceptLen,
            uint timeoutSecs,
            uint maxBodyBytes);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ws_decode_webp_rgba(byte[] dataPtr, UIntPtr dataLen);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ws_decode_svg_rgba(
            byte[] svgPtr,
            UIntPtr svgLen,
            uint targetW,
            uint targetH);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint ws_decoded_image_width(IntPtr img);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint ws_decoded_image_height(IntPtr img);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr ws_decoded_image_len(IntPtr img);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_decoded_image_copy(IntPtr img, byte[] outBuf, UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void ws_decoded_image_free(IntPtr img);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern void ws_http_clear_cookies();

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_extract_stylesheets(
            byte[] htmlPtr,
            UIntPtr htmlLen,
            byte[] baseUrlPtr,
            UIntPtr baseUrlLen,
            byte[] outBuf,
            UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern int ws_extract_scripts(
            byte[] htmlPtr,
            UIntPtr htmlLen,
            byte[] baseUrlPtr,
            UIntPtr baseUrlLen,
            byte[] outBuf,
            UIntPtr outCap);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern IntPtr ws_render_html_v2(
            byte[] htmlPtr,
            UIntPtr htmlLen,
            byte[] baseUrlPtr,
            UIntPtr baseUrlLen,
            byte[] extraCssPtr,
            UIntPtr extraCssLen,
            byte[] extraJsPtr,
            UIntPtr extraJsLen,
            byte[] uaPtr,
            UIntPtr uaLen,
            float viewportWidth,
            byte darkMode,
            byte blockAds,
            byte blockImages,
            byte readerMode,
            byte enableJs);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern UIntPtr ws_render_buffer_len(IntPtr buf);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint ws_render_buffer_js_scripts(IntPtr buf);

        [DllImport(NativeDllName, CallingConvention = CallingConvention.Cdecl)]
        private static extern uint ws_render_buffer_js_errors(IntPtr buf);

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

        public static byte[] DecodeWebPToRgba(byte[] webpBytes, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (!IsAvailable || webpBytes == null || webpBytes.Length < 12) return null;

            return ReadDecodedImageAndFree(
                ws_decode_webp_rgba(webpBytes, (UIntPtr)webpBytes.Length),
                out width,
                out height);
        }

        public static byte[] DecodeSvgToRgba(string svgXml, int targetWidth, int targetHeight, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (!IsAvailable || string.IsNullOrWhiteSpace(svgXml)) return null;

            byte[] svgBytes = Encoding.UTF8.GetBytes(svgXml);
            uint tw = targetWidth > 0 ? (uint)targetWidth : 0u;
            uint th = targetHeight > 0 ? (uint)targetHeight : 0u;
            return ReadDecodedImageAndFree(
                ws_decode_svg_rgba(svgBytes, (UIntPtr)svgBytes.Length, tw, th),
                out width,
                out height);
        }

        private static byte[] ReadDecodedImageAndFree(IntPtr imgHandle, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (imgHandle == IntPtr.Zero) return null;

            try
            {
                uint w = ws_decoded_image_width(imgHandle);
                uint h = ws_decoded_image_height(imgHandle);
                ulong len = ws_decoded_image_len(imgHandle).ToUInt64();
                if (w == 0 || h == 0 || len == 0 || len > int.MaxValue) return null;

                var rgba = new byte[(int)len];
                int copied = ws_decoded_image_copy(imgHandle, rgba, (UIntPtr)rgba.Length);
                if (copied <= 0) return null;

                width = (int)w;
                height = (int)h;
                return rgba;
            }
            catch
            {
                return null;
            }
            finally
            {
                ws_decoded_image_free(imgHandle);
            }
        }

        public static void ClearCookies()
        {
            if (!IsAvailable) return;
            try
            {
                ws_http_clear_cookies();
            }
            catch { }
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

        public static async Task<WebEngineResponse> SendHttpRequestAsync(WebEngineRequest request)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));
            if (request.CancellationToken.IsCancellationRequested)
            {
                return new WebEngineResponse
                {
                    RequestId = request.RequestId,
                    IsSuccess = false,
                    ErrorType = WebEngineErrorType.Cancelled,
                    ErrorMessage = "Yêu cầu đã bị hủy."
                };
            }

            bool acquired = false;
            try
            {
                await HttpGate.WaitAsync(request.CancellationToken);
                acquired = true;
                if (request.CancellationToken.IsCancellationRequested)
                {
                    return new WebEngineResponse
                    {
                        RequestId = request.RequestId,
                        IsSuccess = false,
                        ErrorType = WebEngineErrorType.Cancelled,
                        ErrorMessage = "Yêu cầu đã bị hủy."
                    };
                }
                return await Task.Run(() => ExecuteHttpSync(request));
            }
            catch (OperationCanceledException)
            {
                return new WebEngineResponse
                {
                    RequestId = request.RequestId,
                    IsSuccess = false,
                    ErrorType = WebEngineErrorType.Cancelled,
                    ErrorMessage = "Yêu cầu đã bị hủy."
                };
            }
            finally
            {
                if (acquired) HttpGate.Release();
            }
        }

        public static Task<RustRenderPage> RenderHtmlAsync(
            string html,
            string baseUrl,
            float viewportWidth,
            bool darkMode,
            string extraCss = "",
            bool blockAds = false,
            bool blockImages = false,
            bool readerMode = false,
            string extraJs = "",
            string userAgent = "",
            bool enableJs = false)
        {
            return Task.Run(() => RenderHtmlSync(
                html ?? string.Empty,
                baseUrl ?? string.Empty,
                extraCss ?? string.Empty,
                extraJs ?? string.Empty,
                userAgent ?? string.Empty,
                viewportWidth,
                darkMode,
                blockAds,
                blockImages,
                readerMode,
                enableJs));
        }

        public static async Task<RustRenderPage> FetchAndRenderAsync(
            string url,
            string userAgent,
            float viewportWidth,
            bool darkMode,
            int timeoutSecs = 20,
            bool blockAds = false,
            bool blockImages = false,
            bool readerMode = false,
            string method = "GET",
            string postBody = null,
            string requestContentType = null,
            bool enableJs = true,
            CancellationToken ct = default(CancellationToken))
        {
            var httpResp = await SendHttpRequestAsync(new WebEngineRequest
            {
                Url = url,
                Method = string.IsNullOrWhiteSpace(method) ? "GET" : method,
                Body = postBody,
                ContentType = requestContentType ?? "application/x-www-form-urlencoded",
                UserAgent = userAgent,
                TimeoutSeconds = timeoutSecs,
                UseRustTlsEngine = true,
                CancellationToken = ct
            });

            string effectiveUrl = !string.IsNullOrWhiteSpace(httpResp.FinalUrl) ? httpResp.FinalUrl : url;
            string htmlToRender;
            bool isHtml = false;

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
                    $"<html><head><title>{WebUtility.HtmlEncode(effectiveUrl)}</title></head>" +
                    $"<body><h3>Nội dung ({WebUtility.HtmlEncode(httpResp.ContentType)})</h3>" +
                    $"<pre>{safeText}</pre></body></html>";
            }
            else
            {
                htmlToRender = httpResp.Body ?? string.Empty;
                isHtml = true;
            }

            string combinedCss = string.Empty;
            string combinedJs = string.Empty;
            int cssCount = 0;

            if (isHtml && !readerMode && !string.IsNullOrEmpty(htmlToRender) && !ct.IsCancellationRequested)
            {
                var cssTask = FetchExternalResourcesAsync(
                    ws_extract_stylesheets,
                    htmlToRender,
                    effectiveUrl,
                    userAgent,
                    blockAds,
                    MaxExternalCssBytes,
                    "\n",
                    ct);

                var jsTask = enableJs
                    ? FetchExternalResourcesAsync(
                        ws_extract_scripts,
                        htmlToRender,
                        effectiveUrl,
                        userAgent,
                        blockAds,
                        MaxExternalJsBytes,
                        ScriptChunkSeparator,
                        ct)
                    : Task.FromResult(Tuple.Create(string.Empty, 0));

                await Task.WhenAll(cssTask, jsTask);
                combinedCss = cssTask.Result.Item1;
                cssCount = cssTask.Result.Item2;
                combinedJs = jsTask.Result.Item1;
            }

            var page = await RenderHtmlAsync(
                htmlToRender,
                effectiveUrl,
                viewportWidth,
                darkMode,
                combinedCss,
                blockAds,
                blockImages,
                readerMode,
                combinedJs,
                userAgent,
                isHtml && enableJs && !readerMode);

            page.Url = effectiveUrl;
            page.SourceHtml = htmlToRender;
            page.CombinedCss = combinedCss;
            page.CombinedJs = combinedJs;
            page.ExternalCssCount = cssCount;
            page.IsReaderMode = readerMode;
            page.TransportInfo = httpResp.TransportInfo;
            page.ElapsedMs = httpResp.ResponseTimeMs;
            return page;
        }

        private static bool IsTrackerUrl(string url)
        {
            return url.IndexOf("doubleclick", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("googlesyndication", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("google-analytics", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("googletagmanager", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("fbevents", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("cookie", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   url.IndexOf("onetrust", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static async Task<Tuple<string, int>> FetchExternalResourcesAsync(
            NativeExtractUrlsDelegate extractor,
            string html,
            string baseUrl,
            string userAgent,
            bool blockAds,
            int maxBytes,
            string joinSeparator,
            CancellationToken ct)
        {
            try
            {
                var urls = ExtractUrlList(extractor, html, baseUrl);
                if (blockAds)
                {
                    urls = urls.Where(u => !IsTrackerUrl(u)).ToList();
                }

                var targets = urls.Take(MaxExternalResources).ToList();
                if (targets.Count == 0)
                {
                    return Tuple.Create(string.Empty, 0);
                }

                var tasks = targets.Select(u => SendHttpRequestAsync(new WebEngineRequest
                {
                    Url = u,
                    Method = "GET",
                    UserAgent = userAgent,
                    TimeoutSeconds = 6,
                    MaxResponseBytes = maxBytes,
                    UseRustTlsEngine = true,
                    CancellationToken = ct
                })).ToArray();

                var responses = await Task.WhenAll(tasks);
                var chunks = new List<string>();
                foreach (var resp in responses)
                {
                    if (resp != null && resp.IsSuccess && !string.IsNullOrEmpty(resp.Body))
                    {
                        chunks.Add(resp.Body);
                    }
                }
                return Tuple.Create(string.Join(joinSeparator, chunks), chunks.Count);
            }
            catch
            {
                return Tuple.Create(string.Empty, 0);
            }
        }

        private static List<string> ExtractUrlList(NativeExtractUrlsDelegate extractor, string html, string baseUrl)
        {
            var list = new List<string>();
            try
            {
                byte[] htmlBytes = Encoding.UTF8.GetBytes(html ?? string.Empty);
                byte[] urlBytes = Encoding.UTF8.GetBytes(baseUrl ?? string.Empty);
                var outBuf = new byte[8192];
                int written = extractor(
                    htmlBytes, (UIntPtr)htmlBytes.Length,
                    urlBytes, (UIntPtr)urlBytes.Length,
                    outBuf, (UIntPtr)outBuf.Length);

                if (written > 0)
                {
                    int safeLen = Math.Min(written, outBuf.Length);
                    string joined = Encoding.UTF8.GetString(outBuf, 0, safeLen);
                    if (safeLen >= outBuf.Length - 4)
                    {
                        int lastNl = joined.LastIndexOf('\n');
                        if (lastNl > 0) joined = joined.Substring(0, lastNl);
                    }
                    foreach (var line in joined.Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        string u = line.Trim();
                        if (!string.IsNullOrEmpty(u))
                        {
                            list.Add(u);
                        }
                    }
                }
            }
            catch { }
            return list;
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
            byte[] ctBytes = string.IsNullOrWhiteSpace(request.ContentType)
                ? new byte[0]
                : Encoding.UTF8.GetBytes(request.ContentType);
            byte[] bodyBytes = string.IsNullOrEmpty(request.Body)
                ? new byte[0]
                : Encoding.UTF8.GetBytes(request.Body);
            byte[] refererBytes = string.IsNullOrWhiteSpace(request.Referer)
                ? new byte[0]
                : Encoding.UTF8.GetBytes(request.Referer);
            byte[] acceptBytes = string.IsNullOrWhiteSpace(request.Accept)
                ? new byte[0]
                : Encoding.UTF8.GetBytes(request.Accept);

            IntPtr handle = IntPtr.Zero;
            try
            {
                handle = ws_http_request_v3(
                    methodBytes, (UIntPtr)methodBytes.Length,
                    urlBytes, (UIntPtr)urlBytes.Length,
                    uaBytes, (UIntPtr)uaBytes.Length,
                    ctBytes, (UIntPtr)ctBytes.Length,
                    bodyBytes, (UIntPtr)bodyBytes.Length,
                    refererBytes, (UIntPtr)refererBytes.Length,
                    acceptBytes, (UIntPtr)acceptBytes.Length,
                    (uint)Math.Max(5, request.TimeoutSeconds),
                    (uint)Math.Max(0, request.MaxResponseBytes));

                if (handle == IntPtr.Zero)
                {
                    result.IsSuccess = false;
                    result.ErrorType = WebEngineErrorType.Unknown;
                    result.ErrorMessage = "ws_http_request_v2 trả về handle rỗng.";
                    return result;
                }

                int status = ws_http_response_status(handle);
                ulong elapsed = ws_http_response_elapsed_ms(handle);
                string contentType = ReadOptionalUtf8Buffer((buf, cap) => ws_http_response_content_type(handle, buf, cap));
                string tlsInfo = ReadOptionalUtf8Buffer((buf, cap) => ws_http_response_tls_info(handle, buf, cap));
                string finalUrl = ReadOptionalUtf8Buffer((buf, cap) => ws_http_response_final_url(handle, buf, cap));
                string errMsg = ReadOptionalUtf8Buffer((buf, cap) => ws_http_response_error(handle, buf, cap));

                result.StatusCode = status;
                result.ResponseTimeMs = (long)elapsed;
                result.ContentType = contentType;
                result.FinalUrl = string.IsNullOrEmpty(finalUrl) ? request.Url : finalUrl;
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
                            result.Body = DecodeTextBody(raw, copied, contentType);
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

        private static string DecodeTextBody(byte[] raw, int count, string contentType)
        {
            if (raw == null || count <= 0) return string.Empty;
            if (!string.IsNullOrEmpty(contentType) &&
                (contentType.IndexOf("image/", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 contentType.IndexOf("octet-stream", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return string.Empty;
            }

            if (!string.IsNullOrEmpty(contentType))
            {
                int idx = contentType.IndexOf("charset=", StringComparison.OrdinalIgnoreCase);
                if (idx >= 0)
                {
                    string cs = contentType.Substring(idx + 8).Trim().Trim('"', '\'');
                    int semi = cs.IndexOf(';');
                    if (semi >= 0) cs = cs.Substring(0, semi).Trim();
                    if (!string.IsNullOrEmpty(cs) &&
                        !cs.Equals("utf-8", StringComparison.OrdinalIgnoreCase) &&
                        !cs.Equals("utf8", StringComparison.OrdinalIgnoreCase))
                    {
                        try
                        {
                            var enc = Encoding.GetEncoding(cs);
                            return enc.GetString(raw, 0, count);
                        }
                        catch { }
                    }
                }
            }

            return Encoding.UTF8.GetString(raw, 0, Math.Min(count, raw.Length));
        }

        private static RustRenderPage RenderHtmlSync(
            string html,
            string baseUrl,
            string extraCss,
            string extraJs,
            string userAgent,
            float viewportWidth,
            bool darkMode,
            bool blockAds,
            bool blockImages,
            bool readerMode,
            bool enableJs)
        {
            byte[] htmlBytes = Encoding.UTF8.GetBytes(html);
            byte[] urlBytes = Encoding.UTF8.GetBytes(baseUrl);
            byte[] cssBytes = string.IsNullOrEmpty(extraCss) ? new byte[0] : Encoding.UTF8.GetBytes(extraCss);
            byte[] jsBytes = string.IsNullOrEmpty(extraJs) ? new byte[0] : Encoding.UTF8.GetBytes(extraJs);
            byte[] uaBytes = string.IsNullOrEmpty(userAgent) ? new byte[0] : Encoding.UTF8.GetBytes(userAgent);
            IntPtr bufHandle = IntPtr.Zero;

            try
            {
                bufHandle = ws_render_html_v2(
                    htmlBytes, (UIntPtr)htmlBytes.Length,
                    urlBytes, (UIntPtr)urlBytes.Length,
                    cssBytes, (UIntPtr)cssBytes.Length,
                    jsBytes, (UIntPtr)jsBytes.Length,
                    uaBytes, (UIntPtr)uaBytes.Length,
                    viewportWidth,
                    darkMode ? (byte)1 : (byte)0,
                    blockAds ? (byte)1 : (byte)0,
                    blockImages ? (byte)1 : (byte)0,
                    readerMode ? (byte)1 : (byte)0,
                    enableJs ? (byte)1 : (byte)0);

                if (bufHandle == IntPtr.Zero)
                {
                    return new RustRenderPage { Url = baseUrl, Title = baseUrl, ContentWidth = viewportWidth, ContentHeight = 200 };
                }

                int jsScripts = (int)ws_render_buffer_js_scripts(bufHandle);
                int jsErrors = (int)ws_render_buffer_js_errors(bufHandle);
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

                var page = DecodeDisplayList(raw, copied, baseUrl);
                page.JsScriptsExecuted = jsScripts;
                page.JsErrors = jsErrors;
                return page;
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
                if (magic != RenderMagicWsr2)
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
                    int formId = reader.ReadInt32();
                    string text = ReadLengthPrefixedUtf8(reader);
                    string href = ReadLengthPrefixedUtf8(reader);
                    string extra = ReadLengthPrefixedUtf8(reader);

                    var item = new RenderDisplayItem
                    {
                        Kind = (RenderItemKind)kind,
                        Bold = (flags & 1) != 0,
                        Italic = (flags & 2) != 0,
                        Underline = (flags & 4) != 0,
                        IsPassword = (flags & 8) != 0,
                        IsMultiline = (flags & 16) != 0,
                        CenterText = (flags & 32) != 0,
                        IsChecked = (flags & 64) != 0,
                        X = x,
                        Y = y,
                        Width = w,
                        Height = h,
                        FontSize = fontSize,
                        ForegroundColor = ArgbToColor(fg),
                        BackgroundColor = ArgbToColor(bg),
                        BorderColor = ArgbToColor(border),
                        FormId = formId,
                        Text = text,
                        Href = href,
                        Extra = extra
                    };

                    if (item.Kind == RenderItemKind.Hidden)
                    {
                        page.HiddenInputs.Add(item);
                    }
                    else
                    {
                        page.Items.Add(item);
                    }
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
