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
  local out
  out=$(printf '%s\n' "$TOOLS_LIST" | timeout 60 "$@" 2>/dev/null)

  local missing=()
  local tool
  for tool in "${EXPECTED_TOOLS[@]}"; do
    grep -q "\"$tool\"" <<<"$out" || missing+=("$tool")
  done

  if [[ ${#missing[@]} -gt 0 ]]; then
    echo "    functional gate FAILED -- missing: ${missing[*]}"
    return 1
  fi
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

grep -E "IL[0-9]{4}" "$OUT/publish-sc-trim.log" > "$OUT/trim-warnings.txt" 2>/dev/null || true
echo "trim warnings archived: $(wc -l < "$OUT/trim-warnings.txt" 2>/dev/null || echo 0)"
echo

for id in fdd sc sc-trim sc-r2r; do
  bin="$OUT/publish/$id/Talent.Mcp.Server.Stdio"
  if [[ ! -x "$bin" ]]; then
    echo "==> $id: no binary, skipping"
    continue
  fi
  echo "==> measuring $id"
  if functional_gate "$bin"; then
    measure "$id" "$bin"
  else
    echo "    timings NOT recorded -- a config that serves nothing is not a faster config"
  fi
done

echo
echo "wrote $OUT/startup-metrics.json and $OUT/startup-metrics.md"
