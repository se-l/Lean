# OTLP log shipping (host-local OpenTelemetry Collector)

`QuantConnect.Logging.OpenTelemetryLogHandler` ships every Lean log line to the
OpenTelemetry Collector running on the same host, over OTLP/HTTP with JSON encoding
(`POST <endpoint>/v1/logs`). The collector owns the fragile hop to the backend
(SigNoz) — retries, spooling, timeouts — so a wedged or unreachable backend can only
cost delayed or dropped log records; it can never stall a Lean run.

This mirrors the Fino.jl logging topology (`Fino.jl/src/shared/Log.jl`), with the
same two resource attributes so both stacks are filterable the same way in SigNoz.

## Enabling

The handler is opt-in. Nothing changes unless an endpoint is configured — with no
`OTEL_*` variables set, Lean logs to the console and `log.txt`
exactly as before.

```bash
export OTEL_ENDPOINT=http://127.0.0.1:4318     # the /v1/logs path is appended
export OTEL_SERVICE_NAME=lean-backtest          # default "lean"
```

Run a backtest as usual (`Launcher/bin/Release/QuantConnect.Lean.Launcher`, or the
NUnit runner); the handler is picked up by `CompositeLogHandler`'s default
constructor and by the composite that `Tests/AlgorithmRunner.cs` builds per run.

## Configuration

| Variable | Meaning |
| --- | --- |
| `OTEL_ENDPOINT` | Collector URL. Used first, then `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT`, then `OTEL_EXPORTER_OTLP_ENDPOINT`. |
| `OTEL_EXPORTER_OTLP_LOGS_ENDPOINT` / `OTEL_EXPORTER_OTLP_ENDPOINT` | Standard OTel names, honoured as fallbacks. |
| `OTEL_ENABLED` | `true/1/yes` enables with the default endpoint (`http://127.0.0.1:4318/v1/logs`); `false/0/no` disables even when an endpoint is set. |
| `OTEL_SERVICE_NAME` | `service.name` resource attribute (default `lean`). |
| `OTEL_HOSTNAME` | `host.name` override (default: the machine name, recovered from the network identity inside a container). |
| `CONTAINER_NAME` | `container.name` resource attribute (Lean's compose files already set it per service). |
| `OTEL_RESOURCE_ATTRIBUTES` | Standard `k=v,k=v` pairs, added to the resource. |

Endpoint normalisation: a bare `host:port` gets `http://`; a URL without a port gets
`:4318`; a URL without a path gets `/v1/logs`.

### In the compose stacks

`docker/docker-compose.yml` wires the Lean services to the host-local collector on the
**docker bridge gateway**, because `127.0.0.1` inside a container is the container:

```yaml
- OTEL_ENDPOINT=http://${HOST_BRIDGE:-172.17.0.1}:4318
- OTEL_ENABLED=${FINO_OTEL_ENABLED:-0}
- OTEL_SERVICE_NAME=trader.1
- OTEL_HOSTNAME=${OTEL_HOSTNAME:-}
```

`FINO_OTEL_ENDPOINT` is deliberately **not** reused: Fino's Julia sink speaks gRPC on
4317 while this handler posts OTLP/HTTP JSON to 4318, so sharing the variable would point
the C# side at a port that cannot accept it.

### Ports

The collector listens on **4317 (gRPC)** and **4318 (HTTP)**. This handler posts
HTTP/JSON, so point it at **4318**. Port 4317 will be rejected and the handler prints
a warning at startup.

## What lands in the backend

Resource attributes on every record — identical names to Fino.jl's `src/shared/Log.jl`, so
both stacks answer the same SigNoz filters:

- `service.name` — from `OTEL_SERVICE_NAME`, default `lean`
- `host.name` — machine name, or `OTEL_HOSTNAME`; inside a container, the machine name
  recovered from the network identity (falls back to the container id when that fails,
  and the collector's `transform/hostname` rewrites it to the real machine name)
- `host.ip` — primary non-loopback IPv4 address
- `container.id` — full container id, `""` when not in a container
- `container.name` — `CONTAINER_NAME`, `""` when unset
- `process.pid`
- everything in `OTEL_RESOURCE_ATTRIBUTES`

`container.id` / `container.name` are always present and empty on the host, so one
predicate covers "in a container": `container.id != ''`.

Records carry Lean's own severity vocabulary, with OTel-standard numeric severities:

| Lean call | `severityText` | `severityNumber` |
| --- | --- | --- |
| `Log.Trace` | `TRACE` | 9 (INFO) |
| `Log.Debug` | `DEBUG` | 5 (DEBUG) |
| `Log.Error` | `ERROR` | 17 (ERROR) |

The message body is the raw Lean log text, so filters like
`body CONTAINS 'Order'` work unchanged.

## Delivery semantics

- Producers only enqueue; a background thread exports. The queue is bounded
  (10 000 records) and oldest-first drops are counted and reported on stderr —
  telemetry never backpressures the algorithm.
- Export cadence: every 2 s, or on dispose. A failed export enters exponential
  backoff (5 s → 120 s cap) and the batch is dropped; one stderr line marks the
  transition in and out of "backend unreachable".
- HTTP timeout is 5 s per export; dispose flushes with a 3 s budget. The handler also
  flushes from `AppDomain.ProcessExit`, because the Launcher never disposes the
  global `Log.LogHandler`.

## Notes

- `QuantConnect.Logging` cannot reference `QuantConnect.Configuration` (Common
  references Logging, so the reverse reference would be circular). Configuration is
  therefore environment-only — put the variables in the shell, the systemd unit, or
  the container environment; `Launcher/config.json` cannot carry them.
- In a container `Environment.MachineName` is the container id. Set `OTEL_HOSTNAME`
  in the compose file to the physical host so `host.name` stays filterable.
- `MaintainLogHandlerAttribute` in the test suite may already build a composite from
  `-log-handler`; if that handler is itself a `CompositeLogHandler` **and** the OTel
  variables are set, records are shipped twice (once from each composite). Plain
  `dotnet test` is unaffected: it starts from `ConsoleLogHandler`.
