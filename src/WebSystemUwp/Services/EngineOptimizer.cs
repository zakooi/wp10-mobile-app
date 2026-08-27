using System;
using System.Collections.Generic;

namespace WebSystemUwp.Services
{
    public enum UserAgentProfile
    {
        ChromeMobile,
        FirefoxMobile,
        SafariIos,
        ChromeDesktop
    }

    /// <summary>
    /// Bộ tối ưu hóa và nâng cấp năng lực Web Engine cho Windows 10 Mobile.
    /// Cung cấp JS Polyfills, CSS Chặn quảng cáo, Dark Mode, Reader Mode, Find in Page, và User-Agent Spoofing.
    /// </summary>
    public static class EngineOptimizer
    {
        public static readonly Dictionary<UserAgentProfile, string> UserAgents = new Dictionary<UserAgentProfile, string>
        {
            {
                UserAgentProfile.ChromeMobile,
                "Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Mobile Safari/537.36"
            },
            {
                UserAgentProfile.FirefoxMobile,
                "Mozilla/5.0 (Android 14; Mobile; rv:128.0) Gecko/128.0 Firefox/128.0"
            },
            {
                UserAgentProfile.SafariIos,
                "Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1"
            },
            {
                UserAgentProfile.ChromeDesktop,
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/125.0.0.0 Safari/537.36"
            }
        };

        /// <summary>
        /// JavaScript Polyfills giúp bổ sung các hàm ES6/ES2020 còn thiếu trên trình duyệt.
        /// </summary>
        public static string GetModernPolyfillsScript()
        {
            return @"
(function() {
    try {
        if (typeof globalThis === 'undefined') { window.globalThis = window; }
        if (!Object.fromEntries) {
            Object.fromEntries = function(entries) {
                if (!entries || !entries[Symbol.iterator]) { throw new TypeError('entries must be iterable'); }
                var obj = {};
                for (var pair of entries) {
                    if (Object(pair) !== pair) { throw new TypeError('iterable element is not an entry object'); }
                    obj[pair[0]] = pair[1];
                }
                return obj;
            };
        }
        if (!Array.prototype.flat) {
            Array.prototype.flat = function(depth) {
                depth = depth === undefined ? 1 : Number(depth);
                return depth > 0 ? Array.prototype.reduce.call(this, function(acc, val) {
                    return acc.concat(Array.isArray(val) ? val.flat(depth - 1) : val);
                }, []) : Array.prototype.slice.call(this);
            };
        }
        if (!Array.prototype.flatMap) {
            Array.prototype.flatMap = function(callback, thisArg) {
                return this.map(callback, thisArg).flat();
            };
        }
        if (!Promise.allSettled) {
            Promise.allSettled = function(promises) {
                return Promise.all(promises.map(function(p) {
                    return Promise.resolve(p).then(
                        function(value) { return { status: 'fulfilled', value: value }; },
                        function(reason) { return { status: 'rejected', reason: reason }; }
                    );
                }));
            };
        }
        if (window.crypto && !window.crypto.randomUUID) {
            window.crypto.randomUUID = function() {
                return ([1e7]+-1e3+-4e3+-8e3+-1e11).replace(/[018]/g, function(c) {
                    return (c ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> c / 4).toString(16);
                });
            };
        }
        window.onerror = function(msg, url, lineNo) {
            console.warn('[WebSystem Polyfill] Suppressed Script Error:', msg, 'at', url, lineNo);
            return true;
        };
    } catch(e) {}
})();
";
        }

        /// <summary>
        /// CSS và Script chặn quảng cáo nặng, cookie popups, trackers.
        /// </summary>
        public static string GetContentBlockerScript()
        {
            return @"
(function() {
    try {
        var style = document.createElement('style');
        style.id = 'websystem-adblock-style';
        style.textContent = `
            ins.adsbygoogle, div[id*='google_ads'], div[class*='adsbygoogle'],
            div[id*='taboola-'], div[class*='taboola-'], div[id*='outbrain-'],
            div[class*='outbrain-'], iframe[src*='doubleclick.net'],
            iframe[src*='googlesyndication.com'], div[class*='banner-ads'],
            div[class*='ad-container'], div[id*='ad-wrapper'],
            #onetrust-consent-sdk, .cookie-banner, #cookie-law-info-bar,
            .qc-cmp-ui-container, div[class*='cookie-consent'],
            div[id*='cookie-notice'], .cc-window {
                display: none !important;
                visibility: hidden !important;
                height: 0 !important;
                max-height: 0 !important;
                opacity: 0 !important;
                pointer-events: none !important;
            }
        `;
        if (document.head && !document.getElementById('websystem-adblock-style')) {
            document.head.appendChild(style);
        }
    } catch(e) {}
})();
";
        }

        /// <summary>
        /// Chế độ tiết kiệm dữ liệu: Chặn tải hình ảnh.
        /// </summary>
        public static string GetImageBlockerScript()
        {
            return @"
(function() {
    try {
        var style = document.createElement('style');
        style.id = 'websystem-imgblock-style';
        style.textContent = `
            img, svg, picture, video, canvas, [style*='background-image'] {
                display: none !important;
                visibility: hidden !important;
            }
        `;
        if (document.head && !document.getElementById('websystem-imgblock-style')) {
            document.head.appendChild(style);
        }
    } catch(e) {}
})();
";
        }

        /// <summary>
        /// CSS Nền tối chuẩn AMOLED.
        /// </summary>
        public static string GetDarkModeScript()
        {
            return @"
(function() {
    try {
        var existing = document.getElementById('websystem-darkmode-style');
        if (existing) {
            existing.remove();
            return;
        }
        var style = document.createElement('style');
        style.id = 'websystem-darkmode-style';
        style.textContent = `
            html, body {
                background-color: #121212 !important;
                color: #e0e0e0 !important;
            }
            p, span, h1, h2, h3, h4, h5, h6, li, td, th {
                color: #e0e0e0 !important;
            }
            a { color: #8ab4f8 !important; }
            input, textarea, select {
                background-color: #242424 !important;
                color: #ffffff !important;
                border-color: #444444 !important;
            }
            img, video { filter: brightness(0.85) contrast(1.1) !important; }
        `;
        if (document.head) {
            document.head.appendChild(style);
        }
    } catch(e) {}
})();
";
        }

        /// <summary>
        /// Tìm kiếm từ khóa trong trang và highlight kết quả.
        /// </summary>
        public static string GetFindInPageScript(string keyword, int targetIndex = 0)
        {
            string safeKey = keyword.Replace("\\", "\\\\").Replace("'", "\\'");
            string template = @"
(function() {
    try {
        document.querySelectorAll('mark.ws-find-mark').forEach(function(m) {
            var parent = m.parentNode;
            parent.replaceChild(document.createTextNode(m.textContent), m);
            parent.normalize();
        });

        var query = '{{KEYWORD}}';
        if (!query) return '0/0';

        var matches = [];
        var walk = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT, null, false);
        var n;
        while(n = walk.nextNode()) {
            if (n.parentNode && ['SCRIPT','STYLE','NOSCRIPT','TEXTAREA','INPUT'].indexOf(n.parentNode.tagName) === -1) {
                var idx = n.nodeValue.toLowerCase().indexOf(query.toLowerCase());
                if (idx !== -1) {
                    matches.push({ node: n, index: idx });
                }
            }
        }

        var count = matches.length;
        if (count === 0) return '0/0';

        var active = {{TARGET_INDEX}} % count;
        if (active < 0) active += count;

        var target = matches[active];
        if (target) {
            var span = document.createElement('mark');
            span.className = 'ws-find-mark';
            span.style.backgroundColor = '#FF9800';
            span.style.color = '#000000';
            span.style.borderRadius = '2px';
            span.style.padding = '1px 2px';

            var after = target.node.splitText(target.index);
            after.nodeValue = after.nodeValue.substring(query.length);
            span.appendChild(document.createTextNode(query));
            target.node.parentNode.insertBefore(span, after);

            span.scrollIntoView({ behavior: 'smooth', block: 'center' });
        }

        return (active + 1) + '/' + count;
    } catch(e) {
        return '0/0';
    }
})();
";
            return template.Replace("{{KEYWORD}}", safeKey).Replace("{{TARGET_INDEX}}", targetIndex.ToString());
        }

        public static string GetClearFindInPageScript()
        {
            return @"
(function() {
    try {
        document.querySelectorAll('mark.ws-find-mark').forEach(function(m) {
            var parent = m.parentNode;
            parent.replaceChild(document.createTextNode(m.textContent), m);
            parent.normalize();
        });
    } catch(e) {}
})();
";
        }

        /// <summary>
        /// Phóng to / thu nhỏ nội dung trang web theo tỷ lệ phần trăm (vd: 120 = 120%).
        /// </summary>
        public static string GetZoomScript(int zoomPercent)
        {
            double scale = zoomPercent / 100.0;
            return $"document.body.style.zoom = '{scale:F2}';";
        }

        /// <summary>
        /// Chế độ đọc báo (Reader Mode): Trích xuất tiêu đề và bài viết sạch sẽ.
        /// </summary>
        public static string GetReaderModeScript()
        {
            return @"
(function() {
    try {
        var existing = document.getElementById('ws-reader-container');
        if (existing) {
            existing.remove();
            return;
        }

        var title = document.querySelector('h1')?.innerText || document.title;
        var paragraphs = [];
        document.querySelectorAll('article p, main p, .content p, .article-body p, p').forEach(function(p) {
            var txt = p.innerText.trim();
            if (txt.length > 50) {
                paragraphs.push('<p style=""margin-bottom:1.2em;line-height:1.75;font-size:18px;color:#E0E0E0;"">' + txt + '</p>');
            }
        });

        if (paragraphs.length === 0) {
            alert('Không tìm thấy nội dung bài viết phù hợp để bật chế độ đọc.');
            return;
        }

        var readerDiv = document.createElement('div');
        readerDiv.id = 'ws-reader-container';
        readerDiv.style.cssText = 'position:fixed;top:0;left:0;width:100vw;height:100vh;background:#181818;color:#E0E0E0;z-index:999999;overflow-y:auto;padding:24px 20px;box-sizing:border-box;font-family:system-ui,-apple-system,Segoe UI,Roboto,sans-serif;';

        readerDiv.innerHTML = `
            <div style=""max-width:680px;margin:0 auto;"">
                <button onclick=""document.getElementById('ws-reader-container').remove();"" style=""background:#333;color:#fff;border:none;padding:8px 16px;border-radius:20px;font-size:14px;margin-bottom:20px;cursor:pointer;"">✕ Thoát Chế Độ Đọc</button>
                <h1 style=""font-size:24px;font-weight:bold;margin-bottom:20px;color:#8AB4F8;line-height:1.3;"">${title}</h1>
                <hr style=""border:none;border-top:1px solid #333;margin-bottom:24px;"" />
                ${paragraphs.join('')}
                <div style=""height:60px;""></div>
            </div>
        `;
        document.body.appendChild(readerDiv);
    } catch(e) {
        alert('Lỗi khởi động chế độ đọc: ' + e.message);
    }
})();
";
        }
    }
}
