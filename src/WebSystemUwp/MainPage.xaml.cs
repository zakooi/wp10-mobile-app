using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Text;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
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
        private CancellationTokenSource _rustNavCts;
        private readonly Dictionary<int, CanvasTextFormat> _textFormats = new Dictionary<int, CanvasTextFormat>();
        private readonly Dictionary<string, CanvasBitmap> _rustImageCache = new Dictionary<string, CanvasBitmap>(StringComparer.OrdinalIgnoreCase);
        private bool _isRustReaderMode;
        private bool _isRustLoading;
        private bool _isApplyingSettings;
        private RenderDisplayItem _editingRustInput;
        private string _rustFindQuery = string.Empty;
        private readonly List<int> _rustFindMatchIndices = new List<int>();
        private int _rustFindActiveSlot = -1;

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
            try
            {
                DataTransferManager.GetForCurrentView().DataRequested -= Share_DataRequested;
            }
            catch { }

            CancelPendingRustNav();
            foreach (var fmt in _textFormats.Values)
            {
                fmt.Dispose();
            }
            _textFormats.Clear();
            ClearRustImageCache();
        }

        private CancellationToken RenewRustNavToken()
        {
            CancelPendingRustNav();
            _rustNavCts = new CancellationTokenSource();
            return _rustNavCts.Token;
        }

        private void CancelPendingRustNav()
        {
            if (_rustNavCts != null)
            {
                try { _rustNavCts.Cancel(); } catch { }
                _rustNavCts.Dispose();
                _rustNavCts = null;
            }
        }

        private void ClearRustImageCache()
        {
            foreach (var bmp in _rustImageCache.Values)
            {
                bmp?.Dispose();
            }
            _rustImageCache.Clear();
        }

        private async void MainPage_Loaded(object sender, RoutedEventArgs e)
        {
            try
            {
                DataTransferManager.GetForCurrentView().DataRequested += Share_DataRequested;
            }
            catch { }

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
            _isApplyingSettings = true;
            try
            {
                MenuToggleRustEngine.IsChecked = SettingsService.UseRustBrowserEngine && RustEngineBridge.IsAvailable;
                MenuToggleRustJs.IsChecked = SettingsService.UseRustJavaScript && RustEngineBridge.IsAvailable;
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
            finally
            {
                _isApplyingSettings = false;
            }
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
                StopRustLoadingUi();
                NewTabHomeView.Visibility = Visibility.Visible;
                BrowserWebView.Visibility = Visibility.Collapsed;
                RustViewportContainer.Visibility = Visibility.Collapsed;
                RustInputOverlay.Visibility = Visibility.Collapsed;
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
                RustViewportContainer.Visibility = Visibility.Visible;
                OmniLockIcon.Text = tab.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "\uE72E" : "\uE7BA";
                UpdateBookmarkStarStatus(tab.Url);
                UpdateOmniboxDisplay();
                StopRustLoadingUi();
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

        private void TabListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is BrowserTab tab)
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
                RustViewportContainer.Visibility = Visibility.Visible;
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

            RustViewportContainer.Visibility = Visibility.Collapsed;
            RustInputOverlay.Visibility = Visibility.Collapsed;
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

        private async void LoadUrlWithRustEngineAsync(
            string targetUrl,
            string method = "GET",
            string postBody = null,
            string requestContentType = null)
        {
            ulong seq = ++_rustNavSeq;
            CancellationToken ct = RenewRustNavToken();
            _isRustReaderMode = false;
            _isRustLoading = true;
            BottomRefreshIcon.Text = "\uE10A";
            RustFallbackBanner.Visibility = Visibility.Collapsed;
            RustInputOverlay.Visibility = Visibility.Collapsed;
            BrowserProgressBar.Visibility = Visibility.Visible;
            BrowserProgressBar.IsIndeterminate = true;

            try
            {
                string ua = EngineOptimizer.UserAgents.TryGetValue(_currentUa, out var profileUa)
                    ? profileUa
                    : null;
                float viewportWidth = GetRustViewportWidth();
                bool darkMode = MenuToggleDarkMode.IsChecked;
                bool blockAds = MenuToggleAdBlock.IsChecked;
                bool blockImages = MenuToggleImgBlock.IsChecked;

                var page = await RustEngineBridge.FetchAndRenderAsync(
                    targetUrl,
                    ua,
                    viewportWidth,
                    darkMode,
                    20,
                    blockAds,
                    blockImages,
                    false,
                    method,
                    postBody,
                    requestContentType,
                    MenuToggleRustJs.IsChecked,
                    ct);
                if (seq != _rustNavSeq || ct.IsCancellationRequested) return;

                string finalUrl = string.IsNullOrWhiteSpace(page.Url) ? targetUrl : page.Url;
                if (_activeTab != null)
                {
                    _activeTab.RenderedPage = page;
                    _activeTab.Title = string.IsNullOrWhiteSpace(page.Title) ? finalUrl : page.Title;
                    _activeTab.Url = finalUrl;
                }

                HistoryService.RecordVisit(_history, _activeTab?.Title ?? finalUrl, finalUrl);
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
                    _isRustLoading = false;
                    BottomRefreshIcon.Text = "\uE149";
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
            _rustFindMatchIndices.Clear();
            _rustFindActiveSlot = -1;
            ClearRustImageCache();

            bool dark = MenuToggleDarkMode.IsChecked;
            Color clear = dark ? Color.FromArgb(255, 18, 18, 18) : Color.FromArgb(255, 250, 250, 250);
            RustBrowserScrollViewer.Background = new SolidColorBrush(clear);
            RustVirtualCanvas.ClearColor = clear;

            float scale = Math.Max(0.5f, _currentZoom / 100.0f);
            RustVirtualCanvas.Width = Math.Max(280, page.ContentWidth * scale);
            RustVirtualCanvas.Height = Math.Max(400, page.ContentHeight * scale);
            RustBrowserScrollViewer.ChangeView(0, 0, null, true);
            RustVirtualCanvas.Invalidate();

            CheckRustJsFallbackHint(page);
            if (!MenuToggleImgBlock.IsChecked)
            {
                LoadRustPageImagesAsync(page, _rustNavSeq, _rustNavCts?.Token ?? CancellationToken.None);
            }
            if (!string.IsNullOrEmpty(_rustFindQuery))
            {
                UpdateRustFindMatches(0);
            }
        }

        private void CheckRustJsFallbackHint(RustRenderPage page)
        {
            if (page == null || page.IsReaderMode)
            {
                RustFallbackBanner.Visibility = Visibility.Collapsed;
                return;
            }

            int textChars = 0;
            bool mentionsJs = false;
            foreach (var item in page.Items)
            {
                if ((item.Kind == RenderItemKind.Text || item.Kind == RenderItemKind.Link) && !string.IsNullOrWhiteSpace(item.Text))
                {
                    textChars += item.Text.Length;
                    if (item.Text.IndexOf("enable javascript", StringComparison.OrdinalIgnoreCase) >= 0 ||
                        item.Text.IndexOf("javascript is required", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        mentionsJs = true;
                    }
                }
            }

            if (mentionsJs || (textChars < 24 && page.Items.Count <= 3))
            {
                RustFallbackBanner.Visibility = Visibility.Visible;
            }
            else
            {
                RustFallbackBanner.Visibility = Visibility.Collapsed;
            }
        }

        private static bool IsWebPBytes(byte[] bytes)
        {
            return bytes != null &&
                   bytes.Length >= 12 &&
                   bytes[0] == 'R' && bytes[1] == 'I' && bytes[2] == 'F' && bytes[3] == 'F' &&
                   bytes[8] == 'W' && bytes[9] == 'E' && bytes[10] == 'B' && bytes[11] == 'P';
        }

        private async Task<CanvasBitmap> LoadBitmapFromBytesAsync(byte[] bytes, ulong navSeq, CancellationToken ct, RustRenderPage page)
        {
            if (bytes == null || bytes.Length == 0 || bytes.Length > 2 * 1024 * 1024)
            {
                return null;
            }
            try
            {
                ICanvasResourceCreator creator = (ICanvasResourceCreator)RustVirtualCanvas.Device ?? CanvasDevice.GetSharedDevice();

                if (IsWebPBytes(bytes))
                {
                    byte[] rgba = RustEngineBridge.DecodeWebPToRgba(bytes, out int w, out int h);
                    if (rgba == null || w <= 0 || h <= 0) return null;
                    if (ct.IsCancellationRequested || navSeq != _rustNavSeq || _activeRustPage != page) return null;
                    return CanvasBitmap.CreateFromBytes(creator, rgba, w, h, DirectXPixelFormat.R8G8B8A8UIntNormalized);
                }

                using (var ras = new InMemoryRandomAccessStream())
                {
                    using (var writer = new DataWriter(ras.GetOutputStreamAt(0)))
                    {
                        writer.WriteBytes(bytes);
                        await writer.StoreAsync();
                        await writer.FlushAsync();
                        writer.DetachStream();
                    }
                    ras.Seek(0);
                    var bmp = await CanvasBitmap.LoadAsync(creator, ras);
                    if (ct.IsCancellationRequested || navSeq != _rustNavSeq || _activeRustPage != page)
                    {
                        bmp.Dispose();
                        return null;
                    }
                    return bmp;
                }
            }
            catch
            {
                return null;
            }
        }

        private static byte[] TryDecodeDataImageBase64(string dataUri)
        {
            if (string.IsNullOrEmpty(dataUri) ||
                !dataUri.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
            int commaIdx = dataUri.IndexOf(',');
            if (commaIdx <= 0) return null;
            string meta = dataUri.Substring(0, commaIdx);
            if (meta.IndexOf(";base64", StringComparison.OrdinalIgnoreCase) < 0 ||
                meta.IndexOf("svg", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return null;
            }
            try
            {
                return Convert.FromBase64String(dataUri.Substring(commaIdx + 1));
            }
            catch
            {
                return null;
            }
        }

        private async void LoadRustPageImagesAsync(RustRenderPage page, ulong navSeq, CancellationToken ct)
        {
            if (page == null) return;

            var allSources = page.Items
                .Where(i => i.Kind == RenderItemKind.Image && !string.IsNullOrWhiteSpace(i.Href))
                .Select(i => i.Href)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(32)
                .ToList();

            if (allSources.Count == 0) return;

            bool anyInlineLoaded = false;
            foreach (var src in allSources)
            {
                if (ct.IsCancellationRequested || navSeq != _rustNavSeq || _activeRustPage != page) return;
                byte[] inlineBytes = TryDecodeDataImageBase64(src);
                if (inlineBytes != null)
                {
                    var bmp = await LoadBitmapFromBytesAsync(inlineBytes, navSeq, ct, page);
                    if (bmp != null)
                    {
                        _rustImageCache[src] = bmp;
                        anyInlineLoaded = true;
                    }
                }
            }
            if (anyInlineLoaded && !ct.IsCancellationRequested && navSeq == _rustNavSeq && _activeRustPage == page)
            {
                RustVirtualCanvas.Invalidate();
            }

            var imageUrls = allSources
                .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                            u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (imageUrls.Count == 0) return;

            string ua = EngineOptimizer.UserAgents.TryGetValue(_currentUa, out var profileUa)
                ? profileUa
                : null;

            const int batchSize = 4;
            for (int i = 0; i < imageUrls.Count; i += batchSize)
            {
                if (ct.IsCancellationRequested || navSeq != _rustNavSeq || _activeRustPage != page) return;

                var batch = imageUrls.Skip(i).Take(batchSize).ToList();
                var responses = await Task.WhenAll(batch.Select(imgUrl => RustEngineBridge.SendHttpRequestAsync(new WebEngineRequest
                {
                    Url = imgUrl,
                    Method = "GET",
                    UserAgent = ua,
                    Referer = page.Url,
                    Accept = "image/jpeg,image/png,image/gif,image/bmp,image/webp;q=0.8,*/*;q=0.5",
                    TimeoutSeconds = 8,
                    MaxResponseBytes = 2 * 1024 * 1024,
                    UseRustTlsEngine = true,
                    CancellationToken = ct
                })));

                bool anyLoaded = false;
                for (int b = 0; b < batch.Count; b++)
                {
                    if (ct.IsCancellationRequested || navSeq != _rustNavSeq || _activeRustPage != page) return;
                    var resp = responses[b];
                    if (resp == null || !resp.IsSuccess) continue;

                    var bmp = await LoadBitmapFromBytesAsync(resp.RawBytes, navSeq, ct, page);
                    if (bmp != null)
                    {
                        _rustImageCache[batch[b]] = bmp;
                        anyLoaded = true;
                    }
                }

                if (anyLoaded && !ct.IsCancellationRequested && navSeq == _rustNavSeq && _activeRustPage == page)
                {
                    RustVirtualCanvas.Invalidate();
                }
            }
        }

        private CanvasTextFormat GetCachedTextFormat(float fontSize, bool bold, bool italic, bool center = false)
        {
            int sizeKey = (int)Math.Round(fontSize * 10);
            int key = (sizeKey << 3) | (bold ? 1 : 0) | (italic ? 2 : 0) | (center ? 4 : 0);

            if (!_textFormats.TryGetValue(key, out var fmt))
            {
                fmt = new CanvasTextFormat
                {
                    FontFamily = "Segoe UI",
                    FontSize = Math.Max(10f, fontSize),
                    FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
                    FontStyle = italic ? FontStyle.Italic : FontStyle.Normal,
                    HorizontalAlignment = center ? CanvasHorizontalAlignment.Center : CanvasHorizontalAlignment.Left,
                    VerticalAlignment = center ? CanvasVerticalAlignment.Center : CanvasVerticalAlignment.Top,
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
            bool hasFind = !string.IsNullOrEmpty(_rustFindQuery);
            int activeItemIdx = (_rustFindActiveSlot >= 0 && _rustFindActiveSlot < _rustFindMatchIndices.Count)
                ? _rustFindMatchIndices[_rustFindActiveSlot]
                : -1;

            foreach (var region in args.InvalidatedRegions)
            {
                using (var ds = sender.CreateDrawingSession(region))
                {
                    for (int idx = 0; idx < _activeRustPage.Items.Count; idx++)
                    {
                        var item = _activeRustPage.Items[idx];
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
                        else if (item.Kind == RenderItemKind.Image)
                        {
                            if (!string.IsNullOrEmpty(item.Href) &&
                                _rustImageCache.TryGetValue(item.Href, out var bmp) &&
                                bmp != null)
                            {
                                Size bmpSize = bmp.Size;
                                if (bmpSize.Width > 0 && bmpSize.Height > 0)
                                {
                                    double ratio = Math.Min(w / bmpSize.Width, h / bmpSize.Height);
                                    double drawW = bmpSize.Width * ratio;
                                    double drawH = bmpSize.Height * ratio;
                                    double drawX = x + (w - drawW) / 2.0;
                                    double drawY = y + (h - drawH) / 2.0;
                                    ds.DrawImage(bmp, new Rect(drawX, drawY, drawW, drawH));
                                }
                            }
                            else
                            {
                                ds.FillRectangle(rect, item.BackgroundColor);
                                ds.DrawRectangle(rect, item.BorderColor, 1.0f);
                                if (!string.IsNullOrEmpty(item.Text))
                                {
                                    var fmt = GetCachedTextFormat(item.FontSize * scale, false, true, true);
                                    ds.DrawText(item.Text, rect, item.ForegroundColor, fmt);
                                }
                            }
                        }
                        else if (item.Kind == RenderItemKind.Input)
                        {
                            string inputType = item.GetExtraPart(2);
                            if (inputType == "checkbox")
                            {
                                float radius = 4.0f * scale;
                                Color boxBg = item.IsChecked ? item.ForegroundColor : item.BackgroundColor;
                                Color boxBorder = item.IsChecked ? item.ForegroundColor : item.BorderColor;
                                ds.FillRoundedRectangle(rect, radius, radius, boxBg);
                                ds.DrawRoundedRectangle(rect, radius, radius, boxBorder, 1.4f);
                                if (item.IsChecked)
                                {
                                    var chkFmt = GetCachedTextFormat(item.FontSize * scale, true, false, true);
                                    ds.DrawText("✓", rect, Colors.White, chkFmt);
                                }
                            }
                            else if (inputType == "radio")
                            {
                                float cx = x + w * 0.5f;
                                float cy = y + h * 0.5f;
                                float rOuter = Math.Min(w, h) * 0.45f;
                                Color ringCol = item.IsChecked ? item.ForegroundColor : item.BorderColor;
                                ds.FillEllipse(cx, cy, rOuter, rOuter, item.BackgroundColor);
                                ds.DrawEllipse(cx, cy, rOuter, rOuter, ringCol, 1.5f);
                                if (item.IsChecked)
                                {
                                    ds.FillEllipse(cx, cy, rOuter * 0.52f, rOuter * 0.52f, item.ForegroundColor);
                                }
                            }
                            else
                            {
                                float radius = 4.0f * scale;
                                ds.FillRoundedRectangle(rect, radius, radius, item.BackgroundColor);
                                ds.DrawRoundedRectangle(rect, radius, radius, item.BorderColor, 1.2f);

                                string placeholder = item.GetExtraPart(1);
                                string displayStr;
                                bool isPlaceholder = false;
                                if (inputType == "select")
                                {
                                    displayStr = "▾ " + item.GetSelectDisplayLabel();
                                }
                                else
                                {
                                    displayStr = item.Text;
                                    isPlaceholder = string.IsNullOrEmpty(displayStr);
                                    if (isPlaceholder)
                                    {
                                        displayStr = string.IsNullOrEmpty(placeholder) ? "Chạm để nhập..." : placeholder;
                                    }
                                    else if (item.IsPassword)
                                    {
                                        displayStr = new string('•', Math.Min(displayStr.Length, 24));
                                    }
                                }

                                Color fg = isPlaceholder ? Color.FromArgb(255, 138, 143, 150) : item.ForegroundColor;
                                var fmt = GetCachedTextFormat(item.FontSize * scale, false, false, false);
                                var textRect = new Rect(x + 8 * scale, y + 8 * scale, Math.Max(10, w - 16 * scale), Math.Max(10, h - 12 * scale));
                                ds.DrawText(displayStr, textRect, fg, fmt);
                            }
                        }
                        else if (item.Kind == RenderItemKind.Button)
                        {
                            float radius = 6.0f * scale;
                            ds.FillRoundedRectangle(rect, radius, radius, item.BackgroundColor);
                            if (item.BorderColor.A > 0)
                            {
                                ds.DrawRoundedRectangle(rect, radius, radius, item.BorderColor, 1.0f);
                            }
                            if (!string.IsNullOrEmpty(item.Text))
                            {
                                var fmt = GetCachedTextFormat(item.FontSize * scale, true, false, true);
                                ds.DrawText(item.Text, rect, item.ForegroundColor, fmt);
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

                            if (hasFind && item.Text.IndexOf(_rustFindQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                Color hlColor = (idx == activeItemIdx)
                                    ? Color.FromArgb(190, 255, 152, 0)
                                    : Color.FromArgb(95, 255, 235, 59);
                                ds.FillRectangle(rect, hlColor);
                            }

                            var fmt = GetCachedTextFormat(item.FontSize * scale, item.Bold, item.Italic, item.CenterText);
                            var textBounds = new Rect(x, y, w + 6, h + 4);
                            if (item.Underline && h > item.FontSize * scale * 1.9f)
                            {
                                using (var layout = new CanvasTextLayout(sender, item.Text, fmt, (float)textBounds.Width, (float)textBounds.Height))
                                {
                                    layout.SetUnderline(0, item.Text.Length, true);
                                    ds.DrawTextLayout(layout, x, y, item.ForegroundColor);
                                }
                            }
                            else
                            {
                                ds.DrawText(item.Text, textBounds, item.ForegroundColor, fmt);
                                if (item.Underline)
                                {
                                    float lineY = y + Math.Max(2f, h - 2.0f * scale);
                                    ds.DrawLine(x, lineY, x + w, lineY, item.ForegroundColor, Math.Max(1.0f, scale));
                                }
                            }
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
                    if (logicalX < item.X || logicalX > item.X + item.Width ||
                        logicalY < item.Y || logicalY > item.Y + item.Height)
                    {
                        continue;
                    }

                    if (item.Kind == RenderItemKind.Input)
                    {
                        string inputType = item.GetExtraPart(2);
                        if (inputType == "checkbox")
                        {
                            item.IsChecked = !item.IsChecked;
                            RustVirtualCanvas.Invalidate();
                            return;
                        }
                        if (inputType == "radio")
                        {
                            string radioName = item.GetExtraPart(0);
                            foreach (var other in _activeRustPage.Items)
                            {
                                if (other.Kind == RenderItemKind.Input &&
                                    other.FormId == item.FormId &&
                                    other.GetExtraPart(2) == "radio" &&
                                    string.Equals(other.GetExtraPart(0), radioName, StringComparison.Ordinal))
                                {
                                    other.IsChecked = (other == item);
                                }
                            }
                            RustVirtualCanvas.Invalidate();
                            return;
                        }
                        if (inputType == "select")
                        {
                            var options = item.GetSelectOptions();
                            if (options.Count > 0)
                            {
                                OpenRustSelectPicker(item, options);
                                return;
                            }
                        }
                        OpenRustInputEditor(item);
                        return;
                    }

                    if (item.Kind == RenderItemKind.Button)
                    {
                        string btnType = item.GetExtraPart(2);
                        if (btnType == "reset")
                        {
                            ResetRustForm(item.FormId);
                            return;
                        }
                        if (item.FormId >= 0)
                        {
                            SubmitRustForm(item.FormId, item);
                            return;
                        }
                    }

                    if (item.Kind == RenderItemKind.Image)
                    {
                        string linkHref = item.GetExtraPart(0);
                        if (!string.IsNullOrEmpty(linkHref))
                        {
                            if (linkHref.StartsWith("#"))
                            {
                                ScrollToRustAnchor(linkHref, scale);
                                return;
                            }
                            if (!linkHref.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                            {
                                NavigateBrowser(linkHref);
                                return;
                            }
                        }
                    }

                    if (item.Kind == RenderItemKind.Link && !string.IsNullOrEmpty(item.Href))
                    {
                        if (item.Href.StartsWith("#"))
                        {
                            ScrollToRustAnchor(item.Href, scale);
                            return;
                        }
                        if (!item.Href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                        {
                            NavigateBrowser(item.Href);
                            return;
                        }
                    }
                }
            }

            ShowBottomNavBar();
        }

        private void ScrollToRustAnchor(string anchorHref, float scale)
        {
            if (_activeRustPage == null) return;
            string frag = anchorHref.TrimStart('#').Trim();
            if (string.IsNullOrEmpty(frag) || frag.Equals("top", StringComparison.OrdinalIgnoreCase))
            {
                RustBrowserScrollViewer.ChangeView(null, 0, null, false);
                return;
            }

            string needle = frag.Replace('-', ' ').Replace('_', ' ');
            foreach (var target in _activeRustPage.Items)
            {
                if ((target.Kind == RenderItemKind.Text || target.Kind == RenderItemKind.Link) &&
                    !string.IsNullOrEmpty(target.Text) &&
                    target.Text.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    double targetY = Math.Max(0, target.Y * scale - 24);
                    RustBrowserScrollViewer.ChangeView(null, targetY, null, false);
                    return;
                }
            }
        }

        private void OpenRustSelectPicker(RenderDisplayItem selectItem, List<RustSelectOption> options)
        {
            _editingRustInput = selectItem;
            string name = selectItem.GetExtraPart(0);
            RustInputPromptLabel.Text = !string.IsNullOrEmpty(name) ? $"Chọn giá trị ({name}):" : "Chọn giá trị:";
            RustInputEditorBox.Visibility = Visibility.Collapsed;
            RustInputPasswordBox.Visibility = Visibility.Collapsed;
            RustInputDoneButton.Visibility = Visibility.Collapsed;
            RustInputSubmitButton.Visibility = Visibility.Collapsed;
            RustSelectListView.ItemsSource = options;
            RustSelectListView.Visibility = Visibility.Visible;
            RustInputOverlay.Visibility = Visibility.Visible;
        }

        private void RustSelectListView_ItemClick(object sender, ItemClickEventArgs e)
        {
            if (_editingRustInput != null && e.ClickedItem is RustSelectOption opt)
            {
                _editingRustInput.Text = opt.Value;
                RustVirtualCanvas.Invalidate();
            }
            RustInputOverlay.Visibility = Visibility.Collapsed;
            RustSelectListView.Visibility = Visibility.Collapsed;
            _editingRustInput = null;
        }

        private void OpenRustInputEditor(RenderDisplayItem inputItem)
        {
            _editingRustInput = inputItem;
            string name = inputItem.GetExtraPart(0);
            string placeholder = inputItem.GetExtraPart(1);
            RustInputPromptLabel.Text = !string.IsNullOrEmpty(placeholder)
                ? placeholder
                : (!string.IsNullOrEmpty(name) ? $"Nhập giá trị ({name}):" : "Nhập nội dung:");

            RustSelectListView.Visibility = Visibility.Collapsed;
            RustInputDoneButton.Visibility = Visibility.Visible;
            RustInputSubmitButton.Visibility = Visibility.Visible;

            if (inputItem.IsPassword)
            {
                RustInputEditorBox.Visibility = Visibility.Collapsed;
                RustInputPasswordBox.Visibility = Visibility.Visible;
                RustInputPasswordBox.Password = inputItem.Text ?? string.Empty;
                RustInputOverlay.Visibility = Visibility.Visible;
                RustInputPasswordBox.Focus(FocusState.Programmatic);
            }
            else
            {
                RustInputPasswordBox.Visibility = Visibility.Collapsed;
                RustInputEditorBox.Visibility = Visibility.Visible;
                RustInputEditorBox.AcceptsReturn = inputItem.IsMultiline;
                RustInputEditorBox.Text = inputItem.Text ?? string.Empty;
                RustInputOverlay.Visibility = Visibility.Visible;
                RustInputEditorBox.Focus(FocusState.Programmatic);
                RustInputEditorBox.SelectAll();
            }
        }

        private void ApplyRustInputEditorValue()
        {
            if (_editingRustInput == null) return;
            _editingRustInput.Text = _editingRustInput.IsPassword
                ? RustInputPasswordBox.Password
                : RustInputEditorBox.Text;
            RustInputOverlay.Visibility = Visibility.Collapsed;
            RustVirtualCanvas.Invalidate();
        }

        private void RustInputDone_Click(object sender, RoutedEventArgs e)
        {
            ApplyRustInputEditorValue();
            _editingRustInput = null;
        }

        private void RustInputSubmit_Click(object sender, RoutedEventArgs e)
        {
            var target = _editingRustInput;
            ApplyRustInputEditorValue();
            _editingRustInput = null;
            if (target != null)
            {
                SubmitRustForm(target.FormId, null, target);
            }
        }

        private void RustInputCancel_Click(object sender, RoutedEventArgs e)
        {
            RustInputOverlay.Visibility = Visibility.Collapsed;
            RustSelectListView.Visibility = Visibility.Collapsed;
            _editingRustInput = null;
        }

        private void RustInputEditor_KeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key == VirtualKey.Enter && (_editingRustInput == null || !_editingRustInput.IsMultiline))
            {
                e.Handled = true;
                RustInputSubmit_Click(sender, e);
            }
        }

        private void ResetRustForm(int formId)
        {
            if (_activeRustPage == null) return;
            foreach (var item in _activeRustPage.Items)
            {
                if (item.Kind == RenderItemKind.Input && item.FormId == formId)
                {
                    string inputType = item.GetExtraPart(2);
                    if (inputType == "checkbox" || inputType == "radio")
                    {
                        item.IsChecked = false;
                    }
                    else
                    {
                        item.Text = string.Empty;
                    }
                }
            }
            RustVirtualCanvas.Invalidate();
        }

        private void SubmitRustForm(int formId, RenderDisplayItem submitBtn, RenderDisplayItem fallbackInput = null)
        {
            if (_activeRustPage == null) return;

            string actionMethod = submitBtn?.Href ?? fallbackInput?.Href ?? string.Empty;
            if (string.IsNullOrEmpty(actionMethod))
            {
                actionMethod = _activeRustPage.Items
                    .FirstOrDefault(i => i.FormId == formId && !string.IsNullOrEmpty(i.Href))?.Href ?? string.Empty;
            }

            string action = _activeTab?.Url ?? DefaultHomeUrl;
            string method = "GET";
            if (!string.IsNullOrEmpty(actionMethod))
            {
                int sep = actionMethod.LastIndexOf('|');
                if (sep > 0)
                {
                    action = actionMethod.Substring(0, sep);
                    method = actionMethod.Substring(sep + 1);
                }
                else
                {
                    action = actionMethod;
                }
            }

            var pairs = new List<string>();
            foreach (var hidden in _activeRustPage.HiddenInputs)
            {
                if (hidden.FormId == formId)
                {
                    string hName = hidden.GetExtraPart(0);
                    if (!string.IsNullOrEmpty(hName))
                    {
                        pairs.Add(Uri.EscapeDataString(hName) + "=" + Uri.EscapeDataString(hidden.Text ?? string.Empty));
                    }
                }
            }
            foreach (var item in _activeRustPage.Items)
            {
                if (item.Kind == RenderItemKind.Input && item.FormId == formId)
                {
                    string fName = item.GetExtraPart(0);
                    string fType = item.GetExtraPart(2);
                    if (fType == "checkbox" || fType == "radio")
                    {
                        if (item.IsChecked && !string.IsNullOrEmpty(fName))
                        {
                            string fVal = !string.IsNullOrEmpty(item.Text) ? item.Text : "on";
                            pairs.Add(Uri.EscapeDataString(fName) + "=" + Uri.EscapeDataString(fVal));
                        }
                    }
                    else if (!string.IsNullOrEmpty(fName))
                    {
                        pairs.Add(Uri.EscapeDataString(fName) + "=" + Uri.EscapeDataString(item.Text ?? string.Empty));
                    }
                }
            }
            if (submitBtn != null)
            {
                string bName = submitBtn.GetExtraPart(0);
                string bVal = submitBtn.GetExtraPart(1);
                if (!string.IsNullOrEmpty(bName))
                {
                    pairs.Add(Uri.EscapeDataString(bName) + "=" + Uri.EscapeDataString(bVal ?? string.Empty));
                }
            }

            string encoded = string.Join("&", pairs);
            if (method.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                if (_activeTab != null && !string.IsNullOrWhiteSpace(_activeTab.Url) && _activeTab.Url != NewTabHomeUrl)
                {
                    _activeTab.BackStack.Push(_activeTab.Url);
                    _activeTab.ForwardStack.Clear();
                    _activeTab.Url = action;
                }
                OmniUrlBox.Text = action;
                OmniLockIcon.Text = action.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "\uE72E" : "\uE7BA";
                LoadUrlWithRustEngineAsync(action, "POST", encoded, "application/x-www-form-urlencoded");
            }
            else
            {
                string sepChar = action.Contains("?") ? "&" : "?";
                string fullUrl = string.IsNullOrEmpty(encoded) ? action : action + sepChar + encoded;
                NavigateBrowser(fullUrl);
            }
        }

        private void RustSwitchToWebView_Click(object sender, RoutedEventArgs e)
        {
            MenuToggleRustEngine.IsChecked = false;
            SettingsService.UseRustBrowserEngine = false;
            if (_activeTab != null && !string.IsNullOrEmpty(_activeTab.Url) && _activeTab.Url != NewTabHomeUrl)
            {
                NavigateBrowser(_activeTab.Url, pushHistory: false);
            }
        }

        private void RustDismissFallback_Click(object sender, RoutedEventArgs e)
        {
            RustFallbackBanner.Visibility = Visibility.Collapsed;
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

        private void StopRustLoadingUi()
        {
            _rustNavSeq++;
            CancelPendingRustNav();
            _isRustLoading = false;
            BottomRefreshIcon.Text = "\uE149";
            BrowserProgressBar.Visibility = Visibility.Collapsed;
            BrowserProgressBar.IsIndeterminate = false;
        }

        private void OmniRefreshButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsRustEngineActive && _isRustLoading)
            {
                StopRustLoadingUi();
                ShowBottomNavBar();
                return;
            }

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
                                 RustViewportContainer.Visibility == Visibility.Visible;
            if (SettingsService.AutoHideBottomBar && isViewingPage)
            {
                _autoHideNavTimer.Start();
            }
        }

        private void HideBottomNavBar()
        {
            bool isViewingPage = BrowserWebView.Visibility == Visibility.Visible ||
                                 RustViewportContainer.Visibility == Visibility.Visible;
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
                        RustViewportContainer.Visibility = Visibility.Visible;
                        string ua = EngineOptimizer.UserAgents.TryGetValue(_currentUa, out var profileUa) ? profileUa : string.Empty;
                        var page = await RustEngineBridge.RenderHtmlAsync(
                            html,
                            item.Url,
                            GetRustViewportWidth(),
                            MenuToggleDarkMode.IsChecked,
                            string.Empty,
                            MenuToggleAdBlock.IsChecked,
                            MenuToggleImgBlock.IsChecked,
                            false,
                            string.Empty,
                            ua,
                            MenuToggleRustJs.IsChecked);
                        page.SourceHtml = html;
                        if (_activeTab != null) _activeTab.RenderedPage = page;
                        DisplayRustPage(page);
                    }
                    else
                    {
                        RustViewportContainer.Visibility = Visibility.Collapsed;
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

            if (IsRustEngineActive)
            {
                _rustFindQuery = q;
                UpdateRustFindMatches(index);
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

        private void UpdateRustFindMatches(int index)
        {
            _rustFindMatchIndices.Clear();
            _rustFindActiveSlot = -1;

            if (_activeRustPage == null || string.IsNullOrEmpty(_rustFindQuery))
            {
                FindCountText.Text = "0/0";
                RustVirtualCanvas.Invalidate();
                return;
            }

            for (int i = 0; i < _activeRustPage.Items.Count; i++)
            {
                var item = _activeRustPage.Items[i];
                if ((item.Kind == RenderItemKind.Text || item.Kind == RenderItemKind.Link || item.Kind == RenderItemKind.Button) &&
                    !string.IsNullOrEmpty(item.Text) &&
                    item.Text.IndexOf(_rustFindQuery, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    _rustFindMatchIndices.Add(i);
                }
            }

            int count = _rustFindMatchIndices.Count;
            if (count == 0)
            {
                FindCountText.Text = "0/0";
                RustVirtualCanvas.Invalidate();
                return;
            }

            int slot = index % count;
            if (slot < 0) slot += count;
            _rustFindActiveSlot = slot;
            FindCountText.Text = $"{slot + 1}/{count}";

            var matchItem = _activeRustPage.Items[_rustFindMatchIndices[slot]];
            float scale = Math.Max(0.5f, _currentZoom / 100.0f);
            double targetY = Math.Max(0, matchItem.Y * scale - 90);
            RustBrowserScrollViewer.ChangeView(null, targetY, null, false);
            RustVirtualCanvas.Invalidate();
        }

        private async void ExecuteFindClear()
        {
            _rustFindQuery = string.Empty;
            _rustFindMatchIndices.Clear();
            _rustFindActiveSlot = -1;
            if (IsRustEngineActive)
            {
                RustVirtualCanvas.Invalidate();
                return;
            }

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
            if (IsRustEngineActive && _activeTab?.RenderedPage?.SourceHtml != null)
            {
                _isRustReaderMode = !_isRustReaderMode;
                ReRenderCurrentRustHtmlAsync();
                return;
            }

            try
            {
                await BrowserWebView.InvokeScriptAsync("eval", new[] { EngineOptimizer.GetReaderModeScript() });
            }
            catch {}
        }

        private void Share_DataRequested(DataTransferManager sender, DataRequestedEventArgs args)
        {
            var req = args.Request;
            req.Data.Properties.Title = _activeTab?.Title ?? "Trang Web";
            string targetUrl = _activeTab?.Url;
            if (!string.IsNullOrWhiteSpace(targetUrl) &&
                Uri.TryCreate(targetUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
                 uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)))
            {
                req.Data.SetWebLink(uri);
            }
            else
            {
                req.Data.SetText(targetUrl ?? DefaultHomeUrl);
            }
        }

        private void ShareCurrentPage_Click(object sender, RoutedEventArgs e)
        {
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

            var savedInputs = new Dictionary<string, string>(StringComparer.Ordinal);
            var savedChecks = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var oldItem in _activeTab.RenderedPage.Items)
            {
                if (oldItem.Kind == RenderItemKind.Input)
                {
                    string fType = oldItem.GetExtraPart(2);
                    if (fType == "checkbox" || fType == "radio")
                    {
                        string chkKey = $"{oldItem.FormId}:{fType}:{oldItem.GetExtraPart(0)}:{oldItem.GetExtraPart(1)}";
                        savedChecks[chkKey] = oldItem.IsChecked;
                    }
                    else
                    {
                        string key = $"{oldItem.FormId}:{oldItem.GetExtraPart(0)}";
                        savedInputs[key] = oldItem.Text ?? string.Empty;
                    }
                }
            }

            string prevCss = _activeTab.RenderedPage.CombinedCss ?? string.Empty;
            string prevJs = _activeTab.RenderedPage.CombinedJs ?? string.Empty;
            string prevTransport = _activeTab.RenderedPage.TransportInfo;
            long prevElapsed = _activeTab.RenderedPage.ElapsedMs;
            int prevCssCount = _activeTab.RenderedPage.ExternalCssCount;
            string ua = EngineOptimizer.UserAgents.TryGetValue(_currentUa, out var profileUa) ? profileUa : string.Empty;

            var page = await RustEngineBridge.RenderHtmlAsync(
                _activeTab.RenderedPage.SourceHtml,
                _activeTab.Url ?? "",
                GetRustViewportWidth(),
                MenuToggleDarkMode.IsChecked,
                prevCss,
                MenuToggleAdBlock.IsChecked,
                MenuToggleImgBlock.IsChecked,
                _isRustReaderMode,
                prevJs,
                ua,
                MenuToggleRustJs.IsChecked && !_isRustReaderMode);

            foreach (var newItem in page.Items)
            {
                if (newItem.Kind == RenderItemKind.Input)
                {
                    string fType = newItem.GetExtraPart(2);
                    if (fType == "checkbox" || fType == "radio")
                    {
                        string chkKey = $"{newItem.FormId}:{fType}:{newItem.GetExtraPart(0)}:{newItem.GetExtraPart(1)}";
                        if (savedChecks.TryGetValue(chkKey, out var isChecked))
                        {
                            newItem.IsChecked = isChecked;
                        }
                    }
                    else
                    {
                        string key = $"{newItem.FormId}:{newItem.GetExtraPart(0)}";
                        if (savedInputs.TryGetValue(key, out var val))
                        {
                            newItem.Text = val;
                        }
                    }
                }
            }

            page.SourceHtml = _activeTab.RenderedPage.SourceHtml;
            page.CombinedCss = prevCss;
            page.CombinedJs = prevJs;
            page.TransportInfo = prevTransport;
            page.ElapsedMs = prevElapsed;
            page.ExternalCssCount = prevCssCount;
            page.IsReaderMode = _isRustReaderMode;
            _activeTab.RenderedPage = page;
            DisplayRustPage(page);
        }

        private void MenuToggleRustJs_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.UseRustJavaScript = MenuToggleRustJs.IsChecked;
            if (IsRustEngineActive && _activeTab?.RenderedPage?.SourceHtml != null)
            {
                ReRenderCurrentRustHtmlAsync();
            }
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

        private void MenuToggleAdBlock_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.AutoBlockAds = MenuToggleAdBlock.IsChecked;
            if (IsRustEngineActive && _activeTab?.RenderedPage?.SourceHtml != null)
            {
                ReRenderCurrentRustHtmlAsync();
            }
            else if (!IsRustEngineActive)
            {
                BrowserWebView.Refresh();
            }
        }

        private void MenuTogglePolyfills_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.AutoInjectPolyfills = MenuTogglePolyfills.IsChecked;
            if (!IsRustEngineActive) BrowserWebView.Refresh();
        }

        private void MenuToggleImgBlock_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.AutoBlockImages = MenuToggleImgBlock.IsChecked;
            if (IsRustEngineActive && _activeTab?.RenderedPage?.SourceHtml != null)
            {
                ReRenderCurrentRustHtmlAsync();
            }
            else if (!IsRustEngineActive)
            {
                BrowserWebView.Refresh();
            }
        }

        private async void MenuToggleDarkMode_Click(object sender, RoutedEventArgs e)
        {
            SettingsService.DarkModeDefault = MenuToggleDarkMode.IsChecked;
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
            if (_isApplyingSettings) return;
            if (SettingsSearchCombo.SelectedItem is ComboBoxItem item && item.Content != null)
            {
                SettingsService.SearchEngine = item.Content.ToString();
            }
        }

        private void SettingsAutoHideToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings) return;
            SettingsService.AutoHideBottomBar = SettingsAutoHideToggle.IsOn;
            if (!SettingsService.AutoHideBottomBar) ShowBottomNavBar();
        }

        private void SettingsAdBlockToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings) return;
            SettingsService.AutoBlockAds = SettingsAdBlockToggle.IsOn;
            MenuToggleAdBlock.IsChecked = SettingsAdBlockToggle.IsOn;
            if (IsRustEngineActive && _activeTab?.RenderedPage?.SourceHtml != null)
            {
                ReRenderCurrentRustHtmlAsync();
            }
        }

        private void SettingsPolyfillsToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings) return;
            SettingsService.AutoInjectPolyfills = SettingsPolyfillsToggle.IsOn;
            MenuTogglePolyfills.IsChecked = SettingsPolyfillsToggle.IsOn;
        }

        private void SettingsDarkModeToggle_Toggled(object sender, RoutedEventArgs e)
        {
            if (_isApplyingSettings) return;
            SettingsService.DarkModeDefault = SettingsDarkModeToggle.IsOn;
            MenuToggleDarkMode.IsChecked = SettingsDarkModeToggle.IsOn;
            if (IsRustEngineActive && _activeTab?.RenderedPage?.SourceHtml != null)
            {
                ReRenderCurrentRustHtmlAsync();
            }
        }

        private async void ClearAllBrowsingData_Click(object sender, RoutedEventArgs e)
        {
            HistoryService.ClearAll(_history);
            RustEngineBridge.ClearCookies();
            try
            {
                await WebView.ClearTemporaryWebDataAsync();
            }
            catch { }
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
