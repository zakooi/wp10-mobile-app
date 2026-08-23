using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Windows.ApplicationModel;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System.Profile;

namespace WebSystemUwp.Services
{
    /// <summary>
    /// Thông tin thiết bị + kiểm tra quyền truy cập hệ thống.
    ///
    /// QUAN TRỌNG:
    /// - Windows 10 Mobile/UWP chạy trong <b>AppContainer</b> sandbox. API WinRT KHÔNG cho
    ///   đọc/ghi registry hệ thống (HKLM). Muốn FullTrust cần desktop bridge <c>runFullTrust</c>,
    ///   mà <b>Windows 10 Mobile không hỗ trợ</b>. Vì vậy phần "can thiệp hệ thống" về bản chất
    ///   <b>không khả thi trên phone</b>.
    /// - <see cref="TryCheckSystemAccessAsync"/> chỉ <b>kiểm tra và báo cáo</b> giới hạn này
    ///   một cách trung thực, không ghi đè gì lên hệ thống.
    /// </summary>
    public static class SystemInterop
    {
        // ===================== P/Invoke (chỉ để KIỂM TRA, không ghi) =====================

        private const int HKEY_LOCAL_MACHINE = unchecked((int)0x80000002);
        private const int KEY_QUERY_VALUE = 0x0001;
        private const int KEY_READ = KEY_QUERY_VALUE | 0x0004 | 0x0008 | 0x0010;

        [DllImport("advapi32.dll", EntryPoint = "RegOpenKeyExW", SetLastError = true)]
        private static extern int RegOpenKeyExW(IntPtr hKey, string lpSubKey, int ulOptions, int samDesired, out IntPtr phkResult);

        [DllImport("advapi32.dll", EntryPoint = "RegQueryValueExW", SetLastError = true)]
        private static extern int RegQueryValueExW(IntPtr hKey, string lpValueName, IntPtr lpReserved, out int lpType, byte[] lpData, ref int lpcbData);

        [DllImport("advapi32.dll", EntryPoint = "RegCloseKey", SetLastError = true)]
        private static extern int RegCloseKey(IntPtr hKey);

        private const int ERROR_SUCCESS = 0;

        // ===================== API công khai =====================

        /// <summary>Thông tin thiết bị — dùng API hợp lệ, không cần quyền cao.</summary>
        public static string GetDeviceInfo()
        {
            var device = new EasClientDeviceInformation();
            var sb = new StringBuilder();

            sb.AppendLine("Tên thiết bị: " + device.FriendlyName);
            sb.AppendLine("Nhà sản xuất: " + device.SystemManufacturer);
            sb.AppendLine("Firmware: " + device.SystemFirmwareVersion);
            sb.AppendLine("Hardware: " + device.SystemHardwareVersion);
            sb.AppendLine("SKU: " + device.SystemSku);

            var version = AnalyticsInfo.VersionInfo;
            sb.AppendLine("DeviceFamily: " + version.DeviceFamily);
            sb.AppendLine("OSVersion: " + FormatVersion(version.DeviceFamilyVersion));

            var pkg = Package.Current;
            sb.AppendLine("Package: " + pkg.Id.Name);
            sb.AppendLine("Version: " + pkg.Id.Version.Major + "." + pkg.Id.Version.Minor + "." + pkg.Id.Version.Build + "." + pkg.Id.Version.Revision);

            return sb.ToString();
        }

        /// <summary>
        /// Thử mở một key hệ thống để xác định quyền AppContainer. KHÔNG ghi gì.
        /// Trả về mô tả trung thực về việc app có thể chạm registry hay không.
        /// </summary>
        public static async Task<string> TryCheckSystemAccessAsync()
        {
            // Chạy trên Task.Run để không chặn UI.
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
                        return "OK: App này mở được registry hệ thống (hiếm — thường cần FullTrust/desktop bridge).";
                    }

                    return "BỊ CHẶN: RegOpenKeyExW trả về code " + result +
                           " (thường là 5 = Access denied). App UWP chạy trong AppContainer " +
                           "không được đọc/ghi registry hệ thống. Muốn FullTrust cần desktop bridge " +
                           "runFullTrust, nhưng Windows 10 Mobile KHÔNG hỗ trợ.";
                }
                catch (Exception ex)
                {
                    return "KHÔNG THỂ: Ngoại lệ khi gọi API registry — " + ex.GetType().Name +
                           ". AppContainer trên Windows 10 Mobile chặn truy cập hệ thống.";
                }
            });
        }

        private static string FormatVersion(string versionNumber)
        {
            // Định dạng <major>.<minor>.<build>... từ chuỗi số nguyên 64-bit của DeviceFamilyVersion.
            if (ulong.TryParse(versionNumber, out ulong v) && v != 0)
            {
                return (v >> 48) + "." + ((v >> 32) & 0xFFFF) + "." + ((v >> 16) & 0xFFFF) + "." + (v & 0xFFFF);
            }
            return versionNumber;
        }
    }
}
