#!/usr/bin/env pwsh
$ErrorActionPreference = 'Stop'

$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
$SamplesDir = Join-Path $ScriptDir 'samples'

if (-not (Test-Path -LiteralPath $SamplesDir -PathType Container)) {
    Write-Error "samples 디렉터리를 찾을 수 없습니다: $SamplesDir"
    exit 1
}

Get-ChildItem -LiteralPath $SamplesDir -Force |
    Where-Object { $_.Name -ne 'PRD.md' } |
    Remove-Item -Recurse -Force

Write-Host 'samples 정리 완료 (PRD.md 제외).'
