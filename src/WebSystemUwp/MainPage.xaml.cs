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
        private ObservableCollection<OfflinePageItem> _offlinePages;
        private BrowserTab _activeTab;
        private UserAgentProfile _currentUa = UserAgentProfile.ChromeMobile;
        private int _findActiveIndex = 0;
        private int _currentZoom = 100;

        private DispatcherTimer _autoHideNavTimer;

        public MainPage()
        {
            InitializeComponent();
            TabListView.ItemsSource = _tabs;
            Loaded += MainPage_Loaded;

            // Timer tự động ẩn thanh điều hướng dưới để tối ưu màn hình Lumia
            _autoHideNavTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _autoHideNavTimer.Tick += AutoHideNavTimer_Tick;
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            // 1. Nạp Bookmarks & Lịch sử & Offline Pages
            _bookmarks = BookmarkService.LoadBookmarks();
            _history = HistoryService.LoadHistory();
            _offlinePages = await CacheService.LoadOfflinePagesAsync();

            BookmarksListView.ItemsSource = _bookmarks;
            HomeBookmarksListView.ItemsSource = _bookmarks;
            HistoryListView.ItemsSource = _history;
            OfflinePagesListView.ItemsSource = _offlinePages;

            // 2. Đồng bộ Settings
            ApplySettingsToUI();

            // 3. Mở tab đầu tiên
            CreateNewTab(DefaultHomeUrl);
            LoadToolsInfo();
        }

        private void ApplySettingsToUI()
        {
            try
            {
                MenuToggleAdBlock.IsChecked = SettingsService.AutoBlockAds;
                MenuToggleImgBlock.IsChecked = SettingsService.AutoBlockImages;
                MenuTogglePolyfills.IsChecked = SettingsService.AutoInjectPolyfills;
                MenuToggleDarkMode.IsChecked = SettingsService.DarkModeDefault;

                SettingsAutoHideToggle.IsOn = SettingsService.AutoHideBottomBar;
                SettingsAdBlockToggle.IsOn = SettingsService.AutoBlockAds;
                SettingsPolyfillsToggle.IsOn = SettingsService.AutoInjectPolyfills;
                SettingsDarkModeToggle.IsOn = SettingsService.DarkModeDefault;

                for (int i = 0; i < SettingsSearchCombo.Items.Count; i++)
                {
                    if (SettingsSearchCombo.Items[i] is ComboBoxItem item &&
                        item.Content?.ToString() == SettingsService.SearchEngine)
                    {
                        SettingsSearchCombo.SelectedIndex = i;
                        break;
                    }
                }
            }
            catch {}
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
                ShowBottomNavBar();
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
            OfflineOverlay.Visibility = Visibility.Collapsed;
            SettingsOverlay.Visibility = Visibility.Collapsed;
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
                    targetUrl = SettingsService.GetSearchUrl(input);
                }
                else
                {
                    targetUrl = "https://" + input;
                }
            }

            NewTabHomeView.Visibility = Visibility.Collapsed;
            BrowserWebView.Visibility = Visibility.Visible;
            ShowBottomNavBar();

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
            if (_activeTab != null && !string.IsNullOrEmpty(_activeTab.Url) && _activeTab.Url != NewTabHomeUrl)
            {
                OmniUrlBox.Text = _activeTab.Url;
            }
            OmniUrlBox.SelectAll();
            ShowBottomNavBar();
        }

        private void OmniUrlBox_LostFocus(object sender, RoutedEventArgs e)
        {
            OmniboxBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(255, 58, 58, 58));
            UpdateOmniboxDisplay();
        }

        private void UpdateOmniboxDisplay()
        {
            if (_activeTab == null || string.IsNullOrEmpty(_activeTab.Url) || _activeTab.Url == NewTabHomeUrl)
                return;

            try
            {
                var uri = new Uri(_activeTab.Url);
                OmniUrlBox.Text = uri.Host.Replace("www.", "");
            }
            catch
            {
                OmniUrlBox.Text = _activeTab.Url;
            }
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
            ShowBottomNavBar();
        }

        private void OmniForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (BrowserWebView.CanGoForward)
            {
                BrowserWebView.GoForward();
            }
            ShowBottomNavBar();
        }

        private void OmniRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            BrowserWebView.Refresh();
            ShowBottomNavBar();
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
        // 3. AUTO-HIDE BOTTOM NAVIGATION BAR (LUMIA 920 OPTIMIZATION)
        // =========================================================================

        private void ShowBottomNavBar()
        {
            BottomNavBar.Visibility = Visibility.Visible;
            FloatingRevealNavButton.Visibility = Visibility.Collapsed;
            _autoHideNavTimer.Stop();

            if (SettingsService.AutoHideBottomBar && BrowserWebView.Visibility == Visibility.Visible)
            {
                _autoHideNavTimer.Start();
            }
        }

        private void HideBottomNavBar()
        {
            if (SettingsService.AutoHideBottomBar && BrowserWebView.Visibility == Visibility.Visible)
            {
                BottomNavBar.Visibility = Visibility.Collapsed;
                FloatingRevealNavButton.Visibility = Visibility.Visible;
            }
        }

        private void AutoHideNavTimer_Tick(object sender, object e)
        {
            _autoHideNavTimer.Stop();
            HideBottomNavBar();
        }

        private void FloatingRevealNavButton_Click(object sender, RoutedEventArgs e)
        {
            ShowBottomNavBar();
        }

        private void BrowserWebView_Tapped(object sender, TappedRoutedEventArgs e)
        {
            ShowBottomNavBar();
        }

        // =========================================================================
        // 4. BOOKMARKS & LỊCH SỬ & OFFLINE PAGES
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
        // 5. OFFLINE PAGES (ĐỌC NGOẠI TUYẾN)
        // =========================================================================

        private async void SaveCurrentPageOffline_Click(object sender, RoutedEventArgs e)
        {
            if (_activeTab == null || string.IsNullOrWhiteSpace(_activeTab.Url) || _activeTab.Url == NewTabHomeUrl)
                return;

            try
            {
                string html = await BrowserWebView.InvokeScriptAsync("eval", new[] { "document.documentElement.outerHTML;" });
                if (!string.IsNullOrWhiteSpace(html))
                {
                    var saved = await CacheService.SavePageOfflineAsync(_activeTab.Title, _activeTab.Url, html);
                    if (saved != null)
                    {
                        _offlinePages.Insert(0, saved);
                    }
                }
            }
            catch {}
        }

        private async void OpenOfflinePages_Click(object sender, RoutedEventArgs e)
        {
            _offlinePages = await CacheService.LoadOfflinePagesAsync();
            OfflinePagesListView.ItemsSource = _offlinePages;
            OfflineOverlay.Visibility = Visibility.Visible;
        }

        private void CloseOfflinePages_Click(object sender, RoutedEventArgs e)
        {
            OfflineOverlay.Visibility = Visibility.Collapsed;
        }

        private async void OfflinePageItem_Click(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is OfflinePageItem item)
            {
                OfflineOverlay.Visibility = Visibility.Collapsed;
                string html = await CacheService.GetOfflinePageHtmlAsync(item.Id);
                if (!string.IsNullOrEmpty(html))
                {
                    NewTabHomeView.Visibility = Visibility.Collapsed;
                    BrowserWebView.Visibility = Visibility.Visible;
                    OmniUrlBox.Text = "offline://" + item.Title;
                    BrowserWebView.NavigateToString(html);
                }
            }
        }

        private async void DeleteOfflinePage_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
            {
                await CacheService.DeleteOfflinePageAsync(id);
                var match = _offlinePages.FirstOrDefault(p => p.Id == id);
                if (match != null) _offlinePages.Remove(match);
            }
        }

        // =========================================================================
        // 6. TÌM TRONG TRANG (FIND IN PAGE)
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
        // 7. ENGINE OPTIMIZATION & TIÊM SCRIPTS
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

            ShowBottomNavBar();
        }

        private void BrowserWebView_ContentLoading(WebView sender, WebViewContentLoadingEventArgs args)
        {
        }

        private async void BrowserWebView_DOMContentLoaded(WebView sender, WebViewDOMContentLoadedEventArgs args)
        {
            if (MenuTogglePolyfills.IsChecked)
            {
                try { await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetModernPolyfillsScript() }); } catch {}
            }

            if (MenuToggleAdBlock.IsChecked)
            {
                try { await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetContentBlockerScript() }); } catch {}
            }

            if (MenuToggleImgBlock.IsChecked)
            {
                try { await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetImageBlockerScript() }); } catch {}
            }

            if (MenuToggleDarkMode.IsChecked)
            {
                try { await sender.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetDarkModeScript() }); } catch {}
            }

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

                HistoryService.RecordVisit(_history, _activeTab.Title, args.Uri.ToString());
                UpdateOmniboxDisplay();
            }

            BottomBackButton.IsEnabled = BrowserWebView.CanGoBack;
            BottomForwardButton.IsEnabled = BrowserWebView.CanGoForward;

            // Bắt đầu đếm 3 giây để tự ẩn Bottom Bar
            if (SettingsService.AutoHideBottomBar)
            {
                _autoHideNavTimer.Start();
            }
        }

        private void BrowserWebView_NewWindowRequested(WebView sender, WebViewNewWindowRequestedEventArgs args)
        {
            args.Handled = true;
            CreateNewTab(args.Uri.ToString());
        }

        // =========================================================================
        // 8. TIỆN ÍCH MENU & CÀI ĐẶT
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

        private void OpenSettings_Click(object sender, RoutedEventArgs e)
        {
            ApplySettingsToUI();
            SettingsOverlay.Visibility = Visibility.Visible;
        }

        private void CloseSettings_Click(object sender, RoutedEventArgs e)
        {
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        private void SettingsSearchCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (SettingsSearchCombo.SelectedItem is ComboBoxItem item && item.Content != null)
            {
                SettingsService.SearchEngine = item.Content.ToString();
            }
        }

        private void SettingsAutoHideToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.AutoHideBottomBar = SettingsAutoHideToggle.IsOn;
            if (!SettingsService.AutoHideBottomBar) ShowBottomNavBar();
        }

        private void SettingsAdBlockToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.AutoBlockAds = SettingsAdBlockToggle.IsOn;
            MenuToggleAdBlock.IsChecked = SettingsAdBlockToggle.IsOn;
        }

        private void SettingsPolyfillsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.AutoInjectPolyfills = SettingsPolyfillsToggle.IsOn;
            MenuTogglePolyfills.IsChecked = SettingsPolyfillsToggle.IsOn;
        }

        private void SettingsDarkModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            SettingsService.DarkModeDefault = SettingsDarkModeToggle.IsOn;
            MenuToggleDarkMode.IsChecked = SettingsDarkModeToggle.IsOn;
        }

        private void ClearAllBrowsingData_Click(object sender, RoutedEventArgs e)
        {
            HistoryService.ClearAll(_history);
            SettingsOverlay.Visibility = Visibility.Collapsed;
        }

        // =========================================================================
        // 9. CÔNG CỤ WEB ENGINE V2 & CHẨN ĐOÁN HỆ THỐNG
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
                ToolsDeviceInfoBox.Text = DeviceService.GetDeviceSummary();
                ToolsDiagnosticsBox.Text = DiagnosticsService.GetFullDiagnosticsSummary();
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

            ToolsApiStatusText.Text = "Đang gửi qua Web Engine v2...";
            ToolsApiResponseBox.Text = "";

            var req = new WebEngineRequest
            {
                Url = url,
                Method = method,
                TimeoutSeconds = 15
            };

            var res = await WebService.RequestAsync(req);
            ToolsApiStatusText.Text = res.IsSuccess
                ? $"✅ {res.StatusCode} {res.StatusText} ({res.ResponseTimeMs} ms)"
                : $"❌ Lỗi: {res.ErrorType} — {res.ErrorMessage}";
            ToolsApiResponseBox.Text = res.Body ?? res.ErrorMessage;
        }

        private async void ToolsTestPing_Click(object sender, RoutedEventArgs e)
        {
            ToolsPingResultText.Text = "Đang đo...";
            long latency = await NetworkService.TestPingAsync("https://www.google.com");
            ToolsPingResultText.Text = latency >= 0 ? $"⚡ {latency} ms" : "❌ Mất kết nối";
        }
    }
}
