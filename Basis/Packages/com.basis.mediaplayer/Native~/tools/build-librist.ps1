<#
  Build librist as a static library for media-rist (the `rist` cargo feature)
  on Windows x64, and stage it into third_party/librist/.

  librist vendors its own mbedTLS and links it into the archive, so a single
  library is produced. meson names it librist.a even under MSVC, so it is
  renamed to rist.lib for the build script's lookup. Build CRT is /MD (meson
  release default), matching the workspace's MSVC default.

  Requires: MSVC (located automatically via vswhere if cl is not already on
  PATH) plus meson + ninja (pip install meson ninja). librist is cloned from
  upstream at the pinned tag media-rist targets.

  Output: third_party/librist/win-x64/rist.lib
          third_party/librist/include/librist/*.h  (committed; the staged lib is not)
#>
[CmdletBinding()]
param(
    [string]$LibristRef  = "v0.2.11",
    [string]$LibristRepo = "https://code.videolan.org/rist/librist.git"
)
$ErrorActionPreference = "Stop"

$Root = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$Work = Join-Path $Root "target\build-librist\win-x64"
$Tp   = Join-Path $Root "third_party\librist"

# meson/ninja: pip --user installs land off PATH; pick them up directly.
$UserScripts = Join-Path (python -c "import site; print(site.USER_BASE)") "Python314\Scripts"
if ((Test-Path $UserScripts) -and ($env:PATH -notlike "*$UserScripts*")) {
    $env:PATH = "$UserScripts;$env:PATH"
}

# MSVC: if cl isn't on PATH (not a Developer prompt), locate it via vswhere and
# import the vcvars64 environment into this session.
if (-not (Get-Command cl -ErrorAction SilentlyContinue)) {
    $vswhere = "C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { throw "cl not on PATH and vswhere not found; run from a Developer prompt." }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { throw "vswhere found no MSVC install with the x64 C++ toolset." }
    $vcvars = Join-Path $vs "VC\Auxiliary\Build\vcvars64.bat"
    if (-not (Test-Path $vcvars)) { throw "vcvars64.bat missing under $vs" }
    cmd /c "`"$vcvars`" >nul 2>&1 && set" | ForEach-Object {
        if ($_ -match '^([^=]+)=(.*)$') { Set-Item -Path "env:$($Matches[1])" -Value $Matches[2] }
    }
    if (-not (Get-Command cl -ErrorAction SilentlyContinue)) { throw "vcvars import failed: cl still not on PATH." }
}

foreach ($tool in @("git", "meson", "ninja", "cl")) {
    if (-not (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "$tool not on PATH. Need MSVC and 'pip install meson ninja'."
    }
}

if (Test-Path $Work) { Remove-Item -Recurse -Force $Work }
New-Item -ItemType Directory -Force -Path $Work | Out-Null
$Src = Join-Path $Work "librist"

git clone --depth 1 -b $LibristRef $LibristRepo $Src
if ($LASTEXITCODE -ne 0) { throw "git clone of librist $LibristRef failed" }

# No external mbedTLS on the path -> librist builds its bundled copy and links it in.
meson setup "$Src/build" "$Src" --default-library=static --buildtype=release
if ($LASTEXITCODE -ne 0) { throw "meson setup failed" }
ninja -C "$Src/build" librist.a   # only the static we link; skip librist's CLI tools/tests
if ($LASTEXITCODE -ne 0) { throw "ninja failed" }

$Lib = Join-Path $Src "build/librist.a"   # meson names it librist.a even on MSVC
if (-not (Test-Path $Lib) -or (Get-Item $Lib).Length -lt 100KB) {
    throw "librist static missing or implausibly small at $Lib"
}

New-Item -ItemType Directory -Force -Path (Join-Path $Tp "win-x64") | Out-Null
New-Item -ItemType Directory -Force -Path (Join-Path $Tp "include/librist") | Out-Null
Copy-Item -Force $Lib (Join-Path $Tp "win-x64/rist.lib")
Copy-Item -Force (Join-Path $Src "include/librist/*.h") (Join-Path $Tp "include/librist/")
$GenInc = Join-Path $Src "build/include/librist"
if (Test-Path $GenInc) { Copy-Item -Force (Join-Path $GenInc "*.h") (Join-Path $Tp "include/librist/") }

Write-Host "Staged: third_party/librist/win-x64/rist.lib + third_party/librist/include/librist/"
