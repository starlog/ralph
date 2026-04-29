#!/usr/bin/env pwsh
# ralph.ps1 - Task executor for projects using Claude Code
# PowerShell port of ralph.sh

#Requires -Version 7.0

[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [string]$Command,

    [Parameter(Position = 1)]
    [string]$Argument
)

$ErrorActionPreference = 'Stop'
$Script:TasksFile = "tasks.json"
$Script:LogDir = ".ralph-logs"
$Script:MaxRetries = if ($env:MAX_RETRIES) { [int]$env:MAX_RETRIES } else { 2 }
$Script:RetryDelay = if ($env:RETRY_DELAY) { [int]$env:RETRY_DELAY } else { 5 }
$Script:ExecMode = "interactive"  # interactive, auto, dry-run
$Script:CommitOnComplete = $true
$Script:CommitTemplate = "[Task #{taskId}] {taskTitle}"
$Script:LogFile = $null
$Script:TmpFilesToClean = @()

# ─── Colors ────────────────────────────────────────────────────────────────
$Script:Colors = @{
    Red    = 'Red'
    Green  = 'Green'
    Yellow = 'Yellow'
    Blue   = 'Blue'
    Cyan   = 'Cyan'
}

# ─── Cleanup handler ───────────────────────────────────────────────────────
$Script:CleanupHandler = {
    Write-Host ""
    Write-Host "Interrupted. Cleaning up..." -ForegroundColor Red

    # Kill child processes
    Get-Job | Stop-Job -PassThru | Remove-Job -Force

    # Clean temp files
    foreach ($file in $Script:TmpFilesToClean) {
        if (Test-Path $file) {
            Remove-Item $file -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Host "Aborted." -ForegroundColor Red
    exit 130
}

# Register cleanup on Ctrl+C
$null = Register-EngineEvent -SourceIdentifier PowerShell.Exiting -Action $Script:CleanupHandler

# ─── Logging ───────────────────────────────────────────────────────────────
function Initialize-Logging {
    if (-not (Test-Path $Script:LogDir)) {
        New-Item -ItemType Directory -Path $Script:LogDir -Force | Out-Null
    }

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
    $Script:LogFile = Join-Path $Script:LogDir "ralph-$timestamp.log"

    $header = @"
Ralph session started at $(Get-Date)
Tasks file: $Script:TasksFile
Exec mode: $Script:ExecMode
────────────────────────────────────────
"@
    Set-Content -Path $Script:LogFile -Value $header
}

function Write-Log {
    param(
        [string]$Level,
        [string]$Message
    )

    if ($Script:LogFile) {
        $timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
        Add-Content -Path $Script:LogFile -Value "[$timestamp] [$Level] $Message"
    }
}

function Write-TaskStart {
    param([string]$TaskId, [string]$Title)
    Write-Log "INFO" "=== Task started: $TaskId - $Title ==="
}

function Write-TaskEnd {
    param([string]$TaskId, [string]$Status)
    Write-Log "INFO" "=== Task ended: $TaskId - status: $Status ==="
}

# ─── Dependency checks ─────────────────────────────────────────────────────
function Test-Dependencies {
    # Check claude
    if (-not (Get-Command claude -ErrorAction SilentlyContinue)) {
        Write-Host "Error: claude CLI is required but not installed." -ForegroundColor Red
        Write-Host "Install Claude Code from: https://claude.ai/code"
        exit 1
    }
}

function Assert-TasksFile {
    if (-not (Test-Path $Script:TasksFile)) {
        Write-Host "Error: $Script:TasksFile not found. Run './ralph.ps1 --plan <prd-file>' to generate it." -ForegroundColor Red
        exit 1
    }
}

# ─── Workflow settings ─────────────────────────────────────────────────────
function Read-WorkflowSettings {
    if (Test-Path $Script:TasksFile) {
        $tasks = Get-Content $Script:TasksFile -Raw | ConvertFrom-Json
        if ($tasks.workflow.onTaskComplete.commitChanges -ne $null) {
            $Script:CommitOnComplete = $tasks.workflow.onTaskComplete.commitChanges
        }
        if ($tasks.workflow.onTaskComplete.commitMessageTemplate) {
            $Script:CommitTemplate = $tasks.workflow.onTaskComplete.commitMessageTemplate
        }
    }
}

# ─── Resolve RALPH_HOME ────────────────────────────────────────────────────
$Script:RalphHome = $PSScriptRoot
$Script:SchemaFile = Join-Path $Script:RalphHome "ralph-schema.json"

# ─── Safe JSON update helper ───────────────────────────────────────────────
function Get-TasksData {
    try {
        $content = Get-Content $Script:TasksFile -Raw -ErrorAction Stop
        return $content | ConvertFrom-Json
    }
    catch {
        Write-Log "ERROR" "Failed to read tasks file: $_"
        Write-Host "Error: Failed to read tasks.json" -ForegroundColor Red
        return $null
    }
}

function Save-TasksData {
    param([object]$TasksData)

    try {
        $json = $TasksData | ConvertTo-Json -Depth 100
        Set-Content -Path $Script:TasksFile -Value $json -ErrorAction Stop
        return $true
    }
    catch {
        Write-Log "ERROR" "Failed to save tasks file: $_"
        Write-Host "Error: Failed to save tasks.json" -ForegroundColor Red
        return $false
    }
}

# ─── Task query functions ──────────────────────────────────────────────────
function Get-NextTask {
    $tasksData = Get-TasksData
    if (-not $tasksData) { return $null }

    $pendingTask = $tasksData.tasks | Where-Object { -not $_.done } | Select-Object -First 1
    return $pendingTask.id
}

function Get-TaskInfo {
    param([string]$TaskId)

    $tasksData = Get-TasksData
    if (-not $tasksData) { return $null }

    $task = $tasksData.tasks | Where-Object { $_.id -eq $TaskId } | Select-Object -First 1
    return $task
}

function Get-TaskPrompt {
    param([string]$TaskId)

    $task = Get-TaskInfo $TaskId
    if (-not $task) { return $null }

    return $task.prompt
}

function Get-OutputFiles {
    param([string]$TaskId)

    $task = Get-TaskInfo $TaskId
    if (-not $task -or -not $task.outputFiles) { return "" }

    return ($task.outputFiles -join ", ")
}

# ─── Dependency management ─────────────────────────────────────────────────
function Test-TaskDependencies {
    param([string]$TaskId)

    $tasksData = Get-TasksData
    if (-not $tasksData) { return $false }

    $task = $tasksData.tasks | Where-Object { $_.id -eq $TaskId } | Select-Object -First 1
    if (-not $task) { return $false }

    $deps = $task.dependsOn
    if (-not $deps -or $deps.Count -eq 0) {
        return $true
    }

    $blocked = $false
    foreach ($depId in $deps) {
        $depTask = $tasksData.tasks | Where-Object { $_.id -eq $depId } | Select-Object -First 1
        if (-not $depTask -or -not $depTask.done) {
            Write-Host "Blocked: Task '$TaskId' depends on '$depId' which is not done yet." -ForegroundColor Red
            Write-Log "WARN" "Task $TaskId blocked by dependency: $depId"
            $blocked = $true
        }
    }

    return -not $blocked
}

function Get-NextReadyTask {
    $tasksData = Get-TasksData
    if (-not $tasksData) { return $null }

    $pendingTasks = $tasksData.tasks | Where-Object { -not $_.done }

    foreach ($task in $pendingTasks) {
        if (Test-TaskDependencies $task.id) {
            return $task.id
        }
    }

    return $null
}

# ─── Task state mutations ──────────────────────────────────────────────────
function Set-TaskDone {
    param([string]$TaskId)

    $tasksData = Get-TasksData
    if (-not $tasksData) { return $false }

    $task = $tasksData.tasks | Where-Object { $_.id -eq $TaskId } | Select-Object -First 1
    if ($task) {
        $task.done = $true
        return Save-TasksData $tasksData
    }

    return $false
}

function Set-SubtaskDone {
    param([string]$TaskId, [string]$SubtaskId)

    $tasksData = Get-TasksData
    if (-not $tasksData) { return $false }

    $task = $tasksData.tasks | Where-Object { $_.id -eq $TaskId } | Select-Object -First 1
    if ($task -and $task.subtasks) {
        $subtask = $task.subtasks | Where-Object { $_.id -eq $SubtaskId } | Select-Object -First 1
        if ($subtask) {
            $subtask.done = $true
            return Save-TasksData $tasksData
        }
    }

    return $false
}

# ─── Sensitive file patterns ───────────────────────────────────────────────
$Script:SensitivePatterns = @(
    ".env", ".env.*", "*.pem", "*.key", "*.p12", "*.pfx",
    "credentials.json", "service-account*.json", ".secret*",
    "*.secrets", "id_rsa", "id_ed25519"
)

# ─── Git commit ────────────────────────────────────────────────────────────
function Invoke-GitCommit {
    param([string]$TaskId, [string]$TaskTitle)

    if (-not $Script:CommitOnComplete) {
        return
    }

    $commitMsg = $Script:CommitTemplate -replace '{taskId}', $TaskId -replace '{taskTitle}', $TaskTitle

    Write-Host "Committing changes..." -ForegroundColor Blue
    Write-Log "INFO" "Committing: $commitMsg"

    # Stage all files
    & git add -A

    # Unstage sensitive files
    foreach ($pattern in $Script:SensitivePatterns) {
        & git reset HEAD -- $pattern 2>$null
    }

    # Warn about sensitive files
    $unstaged = & git status --porcelain | Select-String '^\?\?.*\.(env|pem|key|p12|pfx|secrets)'
    if ($unstaged) {
        Write-Host "Warning: Sensitive files detected and excluded from commit:" -ForegroundColor Yellow
        Write-Host $unstaged
        Write-Log "WARN" "Sensitive files excluded: $unstaged"
    }

    # Commit with Co-Authored-By
    $fullMsg = "$commitMsg`n`nCo-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"
    $result = & git commit -m $fullMsg 2>&1

    if ($LASTEXITCODE -eq 0) {
        Write-Host "Committed: $commitMsg" -ForegroundColor Green
        Write-Log "INFO" "Commit successful: $commitMsg"
    }
    else {
        Write-Host "No changes to commit or commit failed." -ForegroundColor Yellow
        Write-Log "WARN" "Commit failed or no changes"
    }
}

# ─── Display ───────────────────────────────────────────────────────────────
function Show-Task {
    param([string]$TaskId)

    $taskInfo = Get-TaskInfo $TaskId
    if (-not $taskInfo) { return }

    $outputFiles = Get-OutputFiles $TaskId
    $deps = if ($taskInfo.dependsOn) { $taskInfo.dependsOn -join ", " } else { "" }

    $tasksData = Get-TasksData
    if (-not $tasksData) { return }

    $totalTasks = $tasksData.tasks.Count
    $taskOrder = 0
    for ($i = 0; $i -lt $totalTasks; $i++) {
        if ($tasksData.tasks[$i].id -eq $TaskId) {
            $taskOrder = $i + 1
            break
        }
    }

    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
    Write-Host "[$taskOrder/$totalTasks] " -ForegroundColor Yellow -NoNewline
    Write-Host "Task ID: " -ForegroundColor Green -NoNewline
    Write-Host $TaskId
    Write-Host "Phase: " -ForegroundColor Green -NoNewline
    Write-Host "$($taskInfo.phase) | " -NoNewline
    Write-Host "Category: " -ForegroundColor Green -NoNewline
    Write-Host $taskInfo.category
    Write-Host "Title: " -ForegroundColor Green -NoNewline
    Write-Host $taskInfo.title
    Write-Host "Description: " -ForegroundColor Green -NoNewline
    Write-Host $taskInfo.description

    if ($deps) {
        Write-Host "Depends On: " -ForegroundColor Cyan -NoNewline
        Write-Host $deps
    }

    if ($outputFiles) {
        Write-Host "Output Files: " -ForegroundColor Cyan -NoNewline
        Write-Host $outputFiles
    }

    if ($taskInfo.prompt) {
        Write-Host "Claude Prompt: " -ForegroundColor Cyan -NoNewline
        Write-Host "(available)"
    }

    if ($taskInfo.subtasks) {
        Write-Host "Subtasks:" -ForegroundColor Yellow
        foreach ($subtask in $taskInfo.subtasks) {
            $check = if ($subtask.done) { "✓" } else { " " }
            Write-Host "  [$check] $($subtask.id): $($subtask.title)"
        }
    }

    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
    Write-Host ""
}

# ─── Plan generation ───────────────────────────────────────────────────────
function New-Plan {
    param([string]$PrdFile)

    if (-not (Test-Path $PrdFile)) {
        Write-Host "Error: File '$PrdFile' not found." -ForegroundColor Red
        return $false
    }

    if (-not (Test-Path $Script:SchemaFile)) {
        Write-Host "Error: Schema file '$Script:SchemaFile' not found." -ForegroundColor Red
        return $false
    }

    $prdContent = Get-Content $PrdFile -Raw
    $schemaContent = Get-Content $Script:SchemaFile -Raw

    # Check for existing tasks.json
    if (Test-Path $Script:TasksFile) {
        Write-Host "Warning: $Script:TasksFile already exists." -ForegroundColor Yellow
        $overwrite = Read-Host "Overwrite? (y/n)"
        if ($overwrite -ne 'y' -and $overwrite -ne 'Y') {
            Write-Host "Aborted." -ForegroundColor Red
            return $false
        }

        $timestamp = Get-Date -Format "yyyyMMdd-HHmmss"
        $backupFile = "$Script:TasksFile.backup.$timestamp"
        Copy-Item $Script:TasksFile $backupFile
        Write-Host "Backup saved: $backupFile" -ForegroundColor Cyan
    }

    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
    Write-Host "       RALPH - Plan Generator" -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
    Write-Host "PRD File: " -ForegroundColor Cyan -NoNewline
    Write-Host $PrdFile
    Write-Host "Schema: " -ForegroundColor Cyan -NoNewline
    Write-Host $Script:SchemaFile
    Write-Host "Output: " -ForegroundColor Cyan -NoNewline
    Write-Host $Script:TasksFile
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
    Write-Host ""
    Write-Host "Generating task plan with Claude Code..." -ForegroundColor Cyan
    Write-Host ""

    # Build prompt
    $promptHeader = @'
You are a project planner that generates a tasks.json file for the Ralph task executor.

## Your Goal
Read the PRD (Product Requirements Document) below and produce a **single valid JSON** object that conforms to the provided JSON schema. Output ONLY the JSON — no markdown fences, no commentary.

## Task Generation Rules

1. **Break down the PRD into logical features or components.** Each feature becomes a "group" of 4 sequential tasks.

2. **For every feature/component, generate exactly 4 tasks in this order:**

   Step A - **Plan** (category: "plan")
      - id: `{feature}-plan`
      - The prompt must instruct Claude to: analyze requirements for this feature, examine the existing codebase, identify files to create/modify, design the architecture, and write a detailed implementation plan as a markdown file.
      - No dependsOn for the first feature's plan. Subsequent feature plans depend on the previous feature's commit task.

   Step B - **Implementation** (category: "implementation")
      - id: `{feature}-impl`
      - dependsOn: [`{feature}-plan`]
      - The prompt must instruct Claude to: implement the feature according to the plan created in the plan step, create all necessary files, and follow project conventions.

   Step C - **Testing** (category: "testing")
      - id: `{feature}-test`
      - dependsOn: [`{feature}-impl`]
      - The prompt must instruct Claude to: write and run tests for the implemented feature, ensure all tests pass, fix any issues found.

   Step D - **Commit** (category: "commit")
      - id: `{feature}-commit`
      - dependsOn: [`{feature}-test`]
      - The prompt must instruct Claude to: review all changes, stage the relevant files (not sensitive files like .env), and create a git commit with a descriptive message in Korean.

3. **Task ID format:** Use lowercase kebab-case. Example: `user-auth-plan`, `user-auth-impl`, `user-auth-test`, `user-auth-commit`

4. **Phase naming:** Group related features into phases (e.g., "phase1-setup", "phase2-core", "phase3-ui").

5. **Prompts must be detailed and self-contained.** Each prompt should include:
   - Specific files to create or modify (from the PRD context)
   - Technical requirements and acceptance criteria
   - Reference to project conventions when applicable
   - For plan tasks: output a markdown plan file at a specific path
   - For test tasks: specify what to test and expected coverage
   - For commit tasks: specify the commit scope and message format

6. **outputFiles:** List the files each task is expected to create or modify.

7. **Workflow settings:** Set `workflow.onTaskComplete.commitChanges` to `true` (auto-commit after each task step).

8. **All tasks start with `"done": false`.**

9. **Include a `projectName` and `version` field** derived from the PRD.

## JSON Schema
'@

    $prompt = @"
$promptHeader

``````json
$schemaContent
``````

## PRD Document (source: $PrdFile)

$prdContent

## Output
Generate the complete tasks.json now. Output ONLY valid JSON, nothing else.
"@

    # Run Claude Code
    $tmpOutput = [System.IO.Path]::GetTempFileName()
    $Script:TmpFilesToClean += $tmpOutput

    Write-Host "Running Claude Code..." -ForegroundColor Cyan
    Write-Host ""
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
    Write-Host "[Claude Code Output]" -ForegroundColor Yellow
    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow

    if (-not (Invoke-ClaudeStream $prompt $tmpOutput $true)) {
        Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
        Write-Host ""
        Write-Host "Error: Claude Code execution failed." -ForegroundColor Red
        return $false
    }

    Write-Host "━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━" -ForegroundColor Yellow
    Write-Host ""

    # Extract JSON from output
    $rawOutput = Get-Content $tmpOutput -Raw

    # Try to extract from code fences
    $jsonContent = $null
    if ($rawOutput -match '(?s)```(?:json)?\s*\n(.*?)\n```') {
        $lastMatch = $null
        $matches = [regex]::Matches($rawOutput, '(?s)```(?:json)?\s*\n(.*?)\n```')
        if ($matches.Count -gt 0) {
            $lastMatch = $matches[$matches.Count - 1].Groups[1].Value
            try {
                $jsonContent = $lastMatch | ConvertFrom-Json | ConvertTo-Json -Depth 100
            }
            catch {
                $jsonContent = $null
            }
        }
    }

    # Fallback: try parsing entire output
    if (-not $jsonContent) {
        try {
            $cleaned = $rawOutput -replace '(?s)```[^`]*```', ''
            $jsonContent = $cleaned | ConvertFrom-Json | ConvertTo-Json -Depth 100
        }
        catch {
            Write-Host "Error: No valid JSON found in Claude output." -ForegroundColor Red
            Write-Host "Raw output saved to: $tmpOutput" -ForegroundColor Yellow
            return $false
        }
    }

    # Validate structure
    $jsonObj = $jsonContent | ConvertFrom-Json
    if (-not $jsonObj.tasks -or $jsonObj.tasks -isnot [Array]) {
        Write-Host "Error: Generated JSON does not have a valid 'tasks' array." -ForegroundColor Red
        Write-Host "Raw output saved to: $tmpOutput" -ForegroundColor Yellow
        return $false
    }

    # Validate required fields
    $invalidTasks = $jsonObj.tasks | Where-Object { -not $_.id -or -not $_.title -or $null -eq $_.done }
    if ($invalidTasks) {
        Write-Host "Error: $($invalidTasks.Count) task(s) missing required fields (id, title, done)." -ForegroundColor Red
        Write-Host "Raw output saved to: $tmpOutput" -ForegroundColor Yellow
        return $false
    }

    # Validate 4-phase pattern (warn only)
    $totalTasks = $jsonObj.tasks.Count
    $planCount = ($jsonObj.tasks | Where-Object { $_.category -eq 'plan' }).Count
    $implCount = ($jsonObj.tasks | Where-Object { $_.category -eq 'implementation' }).Count
    $testCount = ($jsonObj.tasks | Where-Object { $_.category -eq 'testing' }).Count
    $commitCount = ($jsonObj.tasks | Where-Object { $_.category -eq 'commit' }).Count

    if ($planCount -ne $implCount -or $implCount -ne $testCount -or $testCount -ne $commitCount) {
        Write-Host "Warning: Uneven task phases — plan:$planCount impl:$implCount test:$testCount commit:$commitCount" -ForegroundColor Yellow
    }

    # Write validated JSON
    Set-Content -Path $Script:TasksFile -Value $jsonContent
    Remove-Item $tmpOutput -Force -ErrorAction SilentlyContinue

    # Summary
    Write-Host ""
    Write-Host "Plan generated successfully!" -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
    Write-Host "  Total tasks:  $totalTasks"
    Write-Host "  Features:     $planCount"
    Write-Host "  Per feature:  plan → implementation → testing → commit"
    Write-Host ""
    Write-Host "  Plan:           " -ForegroundColor Cyan -NoNewline
    Write-Host "$planCount tasks"
    Write-Host "  Implementation: " -ForegroundColor Cyan -NoNewline
    Write-Host "$implCount tasks"
    Write-Host "  Testing:        " -ForegroundColor Cyan -NoNewline
    Write-Host "$testCount tasks"
    Write-Host "  Commit:         " -ForegroundColor Cyan -NoNewline
    Write-Host "$commitCount tasks"
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
    Write-Host ""
    Write-Host "Next steps:"
    Write-Host "  ./ralph.ps1 --list       " -ForegroundColor Green -NoNewline
    Write-Host "Review generated tasks"
    Write-Host "  ./ralph.ps1 --dry-run    " -ForegroundColor Green -NoNewline
    Write-Host "Preview execution"
    Write-Host "  ./ralph.ps1 --run        " -ForegroundColor Green -NoNewline
    Write-Host "Execute all tasks"
    Write-Host ""

    return $true
}

# ─── Claude Code streaming helper ──────────────────────────────────────────
function Invoke-ClaudeStream {
    param(
        [string]$Prompt,
        [string]$OutputFile = "",
        [bool]$NoTools = $false
    )

    $rawFile = [System.IO.Path]::GetTempFileName()
    $Script:TmpFilesToClean += $rawFile

    try {
        # Build claude command
        $claudeArgs = @(
            '-p',
            '--dangerously-skip-permissions',
            '--output-format', 'stream-json',
            '--verbose',
            '--include-partial-messages'
        )

        if ($NoTools) {
            $claudeArgs += '--tools', '""'
            $claudeArgs += '--strict-mcp-config'
            $claudeArgs += '--model', 'sonnet'
            $env:CLAUDE_CODE_MAX_OUTPUT_TOKENS = if ($env:CLAUDE_CODE_MAX_OUTPUT_TOKENS) { $env:CLAUDE_CODE_MAX_OUTPUT_TOKENS } else { "65536" }
        }

        # Start claude process
        $psi = New-Object System.Diagnostics.ProcessStartInfo
        $psi.FileName = 'claude'
        $psi.Arguments = $claudeArgs -join ' '
        $psi.RedirectStandardInput = $true
        $psi.RedirectStandardOutput = $true
        $psi.RedirectStandardError = $true
        $psi.UseShellExecute = $false
        $psi.CreateNoWindow = $true

        $process = New-Object System.Diagnostics.Process
        $process.StartInfo = $psi
        $process.Start() | Out-Null

        # Send prompt via stdin if NoTools
        if ($NoTools) {
            $process.StandardInput.WriteLine($Prompt)
            $process.StandardInput.Close()
        }
        else {
            # For normal mode, pass prompt as arg (restart with prompt)
            $process.Kill()
            $claudeArgs += $Prompt
            $output = & claude @claudeArgs 2>&1
            Set-Content -Path $rawFile -Value ($output -join "`n")

            # Process output
            foreach ($line in $output) {
                try {
                    $json = $line | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if ($json) {
                        # Handle streaming deltas
                        if ($json.type -eq 'stream_event' -and $json.event.type -eq 'content_block_delta') {
                            $delta = $json.event.delta.text
                            if ($delta) {
                                Write-Host $delta -NoNewline
                                if ($Script:LogFile) {
                                    Add-Content -Path $Script:LogFile -Value $delta -NoNewline
                                }
                            }
                        }
                        # Handle final message
                        elseif ($json.type -eq 'assistant') {
                            $text = $json.message.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }
                            if ($text -and $OutputFile) {
                                Add-Content -Path $OutputFile -Value $text
                            }
                        }
                    }
                }
                catch {
                    # Not JSON, skip
                }
            }

            Write-Host ""
            return $LASTEXITCODE -eq 0
        }

        # Monitor output (for NoTools mode)
        $outputBuilder = New-Object System.Text.StringBuilder

        $asyncOutput = $process.StandardOutput
        $asyncError = $process.StandardError

        while (-not $process.HasExited) {
            $line = $asyncOutput.ReadLine()
            if ($line) {
                Add-Content -Path $rawFile -Value $line

                try {
                    $json = $line | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if ($json) {
                        # Handle streaming deltas
                        if ($json.type -eq 'stream_event' -and $json.event.type -eq 'content_block_delta') {
                            $delta = $json.event.delta.text
                            if ($delta) {
                                Write-Host $delta -NoNewline
                                if ($Script:LogFile) {
                                    Add-Content -Path $Script:LogFile -Value $delta -NoNewline
                                }
                                [void]$outputBuilder.Append($delta)
                            }
                        }
                        # Handle final message
                        elseif ($json.type -eq 'assistant') {
                            $text = $json.message.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }
                            if ($text) {
                                [void]$outputBuilder.AppendLine($text)
                            }
                        }
                    }
                }
                catch {
                    # Not JSON, skip
                }
            }
        }

        # Flush remaining output
        $remaining = $asyncOutput.ReadToEnd()
        if ($remaining) {
            Add-Content -Path $rawFile -Value $remaining
            foreach ($line in $remaining -split "`n") {
                try {
                    $json = $line | ConvertFrom-Json -ErrorAction SilentlyContinue
                    if ($json -and $json.type -eq 'assistant') {
                        $text = $json.message.content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text }
                        if ($text) {
                            [void]$outputBuilder.AppendLine($text)
                        }
                    }
                }
                catch { }
            }
        }

        Write-Host ""

        # Save output
        if ($OutputFile -and $outputBuilder.Length -gt 0) {
            Set-Content -Path $OutputFile -Value $outputBuilder.ToString()
        }

        $process.WaitForExit()
        return $process.ExitCode -eq 0
    }
    finally {
        if (Test-Path $rawFile) {
            Remove-Item $rawFile -Force -ErrorAction SilentlyContinue
        }
    }
}

# ─── Claude Code execution with retry ──────────────────────────────────────
function Invoke-Claude {
    param([string]$Prompt)

    Write-Host "Prompt:" -ForegroundColor Cyan
    Write-Host "─────────────────────────────────────────────"
    Write-Host $Prompt
    Write-Host "─────────────────────────────────────────────"
    Write-Host ""

    if ($Script:ExecMode -eq 'dry-run') {
        Write-Host "[DRY-RUN] Would execute Claude Code with above prompt" -ForegroundColor Cyan
        Write-Log "INFO" "[DRY-RUN] Skipped Claude Code execution"
        return $true
    }

    for ($attempt = 1; $attempt -le $Script:MaxRetries; $attempt++) {
        if ($attempt -gt 1) {
            Write-Host "Retry attempt $attempt/$Script:MaxRetries (waiting $($Script:RetryDelay)s)..." -ForegroundColor Yellow
            Write-Log "INFO" "Retry attempt $attempt/$Script:MaxRetries"
            Start-Sleep -Seconds $Script:RetryDelay
        }

        Write-Log "INFO" "Running Claude Code (attempt $attempt)"

        if (Invoke-ClaudeStream $Prompt) {
            Write-Log "INFO" "Claude Code execution successful"
            return $true
        }

        Write-Log "ERROR" "Claude Code failed (attempt $attempt)"
        Write-Host "Claude Code failed" -ForegroundColor Red
    }

    Write-Log "ERROR" "Claude Code failed after $Script:MaxRetries attempts"
    Write-Host "Claude Code failed after $Script:MaxRetries attempts" -ForegroundColor Red
    return $false
}

# ─── Interactive task runner ───────────────────────────────────────────────
function Invoke-Task {
    param([string]$TaskId)

    $taskInfo = Get-TaskInfo $TaskId
    if (-not $taskInfo) { return 1 }

    $prompt = Get-TaskPrompt $TaskId

    # Check dependencies
    if (-not (Test-TaskDependencies $TaskId)) {
        Write-Host "Skipping task due to unmet dependencies." -ForegroundColor Yellow
        Write-Log "WARN" "Skipped $TaskId: unmet dependencies"
        return 2
    }

    Show-Task $TaskId

    while ($true) {
        $response = Read-Host "Execute this task? (y/n/p=preview prompt/s=skip/q=quit)"

        switch ($response.ToLower()) {
            'y' {
                Write-TaskStart $TaskId $taskInfo.title
                Write-Host "Executing task: $($taskInfo.title)" -ForegroundColor Blue
                Write-Host ""

                if ($prompt) {
                    Write-Host "Running Claude Code..." -ForegroundColor Cyan
                    Write-Host ""

                    $fullPrompt = @"
Task ID: $TaskId
Task: $($taskInfo.title)

$prompt

참고: tasks.json 파일에서 apiSpecs, samplePages 등 추가 정보를 확인할 수 있습니다.
완료 후 생성된 파일 목록을 알려주세요.
"@

                    if (-not (Invoke-Claude $fullPrompt)) {
                        Write-Host ""
                        Write-Host "✗ Claude Code execution failed" -ForegroundColor Red
                        $continue = Read-Host "Continue anyway? (y/n)"
                        if ($continue -ne 'y' -and $continue -ne 'Y') {
                            Write-TaskEnd $TaskId "failed"
                            return 1
                        }
                    }

                    Write-Host ""
                    Write-Host "✓ Claude Code execution completed" -ForegroundColor Green
                }

                # Process subtasks
                if ($taskInfo.subtasks) {
                    $pendingSubtasks = $taskInfo.subtasks | Where-Object { -not $_.done }
                    foreach ($subtask in $pendingSubtasks) {
                        Write-Host "  Subtask: " -ForegroundColor Yellow -NoNewline
                        Write-Host $subtask.title
                        Set-SubtaskDone $TaskId $subtask.id
                        Write-Host "  ✓ Subtask completed" -ForegroundColor Green
                    }
                }

                Set-TaskDone $TaskId
                Write-Host "✓ Task completed: $($taskInfo.title)" -ForegroundColor Green
                Write-TaskEnd $TaskId "completed"

                Invoke-GitCommit $TaskId $taskInfo.title
                return 0
            }
            'p' {
                if ($prompt) {
                    Write-Host ""
                    Write-Host "Claude Code Prompt:" -ForegroundColor Cyan
                    Write-Host "─────────────────────────────────────────────"
                    Write-Host $prompt
                    Write-Host "─────────────────────────────────────────────"
                    Write-Host ""
                }
                else {
                    Write-Host "No prompt defined for this task." -ForegroundColor Yellow
                }
            }
            's' {
                Write-Host "Skipping task..." -ForegroundColor Yellow
                Write-Log "INFO" "Task $TaskId skipped by user"
                return 0
            }
            'q' {
                Write-Host "Quitting..." -ForegroundColor Red
                Write-Log "INFO" "User quit"
                exit 0
            }
            default {
                Write-Host "Invalid option. Try again." -ForegroundColor Red
            }
        }
    }
}

# ─── Auto task runner ──────────────────────────────────────────────────────
function Invoke-TaskAuto {
    param([string]$TaskId)

    $taskInfo = Get-TaskInfo $TaskId
    if (-not $taskInfo) { return 1 }

    $prompt = Get-TaskPrompt $TaskId

    # Check dependencies
    if (-not (Test-TaskDependencies $TaskId)) {
        Write-Host "Skipping task due to unmet dependencies." -ForegroundColor Yellow
        Write-Log "WARN" "Skipped $TaskId: unmet dependencies"
        return 2
    }

    Write-TaskStart $TaskId $taskInfo.title
    Show-Task $TaskId

    Write-Host "Executing task: $($taskInfo.title)" -ForegroundColor Blue
    Write-Host ""

    if ($prompt) {
        Write-Host "Running Claude Code..." -ForegroundColor Cyan
        Write-Host ""

        $fullPrompt = @"
Task ID: $TaskId
Task: $($taskInfo.title)

$prompt

참고: tasks.json 파일에서 apiSpecs, samplePages 등 추가 정보를 확인할 수 있습니다.
완료 후 생성된 파일 목록을 알려주세요.
"@

        if (Invoke-Claude $fullPrompt) {
            Write-Host ""
            Write-Host "✓ Claude Code execution completed" -ForegroundColor Green
        }
        else {
            Write-Host ""
            Write-Host "✗ Claude Code execution failed" -ForegroundColor Red
            Write-TaskEnd $TaskId "failed"
            return 1
        }
    }
    else {
        Write-Host "No prompt defined for this task. Skipping Claude Code execution." -ForegroundColor Yellow
        Write-Log "INFO" "No prompt for task $TaskId"
    }

    # Process subtasks
    if ($taskInfo.subtasks) {
        $pendingSubtasks = $taskInfo.subtasks | Where-Object { -not $_.done }
        foreach ($subtask in $pendingSubtasks) {
            Write-Host "  Subtask: " -ForegroundColor Yellow -NoNewline
            Write-Host $subtask.title
            Set-SubtaskDone $TaskId $subtask.id
            Write-Host "  ✓ Subtask completed" -ForegroundColor Green
        }
    }

    # Mark done
    Set-TaskDone $TaskId

    if ($Script:ExecMode -ne 'dry-run') {
        Write-Host "✓ Task completed: $($taskInfo.title)" -ForegroundColor Green
        Write-TaskEnd $TaskId "completed"
        Invoke-GitCommit $TaskId $taskInfo.title
    }
    else {
        Write-Host "[DRY-RUN] Would mark task as done: $($taskInfo.title)" -ForegroundColor Cyan
        Write-TaskEnd $TaskId "dry-run"
    }

    return 0
}

# ─── Progress display ──────────────────────────────────────────────────────
function Show-Progress {
    $tasksData = Get-TasksData
    if (-not $tasksData) { return }

    $total = $tasksData.tasks.Count
    $doneCount = ($tasksData.tasks | Where-Object { $_.done }).Count
    $pending = $total - $doneCount

    # Count blocked tasks
    $blockedCount = 0
    $pendingIds = $tasksData.tasks | Where-Object { -not $_.done } | ForEach-Object { $_.id }
    foreach ($tid in $pendingIds) {
        if (-not (Test-TaskDependencies $tid)) {
            $blockedCount++
        }
    }
    $ready = $pending - $blockedCount

    Write-Host ""
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
    Write-Host "       RALPH - Task Executor" -ForegroundColor Green
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
    Write-Host "Total: $total | " -NoNewline
    Write-Host "Done: $doneCount" -ForegroundColor Green -NoNewline
    Write-Host " | " -NoNewline
    Write-Host "Ready: $ready" -ForegroundColor Yellow -NoNewline
    Write-Host " | " -NoNewline
    Write-Host "Blocked: $blockedCount" -ForegroundColor Red

    if ($Script:LogFile) {
        Write-Host "Log: " -ForegroundColor Cyan -NoNewline
        Write-Host $Script:LogFile
    }
    Write-Host "═══════════════════════════════════════════════════════════" -ForegroundColor Blue
}

# ─── Main loops ────────────────────────────────────────────────────────────
function Start-Interactive {
    Initialize-Logging
    Show-Progress

    while ($true) {
        $nextTask = Get-NextReadyTask

        if (-not $nextTask) {
            $remaining = Get-NextTask
            if ($remaining) {
                Write-Host ""
                Write-Host "All remaining tasks are blocked by unmet dependencies:" -ForegroundColor Red
                $tasksData = Get-TasksData
                if ($tasksData) {
                    $blockedTasks = $tasksData.tasks | Where-Object { -not $_.done }
                    foreach ($task in $blockedTasks) {
                        $deps = if ($task.dependsOn) { $task.dependsOn -join ', ' } else { '' }
                        Write-Host "  $($task.id): depends on $deps"
                    }
                }
                Write-Log "WARN" "Execution stopped: remaining tasks blocked by dependencies"
            }
            else {
                Write-Host ""
                Write-Host "All tasks completed!" -ForegroundColor Green
                Write-Log "INFO" "All tasks completed"
            }
            break
        }

        Invoke-Task $nextTask
    }
}

function Start-Auto {
    Initialize-Logging
    Show-Progress

    # Backup for dry-run
    $dryRunBackup = $null
    if ($Script:ExecMode -eq 'dry-run') {
        $dryRunBackup = [System.IO.Path]::GetTempFileName()
        Copy-Item $Script:TasksFile $dryRunBackup
    }

    try {
        while ($true) {
            $nextTask = Get-NextReadyTask

            if (-not $nextTask) {
                $remaining = Get-NextTask
                if ($remaining) {
                    Write-Host ""
                    Write-Host "All remaining tasks are blocked by unmet dependencies:" -ForegroundColor Red
                    $tasksData = Get-TasksData
                    if ($tasksData) {
                        $blockedTasks = $tasksData.tasks | Where-Object { -not $_.done }
                        foreach ($task in $blockedTasks) {
                            $deps = if ($task.dependsOn) { $task.dependsOn -join ', ' } else { '' }
                            Write-Host "  $($task.id): depends on $deps"
                        }
                    }
                    Write-Log "WARN" "Execution stopped: remaining tasks blocked by dependencies"
                }
                else {
                    Write-Host ""
                    Write-Host "All tasks completed!" -ForegroundColor Green
                    Write-Log "INFO" "All tasks completed"
                }
                break
            }

            $result = Invoke-TaskAuto $nextTask
            if ($result -eq 1) {
                Write-Host "Task failed. Stopping auto execution." -ForegroundColor Red
                Write-Log "ERROR" "Auto execution stopped due to task failure"
                break
            }
        }
    }
    finally {
        # Restore for dry-run
        if ($dryRunBackup) {
            Copy-Item $dryRunBackup $Script:TasksFile -Force
            Remove-Item $dryRunBackup -Force -ErrorAction SilentlyContinue
            Write-Host "[DRY-RUN] tasks.json restored to original state." -ForegroundColor Cyan
        }
    }
}

# ─── Command line parsing ──────────────────────────────────────────────────
Test-Dependencies

switch ($Command) {
    '--plan' {
        if (-not $Argument) {
            Write-Host "Error: PRD file required. Usage: ./ralph.ps1 --plan <prd-file>" -ForegroundColor Red
            exit 1
        }
        New-Plan $Argument
    }
    '--run' {
        if ($Argument -and -not $Argument.StartsWith('--')) {
            $Script:TasksFile = $Argument
        }
        Assert-TasksFile
        Read-WorkflowSettings
        $Script:CommitOnComplete = $true
        $Script:ExecMode = 'auto'
        Start-Auto
    }
    '--dry-run' {
        Assert-TasksFile
        Read-WorkflowSettings
        $Script:ExecMode = 'dry-run'
        Start-Auto
    }
    '--task' {
        if (-not $Argument) {
            Write-Host "Error: Task ID required. Usage: ./ralph.ps1 --task <task-id>" -ForegroundColor Red
            exit 1
        }
        Assert-TasksFile
        Read-WorkflowSettings
        $taskInfo = Get-TaskInfo $Argument
        if (-not $taskInfo -or $taskInfo -eq 'null') {
            Write-Host "Error: Task '$Argument' not found." -ForegroundColor Red
            exit 1
        }
        $Script:ExecMode = 'auto'
        Initialize-Logging
        Invoke-TaskAuto $Argument
    }
    '--list' {
        Assert-TasksFile
        Write-Host "Pending Tasks:" -ForegroundColor Blue
        $tasksData = Get-TasksData
        if ($tasksData) {
            $pendingTasks = $tasksData.tasks | Where-Object { -not $_.done }
            foreach ($task in $pendingTasks) {
                $deps = if ($task.dependsOn -and $task.dependsOn.Count -gt 0) { " (depends: $($task.dependsOn -join ', '))" } else { "" }
                Write-Host "[$($task.phase)] $($task.id): $($task.title)$deps"
            }
        }
    }
    '-l' {
        & $PSCommandPath --list
    }
    '--prompts' {
        Assert-TasksFile
        Write-Host "Task Prompts:" -ForegroundColor Blue
        $tasksData = Get-TasksData
        if ($tasksData) {
            $pendingTasks = $tasksData.tasks | Where-Object { -not $_.done }
            foreach ($task in $pendingTasks) {
                Write-Host ""
                Write-Host "═══ $($task.id) =══"
                Write-Host $(if ($task.prompt) { $task.prompt } else { "No prompt defined" })
            }
        }
    }
    '-p' {
        & $PSCommandPath --prompts
    }
    '--status' {
        Assert-TasksFile
        Show-Progress
    }
    '-s' {
        & $PSCommandPath --status
    }
    '--reset' {
        Assert-TasksFile
        Write-Host "Resetting all tasks to pending..." -ForegroundColor Yellow
        $tasksData = Get-TasksData
        if ($tasksData) {
            foreach ($task in $tasksData.tasks) {
                $task.done = $false
                if ($task.subtasks) {
                    foreach ($subtask in $task.subtasks) {
                        $subtask.done = $false
                    }
                }
            }
            Save-TasksData $tasksData
            Write-Host "All tasks reset." -ForegroundColor Green
        }
    }
    '-r' {
        & $PSCommandPath --reset
    }
    '--logs' {
        if (Test-Path $Script:LogDir) {
            Write-Host "Recent logs:" -ForegroundColor Blue
            Get-ChildItem "$Script:LogDir/*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 10 | Format-Table
            $latest = Get-ChildItem "$Script:LogDir/*.log" -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
            if ($latest) {
                Write-Host ""
                Write-Host "View latest: " -ForegroundColor Cyan -NoNewline
                Write-Host "cat $($latest.FullName)"
            }
        }
        else {
            Write-Host "No logs found." -ForegroundColor Yellow
        }
    }
    '--help' {
        Write-Host @'
Usage: ./ralph.ps1 [option]

Options:
  --plan <file>  Generate tasks.json from a PRD file
  --run [file]   Run all pending tasks with Claude Code (default: tasks.json)
  --dry-run      Show what would be executed (no actual changes)
  --task <id>    Run a specific task by ID
  --interactive  Run tasks interactively (confirm each one)
  --list, -l     List all pending tasks
  --prompts, -p  Show all task prompts
  --status, -s   Show progress status
  --reset, -r    Reset all tasks to pending
  --logs         Show recent log files
  --help, -h     Show this help message

Workflow:
  1. ./ralph.ps1 --plan PRD.md    # Generate tasks from PRD
  2. ./ralph.ps1 --list           # Review generated tasks
  3. ./ralph.ps1 --dry-run        # Preview execution
  4. ./ralph.ps1 --run            # Execute all tasks

Environment variables:
  MAX_RETRIES    Max Claude Code retry attempts (default: 2)
  RETRY_DELAY    Seconds between retries (default: 5)

Examples:
  ./ralph.ps1 --plan docs/PRD.md             # Generate plan from PRD
  ./ralph.ps1 --run                           # Run all pending tasks (uses tasks.json)
  ./ralph.ps1 --run my-tasks.json             # Run tasks from custom file
  ./ralph.ps1 --task user-auth-impl           # Run specific task
  ./ralph.ps1 --dry-run                       # Preview without executing
  $env:MAX_RETRIES=3; ./ralph.ps1 --run       # Run with 3 retry attempts
'@
    }
    '-h' {
        & $PSCommandPath --help
    }
    '--interactive' {
        Assert-TasksFile
        Read-WorkflowSettings
        Start-Interactive
    }
    default {
        if ($Command) {
            Write-Host "Unknown option: $Command" -ForegroundColor Red
            Write-Host "Run './ralph.ps1 --help' for usage information."
            exit 1
        }
        else {
            Write-Host @'
Usage: ralph.ps1 [option]

Options:
  --plan <file>  Generate tasks.json from a PRD file
  --run [file]   Run all pending tasks with Claude Code (default: tasks.json)
  --dry-run      Show what would be executed (no actual changes)
  --task <id>    Run a specific task by ID
  --interactive  Run tasks interactively (confirm each one)
  --list, -l     List all pending tasks
  --prompts, -p  Show all task prompts
  --status, -s   Show progress status
  --reset, -r    Reset all tasks to pending
  --logs         Show recent log files
  --help, -h     Show this help message

Workflow:
  1. ralph.ps1 --plan PRD.md    # Generate tasks from PRD
  2. ralph.ps1 --list           # Review generated tasks
  3. ralph.ps1 --dry-run        # Preview execution
  4. ralph.ps1 --run            # Execute all tasks
'@
        }
    }
}
