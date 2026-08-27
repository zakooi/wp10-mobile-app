using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.System;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using WebSystemUwp.Services;

namespace WebSystemUwp
{
    public class BrowserTab
    {
        public string Id { get; set; }
        public string Title { get; set; }
        public string Url { get; set; }
    }

    public sealed partial class MainPage : Page
    {
        private const string NewTabHomeUrl = "about:home";
        private const string DefaultHomeUrl = "https://www.google.com";

        private ObservableCollection<BrowserTab> _tabs = new ObservableCollection<BrowserTab>();
        private BrowserTab _activeTab;
        private UserAgentProfile _currentUa = UserAgentProfile.ChromeMobile;

        public MainPage()
        {
            InitializeComponent();
            TabListView.ItemsSource = _tabs;
            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Mở tab đầu tiên với trang Google
            CreateNewTab(DefaultHomeUrl);
            LoadToolsInfo();
        }

        // =========================================================================
        // 1. QUẢN LÝ ĐA TAB (MULTI-TAB BROWSING)
        // =========================================================================

        private void CreateNewTab(string url)
        {
            var tab = new BrowserTab
            {
                Id = Guid.NewGuid().ToString(),
                Title = "Tab Mới",
                Url = string.IsNullOrWhiteSpace(url) ? NewTabHomeUrl : url
            };
            _tabs.Add(tab);
            SwitchToTab(tab);
        }

        private void SwitchToTab(BrowserTab tab)
        {
            if (tab == null) return;
            _activeTab = tab;
            UpdateTabCountDisplay();

            if (tab.Url == NewTabHomeUrl)
            {
                NewTabHomeView.Visibility = Visibility.Visible;
                BrowserWebView.Visibility = Visibility.Collapsed;
                OmniUrlBox.Text = "";
                OmniLockIcon.Text = "\uE721"; // Search Icon
            }
            else
            {
                NewTabHomeView.Visibility = Visibility.Collapsed;
                BrowserWebView.Visibility = Visibility.Visible;
                NavigateBrowser(tab.Url);
            }

            TabSwitcherOverlay.Visibility = Visibility.Collapsed;
        }

        private void UpdateTabCountDisplay()
        {
            OmniTabCountText.Text = _tabs.Count.ToString();
        }

        private void NewTab_Click(object sender, RoutedEventArgs e)
        {
            CreateNewTab(NewTabHomeUrl);
        }

        private void OmniTabButton_Click(object sender, RoutedEventArgs e)
        {
            TabSwitcherOverlay.Visibility = TabSwitcherOverlay.Visibility == Visibility.Visible
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void CloseTabSwitcher_Click(object sender, RoutedEventArgs e)
        {
            TabSwitcherOverlay.Visibility = Visibility.Collapsed;
        }

        private void TabListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (TabListView.SelectedItem is BrowserTab tab)
            {
                SwitchToTab(tab);
            }
        }

        private void CloseTabButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string tabId)
            {
                var tab = _tabs.FirstOrDefault(t => t.Id == tabId);
                if (tab != null)
                {
                    int index = _tabs.IndexOf(tab);
                    _tabs.Remove(tab);

                    if (_tabs.Count == 0)
                    {
                        CreateNewTab(NewTabHomeUrl);
                    }
                    else if (_activeTab == tab)
                    {
                        int newIndex = Math.Max(0, index - 1);
                        SwitchToTab(_tabs[newIndex]);
                    }
                    else
                    {
                        UpdateTabCountDisplay();
                    }
                }
            }
        }

        // =========================================================================
        // 2. CHROME OMNIBOX & ĐIỀU HƯỚNG
        // =========================================================================

        private void NavigateBrowser(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
                return;

            input = input.Trim();
            string targetUrl = input;

            if (!input.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !input.StartsWith("https://", StringComparison.OrdinalIgnoreCase) &&
                !input.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            {
                if (input.Contains(" ") || !input.Contains("."))
                {
                    targetUrl = "https://www.google.com/search?q=" + Uri.EscapeDataString(input);
                }
                else
                {
                    targetUrl = "https://" + input;
                }
            }

            NewTabHomeView.Visibility = Visibility.Collapsed;
            BrowserWebView.Visibility = Visibility.Visible;

            OmniUrlBox.Text = targetUrl;
            if (_activeTab != null)
            {
                _activeTab.Url = targetUrl;
            }

            try
            {
                BrowserWebView.Navigate(new Uri(targetUrl));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Navigate error: " + ex.Message);
            }
        }

        private void OmniGoButton_Click(object sender, RoutedEventArgs e)
        {
            NavigateBrowser(OmniUrlBox.Text);
        }

        private void OmniUrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                NavigateBrowser(OmniUrlBox.Text);
                e.Handled = true;
            }
        }

        private void HomeSearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                NavigateBrowser(HomeSearchBox.Text);
                e.Handled = true;
            }
        }

        private void OmniClearButton_Click(object sender, RoutedEventArgs e)
        {
            OmniUrlBox.Text = "";
        }

        private void OmniUrlBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            OmniClearButton.Visibility = string.IsNullOrEmpty(OmniUrlBox.Text) ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OmniBackButton_Click(object sender, RoutedEventArgs e)
        {
            if (BrowserWebView.CanGoBack)
            {
                BrowserWebView.GoBack();
            }
            else
            {
                SwitchToTab(_activeTab);
            }
        }

        private void OmniForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (BrowserWebView.CanGoForward)
            {
                BrowserWebView.GoForward();
            }
        }

        private void OmniRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserWebView.Refresh();
        }

        private void OmniHomeButton_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab != null)
            {
                _activeTab.Url = NewTabHomeUrl;
                SwitchToTab(_activeTab);
            }
        }

        private void QuickShortcut_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string url)
            {
                NavigateBrowser(url);
            }
        }

        // =========================================================================
        // 3. ENGINE OPTIMIZATION & TIÊM POLYFILLS / ADBLOCK / DARKMODE
        // =========================================================================

        private void BrowserWebView_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            BrowserProgressBar.Visibility = Visibility.Visible;
            BrowserProgressBar.IsIndeterminate = true;

            if (args.Uri != null)
            {
                OmniUrlBox.Text = args.Uri.ToString();
                OmniLockIcon.Text = args.Uri.Scheme == "https" ? "\uE72E" : "\uE7BA"; // Lock vs Warning icon
                if (_activeTab != null)
                {
                    _activeTab.Url = args.Uri.ToString();
                }
            }
        }

        private void BrowserWebView_ContentLoading(WebView sender, WebViewContentLoadingEventArgs args)
        {
            // Bắt đầu tải nội dung
        }

        private async void BrowserWebView_DOMContentLoaded(WebView sender, WebViewDOMContentLoadedEventArgs args)
        {
            // 1. Tiêm Modern JavaScript Polyfills
            if (MenuTogglePolyfills.IsChecked)
            {
                try
                {
                    await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetModernPolyfillsScript() });
                }
                catch {}
            }

            // 2. Tiêm AdBlock & Cookie Blocker
            if (MenuToggleAdBlock.IsChecked)
            {
                try
                {
                    await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetContentBlockerScript() });
                }
                catch {}
            }

            // 3. Tiêm AMOLED Dark Mode
            if (MenuToggleDarkMode.IsChecked)
            {
                try
                {
                    await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetDarkModeScript() });
                }
                catch {}
            }
        }

        private void BrowserWebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            BrowserProgressBar.Visibility = Visibility.Collapsed;
            BrowserProgressBar.IsIndeterminate = false;

            if (_activeTab != null)
            {
                _activeTab.Title = BrowserWebView.DocumentTitle;
                if (string.IsNullOrWhiteSpace(_activeTab.Title))
                {
                    _activeTab.Title = args.Uri?.Host ?? "Trang web";
                }
            }

            OmniBackButton.IsEnabled = BrowserWebView.CanGoBack;
            BottomBackButton.IsEnabled = BrowserWebView.CanGoBack;
            BottomForwardButton.IsEnabled = BrowserWebView.CanGoForward;
        }

        private void BrowserWebView_NewWindowRequested(WebView sender, WebViewNewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            CreateNewTab(args.Uri.ToString());
        }

        // =========================================================================
        // 4. MENU CHROME & TÙY CHỌN ENGINE
        // =========================================================================

        private void MenuToggleAdBlock_Click(object sender, RoutedEventArgs e)
        {
            BrowserWebView.Refresh();
        }

        private void MenuTogglePolyfills_Click(object sender, RoutedEventArgs e)
        {
            BrowserWebView.Refresh();
        }

        private async void MenuToggleDarkMode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await BrowserWebView.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetDarkModeScript() });
            }
            catch {}
        }

        private void MenuToggleDesktop_Click(object sender, RoutedEventArgs e)
        {
            _currentUa = MenuToggleDesktop.IsChecked ? UserAgentProfile.ChromeDesktop : UserAgentProfile.ChromeMobile;
            BrowserWebView.Refresh();
        }

        private void SetUserAgent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag)
            {
                if (Enum.TryParse<UserAgentProfile>(tag, out var profile))
                {
                    _currentUa = profile;
                    BrowserWebView.Refresh();
                }
            }
        }

        // =========================================================================
        // 5. CÔNG CỤ TOOLS & SYSTEM INSPECTOR
        // =========================================================================

        private void OpenToolsDialog_Click(object sender, RoutedEventArgs e)
        {
            LoadToolsInfo();
            ToolsOverlay.Visibility = Visibility.Visible;
        }

        private void CloseToolsDialog_Click(object sender, RoutedEventArgs e)
        {
            ToolsOverlay.Visibility = Visibility.Collapsed;
        }

        private void LoadToolsInfo()
        {
            try
            {
                ToolsDeviceInfoBox.Text = SystemInterop.GetDeviceInfo();
                ToolsBatteryBox.Text = SystemInterop.GetBatteryAndNetworkInfo();
            }
            catch {}
        }

        private async void ToolsSendApi_Click(object sender, RoutedEventArgs e)
        {
            string url = ToolsApiUrlBox.Text?.Trim();
            if (string.IsNullOrEmpty(url)) return;

            string method = "GET";
            if (ToolsMethodCombo.SelectedItem is ComboBoxItem item && item.Content != null)
                method = item.Content.ToString();

            ToolsApiStatusText.Text = "Đang gửi...";
            ToolsApiResponseBox.Text = "";

            var res = await WebService.ExecuteRequestAsync(url, method);
            ToolsApiStatusText.Text = res.IsSuccess
                ? $"✅ {res.StatusCode} {res.StatusText} ({res.ElapsedMilliseconds} ms)"
                : $"❌ Lỗi: {res.StatusCode} {res.StatusText}";
            ToolsApiResponseBox.Text = res.Body ?? res.ErrorMessage;
        }
    }
}
