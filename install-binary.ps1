<#
.SYNOPSIS
    Ralph universal installer (Windows) — downloads a pre-built binary from GitHub Releases.

.DESCRIPTION
    install-binary.sh의 PowerShell 포팅. .NET SDK 설치 없이 self-contained binary를
    GitHub Releases에서 받아 설치한다. SHA256SUMS.txt가 release에 함께 올라와 있으면
    체크섬 검증까지 수행한다.

.PARAMETER Version
    설치할 release tag (예: v1.22). 미지정 시 latest release 사용.

.PARAMETER Dir
    설치 디렉토리. 기본값 $HOME\.local\bin (install-binary.sh와 동일한 관례).

.PARAMETER Quiet
    상세 로그를 줄인다.

.EXAMPLE
    iwr -useb https://raw.githubusercontent.com/starlog/ralph/main/install-binary.ps1 | iex

.EXAMPLE
    .\install-binary.ps1 -Version v1.22 -Dir "$env:USERPROFILE\bin"

.NOTES
    Environment:
      RALPH_REPO   소스 repo override (기본: starlog/ralph)
#>
[CmdletBinding()]
param(
    [string]$Version = "",
    [string]$Dir = "",
    [switch]$Quiet
)

$ErrorActionPreference = "Stop"

$Repo = if ($env:RALPH_REPO) { $env:RALPH_REPO } else { "starlog/ralph" }
if (-not $Dir) { $Dir = Join-Path $HOME ".local\bin" }

function Log {
    param([string]$Msg)
    if (-not $Quiet) { Write-Host $Msg }
}

function Die {
    param([string]$Msg)
    Write-Host "Error: $Msg" -ForegroundColor Red
    exit 1
}

# ─── 플랫폼 감지 ─────────────────────────────────────────────────────────────
$archRaw = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture
$arch = switch ($archRaw) {
    "X64"   { "x64" }
    "Arm64" { "arm64" }
    default { Die "Unsupported architecture: $archRaw" }
}
$rid = "win-$arch"

# 기본 zip 사용 - 향후 다른 OS 지원 위한 ext 변수 유지
$ext = "zip"

Log "Platform: $rid"

# ─── 최신 버전 조회 ──────────────────────────────────────────────────────────
function Get-LatestTag {
    $api = "https://api.github.com/repos/$Repo/releases/latest"
    try {
        $headers = @{ "User-Agent" = "ralph-installer" }
        $resp = Invoke-RestMethod -Uri $api -Headers $headers -ErrorAction Stop
        return $resp.tag_name
    }
    catch {
        Die "Failed to fetch latest release: $_"
    }
}

if (-not $Version) {
    Log "Fetching latest release tag..."
    $Version = Get-LatestTag
    if (-not $Version) { Die "Could not determine latest version" }
}
Log "Version:  $Version"

# ─── 다운로드 URL 결정 ───────────────────────────────────────────────────────
$asset    = "ralph-$Version-$rid.$ext"
$url      = "https://github.com/$Repo/releases/download/$Version/$asset"
$sumsUrl  = "https://github.com/$Repo/releases/download/$Version/ralph-$Version-SHA256SUMS.txt"

# ─── 임시 디렉토리에 다운로드 ────────────────────────────────────────────────
$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("ralph-install-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tmp -Force | Out-Null

try {
    $assetPath = Join-Path $tmp $asset
    Log "Downloading $asset..."
    try {
        Invoke-WebRequest -Uri $url -OutFile $assetPath -UseBasicParsing -ErrorAction Stop
    }
    catch {
        Die "Download failed: $url`n$_"
    }

    # ─── 체크섬 검증 (제공되면) ──────────────────────────────────────────────
    $sumsPath = Join-Path $tmp "SHA256SUMS.txt"
    $haveSums = $false
    try {
        Invoke-WebRequest -Uri $sumsUrl -OutFile $sumsPath -UseBasicParsing -ErrorAction Stop
        $haveSums = $true
    }
    catch {
        # SHA256SUMS.txt이 없을 수도 있음 — 무시하고 진행
    }

    if ($haveSums) {
        Log "Verifying SHA256..."
        $expected = $null
        foreach ($line in Get-Content $sumsPath) {
            $parts = $line -split '\s+', 2
            if ($parts.Count -eq 2 -and $parts[1].Trim() -eq $asset) {
                $expected = $parts[0].Trim()
                break
            }
        }
        if ($expected) {
            $actual = (Get-FileHash -Path $assetPath -Algorithm SHA256).Hash.ToLower()
            $expectedLower = $expected.ToLower()
            if ($actual -ne $expectedLower) {
                Die "SHA256 mismatch: expected $expectedLower, got $actual"
            }
            Log "  SHA256 OK"
        }
        else {
            Log "  (no checksum entry for $asset in SHA256SUMS.txt, skipping)"
        }
    }

    # ─── 압축 해제 ──────────────────────────────────────────────────────────
    Log "Extracting..."
    $extractDir = Join-Path $tmp "extract"
    New-Item -ItemType Directory -Path $extractDir -Force | Out-Null
    Expand-Archive -Path $assetPath -DestinationPath $extractDir -Force

    $binName = "ralph.exe"
    $binSrc = Get-ChildItem -Path $extractDir -Filter $binName -Recurse -File | Select-Object -First 1
    if (-not $binSrc) { Die "Binary '$binName' not found in archive" }

    # ─── 설치 ───────────────────────────────────────────────────────────────
    if (-not (Test-Path $Dir)) {
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
    }
    $dest = Join-Path $Dir $binName
    Copy-Item -Path $binSrc.FullName -Destination $dest -Force
    Log "Installed: $dest"

    # ─── PATH 안내 ──────────────────────────────────────────────────────────
    $userPath = [Environment]::GetEnvironmentVariable("PATH", "User")
    $machinePath = [Environment]::GetEnvironmentVariable("PATH", "Machine")
    $allPaths = @()
    if ($userPath) { $allPaths += ($userPath -split ';') }
    if ($machinePath) { $allPaths += ($machinePath -split ';') }
    $inPath = $allPaths | Where-Object { $_.Trim().TrimEnd('\') -ieq $Dir.TrimEnd('\') }

    if ($inPath) {
        Log "Done. Run 'ralph --help' to get started."
    }
    else {
        Log ""
        Log "Note: $Dir is not in your PATH."
        Log "To add it permanently to user PATH, run in PowerShell:"
        Log ""
        Log "  [Environment]::SetEnvironmentVariable('PATH', `"$Dir;`" + [Environment]::GetEnvironmentVariable('PATH','User'), 'User')"
        Log ""
        Log "Or for the current session only:"
        Log ""
        Log "  `$env:PATH = `"$Dir;`" + `$env:PATH"
        Log ""
    }
}
finally {
    if (Test-Path $tmp) {
        Remove-Item -Path $tmp -Recurse -Force -ErrorAction SilentlyContinue
    }
}
