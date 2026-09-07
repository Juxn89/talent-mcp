# Pre-tag verification for F5 (Packaging and CI)
# Usage: .\scripts\verify-f5.ps1 [-SkipTests] [-SkipCompose] [-Docker]

param([switch]$SkipTests, [switch]$SkipCompose, [switch]$Docker)

$failed = $false

Write-Host ""
Write-Host "==== F5 Verification ===="
Write-Host ""

# Step 1: Build
Write-Host "1. Building (Release)..." -ForegroundColor Cyan
dotnet build -c Release | Where-Object { $_ -match "error|warning CS" }
if ($LASTEXITCODE -ne 0) {
    Write-Host "FAIL: Build failed" -ForegroundColor Red
    $failed = $true
} else {
    Write-Host "OK: Build succeeded" -ForegroundColor Green
}

# Step 2: Tests
if (-not $SkipTests) {
    if (-not $SkipCompose) {
        Write-Host "2. Starting docker compose..." -ForegroundColor Cyan
        docker compose -f deploy/compose.yaml up -d --wait 2>$null
        if ($LASTEXITCODE -eq 0) {
            Write-Host "OK: Compose started" -ForegroundColor Green
        } else {
            Write-Host "INFO: Compose unavailable, skipping conformance/E2E" -ForegroundColor Yellow
            $SkipCompose = $true
        }
    }

    @(
        "tests/Talent.Architecture.Tests",
        "tests/Talent.Domain.Tests",
        "tests/Talent.Infrastructure.Tests",
        "tests/Talent.Mcp.Tests",
        "tests/Talent.Mcp.Conformance",
        "tests/Talent.Mcp.E2E"
    ) | ForEach-Object {
        $proj = $_
        $name = Split-Path -Leaf $proj

        if (($name -match "Conformance|E2E") -and $SkipCompose) {
            Write-Host "   SKIP: $name (compose unavailable)" -ForegroundColor Yellow
            return
        }

        Write-Host "   Testing $name..." -NoNewline
        $out = dotnet test $proj -c Release --no-build 2>&1 | Out-Null
        if ($LASTEXITCODE -eq 0) {
            Write-Host " OK" -ForegroundColor Green
        } else {
            Write-Host " FAIL" -ForegroundColor Red
            $failed = $true
        }
    }
} else {
    Write-Host "2. Skipping tests (--SkipTests)" -ForegroundColor Yellow
}

# Step 3: Pack
Write-Host "3. Packing NuGet..." -ForegroundColor Cyan

New-Item -ItemType Directory -Path nupkg -ErrorAction SilentlyContinue | Out-Null
Remove-Item -Path nupkg/* -ErrorAction SilentlyContinue

@("src/Talent.Mcp.Server.Stdio", "src/Talent.Mcp.Toolkit") | ForEach-Object {
    $proj = $_
    $name = Split-Path -Leaf $proj
    Write-Host "   Packing $name..." -NoNewline

    & dotnet pack $proj -c Release --no-build --output nupkg 2>&1 | Out-Null

    if ($LASTEXITCODE -eq 0) {
        $pkg = Get-ChildItem nupkg/*.nupkg -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($pkg) {
            Write-Host " OK ($($pkg.Name))" -ForegroundColor Green
        } else {
            Write-Host " FAIL (no .nupkg)" -ForegroundColor Red
            $failed = $true
        }
    } else {
        Write-Host " FAIL" -ForegroundColor Red
        $failed = $true
    }
}

# Step 4: Docker (optional)
if ($Docker) {
    Write-Host "4. Building Docker..." -ForegroundColor Cyan
    & docker build -t talent-mcp:verify . | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Write-Host "   OK: Docker image built" -ForegroundColor Green
    } else {
        Write-Host "   FAIL: Docker build failed" -ForegroundColor Red
        $failed = $true
    }
}

# Summary
Write-Host ""
if (-not $failed) {
    Write-Host "SUCCESS: All checks passed!" -ForegroundColor Green
    Write-Host ""
    Write-Host "Next steps:" -ForegroundColor Green
    Write-Host "  1. git push origin feat/f5-packaging-ci"
    Write-Host "  2. Wait for CI to pass on GitHub"
    Write-Host "  3. Merge PR to main"
    Write-Host "  4. git tag v1.0.0 && git push origin v1.0.0"
    exit 0
} else {
    Write-Host "FAILED: Fix errors above and re-run" -ForegroundColor Red
    exit 1
}
