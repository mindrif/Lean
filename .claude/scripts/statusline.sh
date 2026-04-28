#!/usr/bin/env bash
# Claude Code statusline hook
#
# Features:
# - Project directory
# - Git info with worktree detection
# - Model display
# - Session duration and line changes
# - Session and daily costs (via ccusage)
# - Context window percentage with color coding

set -euo pipefail

# ANSI colors
CYAN=$'\033[1;36m'
GREEN=$'\033[1;32m'
YELLOW=$'\033[1;33m'
RED=$'\033[1;31m'
PURPLE=$'\033[1;35m'
NC=$'\033[0m'

# Emoji icons
ICON_DIR="📁"
ICON_BRANCH="🌿"
ICON_TREE="🌳"
ICON_BOX="📦"
ICON_ROBOT="🤖"
ICON_CLOCK="⏰"
ICON_MONEY="💵"
ICON_BANK="🏦"
ICON_BRAIN="🧠"

# ── Helpers ──────────────────────────────────────────────────

get_git_info() {
    command -v git &>/dev/null || { echo "no-git"; return; }

    local branch
    branch=$(git rev-parse --abbrev-ref HEAD 2>/dev/null) || { echo "no-git"; return; }

    local git_dir git_common is_bare wt_count
    git_dir=$(git rev-parse --git-dir 2>/dev/null || echo "")
    git_common=$(git rev-parse --git-common-dir 2>/dev/null || echo "")
    is_bare=$(git rev-parse --is-bare-repository 2>/dev/null || echo "false")
    wt_count=$(git worktree list 2>/dev/null | wc -l || echo 1)
    wt_count=$(( wt_count + 0 ))  # trim whitespace

    if [[ "$is_bare" == "true" ]]; then
        echo "${ICON_BOX} bare [${wt_count}wt]"
    elif [[ -n "$git_dir" && -n "$git_common" && "$git_dir" != "$git_common" ]]; then
        local main_repo
        main_repo=$(basename "$(dirname "$git_common")")
        echo "${ICON_TREE} ${branch} (<-${main_repo}) [${wt_count}wt]"
    elif (( wt_count > 1 )); then
        echo "${ICON_BRANCH} ${branch} [${wt_count}wt]"
    else
        echo "${ICON_BRANCH} ${branch}"
    fi
}

get_daily_cost() {
    local ccusage_bin="${HOME}/.local/share/mise/installs/npm-ccusage/18.0.10/bin/ccusage"
    [[ -x "$ccusage_bin" ]] || { echo "0.00"; return; }

    local output
    output=$("$ccusage_bin" daily --json --offline 2>/dev/null) || { echo "0.00"; return; }

    echo "$output" | jq -r '
        if .daily then
            (.daily[-1].totalCost // 0) | . * 100 | round | . / 100 | tostring
        elif type == "array" and length > 0 then
            ((.[-1].totalCost // .[-1].total_cost // 0) | . * 100 | round | . / 100 | tostring)
        else
            "0.00"
        end
    ' 2>/dev/null || echo "0.00"
}

format_duration() {
    local ms=$1
    if (( ms <= 0 )); then echo "0m"; return; fi

    local total_seconds=$((ms / 1000))
    local hours=$((total_seconds / 3600))
    local minutes=$(( (total_seconds % 3600) / 60 ))

    if (( hours > 0 )); then
        printf '%dh %02dm' "$hours" "$minutes"
    elif (( minutes > 0 )); then
        echo "${minutes}m"
    else
        echo "<1m"
    fi
}

# ── Main ─────────────────────────────────────────────────────

INPUT=$(cat 2>/dev/null || echo '{}')
if [[ -z "$INPUT" || "$INPUT" == "{}" ]]; then
    echo "No input"
    exit 0
fi

# Extract values
MODEL=$(echo "$INPUT" | jq -r '.model.display_name // "Sonnet"')
SESSION_COST=$(echo "$INPUT" | jq -r '.cost.total_cost_usd // 0')
DURATION_MS=$(echo "$INPUT" | jq -r '.cost.total_duration_ms // 0' | cut -d. -f1)
LINES_ADDED=$(echo "$INPUT" | jq -r '.cost.total_lines_added // 0')
LINES_REMOVED=$(echo "$INPUT" | jq -r '.cost.total_lines_removed // 0')
PERCENT=$(echo "$INPUT" | jq -r '.context_window.used_percentage // 0' | cut -d. -f1)

# Format costs
SESSION_COST=$(printf '%.2f' "$SESSION_COST")

# Derived info
DIR_NAME=$(basename "$PWD")
GIT_INFO=$(get_git_info)
DAILY_COST=$(get_daily_cost)
DURATION=$(format_duration "$DURATION_MS")

# Context color
if (( PERCENT > 80 )); then BRAIN_COL="$RED"
elif (( PERCENT > 50 )); then BRAIN_COL="$YELLOW"
else BRAIN_COL="$GREEN"
fi

# Daily cost color (use awk for float comparison)
if awk "BEGIN{exit !($DAILY_COST > 5.00)}" 2>/dev/null; then
    DAILY_COL="$RED"
else
    DAILY_COL="$PURPLE"
fi

# Build and print
echo "${ICON_DIR} ${CYAN}${DIR_NAME}${NC} | ${GIT_INFO} | ${ICON_ROBOT} ${MODEL} | ${ICON_CLOCK} ${DURATION} | ${GREEN}+${LINES_ADDED}${NC} ${RED}-${LINES_REMOVED}${NC} | ${ICON_MONEY} ${GREEN}\$${SESSION_COST}${NC} | ${ICON_BANK} ${DAILY_COL}\$${DAILY_COST}${NC} | ${ICON_BRAIN} ${BRAIN_COL}${PERCENT}%${NC}"
