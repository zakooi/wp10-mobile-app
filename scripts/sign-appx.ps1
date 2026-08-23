# Ký một file .appx bằng cert .pfx.
# Dùng cho CI (bước "Sign .appx") hoặc chạy tay ở local:
#   powershell -ExecutionPolicy Bypass -File scripts\sign-appx.ps1 `
#       -AppxPath path\to\app.appx -PfxPath path\to\cert.pfx -Password yourpass
param(
    [Parameter(Mandatory = $true)][string]$AppxPath,
    [Parameter(Mandatory = $true)][string]$PfxPath,
    [Parameter(Mandatory = $true)][string]$Password
)

$ErrorActionPreference = 'Stop'

# Tìm signtool trong Windows SDK.
$signtool = Get-ChildItem "C:\Program Files (x86)\Windows Kits\10\bin" -Recurse -Filter signtool.exe -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending |
    Select-Object -First 1 -ExpandProperty FullName

if (-not $signtool) {
    throw "Không tìm thấy signtool.exe dưới 'C:\Program Files (x86)\Windows Kits\10\bin'."
}

if (-not (Test-Path $AppxPath)) {
    throw "Không tìm thấy appx: $AppxPath"
}

& $signtool sign /f $PfxPath /p $Password /fd SHA256 /a $AppxPath
if ($LASTEXITCODE -ne 0) {
    throw "Ký appx thất bại (signtool exit $LASTEXITCODE)."
}

Write-Host "Đã ký: $AppxPath"
