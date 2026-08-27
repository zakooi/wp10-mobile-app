using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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
    /// Quản lý bộ nhớ đệm HTTP và lưu trữ các trang web ngoại tuyến (Offline Pages) để đọc khi mất mạng.
    /// </summary>
    public static class CacheService
    {
        private const string OfflineFolder = "OfflinePages";
        private const string OfflineIndexKey = "OfflinePages_Index_v1";

        public static async Task<ObservableCollection<OfflinePageItem>> LoadOfflinePagesAsync()
        {
            var collection = new ObservableCollection<OfflinePageItem>();
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                if (localSettings.Values.ContainsKey(OfflineIndexKey))
                {
                    string raw = localSettings.Values[OfflineIndexKey] as string;
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        var lines = raw.Split(new[] { "|||" }, StringSplitOptions.RemoveEmptyEntries);
                        foreach (var line in lines)
                        {
                            var parts = line.Split(new[] { "^^^" }, StringSplitOptions.None);
                            if (parts.Length >= 5)
                            {
                                if (long.TryParse(parts[3], out long size) && long.TryParse(parts[4], out long ticks))
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
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("LoadOfflinePages Error: " + ex.Message);
            }
            return collection;
        }

        private static void SaveOfflineIndex(IEnumerable<OfflinePageItem> list)
        {
            try
            {
                var localSettings = ApplicationData.Current.LocalSettings;
                var rawList = list.Select(p => $"{p.Id}^^^{p.Title}^^^{p.Url}^^^{p.SizeBytes}^^^{p.SavedAt.Ticks}");
                localSettings.Values[OfflineIndexKey] = string.Join("|||", rawList);
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
