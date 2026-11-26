using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Windows.Data.Json;
using Windows.Storage;

namespace WebSystemUwp.Services
{
    public class BookmarkItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string IconChar { get; set; }
        public string IconColor { get; set; }
        public DateTime CreatedAt { get; set; }
    }

    /// <summary>
    /// Quản lý danh sách Bookmark lưu dưới dạng JSON trong LocalFolder (không bị giới hạn 8 KB).
    /// </summary>
    public static class BookmarkService
    {
        private const string LegacyBookmarksKey = "UserBookmarks_v1";
        private const string BookmarksFileName = "bookmarks_v1.json";
        private static readonly string[] ColorPalette = { "#EA4335", "#4285F4", "#FBBC05", "#34A853", "#9C27B0", "#009688", "#FF5722", "#607D8B" };

        private static string FilePath => Path.Combine(ApplicationData.Current.LocalFolder.Path, BookmarksFileName);

        public static ObservableCollection<BookmarkItem> LoadBookmarks()
        {
            var collection = new ObservableCollection<BookmarkItem>();
            try
            {
                if (File.Exists(FilePath))
                {
                    string json = File.ReadAllText(FilePath);
                    if (JsonArray.TryParse(json, out var arr))
                    {
                        foreach (var val in arr)
                        {
                            if (val.ValueType != JsonValueType.Object) continue;
                            var obj = val.GetObject();
                            collection.Add(new BookmarkItem
                            {
                                Id = obj.GetNamedString("id", Guid.NewGuid().ToString()),
                                Title = obj.GetNamedString("title", ""),
                                Url = obj.GetNamedString("url", ""),
                                IconChar = obj.GetNamedString("icon", "W"),
                                IconColor = obj.GetNamedString("color", "#4285F4"),
                                CreatedAt = DateTime.UtcNow
                            });
                        }
                    }
                }
                else
                {
                    var localSettings = ApplicationData.Current.LocalSettings;
                    if (localSettings.Values.ContainsKey(LegacyBookmarksKey) &&
                        localSettings.Values[LegacyBookmarksKey] is string raw &&
                        !string.IsNullOrWhiteSpace(raw))
                    {
                        foreach (var line in raw.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries))
                        {
                            var parts = line.Split(new[] { "^^^" }, StringSplitOptions.None);
                            if (parts.Length >= 4)
                            {
                                collection.Add(new BookmarkItem
                                {
                                    Id = parts[0],
                                    Title = parts[1],
                                    Url = parts[2],
                                    IconChar = parts[3],
                                    IconColor = parts.Length > 4 ? parts[4] : "#4285F4",
                                    CreatedAt = DateTime.UtcNow
                                });
                            }
                        }
                        if (collection.Count > 0)
                        {
                            SaveBookmarks(collection);
                            localSettings.Values.Remove(LegacyBookmarksKey);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadBookmarks Error: " + ex.Message);
            }

            if (collection.Count == 0)
            {
                AddDefaultBookmarks(collection);
                SaveBookmarks(collection);
            }

            return collection;
        }

        public static void SaveBookmarks(IEnumerable<BookmarkItem> bookmarks)
        {
            try
            {
                var arr = new JsonArray();
                foreach (var b in bookmarks)
                {
                    var obj = new JsonObject();
                    obj.SetNamedValue("id", JsonValue.CreateStringValue(b.Id ?? string.Empty));
                    obj.SetNamedValue("title", JsonValue.CreateStringValue(b.Title ?? string.Empty));
                    obj.SetNamedValue("url", JsonValue.CreateStringValue(b.Url ?? string.Empty));
                    obj.SetNamedValue("icon", JsonValue.CreateStringValue(b.IconChar ?? "W"));
                    obj.SetNamedValue("color", JsonValue.CreateStringValue(b.IconColor ?? "#4285F4"));
                    arr.Add(obj);
                }
                File.WriteAllText(FilePath, arr.Stringify());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SaveBookmarks Error: " + ex.Message);
            }
        }

        public static BookmarkItem CreateBookmark(string title, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return null;

            string displayTitle = string.IsNullOrWhiteSpace(title) ? GetDomainFromUrl(url) : title;
            string iconChar = displayTitle.Trim().Substring(0, 1).ToUpper();

            int colorIndex = Math.Abs(url.GetHashCode()) % ColorPalette.Length;
            string color = ColorPalette[colorIndex];

            return new BookmarkItem
            {
                Id = Guid.NewGuid().ToString(),
                Title = displayTitle,
                Url = url,
                IconChar = iconChar,
                IconColor = color,
                CreatedAt = DateTime.UtcNow
            };
        }

        public static bool IsBookmarked(IEnumerable<BookmarkItem> bookmarks, string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return false;
            string clean = url.Trim().ToLowerInvariant().TrimEnd('/');
            return bookmarks.Any(b => b.Url.Trim().ToLowerInvariant().TrimEnd('/') == clean);
        }

        private static void AddDefaultBookmarks(ObservableCollection<BookmarkItem> list)
        {
            list.Add(CreateBookmark("Google", "https://www.google.com"));
            list.Add(CreateBookmark("YouTube", "https://m.youtube.com"));
            list.Add(CreateBookmark("Wikipedia", "https://vi.m.wikipedia.org"));
            list.Add(CreateBookmark("VnExpress", "https://vnexpress.net"));
            list.Add(CreateBookmark("GitHub", "https://github.com"));
        }

        private static string GetDomainFromUrl(string url)
        {
            try
            {
                var uri = new Uri(url);
                return uri.Host.Replace("www.", "");
            }
            catch
            {
                return url;
            }
        }
    }
}
