using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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
    /// Quản lý danh sách Bookmark (Dấu trang yêu thích) lưu bền vững trong LocalSettings.
    /// </summary>
    public static class BookmarkService
    {
        private const string BookmarksKey = "UserBookmarks_v1";
        private static readonly string[] ColorPalette = { "#EA4335", "#4285F4", "#FBBC05", "#34A853", "#9C27B0", "#009688", "#FF5722", "#607D8B" };

        public static ObservableCollection<BookmarkItem> LoadBookmarks()
        {
            var collection = new ObservableCollection<BookmarkItem>();
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.ContainsKey(BookmarksKey))
                {
                    string raw = localSettings.Values[BookmarksKey] as string;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var lines = raw.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
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
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadBookmarks Error: " + ex.Message);
            }

            // Nếu rỗng, nạp danh sách gợi ý ban đầu
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
                var localSettings = ApplicationData.Current.LocalSettings;
                var rawList = bookmarks.Select(b => $"{b.Id}^^^{b.Title}^^^{b.Url}^^^{b.IconChar}^^^{b.IconColor}");
                localSettings.Values[BookmarksKey] = string.Join("|||", rawList);
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
