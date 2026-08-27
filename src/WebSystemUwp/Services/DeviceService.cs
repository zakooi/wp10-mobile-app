using System;
using Windows.ApplicationModel;
using Windows.Graphics.Display;
using Windows.Security.ExchangeActiveSyncProvisioning;
using Windows.System.Profile;

namespace WebSystemUwp.Services
{
    /// <summary>
    /// Thông tin phần cứng, SKU và đo đạc hiển thị chuẩn cho Lumia 920 & Windows 10 Mobile.
    /// </summary>
    public static class DeviceService
    {
        public static string FriendlyName { get; private set; }
        public static string Manufacturer { get; private set; }
        public static string FirmwareVersion { get; private set; }
        public static string HardwareVersion { get; private set; }
        public static string Sku { get; private set; }
        public static string OsVersion { get; private set; }
        public static string AppVersion { get; private set; }

        static DeviceService()
        {
            try
            {
                var info = new EasClientDeviceInformation();
                FriendlyName = info.FriendlyName;
                Manufacturer = info.SystemManufacturer;
                FirmwareVersion = info.SystemFirmwareVersion;
                HardwareVersion = info.SystemHardwareVersion;
                Sku = info.SystemSku;

                var ver = AnalyticsInfo.VersionInfo;
                OsVersion = FormatVersion(ver.DeviceFamilyVersion);

                var pkg = Package.Current.Id.Version;
                AppVersion = $"{pkg.Major}.{pkg.Minor}.{pkg.Build}.{pkg.Revision}";
            }
            catch (Exception ex)
            {
                FriendlyName = "Windows 10 Mobile Device";
                Manufacturer = "Microsoft/Nokia";
                OsVersion = "10.0.14393.0";
                AppVersion = "3.1.0.0";
                System.Diagnostics.Debug.WriteLine("DeviceService Init Error: " + ex.Message);
            }
        }

        public static string GetDeviceSummary()
        {
            return $"📱 {FriendlyName} ({Manufacturer})\n" +
                   $"🔧 Firmware: {FirmwareVersion}\n" +
                   $"⚙️ Hardware: {HardwareVersion}\n" +
                   $"🏷️ SKU: {Sku}\n" +
                   $"🖥️ Windows 10 Mobile: {OsVersion}\n" +
                   $"🚀 WebSystem Chrome: v{AppVersion}";
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
