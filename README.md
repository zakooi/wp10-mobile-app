# wp10-mobile-app

Ứng dụng **Windows 10 Mobile (UWP)** — ưu tiên **web hiện đại** (HttpClient, TLS 1.2+, HTTP/2),
kèm phần **thông tin thiết bị** hợp lệ.

> **Cảnh báo nền tảng:** Windows 10 Mobile đã hết hỗ trợ/update (bản cuối 10.0.15254, 1709).
> Mã nguồn dùng mô hình UWP chuẩn; bạn build bằng Visual Studio 2017/2019 + Windows 10 SDK
> 10.0.19041 (MinVersion 10.0.14393). Xem `DECISIONS.md` để biết điểm lệch và giới hạn.

## Tính năng

- **Web** (`Services/WebService.cs`): gọi HTTP/HTTPS bằng `HttpClient`. Hỗ trợ TLS 1.2+/HTTP/2
  (Schannel hiện đại) → kết nối website hiện đại tốt hơn nhiều so với WP8. Hiển thị nội dung trả về,
  bắt lỗi rõ ràng, hủy khi quá thời gian chờ.
- **Hệ thống** (`Services/SystemInterop.cs`):
  - **Thông tin thiết bị** — `Package.Current`, `DeviceInformation`, phiên bản OS, kiến trúc (hợp lệ, cần ít quyền).
  - **Registry / FullTrust** — *KHÔNG khả thi trên mobile*: UWP bị giới hạn AppContainer, và
    Windows 10 Mobile không hỗ trợ desktop bridge `runFullTrust`. App chỉ giải thích điều này thay vì lỗi im lặng.

## Cấu trúc

```
src/WebSystemUwp/
  App.xaml / App.xaml.cs            Khởi động + Frame + navigation
  MainPage.xaml / .cs               Pivot "Web" + "Hệ thống"
  Services/WebService.cs            HttpClient (web hiện đại)
  Services/SystemInterop.cs         Thông tin thiết bị + đánh giá giới hạn AppContainer
  Package.appxmanifest              Identity + Capabilities (internetClient)
  WebSystemUwp.csproj               Project UWP (uap10.0.19041)
  Assets/                           Placeholder logos (thêm PNG khi build)
```

## Yêu cầu build

1. Windows 10 + Visual Studio 2017/2019 (workload **Universal Windows Platform development**).
   Cài **Windows 10 SDK 10.0.19041** (MinVersion 10.0.14393).
2. Mở `WebSystemUwp.csproj`. Thêm nội dung placeholder vào `Assets/` nếu build báo thiếu logo
   (`Square150x150Logo.png`, `Square44x44Logo.png`, `StoreLogo.png`, `SplashScreen.png`).
3. Build → file `.appx`. Deploy lên emulator W10M hoặc máy thật đã bật developer mode + sideload.

## Build bằng GitHub Actions (CI)

Thư mục `.github/workflows/build.yml` tự động build + đóng gói `.appx` khi bạn `git push` lên GitHub.
Runner dùng `windows-2019` (có sẵn VS 2019 + Windows 10 SDK 10.0.19041 — project đã retarget sang SDK này).

- **Kết quả:** tải file `.appx` về từ mục **Artifacts** của run.
- **Không chạy emulator W10M trên CI** (runner không hỗ trợ Hyper-V/nested virtualization). CI chỉ
  compile + đóng gói; bạn tự sideload ở máy local.
- **Cần Assets trước khi build:** bước đóng gói yêu cầu `Assets/*.png` tồn tại. Tạo placeholder bằng:
  ```powershell
  powershell -ExecutionPolicy Bypass -File scripts\generate-assets.ps1
  ```
  hoặc thay bằng logo thật.
- **Platform:** workflow build **matrix** `x86` + `ARM`. Gói **ARM** là gói cài được lên máy W10M thật
  (`WebSystemUwp-ARM`); gói `x86` chỉ để kiểm tra biên dịch.
- **Chữ ký:** gói được ký **tự động** nếu bạn đặt secrets `CERT_BASE64` (Base64 của `.pfx`) +
  `CERT_PASSPHRASE`. Nếu không đặt, gói là **unsigned** → cần ký tay hoặc dùng VS Deploy.

> **Lưu ý:** Windows 10 Mobile đã hết hỗ trợ (bản cuối 10.0.15254). CI build được, nhưng môi trường
> chạy thử cần bạn tự chuẩn bị (máy W10M thật hoặc emulator). Xem `DECISIONS.md`.

## Deploy lên thiết bị thật (Windows 10 Mobile)

Vì bạn có máy W10M thật (ARM), bạn cần **gói ARM đã ký chữ ký** và **bật Developer Mode** trên máy.

1. **Bật Developer Mode:** Cài đặt → Cập nhật & bảo mật → Dành cho nhà phát triển → **Chế độ nhà phát triển**.
   Bật USB debugging, cắm máy qua USB.

2. **Cách A — Deploy bằng Visual Studio (khuyên dùng):** Build **Platform = ARM** rồi **Build → Deploy**.
   VS tự ký bằng *developer certificate* và cài cert lên máy trong quá trình deploy — ít rắc rối nhất.

3. **Cách B — Dùng artifact từ GitHub Actions:**
   - Đặt secrets `CERT_BASE64` (Base64 của file `.pfx`) và `CERT_PASSPHRASE` → workflow tự ký gói ARM.
   - Tải artifact `WebSystemUwp-ARM`, cài cert vào máy (trusted), rồi sideload bằng `Add-AppxPackage`
     (PowerShell) hoặc công cụ deploy.
   - Nếu **không** đặt secrets, artifact là gói **unsigned** → dùng Cách A hoặc ký tay bằng
     `scripts\sign-appx.ps1`.

**Lưu ý:** Cần gói **ARM** cho máy phổ thông. Windows 10 Mobile đã hết hỗ trợ (bản cuối 10.0.15254/1709)
nhưng vẫn sideload được khi bật Developer Mode.

## Giới hạn cần biết

- **Web:** hoạt động tốt với website hiện đại. Vẫn cần kết nối mạng thật (emulator dùng mạng host khi bật).
- **Hệ thống:** registry/FullTrust **không mở được trên Windows 10 Mobile**. Đây là giới hạn thiết kế của
  Windows (AppContainer + không có desktop bridge trên phone), không phải bug.
