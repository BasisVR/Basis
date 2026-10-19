# Build the Android engine and copy the stripped .so into the package's
# Runtime/Plugins/Android/arm64-v8a, where every project using
# com.basis.mediaplayer picks it up. Run from Native~ after any engine change:
#
#   .\tools\stage-android-plugin.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path $PSScriptRoot

. (Join-Path $PSScriptRoot "android-env.ps1") | Out-Null
if ($env:BM_ANDROID_ENV_OK -ne "1") {
    Write-Host "stage-android-plugin: no NDK (see android-env.ps1)" -ForegroundColor Red
    exit 1
}

# The shipping build carries RIST, so the staged binary must too — a
# feature that is on for Windows and Linux and off here would refuse
# rist:// on Quest alone. Needs the static staged first; see that script.
$rist = Join-Path $root "third_party\librist\android-arm64\librist.a"
if (-not (Test-Path $rist)) {
    Write-Host "stage-android-plugin: no staged librist for android-arm64." -ForegroundColor Red
    Write-Host "  Build it first:  bash tools/build-librist-android.sh" -ForegroundColor Red
    exit 1
}

Push-Location $root
try {
    cargo build --target aarch64-linux-android -p media-ffi --release --features rist
    if ($LASTEXITCODE -ne 0) { exit 1 }
} finally {
    Pop-Location
}

$strip = Join-Path $env:ANDROID_NDK "toolchains\llvm\prebuilt\windows-x86_64\bin\llvm-strip.exe"
$targetDir = if ($env:CARGO_TARGET_DIR) { $env:CARGO_TARGET_DIR } else { Join-Path $root "target" }
$built = Join-Path $targetDir "aarch64-linux-android\release\libbasis_media.so"
$dest = Join-Path $root "..\Runtime\Plugins\Android\arm64-v8a\libbasis_media.so"
# --strip-all: .dynsym (the dlopen surface) survives; .symtab and debug
# sections go. The unstripped artefact stays in target/ for symbolication.
& $strip --strip-all -o $dest $built
if ($LASTEXITCODE -ne 0) { exit 1 }
Write-Host "staged $dest ($([math]::Round((Get-Item $dest).Length / 1MB, 1)) MB)" -ForegroundColor Green
