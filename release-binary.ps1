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

# Windows 콘솔(cp949)에서 git이 한국어 커밋 요약을 stdout에 쓸 때
# "fatal: unknown write failure on standard output"으로 죽는 것을 방지.
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$OutputEncoding           = [System.Text.Encoding]::UTF8

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

# ─── Update version references in source/docs via claude CLI ───────────────
# Files that display the current version and must be kept in sync:
#   - Ralph/Commands/DisplayHelpers.cs  (const Version, without the "v" prefix)
#   - CLAUDE.md                          ("Current version: **vX.Y**.")
#   - README.md                          ("Current version: **vX.Y**.")
#   - README.ko.md                       ("현재 버전: **vX.Y**.")
function Update-VersionRefs {
    param([string]$NewVersion)

    Need claude
    $stripped = $NewVersion -replace '^v', ''
    Write-Step "Updating version references to $NewVersion via claude CLI"

    $prompt = @"
Bump the Ralph project version to $NewVersion. Make exactly these edits and nothing else:

1. File: Ralph/Commands/DisplayHelpers.cs
   Replace the existing Version constant value so the line reads:
       public const string Version = "$stripped";
   (no leading "v" — this constant holds the bare number).

2. File: CLAUDE.md
   In the "## Project Overview" paragraph, replace the existing "Current version: **vX.Y**." sentence so it reads "Current version: **$NewVersion**.".

3. File: README.md
   On the first paragraph after the H1, replace the existing "Current version: **vX.Y**." sentence so it reads "Current version: **$NewVersion**.".

4. File: README.ko.md
   On the first paragraph after the H1, replace the existing "현재 버전: **vX.Y**." sentence so it reads "현재 버전: **$NewVersion**.".

Use the Edit tool for each file. Do not modify any other lines, files, or formatting. Do not create new files. Do not run git or any other shell commands.
"@

    claude --dangerously-skip-permissions -p $prompt
    if ($LASTEXITCODE -ne 0) { Fail "claude version-ref update failed" }
}

if (-not $NoTag -and -not $DryRun) {
    git rev-parse -q --verify "refs/tags/$Version" *> $null
    $tagExistsForBump = ($LASTEXITCODE -eq 0)

    if ($tagExistsForBump) {
        Write-Step "Tag $Version already exists locally; skipping version-ref update"
    }
    else {
        if (-not $AllowDirty) {
            git diff-index --quiet HEAD -- *> $null
            if ($LASTEXITCODE -ne 0) {
                Fail "Working tree has uncommitted changes; commit/stash or pass -AllowDirty"
            }
        }

        Update-VersionRefs -NewVersion $Version

        $versionFiles = @(
            'Ralph/Commands/DisplayHelpers.cs',
            'CLAUDE.md',
            'README.md',
            'README.ko.md'
        )
        git diff --quiet -- @versionFiles *> $null
        $unchanged = ($LASTEXITCODE -eq 0)
        if (-not $unchanged) {
            Write-Step "Committing version bump to $Version"
            git add -- @versionFiles
            if ($LASTEXITCODE -ne 0) { Fail "git add failed" }
            git commit -m "릴리스: 버전을 $Version 으로 업데이트"
            if ($LASTEXITCODE -ne 0) { Fail "git commit failed" }
        }
        else {
            Write-Step "Version references already at $Version; nothing to commit"
        }
    }
}

# ─── Generate bilingual release notes via claude CLI ──────────────────────
# Builds a Markdown body with two sections — English "What's Changed" and
# Korean "변경 사항" — derived from the commit log between the previous tag
# and $Version, plus a Full Changelog compare link. Replaces the default
# `--generate-notes` body which is just a compare URL.
function Build-ReleaseNotes {
    param(
        [string]$VersionTag,
        [string]$PrevTag,
        [string]$OutPath
    )

    Need claude

    $commits = git log --format='- %s' "$PrevTag..$VersionTag" 2>$null
    if (-not $commits) {
        $commits = git log --format='- %s' -n 20 $VersionTag 2>$null
    }
    $commitsText = if ($commits) { ($commits -join "`n") } else { "(no commits found between $PrevTag and $VersionTag)" }

    $prompt = @"
Write GitHub release notes for Ralph $VersionTag based on these commits since ${PrevTag}:

$commitsText

Output exactly this Markdown structure and nothing else (no preamble, no code fences, no trailing text):

## What's Changed

<2-5 bullet points summarising user-facing changes in English. Group related commits. Drop noise like the version-bump / release commits ("릴리스: 버전을 ..."). Each bullet starts with a past-tense verb.>

## 변경 사항

<Same bullets translated into natural Korean. Match the count and ordering of the English bullets.>

**Full Changelog**: https://github.com/$Repo/compare/$PrevTag...$VersionTag
"@

    $output = claude --dangerously-skip-permissions -p $prompt
    if ($LASTEXITCODE -ne 0 -or -not $output) { Fail "Failed to generate release notes via claude" }
    if ($output -is [array]) { $output = $output -join "`n" }
    Set-Content -Path $OutPath -Value $output -Encoding UTF8
}

$GeneratedNotesFile = $null

# ─── Tag (before build, so a build failure leaves no orphan tag pushed) ────
if (-not $NoTag -and -not $DryRun) {
    git rev-parse -q --verify "refs/tags/$Version" *> $null
    $tagExists = ($LASTEXITCODE -eq 0)

    if ($tagExists) {
        Write-Step "Tag $Version already exists locally; skipping creation"
    }
    else {
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

# Generate bilingual notes if user didn't pass -Notes
if (-not $Notes) {
    $prevTag = git tag --list 'v[0-9]*' --sort=-v:refname |
        Where-Object { $_ -ne $Version } |
        Select-Object -First 1
    if ($prevTag) {
        $GeneratedNotesFile = [System.IO.Path]::GetTempFileName()
        Write-Step "Generating bilingual release notes ($prevTag → $Version) via claude CLI"
        Build-ReleaseNotes -VersionTag $Version -PrevTag $prevTag -OutPath $GeneratedNotesFile
        $Notes = $GeneratedNotesFile
    }
    else {
        Write-Step "No previous tag found; release will have no body"
    }
}

try {
    gh release view $Version --repo $Repo *> $null
    $exists = ($LASTEXITCODE -eq 0)

    if ($exists) {
        Write-Step "Release $Version already exists on $Repo; uploading assets (clobber)"
        gh release upload $Version @Artifacts --repo $Repo --clobber
        if ($LASTEXITCODE -ne 0) { Fail "gh release upload failed" }
        if ($Notes) {
            Write-Step "Updating release body with bilingual notes"
            gh release edit $Version --repo $Repo --notes-file $Notes
            if ($LASTEXITCODE -ne 0) { Fail "gh release edit failed" }
        }
    }
    else {
        Write-Step "Creating release $Version on $Repo"
        $createArgs = @($Version, '--repo', $Repo, '--title', $Version)
        if ($Draft)       { $createArgs += '--draft' }
        if ($Prerelease)  { $createArgs += '--prerelease' }
        if ($Notes) {
            $createArgs += @('--notes-file', $Notes)
        }
        gh release create @createArgs @Artifacts
        if ($LASTEXITCODE -ne 0) { Fail "gh release create failed" }
    }
}
finally {
    if ($GeneratedNotesFile -and (Test-Path $GeneratedNotesFile)) {
        Remove-Item $GeneratedNotesFile -Force -ErrorAction SilentlyContinue
    }
}

Write-Step "Done. View at: https://github.com/$Repo/releases/tag/$Version"
