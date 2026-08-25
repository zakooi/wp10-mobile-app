using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Devices.Power;
using Windows.Networking.Connectivity;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System.Power;
using Windows.System.Profile;

namespace WebSystemUwp.Services
{
    /// <summary>
    /// Thông tin thiết bị, mạng, pin và kiểm tra bảo mật hệ thống.
    /// </summary>
    public static class SystemInterop
    {
        private const int HKEY_LOCAL_MACHINE = unchecked((int)0x80000002);
        private const int KEY_QUERY_VALUE = 0x0001;
        private const int KEY_READ = KEY_QUERY_VALUE | 0x0004 | 0x0008 | 0x0010;

        [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", SetLastError = true)]
        private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", EntryPoint = "RegCloseKey", SetLastError = true)]
        private static extern int RegCloseKey(IntPtr hKey);

        private const int ERROR_SUCCESS = 0;

        /// <summary>
        /// Lấy toàn bộ thông tin phần cứng & hệ thống.
        /// </summary>
        public static string GetDeviceInfo()
        {
            var device = new EasClientDeviceInformation();
            var sb = new StringBuilder();

            sb.AppendLine("📱 Thiết bị: " + device.FriendlyName);
            sb.AppendLine("🏭 Nhà sản xuất: " + device.SystemManufacturer);
            sb.AppendLine("🔧 Firmware: " + device.SystemFirmwareVersion);
            sb.AppendLine("⚙️ Hardware: " + device.SystemHardwareVersion);
            sb.AppendLine("🏷️ SKU: " + device.SystemSku);

            var version = AnalyticsInfo.VersionInfo;
            sb.AppendLine("📦 DeviceFamily: " + version.DeviceFamily);
            sb.AppendLine("🖥️ Phiên bản OS: " + FormatVersion(version.DeviceFamilyVersion));

            var pkg = Package.Current;
            sb.AppendLine("📦 Package: " + pkg.Id.Name);
            sb.AppendLine("🚀 Phiên bản App: " + pkg.Id.Version.Major + "." + pkg.Id.Version.Minor + "." + pkg.Id.Version.Build + "." + pkg.Id.Version.Revision);

            return sb.ToString();
        }

        /// <summary>
        /// Lấy thông tin trạng thái Pin và Kết nối mạng.
        /// </summary>
        public static string GetBatteryAndNetworkInfo()
        {
            var sb = new StringBuilder();

            // 1. Trạng thái mạng
            try
            {
                var profile = NetworkInformation.GetInternetConnectionProfile();
                if (profile != null)
                {
                    var level = profile.GetNetworkConnectivityLevel();
                    sb.AppendLine("🌐 Trạng thái mạng: " + level.ToString());
                    sb.AppendLine("📶 Tên mạng: " + profile.ProfileName);
                    if (profile.IsWlanConnectionProfile)
                        sb.AppendLine("📡 Loại kết nối: Wi-Fi");
                    else if (profile.IsWwanConnectionProfile)
                        sb.AppendLine("📡 Loại kết nối: Dữ liệu di động (Cellular)");
                }
                else
                {
                    sb.AppendLine("🌐 Trạng thái mạng: Không có kết nối Internet");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("🌐 Mạng: " + ex.Message);
            }

            // 2. Trạng thái Pin
            try
            {
                var report = Battery.AggregateBattery.GetReport();
                if (report != null)
                {
                    sb.AppendLine("⚡ Trạng thái sạc: " + report.Status.ToString());
                    if (report.FullChargeCapacityInMilliwattHours.HasValue && report.RemainingCapacityInMilliwattHours.HasValue && report.FullChargeCapacityInMilliwattHours.Value > 0)
                    {
                        double percent = (double)report.RemainingCapacityInMilliwattHours.Value / report.FullChargeCapacityInMilliwattHours.Value * 100;
                        sb.AppendLine($"🔋 Dung lượng Pin: {percent:F0}% ({report.RemainingCapacityInMilliwattHours} / {report.FullChargeCapacityInMilliwattHours} mWh)");
                    }
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("🔋 Pin: " + ex.Message);
            }

            return sb.ToString();
        }

        /// <summary>
        /// Thử mở một key hệ thống để xác định quyền AppContainer sandbox.
        /// </summary>
        public static async Task<string> TryCheckSystemAccessAsync()
        {
            return await Task.Run(() =>
            {
                try
                {
                    IntPtr hKey;
                    int result = RegOpenKeyExW(new IntPtr(HKEY_LOCAL_MACHINE),
                        @"SOFTWARE\Microsoft\Windows\CurrentVersion", 0, KEY_READ, out hKey);
                    if (result == ERROR_SUCCESS)
                    {
                        RegCloseKey(hKey);
                        return "✅ ĐƯỢC PHÉP: Ứng dụng mở được registry hệ thống.";
                    }

                    return "🛡️ AN TOÀN (BỊ CHẶN): RegOpenKeyExW trả về mã lỗi " + result +
                           " (Access Denied). Ứng dụng đang chạy an toàn trong AppContainer Sandbox của Windows 10 Mobile.";
                }
                catch (Exception ex)
                {
                    return "🛡️ BẢO MẬT: Ngoại lệ sandbox — " + ex.GetType().Name +
                           ". Windows 10 Mobile bảo vệ hệ thống tuyệt đối.";
                }
            });
        }

        private static string FormatVersion(string versionNumber)
        {
            if (ulong.TryParse(versionNumber, out ulong v) && v != 0)
            {
                return (v >> 48) + "." + ((v >> 32) & 0xFFFF) + "." + ((v >> 16) & 0xFFFF) + "." + (v & 0xFFFF);
            }
            return versionNumber;
        }
    }
}
