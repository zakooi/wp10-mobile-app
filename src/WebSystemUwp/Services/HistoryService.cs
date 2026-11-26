using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Windows.Data.Json;
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
    /// Quản lý lịch sử duyệt web lưu dưới dạng JSON trong LocalFolder (không bị giới hạn 8 KB).
    /// </summary>
    public static class HistoryService
    {
        private const string LegacyHistoryKey = "UserHistory_v1";
        private const string HistoryFileName = "history_v1.json";
        private const int MaxHistoryCount = 300;

        private static string FilePath => Path.Combine(ApplicationData.Current.LocalFolder.Path, HistoryFileName);

        public static ObservableCollection<HistoryItem> LoadHistory()
        {
            var collection = new ObservableCollection<HistoryItem>();
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
                            long.TryParse(obj.GetNamedString("ticks", "0"), out long ticks);
                            collection.Add(new HistoryItem
                            {
                                Id = obj.GetNamedString("id", Guid.NewGuid().ToString()),
                                Title = obj.GetNamedString("title", ""),
                                Url = obj.GetNamedString("url", ""),
                                VisitedAt = new DateTime(ticks > 0 ? ticks : DateTime.UtcNow.Ticks, DateTimeKind.Utc)
                            });
                        }
                        return collection;
                    }
                }

                // Migrate legacy LocalSettings format once if present
                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.ContainsKey(LegacyHistoryKey) &&
                    localSettings.Values[LegacyHistoryKey] is string raw &&
                    !string.IsNullOrWhiteSpace(raw))
                {
                    foreach (var line in raw.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split(new[] { "^^^" }, StringSplitOptions.None);
                        if (parts.Length >= 4 && long.TryParse(parts[3], out long ticks))
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
                    if (collection.Count > 0)
                    {
                        SaveHistory(collection);
                        localSettings.Values.Remove(LegacyHistoryKey);
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
                var arr = new JsonArray();
                foreach (var h in items.Take(MaxHistoryCount))
                {
                    var obj = new JsonObject();
                    obj.SetNamedValue("id", JsonValue.CreateStringValue(h.Id ?? string.Empty));
                    obj.SetNamedValue("title", JsonValue.CreateStringValue(h.Title ?? string.Empty));
                    obj.SetNamedValue("url", JsonValue.CreateStringValue(h.Url ?? string.Empty));
                    obj.SetNamedValue("ticks", JsonValue.CreateStringValue(h.VisitedAt.Ticks.ToString()));
                    arr.Add(obj);
                }
                File.WriteAllText(FilePath, arr.Stringify());
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

            var existing = history.FirstOrDefault(h => h.Url.Equals(url, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                history.Remove(existing);
            }

            history.Insert(0, new HistoryItem
            {
                Id = Guid.NewGuid().ToString(),
                Title = displayTitle,
                Url = url,
                VisitedAt = DateTime.UtcNow
            });

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
