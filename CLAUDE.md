# CLAUDE.md — Lean fork conventions

This is `mindrif/Lean`, a fork of `QuantConnect/Lean` maintained for the Kairos
algorithmic trading system. This file captures the conventions specific to
this fork: how it relates to upstream, where Kairos-specific code lives, and
the workflow for staying current with upstream changes.

## Repo layout

```
upstream  → QuantConnect/Lean         (read-only; never push)
origin    → mindrif/Lean              (this fork)
   ├─ master         ← exact mirror of upstream/master, fast-forward only
   └─ kairos-master  ← Kairos dev line; merges upstream periodically + accumulates Kairos work
       └─ feature branches off kairos-master, PR back into it
```

`master` is intentionally kept as a clean mirror — **Kairos-specific commits
never go on `master`**. That gives us a reliable reference point ("what does
upstream look like?") and a place from which to launch upstream syncs.

## Kairos-specific code

Kairos additions live alongside QC code, scoped by namespace and folder:

- `Brokerages/KairosHedging/` — the Kairos Hedging Service brokerage
- `Tests/Brokerages/KairosHedging/` — unit + live-contract tests
- `.claude/`, `.mcp.json`, `.mise.toml`, `justfile` — dev tooling
- `.gitignore` — small Kairos-specific additions (`.env`, statusline negation)

Kairos additions follow the QC in-tree pattern (sibling folders, SDK-glob
auto-include) so we never have to edit `*.csproj` or `*.sln` files. Avoiding
csproj/sln edits dramatically reduces upstream-merge conflict surface.

## Staying in sync with upstream

We sync regularly so the merge stays small. The cost of letting it lag is
non-linear — a 6-month-out-of-date fork takes 10× longer to catch up than a
1-month-out-of-date one.

### Cadence

| Trigger | Action |
|---|---|
| Routine | Monthly sync, even when nothing's urgent |
| Upstream security patch / bugfix you care about | Sync within a few days |
| Before starting substantial new Kairos work | Sync first, so you're building on the latest base |
| New upstream release tag | Sync soon after |

### The sync workflow

```bash
# 1. Update master (the mirror).
#    --ff-only fails loud if master ever diverged. That should never happen
#    because we don't put Kairos commits on master. If it does fail, fix the
#    cause; don't paper over it with a regular merge.
git fetch upstream
git checkout master
git merge --ff-only upstream/master
git push origin master

# 2. Open a sync PR into kairos-master.
git checkout kairos-master
git pull
git checkout -b sync/upstream-$(date +%Y-%m-%d)
git merge master
# resolve any conflicts (only where Kairos changes overlap upstream)
git push -u origin sync/upstream-$(date +%Y-%m-%d)
gh pr create --base kairos-master \
    --title "Sync upstream through $(git rev-parse --short upstream/master)"
```

Open this as a real PR — don't merge directly on `kairos-master`. Reasons:

1. **Reviewable conflict resolution.** When upstream and Kairos touch the
   same file, the resolution lands in this PR alongside the upstream diff
   so reviewers can sanity-check it.
2. **Audit trail.** "We picked up upstream through commit `<sha>` on
   `<date>` via PR #N."
3. **Risk isolation.** If the merge breaks something, the sync PR sits
   until fixed; `kairos-master` keeps moving.

### Why merge, not rebase

`kairos-master` is a shared branch — feature branches read from it.
Rebasing would force-push and orphan everyone else's work. We use merge
commits and accept the slightly busier history; first-parent log views
(`git log --first-parent kairos-master`) still give us a clean Kairos-only
timeline.

## Feature branch workflow

```bash
git checkout kairos-master
git pull
git checkout -b <feature-name>
# … work, commit, possibly squash …
git push -u origin <feature-name>
gh pr create --base kairos-master --title "<concise title>"
```

**Squash-merge** is the default for feature PRs — keeps `kairos-master`'s
linear history readable. Use a regular merge commit only when the multi-
commit history is genuinely informative (rare).

## Tooling

- `just build` — full solution build (24-core default; override via
  `LEAN_CPUS=N just build`)
- `just build-release` — Release-config build
- `just test` — full test suite
- `just test-filter <pattern>` — run a subset
- `just bt` — build + test in one step
- `cld` / `cldc` — start / continue Claude Code sessions in tmux

See `justfile` for the complete recipe list.
