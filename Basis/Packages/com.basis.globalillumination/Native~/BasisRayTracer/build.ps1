param([switch]$Debug, [switch]$Linux)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$deps = Join-Path $root '.deps'
$agility = '1.620.1'
$dxc = '1.9.2609.5'
function Fetch($name, $version, $folder) {
    $target = Join-Path $deps $folder
    if (Test-Path (Join-Path $target 'build')) { return }
    New-Item -ItemType Directory -Force $deps | Out-Null
    $zip = Join-Path $deps "$folder.zip"
    Invoke-WebRequest "https://api.nuget.org/v3-flatcontainer/$name/$version/$name.$version.nupkg" -OutFile $zip
    Expand-Archive $zip $target -Force
    Remove-Item $zip
}
Fetch 'microsoft.direct3d.d3d12' $agility 'd3d12'
Fetch 'microsoft.direct3d.dxc' $dxc 'dxc'
$cargo = (Get-Command cargo -ErrorAction SilentlyContinue).Source
if (-not $cargo) { $cargo = Join-Path $env:USERPROFILE '.cargo\bin\cargo.exe' }
$config = if ($Debug) { 'debug' } else { 'release' }
$cargoArgs = @('build', '-p', 'basis-rt-tracer', '-p', 'basis-rt-bridge')
if (-not $Debug) { $cargoArgs += '--release' }
Push-Location $root
& $cargo @cargoArgs
if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'cargo build failed' }
Pop-Location
$out = Join-Path $root "target\$config"
$bin = Join-Path $root '..\Tracer'
$installed = [System.IO.Path]::GetFullPath((Join-Path $bin 'BasisRayTracer.exe'))
Get-Process BasisRayTracer -ErrorAction SilentlyContinue | Where-Object { $_.Path -eq $installed } | Stop-Process -Force
New-Item -ItemType Directory -Force (Join-Path $bin 'D3D12') | Out-Null
Copy-Item (Join-Path $out 'BasisRayTracer.exe') $bin -Force
Copy-Item (Join-Path $deps 'd3d12\build\native\bin\x64\D3D12Core.dll') (Join-Path $bin 'D3D12') -Force
Copy-Item (Join-Path $deps 'd3d12\LICENSE.txt') (Join-Path $bin 'D3D12\LICENSE-AgilitySDK.txt') -Force
$plugins = Join-Path $root '..\..\Plugins\x86_64'
New-Item -ItemType Directory -Force $plugins | Out-Null
Copy-Item (Join-Path $out 'BasisRayTracingBridge.dll') (Join-Path $plugins 'BasisRayTracingBridge2.dll') -Force
Write-Host "Installed tracer to $bin and bridge to $plugins"
if ($Linux) {
    $zig = (Get-Command zig -ErrorAction SilentlyContinue).Source
    if (-not $zig) { throw 'cross-compiling for Linux needs zig on PATH (winget install zig.zig)' }
    $linker = Join-Path $deps 'zigcc-linux.cmd'
    Set-Content -Path $linker -Value "@`"$zig`" cc -target x86_64-linux-gnu.2.28 %*" -Encoding ascii
    $env:CARGO_TARGET_X86_64_UNKNOWN_LINUX_GNU_LINKER = $linker
    $linuxArgs = $cargoArgs + @('--target', 'x86_64-unknown-linux-gnu')
    Push-Location $root
    & $cargo @linuxArgs
    if ($LASTEXITCODE -ne 0) { Pop-Location; throw 'cargo build for Linux failed (rustup target add x86_64-unknown-linux-gnu)' }
    Pop-Location
    $linuxOut = Join-Path $root "target\x86_64-unknown-linux-gnu\$config"
    Copy-Item (Join-Path $linuxOut 'BasisRayTracer') $bin -Force
    Copy-Item (Join-Path $linuxOut 'libBasisRayTracingBridge.so') (Join-Path $plugins 'libBasisRayTracingBridge2.so') -Force
    Write-Host "Installed Linux tracer to $bin and bridge to $plugins"
}
