param (
    [switch]$DryRun
)

# Mirrors the Basis Server solution into the Unity package at Basis/Packages/com.basis.server.
# The package only ships what Unity compiles (the asmdef'd assemblies) plus Docker and the licence
# files, so test projects, the dev consoles and the benchmark/compute projects stay out of it.

$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

function Find-BasisFoundationDir {
    param (
        [string]$currentDir
    )
    while ($currentDir -and (Split-Path -Leaf $currentDir) -ne "Basis Foundation") {
        $currentDir = Split-Path -Parent $currentDir
    }
    return $currentDir
}

$basisFoundationDir = Find-BasisFoundationDir -currentDir $scriptDir
if ($basisFoundationDir) {
    $source = Join-Path $basisFoundationDir "Basis Unity\Basis Server"
    $destination = Join-Path $basisFoundationDir "Basis Unity\Basis\Packages\com.basis.server"
}
else {
    # Checkouts without the "Basis Foundation" wrapper keep both trees directly under the repo root.
    $source = Join-Path $scriptDir "Basis Server"
    $destination = Join-Path $scriptDir "Basis\Packages\com.basis.server"
}

if (-Not (Test-Path -Path $source)) {
    Write-Host "Error: Source directory not found - $source"
    exit 1
}
if (-Not (Test-Path -Path $destination)) {
    Write-Host "Error: Destination directory not found - $destination"
    exit 1
}

# Top-level entries the package mirrors. Anything absent here is deliberately not shipped:
# BasisServerTests, BasisRestApi.Tests, BasisBenchAgent, BasisServerBenchmark, BasisNetworkCompute,
# BasisServerConsole, BasisNetworkClientConsole, benchmark-results, the .sln and every .csproj.
$includeDirs = @("BasisNetworkClient", "BasisNetworkCore", "BasisNetworkServer", "Contrib", "LiteNetLib", "Docker")
$includeFiles = @("package.json", "profile-run.sh")

# Package-side .meta, .asmdef, .gitignore, .gitattributes, LICENSE and THIRD_PARTY_NOTICES.md are
# authoritative and are never overwritten from source.
$excludeExtensions = @(".dll", ".asmdef", ".meta", ".csproj", ".sln", ".user", ".vsidx", ".cache")
$excludeFragments = @("\obj\", "\bin\", "\.vs\", "\.git\", "\Did.Tests\", "\Crypto.Tests\", "\Dns.Tests\", "PersistentKv")

function Test-Excluded {
    param (
        [string]$fullName
    )
    if ([IO.Path]::GetExtension($fullName) -in $excludeExtensions) { return $true }
    foreach ($fragment in $excludeFragments) {
        if ($fullName -like "*$fragment*") { return $true }
    }
    return $false
}

$copied = 0
$added = 0
$removed = 0

foreach ($entry in $includeDirs) {
    $sourceDir = Join-Path $source $entry
    $destinationDir = Join-Path $destination $entry
    if (-Not (Test-Path -Path $sourceDir)) { continue }

    # Drop package files whose source counterpart is gone, scoped to the mirrored subtree so
    # nothing outside it is ever touched.
    if (Test-Path -Path $destinationDir) {
        Get-ChildItem -Path $destinationDir -Recurse -File | Where-Object { -not (Test-Excluded $_.FullName) } | ForEach-Object {
            $relativePath = $_.FullName.Substring($destinationDir.Length)
            if (-not (Test-Path -Path (Join-Path $sourceDir $relativePath))) {
                if (-not $DryRun) {
                    Remove-Item -Path $_.FullName -Force
                    $metaPath = "$($_.FullName).meta"
                    if (Test-Path -Path $metaPath) { Remove-Item -Path $metaPath -Force }
                }
                Write-Host "DEL  $entry$relativePath"
                $removed++
            }
        }
    }

    Get-ChildItem -Path $sourceDir -Recurse -File | Where-Object { -not (Test-Excluded $_.FullName) } | ForEach-Object {
        $relativePath = $_.FullName.Substring($sourceDir.Length)
        $destinationPath = Join-Path $destinationDir $relativePath
        $destinationFolder = Split-Path -Parent $destinationPath
        if ((-not $DryRun) -and (-not (Test-Path -Path $destinationFolder))) { New-Item -ItemType Directory -Path $destinationFolder -Force | Out-Null }

        if (-not (Test-Path -Path $destinationPath)) {
            if (-not $DryRun) { Copy-Item -Path $_.FullName -Destination $destinationPath -Force }
            Write-Host "ADD  $entry$relativePath"
            $added++
        }
        elseif ((Get-FileHash $_.FullName).Hash -ne (Get-FileHash $destinationPath).Hash) {
            if (-not $DryRun) { Copy-Item -Path $_.FullName -Destination $destinationPath -Force }
            Write-Host "SYNC $entry$relativePath"
            $copied++
        }
    }
}

foreach ($entry in $includeFiles) {
    $sourceFile = Join-Path $source $entry
    $destinationFile = Join-Path $destination $entry
    if (-Not (Test-Path -Path $sourceFile)) { continue }
    if ((-not (Test-Path -Path $destinationFile)) -or ((Get-FileHash $sourceFile).Hash -ne (Get-FileHash $destinationFile).Hash)) {
        if (-not $DryRun) { Copy-Item -Path $sourceFile -Destination $destinationFile -Force }
        Write-Host "SYNC $entry"
        $copied++
    }
}

Write-Host ""
Write-Host "$(if ($DryRun) { 'Dry run: ' } else { 'Export complete. ' })$added added, $copied updated, $removed removed."
Write-Host "Review 'git status' on the package before committing: work authored in the package copy is overwritten by this script."
