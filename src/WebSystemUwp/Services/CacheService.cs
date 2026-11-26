using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Windows.Data.Json;
using Windows.Storage;

namespace WebSystemUwp.Services
{
    public class OfflinePageItem
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
        public string FileName { get; set; }
        public long SizeBytes { get; set; }
        public DateTime SavedAt { get; set; }
        public string FormattedDate => SavedAt.ToLocalTime().ToString("HH:mm - dd/MM/yyyy");
        public string FormattedSize => $"{SizeBytes / 1024.0:F1} KB";
    }

    /// <summary>
    /// Quản lý bộ nhớ đệm và lưu trữ các trang web ngoại tuyến (JSON index trong LocalFolder).
    /// </summary>
    public static class CacheService
    {
        private const string OfflineFolder = "OfflinePages";
        private const string LegacyOfflineIndexKey = "OfflinePages_Index_v1";
        private const string OfflineIndexFileName = "offline_index_v1.json";

        private static string IndexFilePath => Path.Combine(ApplicationData.Current.LocalFolder.Path, OfflineIndexFileName);

        public static Task<ObservableCollection<OfflinePageItem>> LoadOfflinePagesAsync()
        {
            var collection = new ObservableCollection<OfflinePageItem>();
            try
            {
                if (File.Exists(IndexFilePath))
                {
                    string json = File.ReadAllText(IndexFilePath);
                    if (JsonArray.TryParse(json, out var arr))
                    {
                        foreach (var val in arr)
                        {
                            if (val.ValueType != JsonValueType.Object) continue;
                            var obj = val.GetObject();
                            string id = obj.GetNamedString("id", "");
                            if (string.IsNullOrEmpty(id)) continue;
                            long.TryParse(obj.GetNamedString("size", "0"), out long size);
                            long.TryParse(obj.GetNamedString("ticks", "0"), out long ticks);
                            collection.Add(new OfflinePageItem
                            {
                                Id = id,
                                Title = obj.GetNamedString("title", ""),
                                Url = obj.GetNamedString("url", ""),
                                FileName = id + ".html",
                                SizeBytes = size,
                                SavedAt = new DateTime(ticks > 0 ? ticks : DateTime.UtcNow.Ticks, DateTimeKind.Utc)
                            });
                        }
                        return Task.FromResult(collection);
                    }
                }

                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.ContainsKey(LegacyOfflineIndexKey) &&
                    localSettings.Values[LegacyOfflineIndexKey] is string raw &&
                    !string.IsNullOrWhiteSpace(raw))
                {
                    foreach (var line in raw.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var parts = line.Split(new[] { "^^^" }, StringSplitOptions.None);
                        if (parts.Length >= 5 &&
                            long.TryParse(parts[3], out long size) &&
                            long.TryParse(parts[4], out long ticks))
                        {
                            collection.Add(new OfflinePageItem
                            {
                                Id = parts[0],
                                Title = parts[1],
                                Url = parts[2],
                                FileName = parts[0] + ".html",
                                SizeBytes = size,
                                SavedAt = new DateTime(ticks, DateTimeKind.Utc)
                            });
                        }
                    }
                    if (collection.Count > 0)
                    {
                        SaveOfflineIndex(collection);
                        localSettings.Values.Remove(LegacyOfflineIndexKey);
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadOfflinePages Error: " + ex.Message);
            }
            return Task.FromResult(collection);
        }

        private static void SaveOfflineIndex(IEnumerable<OfflinePageItem> list)
        {
            try
            {
                var arr = new JsonArray();
                foreach (var p in list)
                {
                    var obj = new JsonObject();
                    obj.SetNamedValue("id", JsonValue.CreateStringValue(p.Id ?? string.Empty));
                    obj.SetNamedValue("title", JsonValue.CreateStringValue(p.Title ?? string.Empty));
                    obj.SetNamedValue("url", JsonValue.CreateStringValue(p.Url ?? string.Empty));
                    obj.SetNamedValue("size", JsonValue.CreateStringValue(p.SizeBytes.ToString()));
                    obj.SetNamedValue("ticks", JsonValue.CreateStringValue(p.SavedAt.Ticks.ToString()));
                    arr.Add(obj);
                }
                File.WriteAllText(IndexFilePath, arr.Stringify());
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SaveOfflineIndex Error: " + ex.Message);
            }
        }

        public static async Task<OfflinePageItem> SavePageOfflineAsync(string title, string url, string htmlContent)
        {
            if (string.IsNullOrWhiteSpace(htmlContent)) return null;

            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var dir = await localFolder.CreateFolderAsync(OfflineFolder, CreationCollisionOption.OpenIfExists);

                string id = Guid.NewGuid().ToString("N");
                string fileName = id + ".html";

                var file = await dir.CreateFileAsync(fileName, CreationCollisionOption.ReplaceExisting);
                await FileIO.WriteTextAsync(file, htmlContent);

                var props = await file.GetBasicPropertiesAsync();

                var item = new OfflinePageItem
                {
                    Id = id,
                    Title = string.IsNullOrWhiteSpace(title) ? url : title,
                    Url = url,
                    FileName = fileName,
                    SizeBytes = (long)props.Size,
                    SavedAt = DateTime.UtcNow
                };

                var existingPages = await LoadOfflinePagesAsync();
                existingPages.Insert(0, item);
                SaveOfflineIndex(existingPages);

                return item;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("SavePageOfflineAsync Error: " + ex.Message);
                return null;
            }
        }

        public static async Task<string> GetOfflinePageHtmlAsync(string id)
        {
            try
            {
                var localFolder = ApplicationData.Current.LocalFolder;
                var dir = await localFolder.GetFolderAsync(OfflineFolder);
                var file = await dir.GetFileAsync(id + ".html");
                return await FileIO.ReadTextAsync(file);
            }
            catch
            {
                return null;
            }
        }

        public static async Task<bool> DeleteOfflinePageAsync(string id)
        {
            try
            {
                var pages = await LoadOfflinePagesAsync();
                var match = pages.FirstOrDefault(p => p.Id == id);
                if (match != null)
                {
                    pages.Remove(match);
                    SaveOfflineIndex(pages);

                    var localFolder = ApplicationData.Current.LocalFolder;
                    var dir = await localFolder.GetFolderAsync(OfflineFolder);
                    var file = await dir.GetFileAsync(id + ".html");
                    await file.DeleteAsync();
                }
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
