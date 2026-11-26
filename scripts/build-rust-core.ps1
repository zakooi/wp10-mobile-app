# Biên dịch lõi Rust (websystem_core.dll) cho target UWP và chép vào thư mục Native\<Platform>\
# Chạy trên Windows với Visual Studio C++ UWP tools + Rust Nightly (kèm component rust-src):
#   powershell -ExecutionPolicy Bypass -File scripts\build-rust-core.ps1 -Platform ARM
param(
    [Parameter(Mandatory = $false)]
    [ValidateSet('ARM', 'x86', 'x64')]
    [string]$Platform = 'ARM',

    [Parameter(Mandatory = $false)]
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$crateDir = Join-Path $repoRoot 'src\websystem_core'
$outDir = Join-Path $repoRoot "src\WebSystemUwp\Native\$Platform"

switch ($Platform) {
    'ARM' {
        $rustTarget = 'thumbv7a-uwp-windows-msvc'
        $vsArch = 'amd64_arm'
    }
    'x86' {
        $rustTarget = 'i686-uwp-windows-msvc'
        $vsArch = 'amd64_x86'
    }
    'x64' {
        $rustTarget = 'x86_64-uwp-windows-msvc'
        $vsArch = 'amd64'
    }
}

$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path $vswhere)) {
    throw "Không tìm thấy vswhere.exe tại: $vswhere"
}

$vsPath = & $vswhere -latest -products * -property installationPath | Select-Object -First 1
if (-not $vsPath) {
    throw 'Không tìm thấy bản cài đặt Visual Studio.'
}

$vcvarsall = Join-Path $vsPath 'VC\Auxiliary\Build\vcvarsall.bat'
if (-not (Test-Path $vcvarsall)) {
    throw "Không tìm thấy vcvarsall.bat tại: $vcvarsall"
}

$profileDir = if ($Configuration -eq 'Debug') { 'debug' } else { 'release' }
$releaseFlag = if ($Configuration -eq 'Debug') { '' } else { '--release' }

Write-Host "Building websystem_core ($Configuration | $Platform -> $rustTarget)..."

$tempBat = Join-Path ([IO.Path]::GetTempPath()) "build-websystem-core-$Platform-$PID.cmd"
@"
@echo off
call "$vcvarsall" $vsArch uwp
if errorlevel 1 exit /b %errorlevel%
cd /d "$crateDir"
cargo +nightly build $releaseFlag -Z build-std=std,panic_abort --target $rustTarget
"@ | Set-Content -Path $tempBat -Encoding Ascii

try {
    & cmd.exe /c $tempBat
    if ($LASTEXITCODE -ne 0) {
        throw "Build Rust core thất bại (exit code $LASTEXITCODE)."
    }
}
finally {
    Remove-Item $tempBat -ErrorAction SilentlyContinue
}

$builtDll = Join-Path $crateDir "target\$rustTarget\$profileDir\websystem_core.dll"
if (-not (Test-Path $builtDll)) {
    throw "Không tìm thấy file DLL đầu ra: $builtDll"
}

New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$destDll = Join-Path $outDir 'websystem_core.dll'
Copy-Item -Path $builtDll -Destination $destDll -Force
Write-Host "Copied $builtDll -> $destDll"
