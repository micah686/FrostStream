# YouTube Proof-of-Origin Tokens (POT)

YouTube increasingly requires a **Proof-of-Origin Token (POT)** to access many formats. FrostStream
integrates [`bgutil-ytdlp-pot-provider`](https://github.com/Brainicism/bgutil-ytdlp-pot-provider) so
yt-dlp can mint these tokens, wired through NATS so it works across a distributed worker fleet.

## How it works

```
Worker process (yt-dlp + bgutil plugin)
  └─ plugin HTTP call → loopback HTTP→NATS shim (PotShimService, 127.0.0.1)
       └─ NATS request/reply  subject "pot.request"  (queue group "pot-brokers")
            └─ POT Broker (PotBrokerConsumerService, runs on a host near a provider)
                 └─ HTTP → its nearby bgutil provider container  http://localhost:4416
                 └─ only joins the queue group while the provider's /ping is healthy
```

- **Why NATS instead of a provider per worker?** Workers run on heterogeneous hosts (local, server,
  AWS). POT tokens are bound to the visitor/account context carried in each request body — *not* the
  broker's IP — so any healthy broker can answer any worker. Brokers self-balance via the
  `pot-brokers` queue group and drop out when their provider is unhealthy, so there's no single point
  of failure. The provider also solves through the download's own proxy (the plugin forwards
  `--proxy`/`--source-address`), so a shared broker preserves proxy alignment.
- **The plugin** is provisioned automatically: `StartupService` downloads and extracts the bgutil
  plugin zip into `<tools>/yt-dlp-plugins/`. A shared `PotOptionsApplier` injects
  `--plugin-dirs <tools>/yt-dlp-plugins` + `--extractor-args youtubepot-bgutilhttp:base_url=<shim>`
  into **both** the download path and the metadata/listing path (per-video metadata fetch, channel
  discovery, channel asset refresh, and playlist enumeration).

## Configuration

### Worker (every worker)
| Key | Default | Meaning |
|-----|---------|---------|
| `PotProvider__Enabled` | `false` | Master kill-switch. When true the shim starts, the plugin is provisioned, and POT args are injected into every download. |
| `PotProvider__RequestTimeout` | `00:00:30` | How long the shim waits for a broker to return a token. |

### Broker host (any host near a provider)
| Key | Default | Meaning |
|-----|---------|---------|
| `PotBroker__Enabled` | `false` | Run the POT broker role on this host. |
| `PotBroker__ProviderUrl` | — | Base URL of the nearby bgutil provider, e.g. `http://localhost:4416`. |
| `PotBroker__HealthCheckInterval` | `00:00:15` | `/ping` poll cadence that gates queue-group membership. |
| `PotBroker__RequestTimeout` | `00:00:30` | Upper bound on a single provider call. |

The broker role is registered on **DataBridge** via `AddPotBroker(...)`; it no-ops unless
`PotBroker__Enabled` is set, so it's inert on hosts without a co-located provider.

## Running the provider

The provider image tag **must** match the plugin version
(`YtDlpBinaryDownloaderOptions.BgUtilPluginVersion`, pinned to **1.3.1**) — bgutil requires the
server and plugin to be the same version.

### Local dev (Aspire)
`StartPotProvider` adds one `pot-provider` container (`brainicism/bgutil-ytdlp-pot-provider`, port
4416, `/ping` health check). The AppHost enables the broker on DataBridge
(`PotBroker__Enabled=true`, `PotBroker__ProviderUrl=<container>`) and the shim on the Worker
(`PotProvider__Enabled=true`). Override the image tag with `BGUTIL_IMAGE_TAG`.

### Remote workers (local server / AWS)
Run a provider next to each broker host and point the broker at it:
```bash
docker run -d --init --name bgutil-provider -p 4416:4416 \
  brainicism/bgutil-ytdlp-pot-provider:1.3.1
```
Then set on that host: `PotBroker__Enabled=true`, `PotBroker__ProviderUrl=http://localhost:4416`.
Add as many broker hosts as you like — they share the `pot-brokers` queue group. Workers need no
extra config beyond `PotProvider__Enabled=true`; they reach a provider purely over NATS.

## Verification
- `curl http://localhost:4416/ping` → `{"server_uptime":…,"version":…}` (provider up).
- After Worker startup, `<tools>/yt-dlp-plugins/yt_dlp_plugins/` exists and the shim logs its
  loopback URL.
- Submit a YouTube download; the Worker logs show the bgutil extractor-args applied.
- Set `PotProvider__Enabled=false` to disable injection entirely (kill-switch).
