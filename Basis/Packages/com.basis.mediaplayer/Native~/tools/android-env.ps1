# Android build environment for the aarch64-linux-android lane. Dot-source
# before cargo:
#
#   . .\tools\android-env.ps1
#   cargo check --target aarch64-linux-android -p media-ffi
#
# Locates an NDK (ANDROID_NDK_HOME / ANDROID_NDK_ROOT, else the newest
# installed Unity editor's AndroidPlayer NDK) and exports what cargo and
# the native build scripts need:
#   - the API-29 clang driver as linker and CC/CXX (async MediaCodec
#     callbacks and AMediaCodec_getName need API >= 28; 29 keeps headroom
#     below every current Quest OS)
#   - the SDK's own cmake+ninja (version-matched to the NDK) for cmake
#     build scripts (audiopus_sys), with ANDROID_NDK set so CMake's
#     built-in Android support finds the toolchain.
#
# Returns non-zero (and sets $env:BM_ANDROID_ENV_OK = "0") when no NDK is
# found, so CI can skip the lane loudly instead of failing it.

$env:BM_ANDROID_ENV_OK = "0"

function Find-Ndk {
    foreach ($candidate in @($env:ANDROID_NDK_HOME, $env:ANDROID_NDK_ROOT)) {
        if ($candidate -and (Test-Path (Join-Path $candidate "source.properties"))) {
            return (Resolve-Path $candidate).Path
        }
    }
    $editors = Get-ChildItem "C:\Program Files\Unity\Hub\Editor" -ErrorAction SilentlyContinue |
        Sort-Object Name -Descending
    foreach ($editor in $editors) {
        $ndk = Join-Path $editor.FullName "Editor\Data\PlaybackEngines\AndroidPlayer\NDK"
        if (Test-Path (Join-Path $ndk "source.properties")) {
            return $ndk
        }
    }
    return $null
}

$ndk = Find-Ndk
if (-not $ndk) {
    Write-Host "android-env: no NDK found (set ANDROID_NDK_HOME or install a Unity editor with Android support)" -ForegroundColor Yellow
    return 1
}

$bin = Join-Path $ndk "toolchains\llvm\prebuilt\windows-x86_64\bin"
$api = 29
$clang = Join-Path $bin "aarch64-linux-android$api-clang.cmd"
if (-not (Test-Path $clang)) {
    Write-Host "android-env: $clang missing" -ForegroundColor Yellow
    return 1
}

$env:CARGO_TARGET_AARCH64_LINUX_ANDROID_LINKER = $clang
$env:CC_aarch64_linux_android = $clang
$env:CXX_aarch64_linux_android = Join-Path $bin "aarch64-linux-android$api-clang++.cmd"
$env:AR_aarch64_linux_android = Join-Path $bin "llvm-ar.exe"

# CMake's built-in Android support keys off ANDROID_NDK.
$env:ANDROID_NDK = $ndk
$env:ANDROID_NDK_HOME = $ndk
$env:ANDROID_NDK_ROOT = $ndk

# Prefer the SDK's NDK-matched cmake+ninja when present (Unity layout:
# SDK sits beside NDK); fall back to cmake and ninja from PATH. Either way
# the generator must be Ninja: CMake's default on Windows (Visual Studio)
# cannot target Android.
$sdkCmake = Get-ChildItem (Join-Path (Split-Path $ndk) "SDK\cmake") -ErrorAction SilentlyContinue |
    Sort-Object Name -Descending | Select-Object -First 1
if ($sdkCmake) {
    $cmakeBin = Join-Path $sdkCmake.FullName "bin"
    $env:PATH = "$cmakeBin;$env:PATH"
    $env:CMAKE_GENERATOR = "Ninja"
    $env:CMAKE_MAKE_PROGRAM = Join-Path $cmakeBin "ninja.exe"
} elseif (Get-Command ninja -ErrorAction SilentlyContinue) {
    $env:CMAKE_GENERATOR = "Ninja"
}

$env:BM_ANDROID_ENV_OK = "1"
Write-Host "android-env: NDK $ndk (API $api)" -ForegroundColor Cyan
return 0
