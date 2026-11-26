using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;
using Windows.UI;
using Windows.UI.Text;
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
        public Stack<string> BackStack { get; } = new Stack<string>();
        public Stack<string> ForwardStack { get; } = new Stack<string>();
        public RustRenderPage RenderedPage { get; set; }
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
        private RustRenderPage _activeRustPage;
        private ulong _rustNavSeq;
        private readonly Dictionary<int, CanvasTextFormat> _textFormats = new Dictionary<int, CanvasTextFormat>();

        private bool IsRustEngineActive => MenuToggleRustEngine.IsChecked && RustEngineBridge.IsAvailable;

        public MainPage()
        {
            InitializeComponent();
            TabListView.ItemsSource = _tabs;
            Loaded += MainPage_Loaded;
            Unloaded += MainPage_Unloaded;

            // Timer tự động ẩn thanh điều hướng dưới để tối ưu màn hình Lumia
            _autoHideNavTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(3)
            };
            _autoHideNavTimer.Tick += AutoHideNavTimer_Tick;
        }

        private void MainPage_Unloaded(object sender, RoutedEventArgs e)
        {
            foreach (var fmt in _textFormats.Values)
            {
                fmt.Dispose();
            }
            _textFormats.Clear();
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
                MenuToggleRustEngine.IsChecked = SettingsService.UseRustBrowserEngine && RustEngineBridge.IsAvailable;
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
                RustBrowserScrollViewer.Visibility = Visibility.Collapsed;
                OmniUrlBox.Text = "";
                OmniLockIcon.Text = "\uE721"; // Search Icon
                UpdateBookmarkStarStatus("");
                UpdateNavButtons();
                ShowBottomNavBar();
            }
            else if (IsRustEngineActive && tab.RenderedPage != null && string.Equals(tab.RenderedPage.Url, tab.Url, StringComparison.OrdinalIgnoreCase))
            {
                NewTabHomeView.Visibility = Visibility.Collapsed;
                BrowserWebView.Visibility = Visibility.Collapsed;
                RustBrowserScrollViewer.Visibility = Visibility.Visible;
                OmniLockIcon.Text = tab.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "\uE72E" : "\uE7BA";
                UpdateBookmarkStarStatus(tab.Url);
                UpdateOmniboxDisplay();
                DisplayRustPage(tab.RenderedPage);
                UpdateNavButtons();
                ShowBottomNavBar();
            }
            else
            {
                NewTabHomeView.Visibility = Visibility.Collapsed;
                NavigateBrowser(tab.Url, pushHistory: false);
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

        private void NavigateBrowser(string input, bool pushHistory = true)
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

            if (targetUrl.Equals(NewTabHomeUrl, StringComparison.OrdinalIgnoreCase))
            {
                if (_activeTab != null)
                {
                    _activeTab.Url = NewTabHomeUrl;
                    SwitchToTab(_activeTab);
                }
                return;
            }

            NewTabHomeView.Visibility = Visibility.Collapsed;
            ShowBottomNavBar();

            if (IsRustEngineActive)
            {
                if (pushHistory && _activeTab != null &&
                    !string.IsNullOrWhiteSpace(_activeTab.Url) &&
                    _activeTab.Url != NewTabHomeUrl &&
                    !_activeTab.Url.Equals(targetUrl, StringComparison.OrdinalIgnoreCase))
                {
                    _activeTab.BackStack.Push(_activeTab.Url);
                    _activeTab.ForwardStack.Clear();
                }

                BrowserWebView.Visibility = Visibility.Collapsed;
                RustBrowserScrollViewer.Visibility = Visibility.Visible;
                OmniUrlBox.Text = targetUrl;
                OmniLockIcon.Text = targetUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "\uE72E" : "\uE7BA";

                if (_activeTab != null)
                {
                    _activeTab.Url = targetUrl;
                }

                UpdateBookmarkStarStatus(targetUrl);
                LoadUrlWithRustEngineAsync(targetUrl);
                return;
            }

            RustBrowserScrollViewer.Visibility = Visibility.Collapsed;
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

        private float GetRustViewportWidth()
        {
            double width = RustBrowserScrollViewer.ViewportWidth;
            if (width <= 100) width = RustBrowserScrollViewer.ActualWidth;
            if (width <= 100) width = Window.Current.Bounds.Width;
            if (width <= 100) width = 380;
            float scale = Math.Max(0.5f, _currentZoom / 100.0f);
            return (float)Math.Max(220, width / scale);
        }

        private async void LoadUrlWithRustEngineAsync(string targetUrl)
        {
            ulong seq = ++_rustNavSeq;
            BrowserProgressBar.Visibility = Visibility.Visible;
            BrowserProgressBar.IsIndeterminate = true;

            try
            {
                string ua = EngineOptimizer.UserAgents.TryGetValue(_currentUa, out var profileUa)
                    ? profileUa
                    : null;
                float viewportWidth = GetRustViewportWidth();
                bool darkMode = MenuToggleDarkMode.IsChecked;

                var page = await RustEngineBridge.FetchAndRenderAsync(targetUrl, ua, viewportWidth, darkMode, 20);
                if (seq != _rustNavSeq) return;

                if (_activeTab != null)
                {
                    _activeTab.RenderedPage = page;
                    _activeTab.Title = string.IsNullOrWhiteSpace(page.Title) ? targetUrl : page.Title;
                    _activeTab.Url = targetUrl;
                }

                HistoryService.RecordVisit(_history, _activeTab?.Title ?? targetUrl, targetUrl);
                DisplayRustPage(page);
                UpdateOmniboxDisplay();
                UpdateNavButtons();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine("Rust render error: " + ex.Message);
            }
            finally
            {
                if (seq == _rustNavSeq)
                {
                    BrowserProgressBar.Visibility = Visibility.Collapsed;
                    BrowserProgressBar.IsIndeterminate = false;
                    if (SettingsService.AutoHideBottomBar)
                    {
                        _autoHideNavTimer.Start();
                    }
                }
            }
        }

        private void DisplayRustPage(RustRenderPage page)
        {
            _activeRustPage = page;
            bool dark = MenuToggleDarkMode.IsChecked;
            Color clear = dark ? Color.FromArgb(255, 18, 18, 18) : Color.FromArgb(255, 250, 250, 250);
            RustBrowserScrollViewer.Background = new SolidColorBrush(clear);
            RustVirtualCanvas.ClearColor = clear;

            float scale = Math.Max(0.5f, _currentZoom / 100.0f);
            RustVirtualCanvas.Width = Math.Max(280, page.ContentWidth * scale);
            RustVirtualCanvas.Height = Math.Max(400, page.ContentHeight * scale);
            RustBrowserScrollViewer.ChangeView(0, 0, null, true);
            RustVirtualCanvas.Invalidate();
        }

        private CanvasTextFormat GetCachedTextFormat(float fontSize, bool bold, bool italic)
        {
            int sizeKey = (int)Math.Round(fontSize * 10);
            int key = (sizeKey << 2) | (bold ? 1 : 0) | (italic ? 2 : 0);

            if (!_textFormats.TryGetValue(key, out var fmt))
            {
                fmt = new CanvasTextFormat
                {
                    FontFamily = "Segoe UI",
                    FontSize = Math.Max(10f, fontSize),
                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
                    WordWrapping = CanvasWordWrapping.Wrap
                };
                _textFormats[key] = fmt;
            }
            return fmt;
        }

        private void RustVirtualCanvas_RegionsInvalidated(CanvasVirtualControl sender, CanvasRegionsInvalidatedEventArgs args)
        {
            if (_activeRustPage == null) return;
            float scale = Math.Max(0.5f, _currentZoom / 100.0f);

            foreach (var region in args.InvalidatedRegions)
            {
                using (var ds = sender.CreateDrawingSession(region))
                {
                    foreach (var item in _activeRustPage.Items)
                    {
                        float x = item.X * scale;
                        float y = item.Y * scale;
                        float w = item.Width * scale;
                        float h = item.Height * scale;

                        if (x + w < region.Left ||
                            x > region.Right ||
                            y + h < region.Top ||
                            y > region.Bottom)
                        {
                            continue;
                        }

                        var rect = new Rect(x, y, w, h);
                        if (item.Kind == RenderItemKind.Box || item.Kind == RenderItemKind.Divider)
                        {
                            if (item.BackgroundColor.A > 0)
                            {
                                ds.FillRectangle(rect, item.BackgroundColor);
                            }
                            if (item.BorderColor.A > 0)
                            {
                                ds.DrawRectangle(rect, item.BorderColor, 1.0f);
                            }
                        }
                        else if ((item.Kind == RenderItemKind.Text || item.Kind == RenderItemKind.Link) &&
                                 !string.IsNullOrEmpty(item.Text))
                        {
                            if (item.BackgroundColor.A > 0)
                            {
                                ds.FillRectangle(rect, item.BackgroundColor);
                            }
                            if (item.BorderColor.A > 0)
                            {
                                ds.DrawRectangle(rect, item.BorderColor, 1.0f);
                            }

                            var fmt = GetCachedTextFormat(item.FontSize * scale, item.Bold, item.Italic);
                            var textBounds = new Rect(x, y, w + 4, h + 4);
                            ds.DrawText(item.Text, textBounds, item.ForegroundColor, fmt);
                        }
                    }
                }
            }
        }

        private void RustVirtualCanvas_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_activeRustPage != null)
            {
                float scale = Math.Max(0.5f, _currentZoom / 100.0f);
                Point pos = e.GetPosition(RustVirtualCanvas);
                double logicalX = pos.X / scale;
                double logicalY = pos.Y / scale;

                for (int i = _activeRustPage.Items.Count - 1; i >= 0; i--)
                {
                    var item = _activeRustPage.Items[i];
                    if (item.Kind == RenderItemKind.Link &&
                        !string.IsNullOrEmpty(item.Href) &&
                        logicalX >= item.X && logicalX <= item.X + item.Width &&
                        logicalY >= item.Y && logicalY <= item.Y + item.Height)
                    {
                        if (!item.Href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) &&
                            !item.Href.StartsWith("#"))
                        {
                            NavigateBrowser(item.Href);
                            return;
                        }
                    }
                }
            }

            ShowBottomNavBar();
        }

        private void RustBrowserScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!e.IsIntermediate && SettingsService.AutoHideBottomBar)
            {
                _autoHideNavTimer.Start();
            }
        }

        private void UpdateNavButtons()
        {
            if (IsRustEngineActive && _activeTab != null)
            {
                BottomBackButton.IsEnabled = _activeTab.BackStack.Count > 0 || _activeTab.Url != NewTabHomeUrl;
                BottomForwardButton.IsEnabled = _activeTab.ForwardStack.Count > 0;
            }
            else
            {
                BottomBackButton.IsEnabled = BrowserWebView.CanGoBack;
                BottomForwardButton.IsEnabled = BrowserWebView.CanGoForward;
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
            if (IsRustEngineActive && _activeTab != null)
            {
                if (_activeTab.BackStack.Count > 0)
                {
                    if (!string.IsNullOrEmpty(_activeTab.Url) && _activeTab.Url != NewTabHomeUrl)
                    {
                        _activeTab.ForwardStack.Push(_activeTab.Url);
                    }
                    string prevUrl = _activeTab.BackStack.Pop();
                    NavigateBrowser(prevUrl, pushHistory: false);
                }
                else
                {
                    _activeTab.Url = NewTabHomeUrl;
                    SwitchToTab(_activeTab);
                }
                ShowBottomNavBar();
                return;
            }

            if (BrowserWebView.CanGoBack)
            {
                BrowserWebView.GoBack();
            }
            else if (_activeTab != null)
            {
                _activeTab.Url = NewTabHomeUrl;
                SwitchToTab(_activeTab);
            }
            ShowBottomNavBar();
        }

        private void OmniForwardButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsRustEngineActive && _activeTab != null)
            {
                if (_activeTab.ForwardStack.Count > 0)
                {
                    if (!string.IsNullOrEmpty(_activeTab.Url) && _activeTab.Url != NewTabHomeUrl)
                    {
                        _activeTab.BackStack.Push(_activeTab.Url);
                    }
                    string nextUrl = _activeTab.ForwardStack.Pop();
                    NavigateBrowser(nextUrl, pushHistory: false);
                }
                ShowBottomNavBar();
                return;
            }

            if (BrowserWebView.CanGoForward)
            {
                BrowserWebView.GoForward();
            }
            ShowBottomNavBar();
        }

        private void OmniRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsRustEngineActive && _activeTab != null && _activeTab.Url != NewTabHomeUrl)
            {
                NavigateBrowser(_activeTab.Url, pushHistory: false);
            }
            else
            {
                BrowserWebView.Refresh();
            }
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

            bool isViewingPage = BrowserWebView.Visibility == Visibility.Visible ||
                                 RustBrowserScrollViewer.Visibility == Visibility.Visible;
            if (SettingsService.AutoHideBottomBar && isViewingPage)
            {
                _autoHideNavTimer.Start();
            }
        }

        private void HideBottomNavBar()
        {
            bool isViewingPage = BrowserWebView.Visibility == Visibility.Visible ||
                                 RustBrowserScrollViewer.Visibility == Visibility.Visible;
            if (SettingsService.AutoHideBottomBar && isViewingPage)
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
                string html = IsRustEngineActive && _activeTab.RenderedPage != null
                    ? _activeTab.RenderedPage.SourceHtml
                    : await BrowserWebView.InvokeScriptAsync("eval", new[] { "document.documentElement.outerHTML;" });

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
                    OmniUrlBox.Text = "offline://" + item.Title;
                    if (_activeTab != null)
                    {
                        _activeTab.Title = item.Title;
                        _activeTab.Url = item.Url;
                    }

                    if (IsRustEngineActive)
                    {
                        BrowserWebView.Visibility = Visibility.Collapsed;
                        RustBrowserScrollViewer.Visibility = Visibility.Visible;
                        var page = await RustEngineBridge.RenderHtmlAsync(html, item.Url, GetRustViewportWidth(), MenuToggleDarkMode.IsChecked);
                        page.SourceHtml = html;
                        if (_activeTab != null) _activeTab.RenderedPage = page;
                        DisplayRustPage(page);
                    }
                    else
                    {
                        RustBrowserScrollViewer.Visibility = Visibility.Collapsed;
                        BrowserWebView.Visibility = Visibility.Visible;
                        BrowserWebView.NavigateToString(html);
                    }
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

        private void RefreshCurrentTab()
        {
            if (IsRustEngineActive)
            {
                if (_activeTab != null && !string.IsNullOrEmpty(_activeTab.Url) && _activeTab.Url != NewTabHomeUrl)
                {
                    NavigateBrowser(_activeTab.Url, pushHistory: false);
                }
            }
            else
            {
                BrowserWebView.Refresh();
            }
        }

        private async void ReRenderCurrentRustHtmlAsync()
        {
            if (_activeTab?.RenderedPage?.SourceHtml == null) return;

            var page = await RustEngineBridge.RenderHtmlAsync(
                _activeTab.RenderedPage.SourceHtml,
                _activeTab.Url ?? "",
                GetRustViewportWidth(),
                MenuToggleDarkMode.IsChecked);
            page.SourceHtml = _activeTab.RenderedPage.SourceHtml;
            _activeTab.RenderedPage = page;
            DisplayRustPage(page);
        }

        private void SetZoom_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag && int.TryParse(tag, out int zoom))
            {
                _currentZoom = zoom;
                if (IsRustEngineActive)
                {
                    ReRenderCurrentRustHtmlAsync();
                }
                else
                {
                    try { BrowserWebView.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetZoomScript(_currentZoom) }); } catch {}
                }
            }
        }

        private void MenuToggleRustEngine_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.UseRustBrowserEngine = MenuToggleRustEngine.IsChecked;
            if (_activeTab != null && !string.IsNullOrEmpty(_activeTab.Url) && _activeTab.Url != NewTabHomeUrl)
            {
                NavigateBrowser(_activeTab.Url, pushHistory: false);
            }
        }

        private void MenuToggleAdBlock_Click(object sender, RoutedEventArgs e) => BrowserWebView.Refresh();
        private void MenuTogglePolyfills_Click(object sender, RoutedEventArgs e) => BrowserWebView.Refresh();
        private void MenuToggleImgBlock_Click(object sender, RoutedEventArgs e) => BrowserWebView.Refresh();
        private async void MenuToggleDarkMode_Click(object sender, RoutedEventArgs e)
        {
            if (IsRustEngineActive && _activeTab?.RenderedPage?.SourceHtml != null)
            {
                ReRenderCurrentRustHtmlAsync();
                return;
            }

            try { await BrowserWebView.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetDarkModeScript() }); } catch {}
        }

        private void MenuToggleDesktop_Click(object sender, RoutedEventArgs e)
        {
            _currentUa = MenuToggleDesktop.IsChecked ? UserAgentProfile.ChromeDesktop : UserAgentProfile.ChromeMobile;
            RefreshCurrentTab();
        }

        private void SetUserAgent_Click(object sender, RoutedEventArgs e)
        {
            if (sender is MenuFlyoutItem item && item.Tag is string tag && Enum.TryParse<UserAgentProfile>(tag, out var profile))
            {
                _currentUa = profile;
                RefreshCurrentTab();
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
                ToolsDiagnosticsBox.Text = DiagnosticsService.GetFullDiagnosticsSummary() +
                                           "\n" +
                                           RustEngineBridge.GetCoreStatusSummary();
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
                TimeoutSeconds = 15,
                UseRustTlsEngine = ToolsUseRustTlsCheck.IsChecked == true
            };

            var res = await WebService.RequestAsync(req);
            string transport = string.IsNullOrEmpty(res.TransportInfo) ? "" : $" [{res.TransportInfo}]";
            ToolsApiStatusText.Text = res.IsSuccess
                ? $"✅ {res.StatusCode} {res.StatusText} ({res.ResponseTimeMs} ms){transport}"
                : $"❌ Lỗi: {res.ErrorType} — {res.ErrorMessage}{transport}";
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
