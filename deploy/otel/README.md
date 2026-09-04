# OTel Collector config — `collector.yaml`

Versions and the research behind every choice below are in
[`docs/verification/otel-stack-versions.md`](../../docs/verification/otel-stack-versions.md), dated
4 Sep 2026 — including a second pass the same day where the full stack was actually run with Docker
Desktop and every signal (traces, metrics, logs, the Grafana dashboard) was confirmed end to end. This
file is the short version, for whoever is looking at the compose stack, not the paper trail.

## Five things that are not what the obvious tutorial shows

**1. Jaeger v1 is EOL (31 Dec 2025).** `deploy/compose.yaml` runs Jaeger v2, which is rebuilt on the
OTel Collector framework and accepts OTLP directly on 4317/4318. There is no `jaegerexporter`
component in `collector.yaml` — the trace pipeline uses an ordinary OTLP exporter (`otlp_grpc/jaeger`)
pointed at `jaeger:4317`. If a future change reintroduces a dedicated Jaeger exporter, check the
verification record first; that was the wrong shape for this version.

**2. Loki's OTLP endpoint is `/otlp`, not `/otlp/v1/logs`.** The other OTLP HTTP paths follow the
`/v1/<signal>` convention; Loki's does not. `otlp_http/loki` in `collector.yaml` targets
`http://loki:3100/otlp` exactly.

**3. Neither `otel/opentelemetry-collector-contrib` nor `grafana/loki` ships a shell.** `compose.yaml`
gives neither a `healthcheck` — for the Collector, not even `CMD-SHELL` has a binary to run inside the
image; for Loki it goes further, `docker exec ... sh` itself fails with `exec: "sh": executable file
not found in $PATH`, so there is no way to probe it from inside at all. Dependents wait on
`service_started`, not `service_healthy`, for both. OTLP exporters retry on their own if the Collector
is not quite ready yet, so this costs latency, not correctness.

**4. `service.telemetry.metrics.address` does not exist in Collector 0.160.0.** It crash-loops the
container instead of warning (`'migration.MetricsConfigV030' has invalid keys: address`) — the
self-telemetry metrics config moved onto the OTel Go SDK's own "readers" schema. `collector.yaml`
leaves this block out entirely rather than chasing the new schema for a diagnostic-only signal nothing
here depends on.

**5. A Grafana bind mount cannot nest inside another read-only one.** Mounting the dashboard JSON at
`/etc/grafana/provisioning/dashboards/json` — inside the same tree as the `:ro`
`/etc/grafana/provisioning` mount — fails container creation outright ("read-only file system"). The
dashboards volume mounts at `/var/lib/grafana/dashboards` instead (inside the writable `grafana-data`
volume), and `provisioning/dashboards/dashboards.yaml`'s `options.path` points there.

## Why contrib, not core

The `prometheusexporter` (metrics exposed for Prometheus to scrape, used here instead of
remote-write) ships in the contrib distribution. Core alone would cover traces and logs, but contrib
is the one image that covers all three signals this stack needs, so it is the only one used.

## Metric names — confirmed, not predicted

`deploy/grafana/dashboards/talent-mcp-overview.json`'s PromQL uses `talent_tool_duration_milliseconds_bucket`,
`talent_tool_errors_total` and `talent_tasks_in_flight`. These were checked against a live Collector
0.160.0 on 4 Sep 2026 (a standalone OTLP emitter, `curl`'d back off the Collector's `:8889` endpoint and
off Prometheus's own query API) and matched on the first try — see the verification record's finding 5
for the exact commands. If a future Collector upgrade changes its OTel-to-Prometheus sanitization
rules, re-run `curl http://localhost:8889/metrics | grep talent_` and fix the dashboard to match.
