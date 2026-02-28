$ErrorActionPreference = "Stop"

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Project = Join-Path $ScriptDir "Ralph\Ralph.csproj"

Write-Host "===============================" -ForegroundColor Cyan
Write-Host "  Ralph Installer" -ForegroundColor Cyan
Write-Host "===============================" -ForegroundColor Cyan
Write-Host ""

# --- Detect Architecture ---

$arch = if ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture -eq "Arm64") { "arm64" } else { "x64" }
$rid = "win-$arch"

Write-Host "Platform: $rid"

# --- Check .NET SDK ---

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host "Error: .NET SDK not found. Install from https://dot.net/download" -ForegroundColor Red
    exit 1
}

Write-Host ".NET SDK: $(dotnet --version)"
Write-Host ""

# --- Publish ---

$PublishDir = Join-Path $ScriptDir "Ralph\publish"

Write-Host "Publishing ralph for $rid..."
dotnet publish $Project -c Release -r $rid -o $PublishDir --nologo -v q
Write-Host "Build complete."
Write-Host ""

# --- Ask destination ---

$DefaultDir = Join-Path $env:USERPROFILE "bin"
$input = Read-Host "Install directory [$DefaultDir]"
$InstallDir = if ($input) { $input } else { $DefaultDir }

if (-not (Test-Path $InstallDir)) {
    $create = Read-Host "'$InstallDir' does not exist. Create it? [Y/n]"
    if (-not $create -or $create -match "^[Yy]$") {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    } else {
        Write-Host "Aborted." -ForegroundColor Red
        exit 1
    }
}

# --- Copy binary ---

Write-Host "Installing ralph to $InstallDir..."
Copy-Item (Join-Path $PublishDir "ralph.exe") (Join-Path $InstallDir "ralph.exe") -Force

# --- PATH check ---

$currentPath = [Environment]::GetEnvironmentVariable("PATH", "User")
if ($currentPath -split ";" | Where-Object { $_ -eq $InstallDir }) {
    Write-Host ""
    Write-Host "Done! 'ralph' is ready to use." -ForegroundColor Green
} else {
    $addPath = Read-Host "'$InstallDir' is not in PATH. Add to user PATH? [Y/n]"
    if (-not $addPath -or $addPath -match "^[Yy]$") {
        [Environment]::SetEnvironmentVariable("PATH", "$InstallDir;$currentPath", "User")
        Write-Host "Added to user PATH. Restart your terminal for changes to take effect." -ForegroundColor Yellow
    }
    Write-Host ""
    Write-Host "Done! ralph installed at $InstallDir\ralph.exe" -ForegroundColor Green
}
