# Verification · The OTel Collector/Jaeger/Prometheus/Loki/Grafana stack

| | |
|---|---|
| **Date** | 4 Sep 2026 |
| **Phase** | F4 (observability) |
| **Method** | Web research against each project's own GitHub releases and Docker Hub tags, then re-verified by actually running the full stack with Docker Desktop and calling every one of its APIs directly |
| **Why** | Every image in `deploy/compose.yaml`'s F4 block is pinned to an exact tag on purpose (see the file's own top comment: `"latest" makes a green CI run unreproducible three weeks later`), and several of the findings below turned out to have a different answer than the obvious one |
| **Updated** | 4 Sep 2026 (twice, same day) — first, finding 3's tag corrected from a nonexistent `v3.6.13` to the real `3.7.7` after `docker compose up` failed outright on `image not found`, and all five images pulled directly to confirm; second, finding 5 added once Docker Desktop was actually running: two more config bugs (self-telemetry schema, a read-only-mount conflict) found and fixed, and the end-to-end pipeline — including the metric-name prediction the "Open" section had flagged — confirmed for real |

---

## 1. OpenTelemetry Collector — contrib, not core

**Claimed** (assumed going in): either distribution works, since the Collector's job here is just
OTLP-in, three exporters out.

**Observed.** Latest release is **v0.160.0** (2 Sep 2026,
[opentelemetry-collector-releases](https://github.com/open-telemetry/opentelemetry-collector-releases/releases)).
The scrape-style `prometheusexporter` this stack needs (metrics exposed for Prometheus to pull, not
pushed via remote-write) ships in the **contrib** distribution. Core's OTLP receiver and `otlp`/`otlphttp`
exporters would cover traces and logs alone, but contrib is the one image that covers all three signals,
so it is the only distribution actually used.

**Fixed.** `deploy/compose.yaml` pins `otel/opentelemetry-collector-contrib:0.160.0`. Config path inside
that image is `/etc/otelcol-contrib/config.yaml` — `deploy/otel/collector.yaml` mounts there, not to
`/etc/otel-collector-config.yaml` as some older examples show.

## 2. Jaeger v1 is EOL — this is Jaeger v2, and it takes OTLP directly

**Claimed** (assumed going in, and what most existing tutorials show): a
`jaegertracing/all-in-one:1.x` image, with the Collector's `otlp/jaeger` exporter — or Jaeger's own
`jaeger` exporter component — bridging the two.

**Observed.** Jaeger v1 reached end-of-life **31 Dec 2025**. The current stable release is
**Jaeger v2.20.0** (20 Jul 2026,
[releases](https://github.com/jaegertracing/jaeger/releases) /
[getting-started](https://www.jaegertracing.io/docs/2.20/getting-started/)) — v2 is rebuilt on the
OTel Collector framework itself and **accepts OTLP natively on 4317 (gRPC) and 4318 (HTTP)**. There is
no separate Jaeger exporter component to configure: the Collector's ordinary OTLP exporter
(`otlp_grpc/jaeger` in `deploy/otel/collector.yaml` — 0.160.0 warns that the older `otlp` alias for this
exporter type is deprecated) points straight at Jaeger's OTLP port, the same "ingests OTLP directly"
reasoning `Directory.Packages.props` already gives for why the .NET SDK dropped
`OpenTelemetry.Exporter.Jaeger` in favour of `OpenTelemetry.Exporter.OpenTelemetryProtocol`.

**Why it matters.** Following an all-in-one-1.x tutorial here would have produced a config for a
component (`jaegerexporter`) that either does not exist in current contrib builds the way it used to,
or works but adds a translation hop this version does not need. `deploy/otel/collector.yaml`'s trace
pipeline exports straight OTLP, full stop — confirmed reaching Jaeger for real in finding 5.

**Fixed.** `deploy/compose.yaml` pins the official registry image
`cr.jaegertracing.io/jaegertracing/jaeger:2.20.0` (also mirrored on Docker Hub as
`jaegertracing/jaeger`, marked experimental there — the official registry tag is used instead).

## 3. Loki's OTLP endpoint is `/otlp`, not `/otlp/v1/logs` — and its tags carry no `v` prefix

**Claimed** (assumed going in, from the OTLP spec's usual HTTP path convention for other signals):
`/otlp/v1/logs`. A second, separate claim — from the same round of research that got the path
wrong — asserted a tag `v3.6.13`.

**Observed.** Native OTLP log ingestion has been in Loki since **3.0**. The endpoint Loki actually
exposes is **`/otlp`** — confirmed against
[Grafana's own docs](https://grafana.com/docs/loki/latest/send-data/otel/) and a
[grafana/loki issue](https://github.com/grafana/loki/issues/14037) discussing exactly this path.

The `v3.6.13` tag **does not exist** — checked directly against the Docker Hub tags API
(`hub.docker.com/v2/repositories/grafana/loki/tags`) rather than trusting the earlier web-search
summary a second time. Loki's own tags carry no `v` prefix (`3.6.16`, `3.7.7`, …, not `v3.6.16`).
Current stable is **3.7.7** (27 Aug 2026) — pulled and confirmed present with
`docker pull grafana/loki:3.7.7` before pinning it.

**Why it matters.** `docker compose up` would have failed outright on `image not found` the first time
anyone actually ran this stack — caught here, once Docker Desktop was available, rather than shipped.
It is also the reason every other tag in this record (Jaeger, Prometheus, Grafana, the Collector) was
re-verified the same way — pulled or checked against the registry's own tags API — once Docker was
available, not left resting on the original research pass.

**Fixed.** The Collector's OTLP-over-HTTP exporter (`otlp_http/loki` — 0.160.0 warns `otlphttp` is a
deprecated alias for this exporter type) in `deploy/otel/collector.yaml` targets
`http://loki:3100/otlp`, and the single-binary image is started with
`-config.file=/etc/loki/local-config.yaml`, the shape meant for a demo/all-in-one deployment rather
than Loki's microservices mode. `deploy/compose.yaml` pins `grafana/loki:3.7.7`.

## 4. Prometheus and Grafana — current stable, and `grafana/grafana` not `grafana/grafana-oss`

**Observed.** Prometheus latest stable is **v3.14.0** (17 Aug 2026,
[releases](https://github.com/prometheus/prometheus/releases)); pin `prom/prometheus:v3.14.0` — the
Docker Hub `:latest` tag has been reported to lag behind (a
[prometheus/prometheus issue](https://github.com/prometheus/prometheus/issues/16805) on exactly this),
consistent with why every image in this repo is pinned to an exact tag already.

Grafana latest stable is **13.2.1** (2 Sep 2026, security fixes included,
[releases](https://github.com/grafana/grafana/releases)); pin `grafana/grafana:13.2.1`. **Not**
`grafana/grafana-oss` — per
[Grafana's own Docker install docs](https://grafana.com/docs/grafana/latest/setup-grafana/installation/docker/),
that repository stopped receiving updates after 12.4.0. Dashboard/datasource provisioning via YAML
under `/etc/grafana/provisioning/{datasources,dashboards}` is unchanged in 13.2.1 — no format
migration needed for `deploy/grafana/provisioning/`.

## 5. The full stack, run for real: two more config bugs, then a confirmed end-to-end pipeline

The first pass through this record (findings 1-4) was written before Docker Desktop was available in
the working environment, so `deploy/compose.yaml`'s F4 block had never actually been started. Once
Docker was running, bringing the stack up surfaced two more bugs neither web research nor a syntax
check (`docker compose config`) could have caught, plus the confirmation the "Open" section below used
to ask for.

**5a. `service.telemetry.metrics.address` no longer exists in 0.160.0.** `deploy/otel/collector.yaml`
originally set it to expose the Collector's own self-metrics on `:8888`. The container crash-looped:

```
Error: failed to get config: cannot unmarshal the configuration: decoding failed due to the following error(s):
'service.telemetry.metrics' decoding failed due to the following error(s):
'migration.MetricsConfigV030' has invalid keys: address
```

0.160.0 migrated the Collector's self-telemetry metrics configuration onto the OTel Go SDK's own
"readers" schema; the old flat `address` key is rejected outright rather than deprecated-but-tolerated.
**Fixed** by dropping the override — self-telemetry is diagnostic-only and nothing in this stack reads
it, so it is left on the Collector's own default rather than chasing the new schema.
`deploy/prometheus/prometheus.yml`'s `otel-collector-self` scrape job was removed to match.

**5b. `grafana/loki:3.7.7` ships no shell — not even `wget`, and not even `sh`.**
`docker exec talent-loki sh` itself fails (`exec: "sh": executable file not found in $PATH`), so both
`CMD-SHELL` and a plain `CMD wget` healthcheck are impossible without adding a tool the image does not
carry. This is the same "no shell" constraint `deploy/otel/collector.yaml`'s own container already had
(noted in the original finding 1), now confirmed to apply to Loki too. **Fixed** by removing Loki's
healthcheck; the Collector's and Grafana's `depends_on: loki` both use `service_started`, not
`service_healthy`.

**5c. A bind mount cannot create a mountpoint inside another read-only bind mount.** The Grafana
service originally mounted `./grafana/dashboards` at
`/etc/grafana/provisioning/dashboards/json` — nested inside the `:ro` mount of the whole
`/etc/grafana/provisioning` tree from the line above it. Container creation failed:

```
error mounting ".../deploy/grafana/dashboards" to rootfs at "/etc/grafana/provisioning/dashboards/json":
create mountpoint ...: read-only file system
```

**Fixed** by mounting the dashboard JSON under `/var/lib/grafana/dashboards` instead — inside the
writable `grafana-data` volume, not the read-only provisioning tree — and pointing
`deploy/grafana/provisioning/dashboards/dashboards.yaml`'s `options.path` there.

**Confirmed, end to end, with the stack actually running:**

- A standalone OTLP metric emitter recording `talent.tool.duration` (histogram, tag `tool.name`),
  `talent.tool.errors` (counter) and `talent.tasks.in_flight` (gauge) against
  `http://localhost:4317` produced, on the Collector's `:8889` Prometheus endpoint, **exactly** the
  names `talent-mcp-overview.json` had predicted before any of this could be tested:
  `talent_tool_duration_milliseconds_bucket`/`_sum`/`_count`, `talent_tool_errors_total`,
  `talent_tasks_in_flight`. The original prediction needed no correction.
- Prometheus (`GET /api/v1/query`) scraped all three and answered the dashboard's actual panel
  queries — `histogram_quantile(0.95, sum by (le, tool_name) (rate(talent_tool_duration_milliseconds_bucket[5m])))`
  and `sum by (tool_name) (rate(talent_tool_errors_total[5m]))` — with real numeric values once more
  than one sample existed for `rate()` to work with.
- A standalone OTLP trace emitter's `tool.search_jobs` span, sent the same way, was queryable from
  Jaeger's own API (`GET /api/services`, `GET /api/traces`) with its `tool.name` tag intact.
- A standalone OTLP log emitter's record was queryable from Loki's own API
  (`GET /loki/api/v1/query_range`), resource attributes surfaced as stream labels
  (`service_name`, `severity_text`, …).
- Grafana's own API (`GET /api/search`, `GET /api/dashboards/uid/talent-mcp-overview`) confirmed all
  three datasources and the dashboard — all four panels, including the caveat text panel — provisioned
  correctly into a "Talent.Mcp" folder.

`deploy/grafana/dashboards/talent-mcp-overview.json`'s caveat panel and `deploy/otel/README.md`'s
"metric names are unconfirmed" note are now stale in the sense that the confirmation happened — left in
place anyway, because they describe how to re-derive the names if a future Collector upgrade changes
the sanitization rules again, not a one-time disclaimer.

## Reproduction

1. `docker compose -f deploy/compose.yaml up -d otel-collector jaeger prometheus loki grafana`.
2. Run an OTLP emitter (trace/metric/log) against `http://localhost:4317`, or call a real tool through
   either host with `Talent:Otel:Endpoint` set to the same address.
3. Traces: `curl http://localhost:16686/api/services` and `.../api/traces?service=<name>`. Metrics:
   `curl http://localhost:8889/metrics | grep talent_`, or query Prometheus directly at
   `http://localhost:9090/api/v1/query?query=<expr>`. Logs:
   `curl -G http://localhost:3100/loki/api/v1/query_range --data-urlencode 'query={service_name="<name>"}'`.
4. Grafana: `curl -u admin:admin http://localhost:3000/api/search` to confirm the dashboard is
   provisioned, and `.../api/dashboards/uid/talent-mcp-overview` to read its panels back.
