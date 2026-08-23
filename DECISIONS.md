# DECISIONS — wp10-mobile-app

Nhật ký quyết định và điểm lệch so với mục tiêu của người dùng.

## [2026-01-01] Phạm vi: chuyển sang Windows 10 Mobile (UWP), ưu tiên web hiện đại

- **Trạng thái:** Accepted
- **Quyết định:** Xây dựng ứng dụng **Windows 10 Mobile** dùng mô hình **UWP** (C#/XAML, Windows Runtime, `.appx`), thay cho WP8 Silverlight, vì người dùng ưu tiên **"web hiện đại, ít ràng buộc"**.
- **Lý do:** Windows 10 Mobile dùng tầng HTTP mới (Schannel hiện đại, TLS 1.2/1.3, HTTP/2) nên kết nối các website hiện đại gần như không còn vướng giới hạn TLS như WP8 (IE10). Bộ công cụ build cũng gần với nền tảng hiện đại hơn (Visual Studio 2017/2019 + Windows 10 SDK).

## [2026-01-01] Điểm lệch quan trọng #1 — Windows 10 Mobile cũng đã hết hỗ trợ

- **Trạng thái:** Accepted
- **Quyết định:** Vẫn bàn giao mã nguồn + cấu hình đầy đủ, nhưng không xác minh build/chạy trên máy này.
- **Lý do (điểm lệch):** Microsoft kết thúc hỗ trợ/update Windows 10 Mobile (bản cuối 10.0.15254, Fall Creators Update 1709) — người dùng cá nhân thực tế đã dừng từ ~2020. Emulator Windows 10 Mobile và các SDK lịch sử (14393, 16299) vẫn tải được, nhưng **không còn được phát triển**. Do đó không thể đảm bảo trải nghiệm hiện đại trọn vẹn; hệ sinh thái app Store cho W10M đã đóng.

## [2026-01-01] Điểm lệch quan trọng #2 — "Can thiệp hệ thống" bị chặn trên mobile

- **Trạng thái:** Accepted
- **Quyết định:** App không truy cập registry hệ thống (HKLM) được. Phần "Hệ thống" chỉ cung cấp **thông tin thiết bị hợp lệ** (không cần quyền cao) + mã minh họa trường hợp bị AppContainer chặn, kèm giải thích rõ.
- **Lý do (điểm lệch so với yêu cầu "can thiệp hệ thống" ban đầu):**
  - App UWP chạy trong **AppContainer** sandbox; API WinRT không cho đọc/ghi registry hệ thống.
  - Muốn FullTrust/registry cần bridge desktop (`runFullTrust`), mà **Windows 10 Mobile không hỗ trợ desktop bridge** — vì vậy về bản chất **không khả thi trên phone**.
  - Đưa `runFullTrust` vào package phone còn gây lỗi khi deploy/kiểm tra. Vì vậy giữ thiết kế **an toàn & trung thực**: web là trọng tâm, hệ thống chỉ dừng ở thông tin thiết bị + giải thích giới hạn.
- **Phương án thay thế đã cân nhắc:** thêm `<Capability Name="runFullTrust"/>` (chỉ chạy trên desktop Windows 10, không phải W10M) — đã loại vì trái mục tiêu "Windows 10 Mobile".

## [2026-01-01] Quyết định kỹ thuật — web dùng HttpClient UWP

- **Quyết định:** Dùng `System.Net.Http.HttpClient` (UWP). Mặc định đi qua Schannel hiện đại, hỗ trợ TLS 1.2+, HTTP/2.
- **Mục tiêu:** tối đa khả năng kết nối "web hiện tại". Lỗi mạng vẫn được ném rõ ràng (kể cả khi thiết bị offline hoặc site từ chối).
- **Lệch:** Không có — đây là lựa chọn đúng cho mục tiêu web hiện đại.

## [2026-01-01] Retarget sang Windows 10 SDK 10.0.19041 để build trên GitHub Actions

- **Trạng thái:** Accepted
- **Quyết định:** Chuyển `TargetFramework`/`TargetPlatformVersion` từ `uap10.0.16299` sang `uap10.0.19041`; giữ `TargetPlatformMinVersion=10.0.14393`.
- **Lý do:** Windows 10 SDK 10.0.19041 có sẵn trên runner `windows-2019` của GitHub Actions, tránh phải cài SDK lịch sử 16299 trong CI. App vẫn chạy được trên Windows 10 Mobile vì MinVersion 14393 <= 15254.
- **Phương án thay thế đã cân nhắc:** giữ SDK 16299 + cài SDK qua script trong workflow — phức tạp và dễ gãy hơn. Retarget an toàn hơn vì code chỉ dùng API phổ biến từ 14393 trở đi.
- **Lệch so với mục tiêu người dùng:** KHÔNG — chỉ đổi SDK biên dịch, không đổi phạm vi thiết bị chạy.

## [2026-01-01] Workflow GitHub Actions — build + đóng gói, không chạy emulator

- **Trạng thái:** Accepted
- **Quyết định:** Thêm `.github/workflows/build.yml` build + đóng gói `.appx` (Platform x86, unsigned) và upload artifact; KHÔNG chạy emulator W10M.
- **Lý do:** Runner cloud không hỗ trợ Hyper-V/nested virtualization cho emulator Windows 10 Mobile. CI chỉ compile/đóng gói; người dùng tự sideload ở máy local.
- **Phương án thay thế đã cân nhắc:** build ARM trên CI — toolset ARM/ARM64 có thể chưa cài trên runner và vẫn không chạy được emulator, nên giữ x86 cho mục đích kiểm tra biên dịch.
- **Lệch so với mục tiêu người dùng:** KHÔNG — đây là giới hạn nền tảng CI, không phải thay đổi yêu cầu.

## [2026-01-01] Cần Assets/*.png để đóng gói (không tự sinh trong workflow)

- **Trạng thái:** Accepted
- **Quyết định:** Workflow không tự sinh logo placeholder. Cung cấp `scripts/generate-assets.ps1` để người dùng tạo khi cần; nếu thiếu asset, bước đóng gói sẽ báo lỗi.
- **Lý do (điểm lệch):** Người dùng chọn CI "build + đóng gói + artifact" mà chưa yêu cầu tự sinh placeholder; giữ quyền quyết định asset cho người dùng. Việc thiếu asset là điều kiện cần đã nêu rõ trong README + workflow.
- **Lệch so với mục tiêu người dùng:** KHÔNG so với yêu cầu CI đã chọn.

## [2026-01-01] Người dùng có thiết bị thật → build ARM + ký appx tùy chọn

- **Trạng thái:** Accepted
- **Quyết định:** Workflow build **matrix `x86` + `ARM`**; thêm bước **ký appx tùy chọn** (dùng secrets `CERT_BASE64` + `CERT_PASSPHRASE`, gọi `scripts/sign-appx.ps1` qua signtool của Windows SDK).
- **Lý do:** Windows 10 Mobile dùng vi xử lý **ARM**, nên phải có gói ARM đã ký mới sideload được lên máy thật. Ký tự động giúp tải artifact về là cài được ngay.
- **Phương án thay thế đã cân nhắc:** chỉ build x86 (an toàn hơn nhưng không cài được lên máy thật) — đã loại vì người dùng có thiết bị. Không ký trong CI nếu không có secrets — đó là hành vi mặc định, không ép người dùng tạo cert.
- **Lệch so với mục tiêu người dùng:** KHÔNG — ngược lại, đáp ứng đúng nhu cầu "có thiết bị thật để test".

## [2026-01-01] Ghi chú: chưa push được lên GitHub từ môi trường thi hành

- **Trạng thái:** Accepted
- **Quyết định:** Repo đã `git init` + commit (nhánh `feature/uwp-app`, commit `09b658c`), nhưng **chưa push** lên GitHub.
- **Lý do:** Môi trường sandbox chặn TLS ra ngoài (`Invoke-RestMethod` và `curl.exe`/Schannel đều báo `SEC_E_NO_CREDENTIALS` khi bắt tay TLS với `api.github.com`). Đây là giới hạn **môi trường thi hành**, không phải lỗi mã nguồn hay token.
- **Lệch so với mục tiêu người dùng:** CÓ — người dùng yêu cầu upload lên GitHub; bị chặn giữa chừng. Người dùng cần tự push (hướng dẫn trong README/phản hồi), hoặc chạy trên máy có kết nối TLS bình thường.

