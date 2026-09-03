# csmesh installer for Windows - https://github.com/nRafinia/CsMesh
# Usage (PowerShell):
#   irm https://raw.githubusercontent.com/nRafinia/CsMesh/main/install.ps1 | iex
#
# Environment variables (optional):
#   $env:CSMESH_INSTALL_DIR : custom directory to install csmesh.exe (default: $env:LOCALAPPDATA\Programs\csmesh)
#   $env:CSMESH_VERSION     : specific release tag to install (default: latest)
#   $env:CSMESH_USE_DOTNET  : set to "1" to force installation as a .NET global tool

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

# Detect architecture
$Arch = if ([System.Environment]::Is64BitOperatingSystem) {
    if ($env:PROCESSOR_ARCHITECTURE -eq "ARM64") { "arm64" } else { "x64" }
} else {
    "x86"
}

if ($Arch -ne "x64" -and $Arch -ne "arm64") {
    Write-Warn "Prebuilt binaries only support x64 and arm64. Falling back to dotnet tool..."
    if (Install-ViaDotnet) { return }
    Write-Err "Unsupported architecture: $Arch and .NET SDK not found."
    return
}

$AssetName = "csmesh-win-${Arch}.zip"

# Resolve version
if ($env:CSMESH_VERSION) {
    $Version = $env:CSMESH_VERSION
    $DownloadUrl = "https://github.com/$Repo/releases/download/$Version/$AssetName"
} else {
    Write-Info "Resolving latest release of $Repo..."
    $DownloadUrl = "https://github.com/$Repo/releases/latest/download/$AssetName"
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
