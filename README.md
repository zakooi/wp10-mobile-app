# wp10-mobile-app (WebSystem Chrome)

Ứng dụng trình duyệt **Windows 10 Mobile (UWP)** kiến trúc kép: kết hợp **WebView** hệ thống với lõi **Rust Native Browser Core (`websystem_core.dll`) + Win2D (`CanvasVirtualControl`)** để vượt qua giới hạn TLS/HTML/JS cũ trên Windows 10 Mobile (bản cuối 10.0.15254 / 1709).

> **Lưu ý nền tảng:** Windows 10 Mobile đã hết hỗ trợ từ Microsoft. Mã nguồn target Windows 10 SDK `10.0.19041.0` (`MinVersion=10.0.14393.0`) để build được trên Visual Studio 2019/2022 và GitHub Actions (`windows-2022`), trong khi vẫn cài được lên máy Windows 10 Mobile ARM thật.

## Kiến trúc & Tính năng chính

- **Lõi Rust (`src/websystem_core/`):**
  - **HTTP/TLS 1.3 (`http_client.rs`):** `ureq` + `rustls` + `rustls-rustcrypto` + `WebPKI` + cookie jar trong phiên, hỗ trợ HTTP/HTTPS hiện đại độc lập với Schannel cũ của phone.
  - **JavaScript Sandbox (`js_engine.rs`):** nhúng `boa_engine` (ES6+) với DOM sandbox, `DocumentFragment`, `addEventListener`/`click()`, `setTimeout`/`clearTimeout`, `document.cookie`, `URL`/`URLSearchParams`, dynamic inline `<script>`, giới hạn vòng lặp chống treo máy 1 GB RAM.
  - **HTML/CSS Layout (`layout_engine.rs`):** `scraper` (`html5ever`) + `taffy` (Flexbox/Block), parser CSS (class/id/tag, combinator `>`/descendant, `[attr]`, `@media`, `rgba()`), Reader Mode, chặn quảng cáo/cookie banner, form controls (`input`, `textarea`, `checkbox`, `radio`, `select`), xuất display list nhị phân `WSR2`.
- **Ứng dụng UWP (`src/WebSystemUwp/`):**
  - Giao diện kiểu Chrome tối ưu màn hình nhỏ Lumia: đa tab, Omnibox, thanh điều hướng tự ẩn, nút Dừng/Tải lại, Bookmarks, Lịch sử, Lưu trang Offline, Tìm trong trang, Dark Mode, Zoom, User-Agent switcher.
  - **Win2D Direct2D Renderer (`MainPage.xaml.cs`, `RustEngineBridge.cs`):** vẽ `WSR2` trên `CanvasVirtualControl`, tải ảnh bất đồng bộ (HTTP/HTTPS + `data:image/...;base64`), tương tác link/anchor/form.
  - **Công cụ & Chẩn đoán (`DeviceService.cs`, `DiagnosticsService.cs`, `WebService.cs`):** xem thông tin máy, RAM/Pin/mạng và test request trực tiếp qua Rust TLS hoặc `HttpClient`.

## Cấu trúc thư mục

```text
src/
  websystem_core/
    Cargo.toml                 Cấu hình crate cdylib (ureq, rustls, scraper, taffy, boa_engine)
    src/lib.rs                 FFI C-ABI (ws_core_*, ws_http_*, ws_render_html_v2, ...)
    src/http_client.rs         HTTP/TLS 1.3 + cookie jar
    src/js_engine.rs           Boa ES6+ DOM sandbox
    src/layout_engine.rs       HTML5/CSS parser + Taffy layout + WSR2 serializer
  WebSystemUwp/
    MainPage.xaml / .cs        UI Chrome + Win2D renderer + tương tác form/tab
    Services/RustEngineBridge.cs   P/Invoke sang websystem_core.dll & giải mã WSR2
    Services/RustRenderModels.cs   Display list models
    Services/WebService.cs         Điều phối Rust TLS / Schannel HttpClient
    Services/DeviceService.cs, DiagnosticsService.cs, CacheService.cs, ...
    WebSystemUwp.csproj        Project UWP (uap10.0.19041)
scripts/
  build-rust-core.ps1          Build websystem_core.dll (ARM/x86/x64) qua Rust Nightly
  generate-assets.ps1          Sinh placeholder PNG vào Assets/ khi cần
  sign-appx.ps1                Ký .appx bằng signtool
.github/workflows/build.yml    CI chạy Rust unit tests, build ARM + x86 .appx, tạo GitHub Release
```

## Build & Deploy

### 1. Build tự động bằng GitHub Actions (khuyên dùng)
Khi push lên `main` (hoặc tag `v*`):
- Chạy `cargo test --lib` cho `websystem_core` trên `x86`.
- Build `websystem_core.dll` (Rust Nightly `-Z build-std=std,panic_abort`) và đóng gói `.appx` cho **ARM** + **x86**.
- Ký gói `.appx` (dùng secrets `CERT_BASE64` + `CERT_PASSPHRASE`, hoặc tự tạo test certificate `WebSystemUwp.cer`).
- Đóng gói release `test-build` gồm `WebSystemUwp_ARM.appx`, `WebSystemUwp.cer` và các gói dependency.

### 2. Build local trên Windows
Yêu cầu: Windows 10/11, Visual Studio 2019/2022 (workload **UWP development** + C++ ARM tools + Windows 10 SDK `10.0.19041.0`), và Rust Nightly có component `rust-src`:

```powershell
powershell -ExecutionPolicy Bypass -File scripts\build-rust-core.ps1 -Platform ARM -Configuration Release
```

Sau đó mở `src\WebSystemUwp\WebSystemUwp.csproj` trong Visual Studio, chọn `ARM | Release` và Deploy lên máy Windows 10 Mobile đã bật **Developer Mode**.

## Giới hạn hiện tại

- **Chưa test runtime trực tiếp trên máy ARM thật trong CI:** CI chỉ xác minh biên dịch và chạy unit test trên `x86`.
- **JS là static pass lúc tải trang:** `fetch` bị vô hiệu trong layout pass, chưa hỗ trợ SPA/DOM event tương tác liên tục sau khi trang đã render xong.
- **AppContainer trên phone:** Không thể đọc/ghi registry hệ thống hay dùng `runFullTrust` trên Windows 10 Mobile.
