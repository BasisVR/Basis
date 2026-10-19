# The engine's gate on Windows: run it before committing, and every step must
# pass. The RIST, Android and conformance steps print SKIPPED when what they
# need is missing and the run still ends green, so look for those lines; any
# other missing tool fails the run. TESTING.md ("Running the tests") lists
# what each step checks and what it needs. Needs PowerShell 7 (pwsh).
#
#   .\tools\ci.ps1          # the gate
#   .\tools\ci.ps1 -Fuzz    # the gate, then build the fuzz targets (needs
#                           # nightly Rust and cargo-fuzz, which build only
#                           # on Linux or WSL)

param([switch]$Fuzz)

$ErrorActionPreference = "Stop"
Set-Location (Join-Path $PSScriptRoot "..")

function Step($name, $block) {
    Write-Host "== $name" -ForegroundColor Cyan
    & $block
    if ($LASTEXITCODE -ne 0) {
        Write-Host "FAILED: $name" -ForegroundColor Red
        exit 1
    }
}

Step "cargo fmt --check"   { cargo fmt --check }
Step "cargo clippy"        { cargo clippy --workspace --all-targets --examples -- -D warnings }
Step "cargo test"          { cargo test --workspace }
# RIST is an optional transport linked against librist, so its steps run
# only once tools/build-librist.ps1 has built the library.
if (Test-Path "third_party/librist/win-x64/rist.lib") {
    Step "RIST clippy" { cargo clippy -p media-rist -p media-engine --features media-engine/rist --all-targets -- -D warnings }
    Step "RIST tests"  { cargo test -p media-rist --features librist }
} else {
    Write-Host "SKIPPED: RIST, librist not built (tools/build-librist.ps1)" -ForegroundColor Yellow
}
# Android: the engine must compile and lint for Quest (aarch64). Needs the
# Rust target and an Android NDK; android-env.ps1 finds the one Unity's
# Android support installs. Runs in a child shell so the NDK environment
# does not leak into later steps.
$androidTarget = (rustup target list --installed) -contains "aarch64-linux-android"
if ($androidTarget) {
    Step "Android (aarch64)" {
        pwsh -NoProfile -Command {
            Set-Location $args[0]
            . .\tools\android-env.ps1 | Out-Null
            if ($env:BM_ANDROID_ENV_OK -ne "1") {
                Write-Host "SKIPPED: Android, no NDK found (install Unity's Android support)" -ForegroundColor Yellow
                exit 0
            }
            # The Android plugin ships with RIST, so lint with it when
            # librist has been built for Android.
            $rist = Join-Path (Get-Location) "third_party\librist\android-arm64\librist.a"
            if (Test-Path $rist) {
                cargo clippy --target aarch64-linux-android -p media-ffi -p decode-mediacodec --features rist -- -D warnings
            } else {
                Write-Host "NOTE: Android linted without RIST, librist not built for Android (tools/build-librist-android.sh)" -ForegroundColor Yellow
                cargo clippy --target aarch64-linux-android -p media-ffi -p decode-mediacodec -- -D warnings
            }
            exit $LASTEXITCODE
        } -args (Get-Location).Path
    }
} else {
    Write-Host "SKIPPED: Android, Rust target not installed (rustup target add aarch64-linux-android)" -ForegroundColor Yellow
}
Step "cargo deny check"    { cargo deny check }
Step "cargo vet"           { cargo vet }
# Conformance: every MP4 and TS fixture must demux to what ffprobe reads.
if (Get-Command ffprobe -ErrorAction SilentlyContinue) {
    Step "conformance (ffprobe oracle)" { cargo run -q -p bm-probe -- conformance fixtures }
} else {
    Write-Host "SKIPPED: conformance, ffprobe not on PATH (install ffmpeg)" -ForegroundColor Yellow
}
# Software decode: AV1 and Opus through the whole engine, no GPU needed.
Step "software decode (AV1 + Opus)" {
    cargo run -q -p bm-probe -- play fixtures/mkv/av1-opus.webm --duration 8
}
# Impairment: a recorded bad-network profile replayed through the engine
# over a fixture paced at 1x, graded against the buffer sizing model.
# Kept short here; TESTING.md has the full-length runs.
Step "impairment (recorded network delay)" {
    cargo run -q -p bm-probe -- impair fixtures/h264-aac-320x180-30s.ts `
        --profile ts-rtt300-loss005 --duration 25 --depth-ms 3000
}
# Split source: video off one file, audio off another, one session.
Step "split source (two legs, one session)" {
    cargo run -q -p bm-probe -- play fixtures/split/h264-640x360-30fps-video.mp4 `
        --audio-url fixtures/split/aac-48k-stereo-audio.m4a --duration 9
}
if ($Fuzz) {
    Step "cargo fuzz build" { cargo +nightly fuzz build }
}
Write-Host "CI green" -ForegroundColor Green
