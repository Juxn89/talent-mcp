# Verification Scripts

Pre-release verification and testing utilities.

## `verify-f5.ps1` (Windows / PowerShell)

Complete pre-tag verification for F5 (Packaging & CI).

**Usage:**

```powershell
./scripts/verify-f5.ps1 [options]
```

**Options:**

- `--SkipTests` — Skip running all tests (only verify build and packaging)
- `--SkipCompose` — Don't start docker compose (skips conformance/E2E tests)
- `--Docker` — Build and verify Docker image

**Examples:**

```powershell
# Full verification (recommended before tagging)
./scripts/verify-f5.ps1

# Quick build & package check (skip tests)
./scripts/verify-f5.ps1 -SkipTests

# Full verification including Docker image
./scripts/verify-f5.ps1 -Docker

# Local dev: skip compose-dependent tests
./scripts/verify-f5.ps1 -SkipCompose
```

**What it checks:**

1. ✅ Prerequisites (.NET, Docker available)
2. ✅ Build succeeds with zero warnings
3. ✅ All 5 test levels pass (optional with `--SkipTests`)
4. ✅ NuGet packages pack successfully
5. ✅ Package contents valid (tools/ folder present)
6. ✅ Docker image builds (optional with `--Docker`)
7. ✅ Docker image runs as non-root user

**Exit codes:**

- `0` — All checks passed
- `1` — One or more checks failed

---

## `verify-f5.sh` (macOS / Linux / WSL)

Same verification as PowerShell version, bash implementation.

**Usage:**

```bash
chmod +x scripts/verify-f5.sh
./scripts/verify-f5.sh [options]
```

**Options:**

- `--skip-tests` — Skip running all tests
- `--skip-compose` — Don't start docker compose
- `--docker` — Build and verify Docker image

**Examples:**

```bash
# Full verification
./scripts/verify-f5.sh

# Skip tests, only check build & packaging
./scripts/verify-f5.sh --skip-tests

# Full verification with Docker
./scripts/verify-f5.sh --docker
```

---

## Pre-Tag Workflow

Before tagging a release:

```bash
# 1. Run verification (all checks pass)
./scripts/verify-f5.ps1          # or ./scripts/verify-f5.sh

# 2. If all pass, push the branch
git push origin feat/f5-packaging-ci

# 3. Open PR on GitHub and wait for CI to pass

# 4. After CI green and PR merged to main, tag
git checkout main
git pull origin main
git tag v1.0.0
git push origin v1.0.0

# 5. GitHub Actions publish workflow runs automatically
# Watch: https://github.com/juxn89/talent-mcp/actions
```

---

## Troubleshooting

| Issue | Solution |
|---|---|
| Build fails with warnings | Fix Roslyn analyzer violations (check `.editorconfig`) |
| Tests fail | Ensure compose stack running: `docker compose -f deploy/compose.yaml up -d` |
| Docker build fails | Check `Dockerfile` and `.dockerignore` are present |
| Permission denied (bash) | Run `chmod +x scripts/verify-f5.sh` |

---

## Manual Verification Steps (Alternative)

If you prefer to run checks manually:

```bash
# Build
dotnet build -c Release

# Tests
docker compose -f deploy/compose.yaml up -d
dotnet test

# Pack
dotnet pack src/Talent.Mcp.Server.Stdio -c Release --no-build
dotnet pack src/Talent.Mcp.Toolkit -c Release --no-build

# Docker (optional)
docker build -t talent-mcp:verify .
docker run -p 5000:5000 talent-mcp:verify
```

---

## CI/CD Notes

- **Local verification:** Use `verify-f5.ps1` or `verify-f5.sh`
- **GitHub Actions:** `.github/workflows/ci.yml` runs on every PR
- **Release publishing:** `.github/workflows/publish.yml` runs on tag push

---

## `verify-f6.sh` / `verify-f6.ps1`

Pre-tag verification for F6 (trim posture, benchmarks, documentation). Complements the F5 pair
rather than replacing it: F5 checks that the artifacts build and pack, F6 checks what F6 added.

```bash
./scripts/verify-f6.sh [--skip-bench] [--skip-publish]
```

```powershell
.\scripts\verify-f6.ps1 [-SkipBench]
```

**What it checks:**

1. The build is clean with the trim analyzers armed — `IsAotCompatible` turns `IL2026`/`IL3050` into
   errors, so a newly introduced reflection-based serialization site fails here.
2. `TrimPostureRules` — the four inner assemblies still declare `IsTrimmable`. Catches
   `IsAotCompatible` being dropped from a `.csproj`, which otherwise breaks nothing and fails nothing.
3. The task-store `jsonb` wire format and the golden handle vector are unchanged.
4. The three test suites that need no Docker.
5. The benchmark harness executes (`--job dry`). This checks that it *runs*, not that the numbers are
   publishable — publishable numbers come from a deliberate run on named hardware.
6. The trimmed self-contained publish completes (bash only; the PowerShell version skips it, since
   ADR-0007 puts cold-start measurement on one CI runner rather than on a developer laptop).

A successful trimmed publish does **not** prove the binary serves anything. That is the functional
gate in `measure-startup.sh`, which CI runs on every push.

---

## `measure-startup.sh` and `summarize-startup.py`

Cold start and memory for the stdio host, across four publish configurations. Run by the
`startup-benchmark` CI job; runnable locally against any reachable Postgres.

```bash
scripts/measure-startup.sh [--runs 12] [--warmup 2] [--out artifacts]
```

Publishes framework-dependent (the JIT baseline), self-contained, trimmed, and ReadyToRun from one
commit, then for each one runs a functional gate — `tools/list` must return all six tools **by name**
— before recording any timing. A configuration that starts fast and serves nothing is not a faster
configuration, and that failure is silent: `-32601`, no crash, no error log.

Writes `startup-metrics.json` (every raw sample) and `startup-metrics.md` (the table that lands in
ADR-0007 and the job summary), plus `trim-warnings.txt`, the unsuppressed ILLink output.

Requires `/usr/bin/time` — peak RSS is read externally because a process cannot reliably observe its
own peak, and the peak may occur after the last phase marker.

The environment variables it needs are the ones the host refuses to start without:
`ConnectionStrings__Talent` and `Talent__HandleSigningKey`. There is deliberately no flag to skip the
database; see ADR-0007 for why.

