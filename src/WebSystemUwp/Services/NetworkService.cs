using System;
using System.Threading.Tasks;
using Windows.Networking.Connectivity;

namespace WebSystemUwp.Services
{
    public enum NetworkType
    {
        None,
        WiFi,
        Cellular,
        Ethernet,
        Unknown
    }

    /// <summary>
    /// Giám sát và chẩn đoán kết nối mạng thời gian thực trên Windows 10 Mobile.
    /// </summary>
    public static class NetworkService
    {
        public static event Action<bool, NetworkType> NetworkStatusChanged;

        static NetworkService()
        {
            try
            {
                NetworkInformation.NetworkStatusChanged += NetworkInformation_NetworkStatusChanged;
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("NetworkService Init Error: " + ex.Message);
            }
        }

        private static void NetworkInformation_NetworkStatusChanged(object sender)
        {
            var isConnected = IsInternetAvailable();
            var type = GetCurrentNetworkType();
            NetworkStatusChanged?.Invoke(isConnected, type);
        }

        public static bool IsInternetAvailable()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) return false;

                var level = profile.GetNetworkConnectivityLevel();
                return level == NetworkConnectivityLevel.InternetAccess;
            }
            catch
            {
                return false;
            }
        }

        public static NetworkType GetCurrentNetworkType()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) return NetworkType.None;

                if (profile.IsWlanConnectionProfile) return NetworkType.WiFi;
                if (profile.IsWwanConnectionProfile) return NetworkType.Cellular;

                var level = profile.GetNetworkConnectivityLevel();
                if (level == NetworkConnectivityLevel.None) return NetworkType.None;

                return NetworkType.Unknown;
            }
            catch
            {
                return NetworkType.Unknown;
            }
        }

        public static string GetNetworkSummary()
        {
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile == null) return "❌ Ngoại tuyến (Không có kết nối mạng)";

                string typeStr = profile.IsWlanConnectionProfile ? "Wi-Fi" :
                                 profile.IsWwanConnectionProfile ? "Dữ liệu di động (Cellular)" : "Khác";

                var level = profile.GetNetworkConnectivityLevel();
                string levelStr = level == NetworkConnectivityLevel.InternetAccess ? "Internet hoàn chỉnh" : level.ToString();

                return $"🌐 {typeStr} ({profile.ProfileName}) — {levelStr}";
            }
            catch (Exception ex)
            {
                return "🌐 Lỗi kiểm tra mạng: " + ex.Message;
            }
        }

        public static async Task<long> TestPingAsync(string target = "https://www.google.com")
        {
            return await WebService.CheckConnectionAsync(target);
        }
    }
}
