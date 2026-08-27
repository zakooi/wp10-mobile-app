using System;
using Windows.Storage;

namespace WebSystemUwp.Services
{
    /// <summary>
    /// Quản lý cài đặt người dùng lưu bền vững trong LocalSettings.
    /// </summary>
    public static class SettingsService
    {
        private static readonly ApplicationDataContainer Settings = ApplicationData.Current.LocalSettings;

        public static string SearchEngine
        {
            get => GetValue("SearchEngine", "Google");
            set => SetValue("SearchEngine", value);
        }

        public static string HomePageUrl
        {
            get => GetValue("HomePageUrl", "about:home");
            set => SetValue("HomePageUrl", value);
        }

        public static bool AutoHideBottomBar
        {
            get => GetValue("AutoHideBottomBar", true);
            set => SetValue("AutoHideBottomBar", value);
        }

        public static bool AutoBlockAds
        {
            get => GetValue("AutoBlockAds", true);
            set => SetValue("AutoBlockAds", value);
        }

        public static bool AutoBlockImages
        {
            get => GetValue("AutoBlockImages", false);
            set => SetValue("AutoBlockImages", value);
        }

        public static bool AutoInjectPolyfills
        {
            get => GetValue("AutoInjectPolyfills", true);
            set => SetValue("AutoInjectPolyfills", value);
        }

        public static bool DarkModeDefault
        {
            get => GetValue("DarkModeDefault", false);
            set => SetValue("DarkModeDefault", value);
        }

        public static string GetSearchUrl(string query)
        {
            string engine = SearchEngine;
            string encoded = Uri.EscapeDataString(query);

            switch (engine)
            {
                case "Bing":
                    return $"https://www.bing.com/search?q={encoded}";
                case "DuckDuckGo":
                    return $"https://duckduckgo.com/?q={encoded}";
                default:
                    return $"https://www.google.com/search?q={encoded}";
            }
        }

        private static T GetValue<T>(string key, T defaultValue)
        {
            try
            {
                if (Settings.Values.ContainsKey(key))
                {
                    return (T)Settings.Values[key];
                }
            }
            catch {}
            return defaultValue;
        }

        private static void SetValue<T>(string key, T value)
        {
            try
            {
                Settings.Values[key] = value;
            }
            catch {}
        }
    }
}
