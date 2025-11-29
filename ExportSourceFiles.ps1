# Get the directory where the script is located
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition

# Function to search for "Basis Foundation" in parent directories
function Find-BasisFoundationDir {
    param (
        [string]$currentDir
    )
    while ($currentDir -and (Split-Path -Leaf $currentDir) -ne "Basis Foundation") {
        $currentDir = Split-Path -Parent $currentDir
    }
    return $currentDir
}

# Find the "Basis Foundation" directory
$basisFoundationDir = Find-BasisFoundationDir -currentDir $scriptDir

# Validate that we found the expected directory
if (-not $basisFoundationDir) {
    Write-Host "Error: Could not find 'Basis Foundation' directory in parent structure."
    exit 1
}

# Define source and destination relative to "Basis Foundation"
$source = Join-Path $basisFoundationDir "Basis Unity\Basis Server"
$destination = Join-Path $basisFoundationDir "Basis Unity\Basis\Packages\com.basis.server"

# Ensure source exists before proceeding
if (-Not (Test-Path -Path $source)) {
    Write-Host "Error: Source directory not found - $source"
    exit 1
}

# Remove all .cs files in the destination directory
Get-ChildItem -Path $destination -Recurse -Include *.cs | Remove-Item -Force

# Copy files from source to destination, excluding .dll, .asmdef, and obj folders
Get-ChildItem -Path $source -Recurse | Where-Object { 
    $_.Extension -notin @('.dll', '.asmdef') -and
    $_.FullName -notmatch '\\obj\\' -and
    $_.FullName -notlike '*\Contrib\PersistentKv*'
} | ForEach-Object {
    # Compute relative path and determine destination path
    $relativePath = $_.FullName.Substring($source.Length)
    $destinationPath = Join-Path $destination $relativePath

    # Ensure the destination folder exists
    $destinationFolder = Split-Path -Parent $destinationPath
    if (-not (Test-Path -Path $destinationFolder)) {
        New-Item -ItemType Directory -Path $destinationFolder -Force
    }

    # Copy the file to the destination
    if (-not $_.PSIsContainer) {
        Copy-Item -Path $_.FullName -Destination $destinationPath -Force
    }
}

Write-Host "Files copied successfully!"