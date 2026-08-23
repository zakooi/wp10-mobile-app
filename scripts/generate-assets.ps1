# Sinh các file logo placeholder cho Assets/ của app UWP.
# Dùng khi thiếu Assets/*.png mà bước đóng gói .appx yêu cầu.
#
# Chạy từ thư mục dự án:
#   powershell -ExecutionPolicy Bypass -File scripts\generate-assets.ps1

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$base = Join-Path $PSScriptRoot '..\src\WebSystemUwp\Assets'
New-Item -ItemType Directory -Force -Path $base | Out-Null

function New-Logo {
    param([string]$Name, [int]$Width, [int]$Height)
    $bmp = New-Object System.Drawing.Bitmap($Width, $Height)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.Clear([System.Drawing.Color]::FromArgb(0, 122, 204)) # màu xanh placeholder
    $g.Dispose()
    $path = Join-Path $base $Name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "Created $path"
}

New-Logo 'Square150x150Logo.png' 150 150
New-Logo 'Square44x44Logo.png' 44 44
New-Logo 'Wide310x150Logo.png' 310 150
New-Logo 'StoreLogo.png' 50 50
New-Logo 'SplashScreen.png' 620 300

Write-Host 'Done. All Assets/*.png placeholders are ready.'
