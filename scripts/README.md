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
