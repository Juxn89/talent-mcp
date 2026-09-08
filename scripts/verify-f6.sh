#!/usr/bin/env bash
#
# Pre-tag verification for F6 (trim posture, benchmarks, documentation).
#
# Complements verify-f5.sh rather than replacing it: F5 checks that the artifacts build and pack,
# F6 checks the things F6 added -- that the trim gate is armed and clean, that the benchmark harness
# still runs, and that the trimmed publish completes and serves its tools.
#
# Usage: scripts/verify-f6.sh [--skip-bench] [--skip-publish]

set -uo pipefail

SKIP_BENCH=0
SKIP_PUBLISH=0
FAILED=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --skip-bench)   SKIP_BENCH=1;   shift ;;
    --skip-publish) SKIP_PUBLISH=1; shift ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

step() { echo; echo "==> $1"; }
ok()   { echo "    OK: $1"; }
bad()  { echo "    FAIL: $1"; FAILED=1; }

step "1. Build with the trim analyzers armed"
# The point is zero warnings. IsAotCompatible turns IL2026/IL3050 into errors via
# TreatWarningsAsErrors, so a new reflection-based serialization site fails right here.
if dotnet build -c Release --nologo > /tmp/f6-build.log 2>&1; then
  ok "0 warnings, 0 errors"
else
  bad "build failed"
  grep -E "error" /tmp/f6-build.log | head -10
fi

step "2. Trim posture is asserted, not merely configured"
if dotnet test tests/Talent.Architecture.Tests -c Release --no-build --nologo -v:q \
     --filter "TrimPostureRules" > /dev/null 2>&1; then
  ok "the four inner assemblies still declare IsTrimmable"
else
  bad "TrimPostureRules -- IsAotCompatible was probably dropped from a csproj"
fi

step "3. The jsonb wire format and the golden handle vector"
if dotnet test tests/Talent.Mcp.Tests -c Release --no-build --nologo -v:q \
     --filter "TaskStoreSerialization|Handle_bytes_are_unchanged" > /dev/null 2>&1; then
  ok "task-store bytes unchanged; handle vector unchanged"
else
  bad "serialization format changed -- see ADR-0007 before shipping this"
fi

step "4. Tests that need no Docker"
for proj in tests/Talent.Architecture.Tests tests/Talent.Domain.Tests tests/Talent.Mcp.Tests; do
  name=$(basename "$proj")
  if dotnet test "$proj" -c Release --no-build --nologo -v:q > /dev/null 2>&1; then
    ok "$name"
  else
    bad "$name"
  fi
done

if [[ $SKIP_BENCH -eq 0 ]]; then
  step "5. Benchmark harness runs"
  # --job dry: this checks the harness still executes, not that the numbers are publishable.
  # Publishable numbers come from a deliberate run on named hardware, never from a busy machine.
  if dotnet run --project bench/Talent.Mcp.Bench -c Release --no-build -- \
       --filter '*' --job dry > /tmp/f6-bench.log 2>&1; then
    ok "benchmarks execute"
  else
    bad "benchmark harness failed (see /tmp/f6-bench.log)"
  fi
else
  step "5. Benchmarks skipped (--skip-bench)"
fi

if [[ $SKIP_PUBLISH -eq 0 ]]; then
  step "6. Trimmed publish completes"
  # SuppressTrimAnalysisWarnings is required here and ONLY here: ILLink warnings are MSBuild
  # warnings, TreatWarningsAsErrors is global, and the graph contains EF Core. Without it the
  # publish cannot complete at all. See ADR-0007.
  if dotnet publish src/Talent.Mcp.Server.Stdio -c Release -r linux-x64 \
       --self-contained true -p:PublishTrimmed=true -p:TrimMode=full \
       -p:SuppressTrimAnalysisWarnings=true -o /tmp/f6-publish --nologo \
       > /tmp/f6-publish.log 2>&1; then
    ok "trimmed self-contained publish succeeded"
    echo "    NOTE: this does not prove it SERVES anything. scripts/measure-startup.sh"
    echo "          runs the functional gate against live Postgres; CI does that on every push."
  else
    bad "trimmed publish failed (see /tmp/f6-publish.log)"
  fi
else
  step "6. Publish skipped (--skip-publish)"
fi

echo
if [[ $FAILED -eq 0 ]]; then
  echo "SUCCESS: F6 checks passed."
  exit 0
fi
echo "FAILED: fix the above before tagging."
exit 1
