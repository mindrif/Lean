# Lean engine task runner
set dotenv-load := true

# List all recipes
default:
    @just --list

# ── Claude ──────────────────────────────────────────────

# Start interactive Claude session (opus)
[group: 'claude']
cld:
    tmux new-session -s "claude-$(date +%Y%m%d-%H%M%S)" "claude --dangerously-skip-permissions"

# Start Claude in worktree + tmux (isolated background work)
[group: 'claude']
cld-wt:
    claude --dangerously-skip-permissions --worktree --tmux

# Continue last Claude session
[group: 'claude']
cldc:
    tmux new-session -s "claude-$(date +%Y%m%d-%H%M%S)" "claude --dangerously-skip-permissions --continue"

# Continue last session in most recent worktree
[group: 'claude']
cldc-wt:
    @dir=$$(ls -td .claude/worktrees/*/ 2>/dev/null | head -1) && \
    if [ -z "$$dir" ]; then echo "No worktrees found"; exit 1; fi && \
    echo "Resuming in: $$dir" && \
    cd "$$dir" && claude --dangerously-skip-permissions --continue

# ── Build ───────────────────────────────────────────────

# Build the solution
[group: 'build']
build:
    dotnet build QuantConnect.Lean.sln {{ if cpus != "0" { "-maxcpucount:" + cpus } else { "-maxcpucount" } }}

# Build in Release mode
[group: 'build']
build-release:
    dotnet build QuantConnect.Lean.sln -c Release {{ if cpus != "0" { "-maxcpucount:" + cpus } else { "-maxcpucount" } }}

# Clean build artifacts
[group: 'build']
clean:
    dotnet clean QuantConnect.Lean.sln

# ── Test ────────────────────────────────────────────────

# PythonNet requires Python 3.11 (Lean uses QuantConnect.pythonnet 2.0.53)
pydll := env("PYTHONNET_PYDLL", home_directory() / ".local/share/mise/installs/python/3.11/lib/libpython3.11.so")
# Max CPU cores for build/test (default: 24 = half of 48-core box)
cpus := env("LEAN_CPUS", "24")

# Run all tests (run 'just build' first if source changed)
[group: 'test']
test:
    PYTHONNET_PYDLL={{pydll}} dotnet test Tests/QuantConnect.Tests.csproj --no-build -- RunConfiguration.MaxCpuCount={{cpus}}

# Run tests matching a filter
[group: 'test']
test-filter filter:
    PYTHONNET_PYDLL={{pydll}} dotnet test Tests/QuantConnect.Tests.csproj --no-build --filter "{{filter}}" -- RunConfiguration.MaxCpuCount={{cpus}}

# Run B3 (Brazil) fee tests
[group: 'test']
test-b3:
    PYTHONNET_PYDLL={{pydll}} dotnet test Tests/QuantConnect.Tests.csproj --no-build --filter "FullyQualifiedName~Brazil" -- RunConfiguration.MaxCpuCount={{cpus}}

# Run all IB fee model tests (USA, India, Brazil)
[group: 'test']
test-ib-fees:
    PYTHONNET_PYDLL={{pydll}} dotnet test Tests/QuantConnect.Tests.csproj --no-build --filter "FullyQualifiedName~InteractiveBrokersFeeModel" -- RunConfiguration.MaxCpuCount={{cpus}}

# Build and test in one step
[group: 'test']
bt: build test

# ── Upgrades ─────────────────────────────────────────────

# Upgrade Claude Code to latest
[group: 'upgrade']
claude-upgrade:
    claude update
