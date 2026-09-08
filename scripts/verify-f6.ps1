# Pre-tag verification for F6 (trim posture, benchmarks, documentation).
#
# Complements verify-f5.ps1 rather than replacing it. See scripts/verify-f6.sh for the fuller
# commentary; this is the Windows equivalent, minus the trimmed linux-x64 publish, which belongs in
# CI anyway (ADR-0007: cold start is measured on one runner, not on a developer laptop).
#
# Usage: .\scripts\verify-f6.ps1 [-SkipBench]

param([switch]$SkipBench)

$failed = $false

function Step($m) { Write-Host ""; Write-Host "==> $m" -ForegroundColor Cyan }
function Ok($m)   { Write-Host "    OK: $m" -ForegroundColor Green }
function Bad($m)  { Write-Host "    FAIL: $m" -ForegroundColor Red; $script:failed = $true }

Step "1. Build with the trim analyzers armed"
dotnet build -c Release --nologo | Out-Null
if ($LASTEXITCODE -eq 0) { Ok "0 warnings, 0 errors" } else { Bad "build failed" }

Step "2. Trim posture is asserted, not merely configured"
dotnet test tests/Talent.Architecture.Tests -c Release --no-build --nologo -v:q --filter "TrimPostureRules" | Out-Null
if ($LASTEXITCODE -eq 0) { Ok "the four inner assemblies still declare IsTrimmable" }
else { Bad "TrimPostureRules -- IsAotCompatible was probably dropped from a csproj" }

Step "3. The jsonb wire format and the golden handle vector"
dotnet test tests/Talent.Mcp.Tests -c Release --no-build --nologo -v:q --filter "TaskStoreSerialization|Handle_bytes_are_unchanged" | Out-Null
if ($LASTEXITCODE -eq 0) { Ok "task-store bytes unchanged; handle vector unchanged" }
else { Bad "serialization format changed -- see ADR-0007 before shipping this" }

Step "4. Tests that need no Docker"
@("tests/Talent.Architecture.Tests", "tests/Talent.Domain.Tests", "tests/Talent.Mcp.Tests") | ForEach-Object {
    $name = Split-Path -Leaf $_
    dotnet test $_ -c Release --no-build --nologo -v:q | Out-Null
    if ($LASTEXITCODE -eq 0) { Ok $name } else { Bad $name }
}

if (-not $SkipBench) {
    Step "5. Benchmark harness runs"
    # --job dry checks the harness executes; publishable numbers come from a deliberate run on
    # named hardware, never from a busy machine.
    # --artifacts is NOT optional: without it a dry run overwrites the real results in
    # BenchmarkDotNet.Artifacts with meaningless cold-start-per-op numbers.
    $scratch = Join-Path $env:TEMP 'f6-bench-artifacts'
    dotnet run --project bench/Talent.Mcp.Bench -c Release --no-build -- --filter '*' --job dry --artifacts $scratch | Out-Null
    if ($LASTEXITCODE -eq 0) { Ok "benchmarks execute" } else { Bad "benchmark harness failed" }
} else {
    Step "5. Benchmarks skipped (-SkipBench)"
}

Write-Host ""
if (-not $failed) { Write-Host "SUCCESS: F6 checks passed." -ForegroundColor Green; exit 0 }
Write-Host "FAILED: fix the above before tagging." -ForegroundColor Red
exit 1
