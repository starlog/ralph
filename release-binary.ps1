<#
.SYNOPSIS
    Ralph release publisher (PowerShell). Builds self-contained binaries for each
    platform, creates a matching git tag, pushes it, and publishes a GitHub Release
    using the `gh` CLI.

.DESCRIPTION
    Mirrors release-binary.sh. When -Version is omitted, the next version is
    auto-computed from the latest 'v*' tag based on commit-message analysis:
        +0.1  (major) if any commit since the latest tag matches feature/refactor markers
                      (기능추가, 기능개선, 리팩토링, feat, BREAKING)
        +0.01 (minor) otherwise (docs / chore / fix only)

.EXAMPLE
    ./release-binary.ps1                        # auto-bump
    ./release-binary.ps1 -Bump major            # force +0.1
    ./release-binary.ps1 -Version v1.3          # explicit
    ./release-binary.ps1 -SkipBuild             # reuse existing dist/
    ./release-binary.ps1 -DryRun                # build & package only
    ./release-binary.ps1 -NoTag                 # skip tag creation/push
#>

[CmdletBinding(DefaultParameterSetName = 'Auto')]
param(
    [Parameter(ParameterSetName = 'Explicit')]
    [string]$Version,

    [Parameter(ParameterSetName = 'Auto')]
    [ValidateSet('major', 'minor')]
    [string]$Bump,

    [string]$Notes,
    [switch]$Draft,
    [switch]$Prerelease,
    [switch]$NoTag,
    [switch]$NoPush,
    [switch]$AllowDirty,
    [switch]$SkipBuild,
    [switch]$DryRun
)

$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$Project   = Join-Path $ScriptDir 'Ralph\Ralph.csproj'
$DistDir   = Join-Path $ScriptDir 'dist'
$Repo      = if ($env:RALPH_REPO) { $env:RALPH_REPO } else { 'starlog/ralph' }

# RID list mirrors .github/workflows/release.yml
$Rids = @('win-x64', 'osx-x64', 'osx-arm64', 'linux-x64', 'linux-arm64')

function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
function Fail       { param([string]$Message) Write-Error $Message; exit 1 }
function Need       { param([string]$Cmd) if (-not (Get-Command $Cmd -ErrorAction SilentlyContinue)) { Fail "'$Cmd' is required but not installed" } }

# ─── Tool checks ───────────────────────────────────────────────────────────
Need dotnet
Need tar
Need git
if (-not $DryRun) { Need gh }

if ($Notes -and -not (Test-Path $Notes)) {
    Fail "Notes file not found: $Notes"
}

Set-Location $ScriptDir

# ─── Version resolution ────────────────────────────────────────────────────
function Get-LatestTag {
    $tags = git tag --list 'v[0-9]*' --sort=-v:refname
    if ($LASTEXITCODE -ne 0) { return $null }
    $tags | Select-Object -First 1
}

function Get-BumpKind {
    param([string]$SinceTag)
    $log = git log --format='%s' "$SinceTag..HEAD" 2>$null
    if (-not $log) { return 'minor' }
    if ($log -match '기능추가|기능개선|리팩토링|^feat(\(|:|!)|BREAKING CHANGE') {
        return 'major'
    }
    return 'minor'
}

function Step-Version {
    param([string]$Current, [string]$Kind)
    $delta = if ($Kind -eq 'major') { 0.1 } elseif ($Kind -eq 'minor') { 0.01 } else { Fail "internal: unknown bump kind '$Kind'" }
    $num = [double]($Current -replace '^v', '')
    $next = [math]::Round($num + $delta, 2)
    $s = $next.ToString('0.##', [System.Globalization.CultureInfo]::InvariantCulture)
    return "v$s"
}

if (-not $Version) {
    $current = Get-LatestTag
    if (-not $current) { Fail "No prior 'v*' tag found; pass -Version explicitly for the first release" }

    if (-not $Bump) {
        $Bump = Get-BumpKind -SinceTag $current
        Write-Step "Auto-detected bump kind: $Bump (from commits since $current)"
    }
    $Version = Step-Version -Current $current -Kind $Bump
    Write-Step "Resolved version: $current → $Version"
}

if ($Version -notmatch '^v[0-9]') {
    Fail "Version must start with 'v' followed by a digit (e.g. v1.3), got: $Version"
}

# ─── Tag (before build, so a build failure leaves no orphan tag pushed) ────
if (-not $NoTag -and -not $DryRun) {
    git rev-parse -q --verify "refs/tags/$Version" *> $null
    $tagExists = ($LASTEXITCODE -eq 0)

    if ($tagExists) {
        Write-Step "Tag $Version already exists locally; skipping creation"
    }
    else {
        if (-not $AllowDirty) {
            git diff-index --quiet HEAD -- *> $null
            if ($LASTEXITCODE -ne 0) {
                Fail "Working tree has uncommitted changes; commit/stash or pass -AllowDirty"
            }
        }
        Write-Step "Creating annotated tag $Version at HEAD"
        git tag -a $Version -m "Release $Version"
        if ($LASTEXITCODE -ne 0) { Fail "git tag failed" }
    }

    if (-not $NoPush) {
        $remoteTags = git ls-remote --tags origin "refs/tags/$Version" 2>$null
        if ($remoteTags -and $remoteTags -match "refs/tags/$Version") {
            Write-Step "Tag $Version already on origin; skipping push"
        }
        else {
            Write-Step "Pushing tag $Version to origin"
            git push origin "refs/tags/$Version"
            if ($LASTEXITCODE -ne 0) { Fail "git push failed" }
        }
    }
}

# ─── Build ─────────────────────────────────────────────────────────────────
if (-not $SkipBuild) {
    Write-Step "Cleaning $DistDir"
    if (Test-Path $DistDir) { Remove-Item -Recurse -Force $DistDir }
    New-Item -ItemType Directory -Path $DistDir | Out-Null

    foreach ($rid in $Rids) {
        Write-Step "Publishing $rid"
        dotnet publish $Project -c Release -r $rid -o (Join-Path $DistDir $rid) --nologo -v q
        if ($LASTEXITCODE -ne 0) { Fail "dotnet publish failed for $rid" }
    }
}
else {
    Write-Step "Skipping build (-SkipBuild); expecting artifacts in $DistDir"
    if (-not (Test-Path $DistDir)) { Fail "$DistDir does not exist; cannot -SkipBuild" }
}

# ─── Package ───────────────────────────────────────────────────────────────
Write-Step "Packaging archives in $DistDir"

# Clean any stale archives for this version
Get-ChildItem -Path $DistDir -File -Filter "ralph-$Version-*" -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -match '\.(zip|tar\.gz)$' -or $_.Name -eq "ralph-$Version-SHA256SUMS.txt" } |
    Remove-Item -Force

foreach ($rid in $Rids) {
    $src = Join-Path $DistDir $rid
    if ($rid -like 'win-*') {
        $bin = 'ralph.exe'
        $binPath = Join-Path $src $bin
        if (-not (Test-Path $binPath)) { Fail "Missing $binPath" }
        $archive = "ralph-$Version-$rid.zip"
        $archivePath = Join-Path $DistDir $archive
        Compress-Archive -Path $binPath -DestinationPath $archivePath -Force
    }
    else {
        $bin = 'ralph'
        $binPath = Join-Path $src $bin
        if (-not (Test-Path $binPath)) { Fail "Missing $binPath" }
        $archive = "ralph-$Version-$rid.tar.gz"
        $archivePath = Join-Path $DistDir $archive
        # tar -C preserves a flat archive with just the binary
        tar -czf $archivePath -C $src $bin
        if ($LASTEXITCODE -ne 0) { Fail "tar failed for $rid" }
    }
    Write-Step "  $archive"
}

# ─── Checksums ─────────────────────────────────────────────────────────────
$SumsFile = "ralph-$Version-SHA256SUMS.txt"
$SumsPath = Join-Path $DistDir $SumsFile
Write-Step "Generating $SumsFile"

$archiveFiles = Get-ChildItem -Path $DistDir -File |
    Where-Object { $_.Name -match "^ralph-$([regex]::Escape($Version))-.+\.(zip|tar\.gz)$" } |
    Sort-Object Name

# Match `shasum -a 256` output format: "<hash>  <filename>"
$lines = foreach ($f in $archiveFiles) {
    $hash = (Get-FileHash -Algorithm SHA256 -Path $f.FullName).Hash.ToLowerInvariant()
    "$hash  $($f.Name)"
}
Set-Content -Path $SumsPath -Value $lines -Encoding ascii -NoNewline:$false

$Artifacts = @()
foreach ($rid in $Rids) {
    if ($rid -like 'win-*') {
        $Artifacts += (Join-Path $DistDir "ralph-$Version-$rid.zip")
    }
    else {
        $Artifacts += (Join-Path $DistDir "ralph-$Version-$rid.tar.gz")
    }
}
$Artifacts += $SumsPath

Write-Step "Artifacts ready:"
foreach ($f in $Artifacts) { Write-Host "    $f" }

if ($DryRun) {
    Write-Step "Dry run complete; skipping gh release."
    exit 0
}

# ─── Publish via gh ────────────────────────────────────────────────────────
Set-Location $ScriptDir

gh release view $Version --repo $Repo *> $null
$exists = ($LASTEXITCODE -eq 0)

if ($exists) {
    Write-Step "Release $Version already exists on $Repo; uploading assets (clobber)"
    gh release upload $Version @Artifacts --repo $Repo --clobber
    if ($LASTEXITCODE -ne 0) { Fail "gh release upload failed" }
}
else {
    Write-Step "Creating release $Version on $Repo"
    $createArgs = @($Version, '--repo', $Repo, '--title', $Version)
    if ($Draft)       { $createArgs += '--draft' }
    if ($Prerelease)  { $createArgs += '--prerelease' }
    if ($Notes) {
        $createArgs += @('--notes-file', $Notes)
    }
    else {
        $createArgs += '--generate-notes'
    }
    gh release create @createArgs @Artifacts
    if ($LASTEXITCODE -ne 0) { Fail "gh release create failed" }
}

Write-Step "Done. View at: https://github.com/$Repo/releases/tag/$Version"
