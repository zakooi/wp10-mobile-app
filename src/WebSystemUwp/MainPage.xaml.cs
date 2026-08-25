using System;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
using WebSystemUwp.Services;

namespace WebSystemUwp
{
    public sealed partial class MainPage : Page
    {
        private const string DefaultHomeUrl = "https://www.google.com";

        public MainPage()
        {
            InitializeComponent();
            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Tải thông tin hệ thống ban đầu
            LoadSystemInformation();

            // Khởi động trình duyệt với trang chủ
            NavigateBrowser(DefaultHomeUrl);
        }

        // =========================================================================
        // TAB 1: TRÌNH DUYỆT TRỰC QUAN (IN-APP VISUAL WEB BROWSER)
        // =========================================================================

        private void NavigateBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return;

            url = url.Trim();
            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                // Nếu người dùng gõ từ khóa tìm kiếm (chứa dấu cách hoặc không có dấu chấm)
                if (url.Contains(" ") || !url.Contains("."))
                {
                    url = "https://www.google.com/search?q=" + Uri.EscapeDataString(url);
                }
                else
                {
                    url = "https://" + url;
                }
            }

            try
            {
                BrowserUrlBox.Text = url;
                BrowserWebView.Navigate(new Uri(url));
            }
            catch (Exception ex)
            {
                HeaderStatusText.Text = "Lỗi URL: " + ex.Message;
            }
        }

        private void BrowserGoButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateBrowser(BrowserUrlBox.Text);
        }

        private void BrowserUrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                NavigateBrowser(BrowserUrlBox.Text);
                e.Handled = true;
            }
        }

        private void BrowserBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (BrowserWebView.CanGoBack)
            {
                BrowserWebView.GoBack();
            }
        }

        private void BrowserForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (BrowserWebView.CanGoForward)
            {
                BrowserWebView.GoForward();
            }
        }

        private void BrowserRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserWebView.Refresh();
        }

        private void BrowserHomeButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateBrowser(DefaultHomeUrl);
        }

        private void QuickBookmark_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                NavigateBrowser(url);
            }
        }

        private void BrowserWebView_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            BrowserProgressBar.Visibility = Visibility.Visible;
            BrowserProgressBar.IsIndeterminate = true;
            HeaderStatusText.Text = "Đang kết nối...";

            if (args.Uri != null)
            {
                BrowserUrlBox.Text = args.Uri.ToString();
            }
        }

        private void BrowserWebView_ContentLoading(WebView sender, WebViewContentLoadingEventArgs args)
        {
            HeaderStatusText.Text = "Đang tải dữ liệu...";
        }

        private void BrowserWebView_DOMContentLoaded(WebView sender, WebViewDOMContentLoadedEventArgs args)
        {
            HeaderStatusText.Text = "Đang hiển thị...";
        }

        private void BrowserWebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            BrowserProgressBar.Visibility = Visibility.Collapsed;
            BrowserProgressBar.IsIndeterminate = false;

            BrowserBackButton.IsEnabled = BrowserWebView.CanGoBack;
            BrowserForwardButton.IsEnabled = BrowserWebView.CanGoForward;

            if (args.IsSuccess)
            {
                HeaderStatusText.Text = "Đã tải xong (" + (args.Uri?.Host ?? "Web") + ")";
                if (args.Uri != null)
                {
                    BrowserUrlBox.Text = args.Uri.ToString();
                }
            }
            else
            {
                HeaderStatusText.Text = "Không thể tải: " + args.WebErrorStatus.ToString();
            }
        }

        private void BrowserWebView_NewWindowRequested(WebView sender, WebViewNewWindowRequestedEventArgs args)
        {
            // Mở liên kết mới ngay trong chính WebView thay vì mở trình duyệt ngoài
            args.Handled = true;
            sender.Navigate(args.Uri);
        }

        // =========================================================================
        // TAB 2: CÔNG CỤ KIỂM TRA WEB API (HTTP / JSON INSPECTOR)
        // =========================================================================

        private async void ApiSendButton_Click(object sender, RoutedEventArgs e)
        {
            string url = ApiUrlBox.Text?.Trim();
            if (string.IsNullOrEmpty(url))
            {
                ApiStatusText.Text = "⚠️ Vui lòng nhập URL hợp lệ.";
                return;
            }

            if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                url = "https://" + url;
                ApiUrlBox.Text = url;
            }

            string method = "GET";
            if (ApiMethodCombo.SelectedItem is ComboBoxItem item && item.Content != null)
            {
                method = item.Content.ToString();
            }

            SetApiBusy(true);
            ApiStatusText.Text = "Đang gửi yêu cầu " + method + "...";
            ApiTimeText.Text = "";
            ApiResponseBox.Text = "";

            try
            {
                var result = await WebService.ExecuteRequestAsync(url, method);

                ApiTimeText.Text = $"{result.ElapsedMilliseconds} ms";

                if (result.IsSuccess)
                {
                    ApiStatusBadge.Background = new SolidColorBrush(Color.FromArgb(50, 0, 200, 80));
                    ApiStatusText.Text = $"✅ Status: {result.StatusCode} {result.StatusText} ({result.ContentLength:N0} bytes)";
                    ApiResponseBox.Text = result.Body;
                }
                else
                {
                    ApiStatusBadge.Background = new SolidColorBrush(Color.FromArgb(50, 220, 50, 50));
                    ApiStatusText.Text = result.StatusCode > 0
                        ? $"❌ HTTP {result.StatusCode} {result.StatusText}"
                        : "❌ Lỗi kết nối mạng";
                    ApiResponseBox.Text = result.ErrorMessage ?? "Không nhận được phản hồi.";
                }
            }
            catch (Exception ex)
            {
                ApiStatusBadge.Background = new SolidColorBrush(Color.FromArgb(50, 220, 50, 50));
                ApiStatusText.Text = "❌ Lỗi ngoại lệ";
                ApiResponseBox.Text = ex.ToString();
            }
            finally
            {
                SetApiBusy(false);
            }
        }

        private void ApiPreset_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                ApiUrlBox.Text = url;
                ApiSendButton_Click(sender, e);
            }
        }

        private void SetApiBusy(bool busy)
        {
            ApiSendButton.IsEnabled = !busy;
            ApiProgressRing.IsActive = busy;
            ApiProgressRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        }

        // =========================================================================
        // TAB 3: THÔNG TIN HỆ THỐNG (SYSTEM & BATTERY INFO)
        // =========================================================================

        private void LoadSystemInformation()
        {
            try
            {
                SystemDeviceInfoBox.Text = SystemInterop.GetDeviceInfo();
                SystemBatteryBox.Text = SystemInterop.GetBatteryAndNetworkInfo();
            }
            catch (Exception ex)
            {
                SystemDeviceInfoBox.Text = "Lỗi: " + ex.Message;
            }
        }

        private void RefreshSystemInfo_Click(object sender, RoutedEventArgs e)
        {
            LoadSystemInformation();
        }

        private async void CheckSecurityButton_Click(object sender, RoutedEventArgs e)
        {
            CheckSecurityButton.IsEnabled = false;
            SystemSecurityBox.Text = "Đang kiểm tra bảo mật Sandbox...";
            try
            {
                string res = await SystemInterop.TryCheckSystemAccessAsync();
                SystemSecurityBox.Text = res;
            }
            finally
            {
                CheckSecurityButton.IsEnabled = true;
            }
        }
    }
}
