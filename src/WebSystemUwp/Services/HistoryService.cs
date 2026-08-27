using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.Storage;

namespace WebSystemUwp.Services
{
    public class HistoryItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public DateTime VisitedAt { get; set; }
        public string FormattedTime => VisitedAt.ToLocalTime().ToString("HH:mm - dd/MM");
    }

    /// <summary>
    /// Quản lý lịch sử duyệt web lưu trong LocalSettings.
    /// </summary>
    public static class HistoryService
    {
        private const string HistoryKey = "UserHistory_v1";
        private const int MaxHistoryCount = 300;

        public static ObservableCollection<HistoryItem> LoadHistory()
        {
            var collection = new ObservableCollection<HistoryItem>();
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.ContainsKey(HistoryKey))
                {
                    string raw = localSettings.Values[HistoryKey] as string;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var lines = raw.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            var parts = line.Split(new[] { "^^^" }, StringSplitOptions.None);
                            if (parts.Length >= 4)
                            {
                                if (long.TryParse(parts[3], out long ticks))
                                {
                                    collection.Add(new HistoryItem
                                    {
                                        Id = parts[0],
                                        Title = parts[1],
                                        Url = parts[2],
                                        VisitedAt = new DateTime(ticks, DateTimeKind.Utc)
                                    });
                                }
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadHistory Error: " + ex.Message);
            }
            return collection;
        }

        public static void SaveHistory(IEnumerable<HistoryItem> items)
        {
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                var rawList = items.Take(MaxHistoryCount).Select(h => $"{h.Id}^^^{h.Title}^^^{h.Url}^^^{h.VisitedAt.Ticks}");
                localSettings.Values[HistoryKey] = string.Join("|||", rawList);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SaveHistory Error: " + ex.Message);
            }
        }

        public static void RecordVisit(ObservableCollection<HistoryItem> history, string title, string url)
        {
            if (string.IsNullOrWhiteSpace(url) || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return;

            string displayTitle = string.IsNullOrWhiteSpace(title) ? url : title;

            // Xóa mục trùng gần nhất nếu cùng URL
            var existing = history.FirstOrDefault(h => h.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                history.Remove(existing);
            }

            var newItem = new HistoryItem
            {
                Id = Guid.NewGuid().ToString(),
                Title = displayTitle,
                Url = url,
                VisitedAt = DateTime.UtcNow
            };

            history.Insert(0, newItem);

            while (history.Count > MaxHistoryCount)
            {
                history.RemoveAt(history.Count - 1);
            }

            SaveHistory(history);
        }

        public static void ClearAll(ObservableCollection<HistoryItem> history)
        {
            history.Clear();
            SaveHistory(history);
        }
    }
}
