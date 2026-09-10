# csmesh installer for Windows - https://github.com/nRafinia/CsMesh
# Usage (PowerShell):
#   irm https://raw.githubusercontent.com/nRafinia/CsMesh/main/install.ps1 | iex
#
# Environment variables (optional):
#   $env:CSMESH_INSTALL_DIR   : custom directory to install csmesh.exe (default: $env:LOCALAPPDATA\Programs\csmesh)
#   $env:CSMESH_VERSION       : specific release tag to install (default: latest)
#   $env:CSMESH_USE_DOTNET    : set to "1" to force installation as a .NET global tool
#   $env:CSMESH_SKIP_CHECKSUM : set to "1" to skip sha256 verification (not recommended)

$ErrorActionPreference = "Stop"

$Repo = "nRafinia/CsMesh"
$ToolId = "CsMesh"
$BinaryName = "csmesh.exe"

function Write-Info($msg) {
    Write-Host "[INFO] " -ForegroundColor Cyan -NoNewline
    Write-Host $msg
}

function Write-Success($msg) {
    Write-Host "[SUCCESS] " -ForegroundColor Green -NoNewline
    Write-Host $msg
}

function Write-Warn($msg) {
    Write-Host "[WARN] " -ForegroundColor Yellow -NoNewline
    Write-Host $msg
}

function Write-Err($msg) {
    Write-Host "[ERROR] " -ForegroundColor Red -NoNewline
    Write-Host $msg
}

function Install-ViaDotnet() {
    $dotnet = Get-Command "dotnet" -ErrorAction SilentlyContinue
    if ($dotnet) {
        Write-Info "Installing CsMesh as a .NET global tool via 'dotnet tool'..."
        try {
            dotnet tool update --global $ToolId 2>$null
            if ($LASTEXITCODE -ne 0) {
                dotnet tool install --global $ToolId
            }
            if ($LASTEXITCODE -eq 0) {
                Write-Success "csmesh installed successfully via dotnet tool!"
                Write-Host ""
                Write-Info "Run 'csmesh --help' or 'csmesh index' to get started."
                return $true
            }
        }
        catch {
            # continue to return false
        }
    }
    return $false
}

# Detect the OS architecture. PROCESSOR_ARCHITECTURE alone is unreliable: on
# ARM64 Windows it reports AMD64 whenever the host process is running under the
# x64 emulation layer, which misleads the installer into fetching the wrong binary.
# PROCESSOR_ARCHITEW6432 exists only for that emulation case; .NET's
# RuntimeInformation is the authoritative fallback.
function Get-OSArchitecture() {
    # 1. Try .NET Core / PowerShell 7+ method
    $arch = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
    if ($null -ne $arch) {
        switch ($arch) {
            ([System.Runtime.InteropServices.Architecture]::X64)   { return "x64" }
            ([System.Runtime.InteropServices.Architecture]::Arm64) { return "arm64" }
        }
    }

    # 2. Fallback for Windows PowerShell 5.1
    $envArch = $env:PROCESSOR_ARCHITECTURE
    if ($envArch -eq "AMD64") { return "x64" }
    if ($envArch -eq "ARM64") { return "arm64" }

    return "unknown"
}

# Verify the downloaded archive against the release's checksums.txt.
# Fails closed when checksums are present; warns and continues for older
# releases that predate checksum publication (or when the user opted out).
function Verify-Checksum([string]$ReleaseUrl, [string]$AssetPath, [string]$AssetName) {
    if ($env:CSMESH_SKIP_CHECKSUM -eq "1") {
        Write-Warn "Checksum verification skipped (CSMESH_SKIP_CHECKSUM=1)."
        return
    }

    Write-Info "Fetching checksums..."
    $checksumsPath = Join-Path ([System.IO.Path]::GetDirectoryName($AssetPath)) "checksums.txt"
    try {
        Invoke-WebRequest -Uri "$ReleaseUrl/checksums.txt" -OutFile $checksumsPath -UseBasicParsing -TimeoutSec 60
    }
    catch {
        Write-Warn "checksums.txt not found in this release; skipping verification."
        Write-Warn "For verification, pin a version: `$env:CSMESH_VERSION = 'vX.Y.Z' and check the release page."
        return
    }

    # Expected line format: "<sha256>  <filename>" (two spaces, as produced by sha256sum).
    $expected = $null
    foreach ($line in Get-Content $checksumsPath) {
        if ($line -match "^\s*([0-9a-fA-F]{64})\s+\S*\Q$AssetName\E\s*$") {
            $expected = $Matches[1].ToLowerInvariant()
            break
        }
    }

    if (-not $expected) {
        Write-Warn "$AssetName not listed in checksums.txt; skipping verification."
        return
    }

    $actual = (Get-FileHash -Path $AssetPath -Algorithm SHA256).Hash.ToLowerInvariant()

    if ($expected -ne $actual) {
        Write-Err "Checksum mismatch for $AssetName!`n  expected: $expected`n  actual:   $actual`nAborting. The download may be corrupted or tampered with."
        exit 1
    }

    Write-Success "Checksum verified (sha256: $actual)"
}

if ($env:CSMESH_USE_DOTNET -eq "1") {
    if (Install-ViaDotnet) {
        return
    }
    Write-Err "Failed to install via 'dotnet tool'. Make sure the .NET SDK is installed."
    return
}

# Determine target directory
$InstallDir = if ($env:CSMESH_INSTALL_DIR) {
    $env:CSMESH_INSTALL_DIR
} else {
    Join-Path $env:LOCALAPPDATA "Programs\csmesh"
}

# Detect OS architecture (authoritative, emulation-aware)
$Arch = Get-OSArchitecture

if ($Arch -ne "x64" -and $Arch -ne "arm64") {
    Write-Warn "Prebuilt binaries only support x64 and arm64. Falling back to dotnet tool..."
    if (Install-ViaDotnet) { return }
    Write-Err "Unsupported architecture and .NET SDK not found."
    return
}

$AssetName = "csmesh-win-${Arch}.zip"

# Resolve version
if ($env:CSMESH_VERSION) {
    $Version = $env:CSMESH_VERSION
    $ReleaseUrl = "https://github.com/$Repo/releases/download/$Version"
    $DownloadUrl = "$ReleaseUrl/$AssetName"
} else {
    Write-Info "Resolving latest release of $Repo..."
    $ReleaseUrl = "https://github.com/$Repo/releases/latest/download"
    $DownloadUrl = "$ReleaseUrl/$AssetName"
}

$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) ([System.Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null
$ZipPath = Join-Path $TempDir $AssetName

try {
    Write-Info "Downloading $DownloadUrl..."
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12 -bor [Net.SecurityProtocolType]::Tls13

    $downloadSuccess = $false
    try {
        Invoke-WebRequest -Uri $DownloadUrl -OutFile $ZipPath -UseBasicParsing -TimeoutSec 60
        $downloadSuccess = $true
    }
    catch {
        $status = $_.Exception.Response.StatusCode.value__
        Write-Warn "Could not download prebuilt release asset (HTTP $status)."
    }

    if (-not $downloadSuccess) {
        Write-Info "Falling back to installation via .NET global tool..."
        if (Install-ViaDotnet) {
            return
        } else {
            Write-Err "Download failed and .NET SDK was not found on this system.`nYou can install via: dotnet tool install --global CsMesh"
            return
        }
    }

    # Verify integrity before touching the install directory
    Verify-Checksum -ReleaseUrl $ReleaseUrl -AssetPath $ZipPath -AssetName $AssetName

    if (-not (Test-Path $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    }

    Write-Info "Extracting $AssetName to $InstallDir..."
    Expand-Archive -Path $ZipPath -DestinationPath $InstallDir -Force

    $ExePath = Join-Path $InstallDir $BinaryName
    if (-not (Test-Path $ExePath)) {
        Write-Err "Extraction finished but $BinaryName was not found at $ExePath"
        return
    }

    # Add to User PATH if not present
    $UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
    $PathParts = ($UserPath -split ';') | Where-Object { $_ }
    if ($PathParts -notcontains $InstallDir) {
        $NewUserPath = if ([string]::IsNullOrEmpty($UserPath)) { $InstallDir } else { "$UserPath;$InstallDir" }
        [Environment]::SetEnvironmentVariable("Path", $NewUserPath, "User")
        Write-Info "Added $InstallDir to User environment PATH."
    }

    # Add to current PowerShell session PATH
    if (($env:Path -split ';') -notcontains $InstallDir) {
        $env:Path = "$InstallDir;$env:Path"
    }

    Write-Host ""
    Write-Success "csmesh installed successfully at $ExePath"
    Write-Host ""

    # Test version
    try {
        & $ExePath --version
    } catch { }

    Write-Host ""
    Write-Success "csmesh is ready! Run 'csmesh --help' or 'csmesh index' in any .NET project."
    Write-Info "Note: In already open command prompt windows, you may need to restart the terminal for the new PATH to take effect."
}
finally {
    if (Test-Path $TempDir) {
        Remove-Item -Path $TempDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
