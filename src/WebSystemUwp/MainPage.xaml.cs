using System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using WebSystemUwp.Services;

namespace WebSystemUwp
{
    public sealed partial class MainPage : Page
    {
        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Đọc thông tin thiết bị (hợp lệ, không cần quyền cao).
            LoadDeviceInfoButton_Click(sender, e);
        }

        // ===================== WEB =====================

        private async void FetchButton_Click(object sender, RoutedEventArgs e)
        {
            string url = UrlTextBox.Text == null ? string.Empty : UrlTextBox.Text.Trim();
            if (string.IsNullOrEmpty(url))
            {
                WebStatusText.Text = "Vui lòng nhập URL.";
                return;
            }

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                && !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
            }

            SetWebBusy(true);
            try
            {
                WebStatusText.Text = "Đang tải...";
                string body = await WebService.FetchAsync(url);
                WebResultBox.Text = body;
                WebStatusText.Text = "Thành công (" + body.Length + " ký tự).";
            }
            catch (Exception ex)
            {
                WebResultBox.Text = string.Empty;
                WebStatusText.Text = "Lỗi: " + ex.Message;
            }
            finally
            {
                SetWebBusy(false);
            }
        }

        private void SetWebBusy(bool busy)
        {
            FetchButton.IsEnabled = !busy;
            FetchProgress.IsActive = busy;
            FetchProgress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        // ===================== HỆ THỐNG =====================

        private void LoadDeviceInfoButton_Click(object sender, RoutedEventArgs e)
        {
            DeviceInfoBox.Text = SystemInterop.GetDeviceInfo();
        }

        private async void SystemAccessButton_Click(object sender, RoutedEventArgs e)
        {
            string result = await SystemInterop.TryCheckSystemAccessAsync();
            RegistryResultBox.Text = result;
            SystemStatusText.Text = result.Contains("mở được")
                ? "Thiết bị cho phép truy cập hệ thống."
                : "Không thể truy cập hệ thống (giới hạn AppContainer).";
        }
    }
}
