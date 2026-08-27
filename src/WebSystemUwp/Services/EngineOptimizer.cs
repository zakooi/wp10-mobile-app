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
    /// Cung cấp JS Polyfills, CSS Chặn quảng cáo, Dark Mode, và User-Agent Spoofing.
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
        /// JavaScript Polyfills giúp bổ sung các hàm ES6/ES2020 còn thiếu trên trình duyệt,
        /// tránh lỗi Script Error và trang trắng (White Screen).
        /// </summary>
        public static string GetModernPolyfillsScript()
        {
            return @"
(function() {
    try {
        // 1. globalThis polyfill
        if (typeof globalThis === 'undefined') {
            window.globalThis = window;
        }

        // 2. Object.fromEntries polyfill
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

        // 3. Array.prototype.flat & flatMap polyfill
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

        // 4. Promise.allSettled polyfill
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

        // 5. crypto.randomUUID polyfill
        if (window.crypto && !window.crypto.randomUUID) {
            window.crypto.randomUUID = function() {
                return ([1e7]+-1e3+-4e3+-8e3+-1e11).replace(/[018]/g, function(c) {
                    return (c ^ crypto.getRandomValues(new Uint8Array(1))[0] & 15 >> c / 4).toString(16);
                });
            };
        }

        // 6. Suppress legacy window.onerror alerts to prevent white screens
        var originalOnError = window.onerror;
        window.onerror = function(msg, url, lineNo, columnNo, error) {
            console.warn('[WebSystem Polyfill] Suppressed Script Error:', msg, 'at', url, lineNo);
            return true;
        };

        console.log('[WebSystem] Modern Engine Polyfills Injected Successfully!');
    } catch(e) {
        console.error('[WebSystem] Polyfill error:', e);
    }
})();
";
        }

        /// <summary>
        /// CSS và Script chặn quảng cáo nặng, cookie popups, trackers giúp tải trang siêu tốc.
        /// </summary>
        public static string GetContentBlockerScript()
        {
            return @"
(function() {
    try {
        var style = document.createElement('style');
        style.id = 'websystem-adblock-style';
        style.textContent = `
            /* Chặn Banner quảng cáo phổ biến */
            ins.adsbygoogle, div[id*='google_ads'], div[class*='adsbygoogle'],
            div[id*='taboola-'], div[class*='taboola-'], div[id*='outbrain-'],
            div[class*='outbrain-'], iframe[src*='doubleclick.net'],
            iframe[src*='googlesyndication.com'], div[class*='banner-ads'],
            div[class*='ad-container'], div[id*='ad-wrapper'],
            /* Chặn Cookie Popups / GDPR Overlay */
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
        /// CSS Nền tối chuẩn AMOLED tiết kiệm pin cho màn hình Lumia.
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
            a {
                color: #8ab4f8 !important;
            }
            input, textarea, select {
                background-color: #242424 !important;
                color: #ffffff !important;
                border-color: #444444 !important;
            }
            img, video {
                filter: brightness(0.85) contrast(1.1) !important;
            }
        `;
        if (document.head) {
            document.head.appendChild(style);
        }
    } catch(e) {}
})();
";
        }
    }
}
