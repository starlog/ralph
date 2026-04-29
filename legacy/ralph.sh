#!/bin/bash

# ralph.sh - Task executor for deardeer-guest-sample-order project
# Executes tasks one by one from tasks.json using Claude Code

set -o pipefail

TASKS_FILE="tasks.json"
LOG_DIR=".ralph-logs"
MAX_RETRIES=2
RETRY_DELAY=5

# Colors for output
RED='\033[0;31m'
GREEN='\033[0;32m'
YELLOW='\033[1;33m'
BLUE='\033[0;34m'
CYAN='\033[0;36m'
NC='\033[0m' # No Color

# Execution mode (set by command line args)
EXEC_MODE="interactive"  # interactive, auto, dry-run

# ─── Signal handling (Ctrl+C cleanup) ────────────────────────────────────────

TMP_FILES_TO_CLEAN=()

cleanup() {
    # Prevent re-entry
    trap - INT TERM

    echo ""
    echo -e "${RED}Interrupted. Cleaning up...${NC}"

    # Kill all child processes (but not self)
    local children
    children=$(jobs -p 2>/dev/null)
    if [ -n "$children" ]; then
        kill $children 2>/dev/null
        wait $children 2>/dev/null
    fi

    # Also kill any remaining claude processes spawned by this script
    pkill -P $$ 2>/dev/null

    # Clean up temp files
    for f in "${TMP_FILES_TO_CLEAN[@]}"; do
        rm -f "$f"
    done

    echo -e "${RED}Aborted.${NC}"
    exit 130
}

trap cleanup INT TERM

# ─── Logging ────────────────────────────────────────────────────────────────

init_logging() {
    mkdir -p "$LOG_DIR"
    LOG_FILE="$LOG_DIR/ralph-$(date +%Y%m%d-%H%M%S).log"
    echo "Ralph session started at $(date)" > "$LOG_FILE"
    echo "Tasks file: $TASKS_FILE" >> "$LOG_FILE"
    echo "Exec mode: $EXEC_MODE" >> "$LOG_FILE"
    echo "────────────────────────────────────────" >> "$LOG_FILE"
}

log() {
    local level=$1
    shift
    local msg="$*"
    local timestamp=$(date '+%Y-%m-%d %H:%M:%S')
    if [ -n "${LOG_FILE:-}" ]; then
        echo "[$timestamp] [$level] $msg" >> "$LOG_FILE"
    fi
}

log_task_start() {
    local task_id=$1
    local title=$2
    log "INFO" "=== Task started: $task_id - $title ==="
}

log_task_end() {
    local task_id=$1
    local status=$2
    log "INFO" "=== Task ended: $task_id - status: $status ==="
}

# ─── Dependency checks ──────────────────────────────────────────────────────

# Check if jq is installed
if ! command -v jq &> /dev/null; then
    echo -e "${RED}Error: jq is required but not installed.${NC}"
    echo "Install with: brew install jq"
    exit 1
fi

# Check if claude is installed
if ! command -v claude &> /dev/null; then
    echo -e "${RED}Error: claude CLI is required but not installed.${NC}"
    echo "Install Claude Code from: https://claude.ai/code"
    exit 1
fi

# Check if tasks.json exists (deferred for --plan command)
require_tasks_file() {
    if [ ! -f "$TASKS_FILE" ]; then
        echo -e "${RED}Error: $TASKS_FILE not found. Run './ralph.sh --plan <prd-file>' to generate it.${NC}"
        exit 1
    fi
}

# ─── Workflow settings (lazy-loaded) ─────────────────────────────────────────

COMMIT_ON_COMPLETE="true"
COMMIT_TEMPLATE="[Task #{taskId}] {taskTitle}"

load_workflow_settings() {
    if [ -f "$TASKS_FILE" ]; then
        COMMIT_ON_COMPLETE=$(jq -r '.workflow.onTaskComplete.commitChanges // true' "$TASKS_FILE")
        COMMIT_TEMPLATE=$(jq -r '.workflow.onTaskComplete.commitMessageTemplate // "[Task #{taskId}] {taskTitle}"' "$TASKS_FILE")
    fi
}

# ─── Resolve RALPH_HOME (directory where ralph.sh lives) ─────────────────────

RALPH_HOME="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SCHEMA_FILE="$RALPH_HOME/ralph-schema.json"

# ─── Safe JSON update helper ────────────────────────────────────────────────

# Safely update tasks.json: runs jq, validates output, then atomically replaces
safe_jq_update() {
    local jq_filter="$1"
    shift
    local tmp_file
    tmp_file=$(mktemp "${TASKS_FILE}.tmp.XXXXXX")

    if ! jq "$@" "$jq_filter" "$TASKS_FILE" > "$tmp_file" 2>/dev/null; then
        log "ERROR" "jq filter failed: $jq_filter"
        echo -e "${RED}Error: jq filter execution failed${NC}"
        rm -f "$tmp_file"
        return 1
    fi

    # Validate the output is valid JSON and has tasks array
    if ! jq -e '.tasks | type == "array"' "$tmp_file" > /dev/null 2>&1; then
        log "ERROR" "jq produced invalid output for filter: $jq_filter"
        echo -e "${RED}Error: jq produced invalid JSON output${NC}"
        rm -f "$tmp_file"
        return 1
    fi

    mv "$tmp_file" "$TASKS_FILE"
}

# ─── Task query functions ───────────────────────────────────────────────────

get_next_task() {
    jq -r '.tasks[] | select(.done == false) | .id' "$TASKS_FILE" | head -1
}

get_task_info() {
    local task_id=$1
    jq -r --arg id "$task_id" '.tasks[] | select(.id == $id)' "$TASKS_FILE"
}

get_task_prompt() {
    local task_id=$1
    jq -r --arg id "$task_id" '.tasks[] | select(.id == $id) | .prompt // empty' "$TASKS_FILE"
}

get_output_files() {
    local task_id=$1
    jq -r --arg id "$task_id" '.tasks[] | select(.id == $id) | .outputFiles // [] | join(", ")' "$TASKS_FILE"
}

# ─── Dependency management ──────────────────────────────────────────────────

# Check if all dependencies of a task are satisfied (done == true)
check_dependencies() {
    local task_id=$1
    local deps
    deps=$(jq -r --arg id "$task_id" '.tasks[] | select(.id == $id) | .dependsOn // [] | .[]' "$TASKS_FILE")

    if [ -z "$deps" ]; then
        return 0  # No dependencies
    fi

    local blocked=false
    for dep_id in $deps; do
        local dep_done
        dep_done=$(jq -r --arg id "$dep_id" '.tasks[] | select(.id == $id) | .done' "$TASKS_FILE")
        if [ "$dep_done" != "true" ]; then
            echo -e "${RED}Blocked:${NC} Task '$task_id' depends on '$dep_id' which is not done yet."
            log "WARN" "Task $task_id blocked by dependency: $dep_id"
            blocked=true
        fi
    done

    if [ "$blocked" = true ]; then
        return 1
    fi
    return 0
}

# Get next pending task that has all dependencies satisfied
get_next_ready_task() {
    local pending_ids
    pending_ids=$(jq -r '.tasks[] | select(.done == false) | .id' "$TASKS_FILE")

    for task_id in $pending_ids; do
        if check_dependencies "$task_id" 2>/dev/null; then
            echo "$task_id"
            return 0
        fi
    done
    return 1
}

# ─── Task state mutations ───────────────────────────────────────────────────

mark_task_done() {
    local task_id=$1
    safe_jq_update '(.tasks[] | select(.id == $id)).done = true' --arg id "$task_id"
}

mark_subtask_done() {
    local task_id=$1
    local subtask_id=$2
    safe_jq_update '(.tasks[] | select(.id == $id)).subtasks |= map(if .id == $sid then .done = true else . end)' --arg id "$task_id" --arg sid "$subtask_id"
}

# ─── Sensitive file patterns ────────────────────────────────────────────────

SENSITIVE_PATTERNS=(".env" ".env.*" "*.pem" "*.key" "*.p12" "*.pfx" "credentials.json" "service-account*.json" ".secret*" "*.secrets" "id_rsa" "id_ed25519")

# ─── Git commit ─────────────────────────────────────────────────────────────

commit_changes() {
    local task_id=$1
    local task_title=$2

    if [ "$COMMIT_ON_COMPLETE" = "true" ]; then
        local commit_msg
        commit_msg=$(echo "$COMMIT_TEMPLATE" | sed "s/{taskId}/$task_id/g" | sed "s/{taskTitle}/$task_title/g")

        echo -e "${BLUE}Committing changes...${NC}"
        log "INFO" "Committing: $commit_msg"

        # Stage all files except sensitive patterns
        git add -A
        for pattern in "${SENSITIVE_PATTERNS[@]}"; do
            git reset HEAD -- "$pattern" 2>/dev/null
        done

        # Warn if sensitive files were detected
        local unstaged_sensitive
        unstaged_sensitive=$(git status --porcelain | grep -E '^\?\?.*\.(env|pem|key|p12|pfx|secrets)' || true)
        if [ -n "$unstaged_sensitive" ]; then
            echo -e "${YELLOW}Warning: Sensitive files detected and excluded from commit:${NC}"
            echo "$unstaged_sensitive"
            log "WARN" "Sensitive files excluded: $unstaged_sensitive"
        fi

        if git commit -m "$commit_msg

Co-Authored-By: Claude Opus 4.5 <noreply@anthropic.com>"; then
            echo -e "${GREEN}Committed: $commit_msg${NC}"
            log "INFO" "Commit successful: $commit_msg"
        else
            echo -e "${YELLOW}No changes to commit or commit failed.${NC}"
            log "WARN" "Commit failed or no changes"
        fi
    fi
}

# ─── Display ────────────────────────────────────────────────────────────────

display_task() {
    local task_id=$1
    local task_info
    task_info=$(get_task_info "$task_id")

    local title description phase category has_subtasks has_prompt output_files deps
    title=$(echo "$task_info" | jq -r '.title')
    description=$(echo "$task_info" | jq -r '.description')
    phase=$(echo "$task_info" | jq -r '.phase')
    category=$(echo "$task_info" | jq -r '.category')
    has_subtasks=$(echo "$task_info" | jq 'has("subtasks")')
    has_prompt=$(echo "$task_info" | jq 'has("prompt")')
    output_files=$(get_output_files "$task_id")
    deps=$(jq -r --arg id "$task_id" '.tasks[] | select(.id == $id) | .dependsOn // [] | join(", ")' "$TASKS_FILE")

    # Calculate task order [n/total]
    local total_tasks task_order
    total_tasks=$(jq '.tasks | length' "$TASKS_FILE")
    task_order=$(jq -r --arg id "$task_id" '[.tasks[].id] | to_entries[] | select(.value == $id) | .key + 1' "$TASKS_FILE")

    echo ""
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
    echo -e "${YELLOW}[${task_order}/${total_tasks}]${NC} ${GREEN}Task ID:${NC} $task_id"
    echo -e "${GREEN}Phase:${NC} $phase | ${GREEN}Category:${NC} $category"
    echo -e "${GREEN}Title:${NC} $title"
    echo -e "${GREEN}Description:${NC} $description"

    if [ -n "$deps" ]; then
        echo -e "${CYAN}Depends On:${NC} $deps"
    fi

    if [ -n "$output_files" ]; then
        echo -e "${CYAN}Output Files:${NC} $output_files"
    fi

    if [ "$has_prompt" = "true" ]; then
        echo -e "${CYAN}Claude Prompt:${NC} (available)"
    fi

    if [ "$has_subtasks" = "true" ]; then
        echo -e "${YELLOW}Subtasks:${NC}"
        echo "$task_info" | jq -r '.subtasks[] | "  [\(if .done then "✓" else " " end)] \(.id): \(.title)"'
    fi
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
    echo ""
}

# ─── Plan generation ─────────────────────────────────────────────────────────

generate_plan() {
    local prd_file="$1"

    if [ ! -f "$prd_file" ]; then
        echo -e "${RED}Error: File '$prd_file' not found.${NC}"
        return 1
    fi

    if [ ! -f "$SCHEMA_FILE" ]; then
        echo -e "${RED}Error: Schema file '$SCHEMA_FILE' not found.${NC}"
        return 1
    fi

    local prd_content
    prd_content=$(cat "$prd_file")

    local schema_content
    schema_content=$(cat "$SCHEMA_FILE")

    # Check for existing tasks.json
    if [ -f "$TASKS_FILE" ]; then
        echo -e "${YELLOW}Warning: $TASKS_FILE already exists.${NC}"
        echo -e "${YELLOW}Overwrite? (y/n):${NC} "
        read -r overwrite_response
        if [ "$overwrite_response" != "y" ] && [ "$overwrite_response" != "Y" ]; then
            echo -e "${RED}Aborted.${NC}"
            return 1
        fi
        # Backup existing file
        local backup_file="${TASKS_FILE}.backup.$(date +%Y%m%d-%H%M%S)"
        cp "$TASKS_FILE" "$backup_file"
        echo -e "${CYAN}Backup saved: $backup_file${NC}"
    fi

    echo ""
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
    echo -e "${GREEN}       RALPH - Plan Generator${NC}"
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
    echo -e "${CYAN}PRD File:${NC} $prd_file"
    echo -e "${CYAN}Schema:${NC} $SCHEMA_FILE"
    echo -e "${CYAN}Output:${NC} $TASKS_FILE"
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
    echo ""
    echo -e "${CYAN}Generating task plan with Claude Code...${NC}"
    echo ""

    # Build prompt via temp file to avoid bash parsing issues with heredoc + subshell
    local prompt_file
    prompt_file=$(mktemp)
    cat > "$prompt_file" <<'PROMPT_HEADER'
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
PROMPT_HEADER

    # Append dynamic content (schema + PRD)
    {
        echo ""
        echo '```json'
        cat "$SCHEMA_FILE"
        echo '```'
        echo ""
        echo "## PRD Document (source: $prd_file)"
        echo ""
        cat "$prd_file"
        echo ""
        echo "## Output"
        echo "Generate the complete tasks.json now. Output ONLY valid JSON, nothing else."
    } >> "$prompt_file"

    local plan_prompt
    plan_prompt=$(cat "$prompt_file")
    rm -f "$prompt_file"

    # Run Claude Code and capture output with real-time display
    local tmp_output
    tmp_output=$(mktemp)
    TMP_FILES_TO_CLEAN+=("$tmp_output")
    > "$tmp_output"

    echo -e "${CYAN}Running Claude Code...${NC}"
    echo ""
    echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo -e "${YELLOW}[Claude Code Output]${NC}"
    echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"

    if ! run_claude_stream "$plan_prompt" "$tmp_output" "true"; then
        echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
        echo ""
        echo -e "${RED}Error: Claude Code execution failed.${NC}"
        rm -f "$tmp_output"
        return 1
    fi

    echo -e "${YELLOW}━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━━${NC}"
    echo ""

    # Extract JSON from output
    # Strategy: extract the LAST complete fenced block (handles truncation + retry)
    local json_content
    json_content=$(awk '
        /^[[:space:]]*```/ {
            if (in_block) { last=buf; in_block=0 }
            else { in_block=1; buf="" }
            next
        }
        in_block { buf = buf $0 "\n" }
        END { printf "%s", last }
    ' "$tmp_output" | jq '.' 2>/dev/null)

    # Fallback: strip all fences and try parsing the whole thing
    if [ -z "$json_content" ]; then
        json_content=$(sed '/^[[:space:]]*```/d' "$tmp_output" | jq '.' 2>/dev/null)
    fi

    if [ -z "$json_content" ]; then
        echo -e "${RED}Error: No valid JSON found in Claude output.${NC}"
        echo -e "${YELLOW}Raw output saved to: $tmp_output${NC}"
        return 1
    fi

    # Validate JSON structure
    if ! echo "$json_content" | jq -e '.tasks | type == "array"' > /dev/null 2>&1; then
        echo -e "${RED}Error: Generated JSON does not have a valid 'tasks' array.${NC}"
        echo -e "${YELLOW}Raw output saved to: $tmp_output${NC}"
        return 1
    fi

    # Validate all tasks have required fields
    local invalid_tasks
    invalid_tasks=$(echo "$json_content" | jq '[.tasks[] | select(.id == null or .title == null or .done == null)] | length')
    if [ "$invalid_tasks" -gt 0 ]; then
        echo -e "${RED}Error: $invalid_tasks task(s) missing required fields (id, title, done).${NC}"
        echo -e "${YELLOW}Raw output saved to: $tmp_output${NC}"
        return 1
    fi

    # Validate 4-phase pattern (warn only, don't block)
    local total_tasks
    total_tasks=$(echo "$json_content" | jq '.tasks | length')
    local plan_count impl_count test_count commit_count
    plan_count=$(echo "$json_content" | jq '[.tasks[] | select(.category == "plan")] | length')
    impl_count=$(echo "$json_content" | jq '[.tasks[] | select(.category == "implementation")] | length')
    test_count=$(echo "$json_content" | jq '[.tasks[] | select(.category == "testing")] | length')
    commit_count=$(echo "$json_content" | jq '[.tasks[] | select(.category == "commit")] | length')

    if [ "$plan_count" -ne "$impl_count" ] || [ "$impl_count" -ne "$test_count" ] || [ "$test_count" -ne "$commit_count" ]; then
        echo -e "${YELLOW}Warning: Uneven task phases — plan:$plan_count impl:$impl_count test:$test_count commit:$commit_count${NC}"
    fi

    # Write the validated JSON
    echo "$json_content" | jq '.' > "$TASKS_FILE"
    rm -f "$tmp_output"

    # Summary
    local feature_count=$plan_count
    echo ""
    echo -e "${GREEN}Plan generated successfully!${NC}"
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
    echo -e "  Total tasks:  $total_tasks"
    echo -e "  Features:     $feature_count"
    echo -e "  Per feature:  plan → implementation → testing → commit"
    echo ""
    echo -e "  ${CYAN}Plan:${NC}           $plan_count tasks"
    echo -e "  ${CYAN}Implementation:${NC} $impl_count tasks"
    echo -e "  ${CYAN}Testing:${NC}        $test_count tasks"
    echo -e "  ${CYAN}Commit:${NC}         $commit_count tasks"
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
    echo ""
    echo -e "Next steps:"
    echo -e "  ${GREEN}./ralph.sh --list${NC}       Review generated tasks"
    echo -e "  ${GREEN}./ralph.sh --dry-run${NC}    Preview execution"
    echo -e "  ${GREEN}./ralph.sh --run${NC}        Execute all tasks"
    echo ""
}

# ─── Claude Code streaming helper (Ctrl+C safe) ─────────────────────────────
#
# Runs claude in background, monitors output via polling, waits with 'wait'
# builtin which is the ONLY bash builtin that lets traps fire immediately.
# This makes Ctrl+C work: trap fires → kills children → exits.

run_claude_stream() {
    local prompt="$1"
    local output_file="${2:-}"  # optional: file to save parsed text output
    local no_tools="${3:-false}"  # optional: set "true" to disable all tools

    local raw_file
    raw_file=$(mktemp)
    TMP_FILES_TO_CLEAN+=("$raw_file")

    # Build claude command args
    local claude_args=(-p --dangerously-skip-permissions --output-format stream-json --verbose --include-partial-messages)
    if [ "$no_tools" = "true" ]; then
        # Disable built-in tools and MCP servers to prevent tool-use loops
        # Use sonnet for faster output (plan generation doesn't need opus)
        claude_args+=(--tools "" --strict-mcp-config --model sonnet)
        # Large PRDs can produce JSON exceeding the default 32k token limit
        export CLAUDE_CODE_MAX_OUTPUT_TOKENS="${CLAUDE_CODE_MAX_OUTPUT_TOKENS:-65536}"
    fi

    # Run claude in background, writing stream-json to raw_file
    # When no_tools=true, pipe prompt via stdin (--tools "" breaks positional arg parsing)
    if [ "$no_tools" = "true" ]; then
        echo "$prompt" | claude "${claude_args[@]}" > "$raw_file" 2>&1 &
    else
        claude "${claude_args[@]}" "$prompt" > "$raw_file" 2>&1 &
    fi
    local claude_pid=$!

    # Monitor: poll raw_file for new lines, parse stream-json, display text
    # Handles both streaming deltas (stream_event) and final messages (assistant)
    (
        process_lines() {
            while IFS= read -r line; do
                # 1. New text block start: add newline to separate from previous output
                local block_start
                block_start=$(echo "$line" | jq -r 'select(.type == "stream_event" and .event.type == "content_block_start" and .event.content_block.type == "text") | "1"' 2>/dev/null)
                if [ "$block_start" = "1" ]; then
                    printf "\n"
                    continue
                fi
                # 2. Streaming delta: real-time text chunks
                local delta
                delta=$(echo "$line" | jq -r 'select(.type == "stream_event" and .event.type == "content_block_delta") | .event.delta.text // empty' 2>/dev/null)
                if [ -n "$delta" ]; then
                    printf "%s" "$delta"
                    [ -n "${LOG_FILE:-}" ] && printf "%s" "$delta" >> "$LOG_FILE"
                    continue
                fi
                # 3. Final assistant message: write full text to output_file for downstream parsing
                local text
                text=$(echo "$line" | jq -r 'select(.type == "assistant") | .message.content[]?.text // empty' 2>/dev/null)
                if [ -n "$text" ]; then
                    [ -n "$output_file" ] && printf "%s\n" "$text" >> "$output_file"
                fi
            done
        }

        local n=0
        while kill -0 $claude_pid 2>/dev/null; do
            local total
            total=$(wc -l < "$raw_file" 2>/dev/null)
            total=${total##* }
            if [ "${total:-0}" -gt "$n" ] 2>/dev/null; then
                sed -n "$(( n + 1 )),${total}p" "$raw_file" | process_lines
                n=$total
            fi
            sleep 0.2
        done
        # Flush remaining lines after claude exits
        local total
        total=$(wc -l < "$raw_file" 2>/dev/null)
        total=${total##* }
        if [ "${total:-0}" -gt "$n" ] 2>/dev/null; then
            sed -n "$(( n + 1 )),${total}p" "$raw_file" | process_lines
        fi
        echo ""  # Final newline after streaming deltas
    ) &
    local monitor_pid=$!

    # 'wait' is the ONLY bash builtin where traps fire immediately on signal.
    # This is what makes Ctrl+C work during claude execution.
    wait $claude_pid 2>/dev/null
    local exit_code=$?

    # Give monitor time to flush remaining output, then kill it
    sleep 1
    kill $monitor_pid 2>/dev/null
    wait $monitor_pid 2>/dev/null

    rm -f "$raw_file"
    return $exit_code
}

# ─── Claude Code execution with retry ───────────────────────────────────────

run_claude() {
    local prompt="$1"

    # Display the prompt
    echo -e "${CYAN}Prompt:${NC}"
    echo "─────────────────────────────────────────────"
    echo "$prompt"
    echo "─────────────────────────────────────────────"
    echo ""

    if [ "$EXEC_MODE" = "dry-run" ]; then
        echo -e "${CYAN}[DRY-RUN] Would execute Claude Code with above prompt${NC}"
        log "INFO" "[DRY-RUN] Skipped Claude Code execution"
        return 0
    fi

    local attempt=1
    while [ $attempt -le $MAX_RETRIES ]; do
        if [ $attempt -gt 1 ]; then
            echo -e "${YELLOW}Retry attempt $attempt/$MAX_RETRIES (waiting ${RETRY_DELAY}s)...${NC}"
            log "INFO" "Retry attempt $attempt/$MAX_RETRIES"
            sleep "$RETRY_DELAY"
        fi

        log "INFO" "Running Claude Code (attempt $attempt)"

        if run_claude_stream "$prompt"; then
            log "INFO" "Claude Code execution successful"
            return 0
        fi

        local claude_exit=$?
        log "ERROR" "Claude Code failed with exit code $claude_exit (attempt $attempt)"
        echo -e "${RED}Claude Code failed (exit code: $claude_exit)${NC}"
        attempt=$((attempt + 1))
    done

    log "ERROR" "Claude Code failed after $MAX_RETRIES attempts"
    echo -e "${RED}Claude Code failed after $MAX_RETRIES attempts${NC}"
    return 1
}

# ─── Interactive task runner (loop, no recursion) ───────────────────────────

run_task() {
    local task_id=$1
    local task_info title has_subtasks prompt
    task_info=$(get_task_info "$task_id")
    title=$(echo "$task_info" | jq -r '.title')
    has_subtasks=$(echo "$task_info" | jq 'has("subtasks")')
    prompt=$(get_task_prompt "$task_id")

    # Check dependencies
    if ! check_dependencies "$task_id"; then
        echo -e "${YELLOW}Skipping task due to unmet dependencies.${NC}"
        log "WARN" "Skipped $task_id: unmet dependencies"
        return 2  # special code: blocked
    fi

    display_task "$task_id"

    # Loop instead of recursion to prevent stack overflow on repeated 'p'
    while true; do
        echo -e "${YELLOW}Execute this task? (y/n/p=preview prompt/s=skip/q=quit):${NC} "
        read -r response

        case $response in
            y|Y)
                log_task_start "$task_id" "$title"
                echo -e "${BLUE}Executing task: $title${NC}"
                echo ""

                if [ -n "$prompt" ]; then
                    echo -e "${CYAN}Running Claude Code...${NC}"
                    echo ""

                    local full_prompt="Task ID: $task_id
Task: $title

$prompt

참고: tasks.json 파일에서 apiSpecs, samplePages 등 추가 정보를 확인할 수 있습니다.
완료 후 생성된 파일 목록을 알려주세요."

                    if ! run_claude "$full_prompt"; then
                        echo ""
                        echo -e "${RED}✗ Claude Code execution failed${NC}"
                        echo -e "${YELLOW}Continue anyway? (y/n):${NC} "
                        read -r continue_response
                        if [ "$continue_response" != "y" ] && [ "$continue_response" != "Y" ]; then
                            log_task_end "$task_id" "failed"
                            return 1
                        fi
                    fi
                    echo ""
                    echo -e "${GREEN}✓ Claude Code execution completed${NC}"
                fi

                # Process subtasks
                if [ "$has_subtasks" = "true" ]; then
                    local subtasks
                    subtasks=$(echo "$task_info" | jq -r '.subtasks[] | select(.done == false) | .id')
                    for subtask_id in $subtasks; do
                        local subtask_title
                        subtask_title=$(echo "$task_info" | jq -r --arg sid "$subtask_id" '.subtasks[] | select(.id == $sid) | .title')
                        echo -e "  ${YELLOW}Subtask:${NC} $subtask_title"
                        mark_subtask_done "$task_id" "$subtask_id"
                        echo -e "  ${GREEN}✓ Subtask completed${NC}"
                    done
                fi

                mark_task_done "$task_id"
                echo -e "${GREEN}✓ Task completed: $title${NC}"
                log_task_end "$task_id" "completed"

                commit_changes "$task_id" "$title"
                return 0
                ;;
            p|P)
                # Preview prompt (loops back to ask again, no recursion)
                if [ -n "$prompt" ]; then
                    echo ""
                    echo -e "${CYAN}Claude Code Prompt:${NC}"
                    echo "─────────────────────────────────────────────"
                    echo "$prompt"
                    echo "─────────────────────────────────────────────"
                    echo ""
                else
                    echo -e "${YELLOW}No prompt defined for this task.${NC}"
                fi
                # Loop continues — asks again
                ;;
            s|S)
                echo -e "${YELLOW}Skipping task...${NC}"
                log "INFO" "Task $task_id skipped by user"
                return 0
                ;;
            q|Q)
                echo -e "${RED}Quitting...${NC}"
                log "INFO" "User quit"
                exit 0
                ;;
            *)
                echo -e "${RED}Invalid option. Try again.${NC}"
                # Loop continues
                ;;
        esac
    done
}

# ─── Auto task runner ───────────────────────────────────────────────────────

run_task_auto() {
    local task_id=$1
    local task_info title has_subtasks prompt
    task_info=$(get_task_info "$task_id")
    title=$(echo "$task_info" | jq -r '.title')
    has_subtasks=$(echo "$task_info" | jq 'has("subtasks")')
    prompt=$(get_task_prompt "$task_id")

    # Check dependencies
    if ! check_dependencies "$task_id"; then
        echo -e "${YELLOW}Skipping task due to unmet dependencies.${NC}"
        log "WARN" "Skipped $task_id: unmet dependencies"
        return 2
    fi

    log_task_start "$task_id" "$title"
    display_task "$task_id"

    echo -e "${BLUE}Executing task: $title${NC}"
    echo ""

    if [ -n "$prompt" ]; then
        echo -e "${CYAN}Running Claude Code...${NC}"
        echo ""

        local full_prompt="Task ID: $task_id
Task: $title

$prompt

참고: tasks.json 파일에서 apiSpecs, samplePages 등 추가 정보를 확인할 수 있습니다.
완료 후 생성된 파일 목록을 알려주세요."

        if run_claude "$full_prompt"; then
            echo ""
            echo -e "${GREEN}✓ Claude Code execution completed${NC}"
        else
            echo ""
            echo -e "${RED}✗ Claude Code execution failed${NC}"
            log_task_end "$task_id" "failed"
            return 1
        fi
    else
        echo -e "${YELLOW}No prompt defined for this task. Skipping Claude Code execution.${NC}"
        log "INFO" "No prompt for task $task_id"
    fi

    # Process subtasks
    if [ "$has_subtasks" = "true" ]; then
        local subtasks
        subtasks=$(echo "$task_info" | jq -r '.subtasks[] | select(.done == false) | .id')
        for subtask_id in $subtasks; do
            local subtask_title
            subtask_title=$(echo "$task_info" | jq -r --arg sid "$subtask_id" '.subtasks[] | select(.id == $sid) | .title')
            echo -e "  ${YELLOW}Subtask:${NC} $subtask_title"
            mark_subtask_done "$task_id" "$subtask_id"
            echo -e "  ${GREEN}✓ Subtask completed${NC}"
        done
    fi

    # Always mark done (needed for dependency advancement, dry-run restores tasks.json later)
    mark_task_done "$task_id"

    if [ "$EXEC_MODE" != "dry-run" ]; then
        echo -e "${GREEN}✓ Task completed: $title${NC}"
        log_task_end "$task_id" "completed"
        commit_changes "$task_id" "$title"
    else
        echo -e "${CYAN}[DRY-RUN] Would mark task as done: $title${NC}"
        log_task_end "$task_id" "dry-run"
    fi

    return 0
}

# ─── Progress display ───────────────────────────────────────────────────────

show_progress() {
    local total done_count pending blocked_count
    total=$(jq '.tasks | length' "$TASKS_FILE")
    done_count=$(jq '[.tasks[] | select(.done == true)] | length' "$TASKS_FILE")
    pending=$((total - done_count))

    # Count blocked tasks
    blocked_count=0
    local pending_ids
    pending_ids=$(jq -r '.tasks[] | select(.done == false) | .id' "$TASKS_FILE")
    for tid in $pending_ids; do
        if ! check_dependencies "$tid" 2>/dev/null; then
            blocked_count=$((blocked_count + 1))
        fi
    done
    local ready=$((pending - blocked_count))

    echo ""
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
    echo -e "${GREEN}       RALPH - Task Executor for DearDeer Project${NC}"
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
    echo -e "Total: $total | ${GREEN}Done: $done_count${NC} | ${YELLOW}Ready: $ready${NC} | ${RED}Blocked: $blocked_count${NC}"
    if [ -n "${LOG_FILE:-}" ]; then
        echo -e "${CYAN}Log: $LOG_FILE${NC}"
    fi
    echo -e "${BLUE}═══════════════════════════════════════════════════════════${NC}"
}

# ─── Main loops ─────────────────────────────────────────────────────────────

main() {
    init_logging
    show_progress

    while true; do
        local next_task
        next_task=$(get_next_ready_task)

        if [ -z "$next_task" ]; then
            # Check if there are still pending but blocked tasks
            local remaining
            remaining=$(get_next_task)
            if [ -n "$remaining" ]; then
                echo ""
                echo -e "${RED}All remaining tasks are blocked by unmet dependencies:${NC}"
                jq -r '.tasks[] | select(.done == false) | "  \(.id): depends on \(.dependsOn // [] | join(", "))"' "$TASKS_FILE"
                log "WARN" "Execution stopped: remaining tasks blocked by dependencies"
            else
                echo ""
                echo -e "${GREEN}All tasks completed!${NC}"
                log "INFO" "All tasks completed"
            fi
            break
        fi

        run_task "$next_task"
    done
}

main_auto() {
    init_logging
    show_progress

    # Backup tasks.json for dry-run (will be restored at the end)
    local dry_run_backup=""
    if [ "$EXEC_MODE" = "dry-run" ]; then
        dry_run_backup=$(mktemp)
        cp "$TASKS_FILE" "$dry_run_backup"
    fi

    while true; do
        local next_task
        next_task=$(get_next_ready_task)

        if [ -z "$next_task" ]; then
            local remaining
            remaining=$(get_next_task)
            if [ -n "$remaining" ]; then
                echo ""
                echo -e "${RED}All remaining tasks are blocked by unmet dependencies:${NC}"
                jq -r '.tasks[] | select(.done == false) | "  \(.id): depends on \(.dependsOn // [] | join(", "))"' "$TASKS_FILE"
                log "WARN" "Execution stopped: remaining tasks blocked by dependencies"
            else
                echo ""
                echo -e "${GREEN}All tasks completed!${NC}"
                log "INFO" "All tasks completed"
            fi
            break
        fi

        if ! run_task_auto "$next_task"; then
            local exit_code=$?
            if [ $exit_code -eq 2 ]; then
                continue  # blocked, try next
            fi
            echo -e "${RED}Task failed. Stopping auto execution.${NC}"
            log "ERROR" "Auto execution stopped due to task failure"
            break
        fi
    done

    # Restore tasks.json after dry-run
    if [ -n "$dry_run_backup" ]; then
        cp "$dry_run_backup" "$TASKS_FILE"
        rm -f "$dry_run_backup"
        echo -e "${CYAN}[DRY-RUN] tasks.json restored to original state.${NC}"
    fi
}

# ─── Command line parsing ───────────────────────────────────────────────────

case "${1:-}" in
    --plan)
        if [ -z "${2:-}" ]; then
            echo -e "${RED}Error: PRD file required. Usage: ./ralph.sh --plan <prd-file>${NC}"
            exit 1
        fi
        generate_plan "$2"
        ;;
    --run)
        if [ -n "${2:-}" ] && [[ ! "$2" == --* ]]; then
            TASKS_FILE="$2"
        fi
        require_tasks_file
        load_workflow_settings
        COMMIT_ON_COMPLETE="true"  # --run always commits after each task step
        EXEC_MODE="auto"
        main_auto
        ;;
    --dry-run)
        require_tasks_file
        load_workflow_settings
        EXEC_MODE="dry-run"
        main_auto
        ;;
    --task)
        if [ -z "${2:-}" ]; then
            echo -e "${RED}Error: Task ID required. Usage: ./ralph.sh --task <task-id>${NC}"
            exit 1
        fi
        require_tasks_file
        load_workflow_settings
        TASK_ID="$2"
        if [ -z "$(get_task_info "$TASK_ID")" ] || [ "$(get_task_info "$TASK_ID")" = "null" ]; then
            echo -e "${RED}Error: Task '$TASK_ID' not found.${NC}"
            exit 1
        fi
        EXEC_MODE="auto"
        init_logging
        run_task_auto "$TASK_ID"
        ;;
    --list|-l)
        require_tasks_file
        echo -e "${BLUE}Pending Tasks:${NC}"
        jq -r '.tasks[] | select(.done == false) | "[\(.phase)] \(.id): \(.title)\(if .dependsOn and (.dependsOn | length > 0) then " (depends: \(.dependsOn | join(", ")))" else "" end)"' "$TASKS_FILE"
        ;;
    --prompts|-p)
        require_tasks_file
        echo -e "${BLUE}Task Prompts:${NC}"
        jq -r '.tasks[] | select(.done == false) | "\n═══ \(.id) ═══\n\(.prompt // "No prompt defined")"' "$TASKS_FILE"
        ;;
    --status|-s)
        require_tasks_file
        show_progress
        ;;
    --reset|-r)
        require_tasks_file
        echo -e "${YELLOW}Resetting all tasks to pending...${NC}"
        safe_jq_update '(.tasks[]).done = false | (.tasks[]).subtasks |= (if . then map(.done = false) else . end)'
        echo -e "${GREEN}All tasks reset.${NC}"
        ;;
    --logs)
        if [ -d "$LOG_DIR" ]; then
            echo -e "${BLUE}Recent logs:${NC}"
            ls -lt "$LOG_DIR"/*.log 2>/dev/null | head -10
            echo ""
            echo -e "${CYAN}View latest: cat $(ls -t "$LOG_DIR"/*.log 2>/dev/null | head -1)${NC}"
        else
            echo -e "${YELLOW}No logs found.${NC}"
        fi
        ;;
    --help|-h)
        echo "Usage: ./ralph.sh [option]"
        echo ""
        echo "Options:"
        echo "  --plan <file>  Generate tasks.json from a PRD file"
        echo "  (none)         Run tasks interactively"
        echo "  --run [file]   Run all pending tasks with Claude Code (default: tasks.json)"
        echo "  --dry-run      Show what would be executed (no actual changes)"
        echo "  --task <id>    Run a specific task by ID"
        echo "  --list, -l     List all pending tasks"
        echo "  --prompts, -p  Show all task prompts"
        echo "  --status, -s   Show progress status"
        echo "  --reset, -r    Reset all tasks to pending"
        echo "  --logs         Show recent log files"
        echo "  --help, -h     Show this help message"
        echo ""
        echo "Workflow:"
        echo "  1. ./ralph.sh --plan PRD.md    # Generate tasks from PRD"
        echo "  2. ./ralph.sh --list           # Review generated tasks"
        echo "  3. ./ralph.sh --dry-run        # Preview execution"
        echo "  4. ./ralph.sh --run            # Execute all tasks"
        echo ""
        echo "Environment variables:"
        echo "  MAX_RETRIES    Max Claude Code retry attempts (default: 2)"
        echo "  RETRY_DELAY    Seconds between retries (default: 5)"
        echo ""
        echo "Examples:"
        echo "  ./ralph.sh --plan docs/PRD.md             # Generate plan from PRD"
        echo "  ./ralph.sh --run                           # Run all pending tasks (uses tasks.json)"
        echo "  ./ralph.sh --run my-tasks.json             # Run tasks from custom file"
        echo "  ./ralph.sh --task user-auth-impl           # Run specific task"
        echo "  ./ralph.sh --dry-run                       # Preview without executing"
        echo "  MAX_RETRIES=3 ./ralph.sh --run             # Run with 3 retry attempts"
        ;;
    "")
        # No arguments: show help
        echo "Usage: ralph.sh [option]"
        echo ""
        echo "Options:"
        echo "  --plan <file>  Generate tasks.json from a PRD file"
        echo "  --run [file]   Run all pending tasks with Claude Code (default: tasks.json)"
        echo "  --dry-run      Show what would be executed (no actual changes)"
        echo "  --task <id>    Run a specific task by ID"
        echo "  --interactive  Run tasks interactively (confirm each one)"
        echo "  --list, -l     List all pending tasks"
        echo "  --prompts, -p  Show all task prompts"
        echo "  --status, -s   Show progress status"
        echo "  --reset, -r    Reset all tasks to pending"
        echo "  --logs         Show recent log files"
        echo "  --help, -h     Show this help message"
        echo ""
        echo "Workflow:"
        echo "  1. ralph.sh --plan PRD.md    # Generate tasks from PRD"
        echo "  2. ralph.sh --list           # Review generated tasks"
        echo "  3. ralph.sh --dry-run        # Preview execution"
        echo "  4. ralph.sh --run            # Execute all tasks"
        ;;
    --interactive)
        require_tasks_file
        load_workflow_settings
        main
        ;;
    *)
        echo -e "${RED}Unknown option: $1${NC}"
        echo "Run 'ralph.sh --help' for usage information."
        exit 1
        ;;
esac
