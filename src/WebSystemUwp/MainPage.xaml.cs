using System;
using System.Collections.ObjectModel;
using System.Linq;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Xaml;
using Windows.UI.Xaml.Controls;
using Windows.UI.Xaml.Input;
using Windows.UI.Xaml.Media;
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
        private ObservableCollection<BookmarkItem> _bookmarks;
        private ObservableCollection<HistoryItem> _history;
        private BrowserTab _activeTab;
        private UserAgentProfile _currentUa = UserAgentProfile.ChromeMobile;
        private int _findActiveIndex = 0;
        private int _currentZoom = 100;

        public MainPage()
        {
            InitializeComponent();
            TabListView.ItemsSource = _tabs;
            Loaded += MainPage_Loaded;
        }

        private void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            // Nạp Bookmarks & Lịch sử
            _bookmarks = BookmarkService.LoadBookmarks();
            _history = HistoryService.LoadHistory();

            BookmarksListView.ItemsSource = _bookmarks;
            HomeBookmarksListView.ItemsSource = _bookmarks;
            HistoryListView.ItemsSource = _history;

            // Mở tab đầu tiên
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
                UpdateBookmarkStarStatus("");
            }
            else
            {
                NewTabHomeView.Visibility = Visibility.Collapsed;
                BrowserWebView.Visibility = Visibility.Visible;
                NavigateBrowser(tab.Url);
            }

            TabSwitcherOverlay.Visibility = Visibility.Collapsed;
            BookmarksOverlay.Visibility = Visibility.Collapsed;
            HistoryOverlay.Visibility = Visibility.Collapsed;
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

            UpdateBookmarkStarStatus(targetUrl);

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

        private void OmniUrlBox_GotFocus(object sender, RoutedEventArgs e)
        {
            OmniboxBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 138, 180, 248)); // #8AB4F8
            OmniUrlBox.SelectAll();
        }

        private void OmniUrlBox_LostFocus(object sender, RoutedEventArgs e)
        {
            OmniboxBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 60, 60, 60));
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
        // 3. BOOKMARKS & LỊCH SỬ (HISTORY)
        // =========================================================================

        private void UpdateBookmarkStarStatus(string url)
        {
            bool bookmarked = BookmarkService.IsBookmarked(_bookmarks, url);
            OmniBookmarkStar.Text = bookmarked ? "\uE735" : "\uE734"; // Filled vs Outline star
            OmniBookmarkStar.Foreground = new SolidColorBrush(bookmarked ? Color.FromArgb(255, 251, 188, 5) : Color.FromArgb(255, 158, 158, 158));
        }

        private void OmniBookmarkButton_Click(object sender, RoutedEventArgs e)
        {
            string currentUrl = _activeTab?.Url;
            if (string.IsNullOrWhiteSpace(currentUrl) || currentUrl == NewTabHomeUrl) return;

            string title = _activeTab?.Title ?? currentUrl;

            if (BookmarkService.IsBookmarked(_bookmarks, currentUrl))
            {
                var existing = _bookmarks.FirstOrDefault(b => b.Url.Equals(currentUrl, StringComparison.OrdinalIgnoreCase));
                if (existing != null)
                {
                    _bookmarks.Remove(existing);
                    BookmarkService.SaveBookmarks(_bookmarks);
                }
            }
            else
            {
                var newItem = BookmarkService.CreateBookmark(title, currentUrl);
                if (newItem != null)
                {
                    _bookmarks.Insert(0, newItem);
                    BookmarkService.SaveBookmarks(_bookmarks);
                }
            }

            UpdateBookmarkStarStatus(currentUrl);
        }

        private void OpenBookmarks_Click(object sender, RoutedEventArgs e)
        {
            BookmarksOverlay.Visibility = Visibility.Visible;
        }

        private void CloseBookmarks_Click(object sender, RoutedEventArgs e)
        {
            BookmarksOverlay.Visibility = Visibility.Collapsed;
        }

        private void BookmarkItem_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BookmarkItem b)
            {
                BookmarksOverlay.Visibility = Visibility.Collapsed;
                NavigateBrowser(b.Url);
            }
        }

        private void HomeBookmarkItem_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BookmarkItem b)
            {
                NavigateBrowser(b.Url);
            }
        }

        private void DeleteBookmarkButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var item = _bookmarks.FirstOrDefault(b => b.Id == id);
                if (item != null)
                {
                    _bookmarks.Remove(item);
                    BookmarkService.SaveBookmarks(_bookmarks);
                    UpdateBookmarkStarStatus(_activeTab?.Url);
                }
            }
        }

        private void OpenHistory_Click(object sender, RoutedEventArgs e)
        {
            HistoryOverlay.Visibility = Visibility.Visible;
        }

        private void CloseHistory_Click(object sender, RoutedEventArgs e)
        {
            HistoryOverlay.Visibility = Visibility.Collapsed;
        }

        private void HistoryItem_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is HistoryItem h)
            {
                HistoryOverlay.Visibility = Visibility.Collapsed;
                NavigateBrowser(h.Url);
            }
        }

        private void DeleteHistoryItem_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                var item = _history.FirstOrDefault(h => h.Id == id);
                if (item != null)
                {
                    _history.Remove(item);
                    HistoryService.SaveHistory(_history);
                }
            }
        }

        private void ClearHistory_Click(object sender, RoutedEventArgs e)
        {
            HistoryService.ClearAll(_history);
        }

        // =========================================================================
        // 4. TÌM TRONG TRANG (FIND IN PAGE)
        // =========================================================================

        private void OpenFindInPage_Click(object sender, RoutedEventArgs e)
        {
            FindInPageBar.Visibility = Visibility.Visible;
            FindInputBox.Focus(FocusState.Programmatic);
        }

        private void CloseFindInPage_Click(object sender, RoutedEventArgs e)
        {
            FindInPageBar.Visibility = Visibility.Collapsed;
            FindInputBox.Text = "";
            FindCountText.Text = "0/0";
            ExecuteFindClear();
        }

        private async void ExecuteFindQuery(int index)
        {
            string q = FindInputBox.Text?.Trim();
            if (string.IsNullOrEmpty(q))
            {
                FindCountText.Text = "0/0";
                ExecuteFindClear();
                return;
            }

            try
            {
                string script = EngineOptimizer.GetFindInPageScript(q, index);
                string res = await BrowserWebView.InvokeScriptAsync("eval", new[] { script });
                FindCountText.Text = string.IsNullOrEmpty(res) ? "0/0" : res;
            }
            catch {}
        }

        private async void ExecuteFindClear()
        {
            try
            {
                await BrowserWebView.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetClearFindInPageScript() });
            }
            catch {}
        }

        private void FindInputBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            _findActiveIndex = 0;
            ExecuteFindQuery(_findActiveIndex);
        }

        private void FindInputBox_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter)
            {
                _findActiveIndex++;
                ExecuteFindQuery(_findActiveIndex);
                e.Handled = true;
            }
        }

        private void FindNext_Click(object sender, RoutedEventArgs e)
        {
            _findActiveIndex++;
            ExecuteFindQuery(_findActiveIndex);
        }

        private void FindPrev_Click(object sender, RoutedEventArgs e)
        {
            _findActiveIndex--;
            ExecuteFindQuery(_findActiveIndex);
        }

        // =========================================================================
        // 5. ENGINE OPTIMIZATION & TIÊM SCRIPTS
        // =========================================================================

        private void BrowserWebView_NavigationStarting(WebView sender, WebViewNavigationStartingEventArgs args)
        {
            BrowserProgressBar.Visibility = Visibility.Visible;
            BrowserProgressBar.IsIndeterminate = true;

            if (args.Uri != null)
            {
                OmniUrlBox.Text = args.Uri.ToString();
                OmniLockIcon.Text = args.Uri.Scheme == "https" ? "\uE72E" : "\uE7BA";
                if (_activeTab != null)
                {
                    _activeTab.Url = args.Uri.ToString();
                }
                UpdateBookmarkStarStatus(args.Uri.ToString());
            }
        }

        private void BrowserWebView_ContentLoading(WebView sender, WebViewContentLoadingEventArgs args)
        {
        }

        private async void BrowserWebView_DOMContentLoaded(WebView sender, WebViewDOMContentLoadedEventArgs args)
        {
            // 1. Tiêm Modern JS Polyfills
            if (MenuTogglePolyfills.IsChecked)
            {
                try { await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetModernPolyfillsScript() }); } catch {}
            }

            // 2. Tiêm Chặn quảng cáo
            if (MenuToggleAdBlock.IsChecked)
            {
                try { await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetContentBlockerScript() }); } catch {}
            }

            // 3. Tiêm Chặn hình ảnh
            if (MenuToggleImgBlock.IsChecked)
            {
                try { await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetImageBlockerScript() }); } catch {}
            }

            // 4. Tiêm Dark Mode
            if (MenuToggleDarkMode.IsChecked)
            {
                try { await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetDarkModeScript() }); } catch {}
            }

            // 5. Tiêm Zoom
            if (_currentZoom != 100)
            {
                try { await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetZoomScript(_currentZoom) }); } catch {}
            }
        }

        private void BrowserWebView_NavigationCompleted(WebView sender, WebViewNavigationCompletedEventArgs args)
        {
            BrowserProgressBar.Visibility = Visibility.Collapsed;
            BrowserProgressBar.IsIndeterminate = false;

            if (_activeTab != null && args.Uri != null)
            {
                _activeTab.Title = BrowserWebView.DocumentTitle;
                if (string.IsNullOrWhiteSpace(_activeTab.Title))
                {
                    _activeTab.Title = args.Uri.Host;
                }

                // Ghi lại lịch sử truy cập
                HistoryService.RecordVisit(_history, _activeTab.Title, args.Uri.ToString());
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
        // 6. TIỆN ÍCH MENU: READER MODE, ZOOM, SHARE, COPY, USER-AGENT
        // =========================================================================

        private async void ToggleReaderMode_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await BrowserWebView.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetReaderModeScript() });
            }
            catch {}
        }

        private void ShareCurrentPage_Click(object sender, RoutedEventArgs e)
        {
            var dataTransferManager = DataTransferManager.GetForCurrentView();
            dataTransferManager.DataRequested += (s, args) =>
            {
                var req = args.Request;
                req.Data.Properties.Title = _activeTab?.Title ?? "Trang Web";
                req.Data.SetWebLink(new Uri(_activeTab?.Url ?? DefaultHomeUrl));
            };
            DataTransferManager.ShowShareUI();
        }

        private void CopyCurrentUrl_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrEmpty(_activeTab?.Url))
            {
                var package = new DataPackage();
                package.SetText(_activeTab.Url);
                Clipboard.SetContent(package);
            }
        }

        private void SetZoom_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag && int.TryParse(tag, out int zoom))
            {
                _currentZoom = zoom;
                try { BrowserWebView.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetZoomScript(_currentZoom) }); } catch {}
            }
        }

        private void MenuToggleAdBlock_Click(object sender, RoutedEventArgs e) => BrowserWebView.Refresh();
        private void MenuTogglePolyfills_Click(object sender, RoutedEventArgs e) => BrowserWebView.Refresh();
        private void MenuToggleImgBlock_Click(object sender, RoutedEventArgs e) => BrowserWebView.Refresh();
        private async void MenuToggleDarkMode_Click(object sender, RoutedEventArgs e)
        {
            try { await BrowserWebView.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetDarkModeScript() }); } catch {}
        }

        private void MenuToggleDesktop_Click(object sender, RoutedEventArgs e)
        {
            _currentUa = MenuToggleDesktop.IsChecked ? UserAgentProfile.ChromeDesktop : UserAgentProfile.ChromeMobile;
            BrowserWebView.Refresh();
        }

        private void SetUserAgent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag && Enum.TryParse<UserAgentProfile>(tag, out var profile))
            {
                _currentUa = profile;
                BrowserWebView.Refresh();
            }
        }

        // =========================================================================
        // 7. CÔNG CỤ TOOLS & SYSTEM INSPECTOR
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
