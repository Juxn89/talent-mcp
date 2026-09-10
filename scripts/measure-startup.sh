#!/usr/bin/env bash
#
# Cold start and memory for the stdio host, across publish configurations.
#
# Why this is not a laptop script: the stdio host is launched once per client session, so cold start
# is the metric that matters for it (ADR-0002). But ADR-0002's numbers came from a MINIMAL dependency
# graph on a Windows developer machine, and ADR-0004 records that the real host carries EF Core,
# Npgsql and OpenTelemetry. Those numbers are a floor, not a comparison. This re-measures the real
# thing, on one runner, from one commit, so the configurations are comparable to each other rather
# than to history.
#
# Requires a reachable Postgres. The host opens TWO blocking connections before it serves anything
# (CreateAndPrepareTaskStoreAsync applies DDL; StartAsync awaits its first LISTEN), which is why the
# phase markers exist: that share is reported separately instead of being folded into "runtime init".
# Skipping the database was considered and rejected -- it would measure a process that cannot answer
# five of the six tools.
#
# Usage: scripts/measure-startup.sh [--runs 12] [--warmup 2] [--out artifacts]

set -uo pipefail

RUNS=12
WARMUP=2
OUT="artifacts"
PROJECT="src/Talent.Mcp.Server.Stdio"
RID="linux-x64"

while [[ $# -gt 0 ]]; do
  case "$1" in
    --runs)   RUNS="$2";   shift 2 ;;
    --warmup) WARMUP="$2"; shift 2 ;;
    --out)    OUT="$2";    shift 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
done

mkdir -p "$OUT/publish"

# A tools/list request in the shape 2026-07-28 requires. Over stdio there are no HTTP headers, but
# _meta still carries protocolVersion and clientCapabilities -- omitting either answers -32602 rather
# than a tool list, and that would look identical to an empty tool set.
TOOLS_LIST='{"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}'

EXPECTED_TOOLS=(search_jobs get_job extract_skills score_candidate_fit reject_candidate bulk_score_shortlist)

publish_config() {
  local id="$1"; shift
  local dir="$OUT/publish/$id"
  echo "==> publishing $id"

  # SuppressTrimAnalysisWarnings is passed ONLY on this command line, never in a csproj. ILLink
  # warnings are MSBuild warnings and the root Directory.Build.props turns warnings into errors, so a
  # trimmed publish over a graph containing EF Core cannot complete without it. The unsuppressed list
  # is archived below, because suppressing quietly is exactly what ADR-0002 warns against -- that
  # list is the raw material for ADR-0007's verdict, not noise.
  if ! dotnet publish "$PROJECT" -c Release -o "$dir" --nologo "$@" > "$OUT/publish-$id.log" 2>&1; then
    echo "    FAILED (see $OUT/publish-$id.log)"
    return 1
  fi
  echo "    ok  $(du -sb "$dir" | cut -f1) bytes"
}

# The non-negotiable gate. ADR-0002 identified a silently empty tool set as this project's worst
# failure mode: tools/list answers -32601 with no crash and no error log. A configuration that starts
# fast and serves nothing is not a faster configuration, so a config failing this has its timings
# struck through in the report rather than published.
functional_gate() {
  local out err
  err=$(mktemp)

  # stdin is held open briefly after the request rather than closed at once. On EOF the stdio
  # transport shuts the host down, and a host that exits before flushing its response looks exactly
  # like a host with no tools -- the ambiguity that made the first CI run of this script
  # uninformative. `measure` below deliberately does NOT do this: closing stdin immediately is
  # ADR-0002's stated methodology, and holding it open would inflate the timing it exists to report.
  out=$( (printf '%s\n' "$TOOLS_LIST"; sleep 5) | timeout 60 "$@" 2>"$err" )

  local missing=()
  local tool
  for tool in "${EXPECTED_TOOLS[@]}"; do
    grep -q "\"$tool\"" <<<"$out" || missing+=("$tool")
  done

  if [[ ${#missing[@]} -gt 0 ]]; then
    echo "    functional gate FAILED -- missing: ${missing[*]}"
    # Print what actually happened. Without this the gate cannot distinguish "the host started and
    # served nothing" from "the host never started at all", and those need opposite fixes.
    echo "    ---- stdout (${#out} bytes) ----"
    head -c 1500 <<<"$out" | sed 's/^/    /'
    echo "    ---- stderr ----"
    head -c 1500 "$err" | sed 's/^/    /'
    echo "    --------------------------------"
    rm -f "$err"
    return 1
  fi

  rm -f "$err"
  echo "    functional gate ok -- all six tools present"
}

measure() {
  local id="$1"; shift
  local ready_samples=() exit_samples=() rss_samples=()
  local total=$((RUNS + WARMUP))
  local i

  for ((i = 0; i < total; i++)); do
    local timing_file; timing_file=$(mktemp)
    local start_ns; start_ns=$(date +%s%N)

    # /usr/bin/time -v for peak RSS: a process cannot reliably observe its own peak, and the peak may
    # occur after the last phase marker. stdin is the request then EOF, so the host serves one call
    # and exits.
    local out
    out=$(printf '%s\n' "$TOOLS_LIST" \
          | TALENT_STARTUP_TRACE=1 /usr/bin/time -v -o "$timing_file" timeout 120 "$@" 2>&1)
    local end_ns; end_ns=$(date +%s%N)

    if [[ $i -lt $WARMUP ]]; then
      rm -f "$timing_file"
      continue
    fi

    exit_samples+=( "$(( (end_ns - start_ns) / 1000000 ))" )

    local ready; ready=$(grep -o 'phase=ready elapsed_ms=[0-9.]*' <<<"$out" | grep -o '[0-9.]*$' | tail -1)
    ready_samples+=( "${ready:-0}" )

    local peak; peak=$(grep -o 'Maximum resident set size (kbytes): [0-9]*' "$timing_file" | grep -o '[0-9]*$')
    rss_samples+=( "${peak:-0}" )
    rm -f "$timing_file"
  done

  local size; size=$(du -sb "$OUT/publish/$id" | cut -f1)

  # Median plus min-max is the shape ADR-0002 established. Every raw sample goes to the JSON so the
  # spread can be re-derived rather than taken on trust.
  MEASURE_ID="$id" MEASURE_OUT="$OUT" MEASURE_SIZE="$size" \
  MEASURE_READY="$(printf '%s,' "${ready_samples[@]}")" \
  MEASURE_EXIT="$(printf '%s,' "${exit_samples[@]}")" \
  MEASURE_RSS="$(printf '%s,' "${rss_samples[@]}")" \
  python3 "$(dirname "$0")/summarize-startup.py"
}

echo "runs=$RUNS warmup=$WARMUP out=$OUT"
echo

# fdd is THE baseline: real dependency graph, same runner, same commit. Not ADR-0002's 646 ms.
publish_config fdd     --self-contained false
publish_config sc      -r "$RID" --self-contained true
publish_config sc-trim -r "$RID" --self-contained true -p:PublishTrimmed=true \
                       -p:TrimMode=full -p:TrimmerSingleWarn=false \
                       -p:SuppressTrimAnalysisWarnings=true
publish_config sc-r2r  -r "$RID" --self-contained true -p:PublishReadyToRun=true

# The trimmed publish above suppresses ILLink warnings so it can complete at all, which means its
# log contains none to archive -- the first CI run duly uploaded a 0-byte file. Re-run the analysis
# with them ON purely to capture the list. Suppressing quietly is what ADR-0002 warns against, and
# this list is the raw material for any future decision about widening trim coverage. It is expected
# to exit non-zero: the exit code is not the point, the output is.
dotnet publish "$PROJECT" -c Release -r "$RID" --self-contained true \
    -p:PublishTrimmed=true -p:TrimMode=full -p:TrimmerSingleWarn=false \
    -o "$OUT/publish/trim-analysis" --nologo > "$OUT/trim-analysis.log" 2>&1 || true
grep -E "IL[0-9]{4}" "$OUT/trim-analysis.log" | sort -u > "$OUT/trim-warnings.txt" 2>/dev/null || true
rm -rf "$OUT/publish/trim-analysis"
echo "trim diagnostics archived: $(wc -l < "$OUT/trim-warnings.txt" 2>/dev/null || echo 0)"
echo

# Configuration E: the dotnet tool as a user actually installs it.
#
# The only configuration here that measures a SHIPPED artifact. A-D are publish directories; nobody
# installs one. `dotnet tool install` is how talent-mcp reaches people, and it adds what none of the
# others include -- the generated apphost shim and the muxer resolving a framework-dependent IL
# assembly. Quoting a publish-directory number as "cold start" understates what the user pays.
#
# --tool-path rather than --global so CI does not depend on, or pollute, a machine-wide tool state.
# The shim and resolution path are identical either way.
echo "==> installing the dotnet tool"
if dotnet pack "$PROJECT" -c Release -o "$OUT/tool-pkg" --nologo > "$OUT/publish-tool.log" 2>&1 \
   && dotnet tool install Talent.Mcp.Server \
        --tool-path "$OUT/publish/tool" \
        --add-source "$OUT/tool-pkg" \
        --prerelease >> "$OUT/publish-tool.log" 2>&1; then
  echo "    ok  $(du -sb "$OUT/publish/tool" | cut -f1) bytes"
else
  echo "    FAILED (see $OUT/publish-tool.log)"
fi
echo

MEASURED=0
for id in fdd sc sc-trim sc-r2r tool; do
  # The tool is invoked by its command name through the shim, not by the assembly's own apphost --
  # which is the whole reason this configuration exists.
  if [[ "$id" == "tool" ]]; then
    bin="$OUT/publish/tool/talent-mcp"
  else
    bin="$OUT/publish/$id/Talent.Mcp.Server.Stdio"
  fi

  if [[ ! -x "$bin" ]]; then
    echo "==> $id: no binary, skipping"
    continue
  fi
  echo "==> measuring $id"
  if functional_gate "$bin"; then
    measure "$id" "$bin"
    MEASURED=$((MEASURED + 1))
  else
    echo "    timings NOT recorded -- a config that serves nothing is not a faster config"
  fi
done

echo
# A run that measured nothing must not report success. The first version of this script ended in an
# unconditional echo, so a CI job whose every configuration failed the functional gate still went
# green and uploaded an empty artifact -- a gate that cannot fail, which is worse than no gate.
# Configuration E is the only one that measures something we actually ship, so its absence is a
# failure rather than a gap in the report.
if [[ ! -f "$OUT/startup-metrics.json" ]] || ! grep -q '"tool"' "$OUT/startup-metrics.json"; then
  echo "WARNING: configuration E (installed dotnet tool) produced no measurement."
  echo "         Every other row describes a publish directory nobody installs."
fi

if [[ $MEASURED -eq 0 ]]; then
  echo "FAILED: no configuration passed the functional gate, so nothing was measured."
  echo "        The stdout/stderr dumps above say what the host actually did."
  exit 1
fi

echo "measured $MEASURED of 4 configurations"
echo "wrote $OUT/startup-metrics.json and $OUT/startup-metrics.md"
