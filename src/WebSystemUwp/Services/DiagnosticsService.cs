using System;
using System.Text;
using Windows.Devices.Power;
using Windows.System;
using Windows.System.Power;

namespace WebSystemUwp.Services
{
    public class MemoryReport
    {
        public double AppUsageMb { get; set; }
        public double AppLimitMb { get; set; }
        public double UsagePercentage { get; set; }
        public AppMemoryUsageLevel UsageLevel { get; set; }
    }

    public class BatteryStatusReport
    {
        public BatteryStatus Status { get; set; }
        public double ChargePercentage { get; set; }
        public int RemainingMwh { get; set; }
        public int FullChargeMwh { get; set; }
    }

    /// <summary>
    /// Giám sát mức sử dụng tài nguyên (RAM, Pin, Hiệu năng) thời gian thực trên Lumia.
    /// </summary>
    public static class DiagnosticsService
    {
        public static MemoryReport GetMemoryReport()
        {
            var report = new MemoryReport();
            try
            {
                ulong usageBytes = MemoryManager.AppMemoryUsage;
                ulong limitBytes = MemoryManager.AppMemoryUsageLimit;

                report.AppUsageMb = usageBytes / (1024.0 * 1024.0);
                report.AppLimitMb = limitBytes / (1024.0 * 1024.0);
                report.UsageLevel = MemoryManager.AppMemoryUsageLevel;

                if (limitBytes > 0)
                {
                    report.UsagePercentage = (double)usageBytes / limitBytes * 100.0;
                }
            }
            catch
            {
                report.AppUsageMb = 0;
                report.AppLimitMb = 512;
            }
            return report;
        }

        public static BatteryStatusReport GetBatteryReport()
        {
            var report = new BatteryStatusReport();
            try
            {
                var batt = Battery.AggregateBattery.GetReport();
                if (batt != null)
                {
                    report.Status = batt.Status;
                    report.RemainingMwh = batt.RemainingCapacityInMilliwattHours ?? 0;
                    report.FullChargeMwh = batt.FullChargeCapacityInMilliwattHours ?? 0;

                    if (report.FullChargeMwh > 0)
                    {
                        report.ChargePercentage = (double)report.RemainingMwh / report.FullChargeMwh * 100.0;
                    }
                }
            }
            catch {}
            return report;
        }

        public static string GetFullDiagnosticsSummary()
        {
            var sb = new StringBuilder();

            // RAM
            var mem = GetMemoryReport();
            sb.AppendLine($"📊 Bộ nhớ RAM: {mem.AppUsageMb:F1} MB / {mem.AppLimitMb:F1} MB ({mem.UsagePercentage:F0}%)");
            sb.AppendLine($"📈 Mức độ tải RAM: {mem.UsageLevel}");

            // PIN
            var batt = GetBatteryReport();
            sb.AppendLine($"⚡ Trạng thái sạc: {batt.Status}");
            sb.AppendLine($"🔋 Pin: {batt.ChargePercentage:F0}% ({batt.RemainingMwh} / {batt.FullChargeMwh} mWh)");

            // MẠNG
            sb.AppendLine(NetworkService.GetNetworkSummary());

            return sb.ToString();
        }
    }
}
