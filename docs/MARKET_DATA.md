# Market-data configuration

Open **Market data** in the local dashboard. This release supports saved provider settings,
exact MES/MNQ contract bindings, external quote/bar ingress, and a bounded T4 simulator
configuration check. No account access or market-data subscription is included.

**Continuous T4 streaming is not implemented.** A successful diagnostic checks a connection
at one point in time and then closes it. It does not connect the chart, warm up indicators,
start trading, submit orders, or prove that future data will be available.

## Prepare setup without an account

1. Select **T4 simulator · configuration check**.
2. Enable MES and/or MNQ. Save the configuration even if you do not yet have the provider IDs.
3. Review **Setup readiness** for the missing server key and contract IDs.
4. After authorized simulator/data access is available, obtain the exact IDs from T4.
   Save the IDs, configure the server key, restart, and run **Test T4 configuration**.

All provider/contract changes require a flat, paused paper session without pending entries.
Saving clears received quotes, contract bindings and indicator history. Risk baselines,
fills and closed trades are preserved. There is no automatic rollover. Flatten while the
old contract's feed is still available before selecting a new expiry.

| Dashboard field | T4 v2 field | Meaning |
|---|---|---|
| Instrument | Local symbol | MES or MNQ |
| T4 exchange ID | `exchange_id` | Exact provider exchange identifier |
| T4 product / contract ID | `contract_id` | Product identifier, not the expiring instrument |
| Expiring market ID | `market_id` | Exact expiring instrument, never a continuous alias |

IDs are opaque provider strings: do not derive or split them. The diagnostic compares
the returned exchange and product IDs, requires an enabled outright future with a future
last-trading timestamp, and checks the local MES/MNQ tick and point-value specifications.
Unknown, delayed, or absent exchange entitlements fail the readiness check.

## Server credentials

Provider keys never belong in browser JavaScript, this form, Git, logs, or chat messages.
Use the server process environment, or create the already Git-ignored file
`config/appsettings.Local.json` in the repository:

```json
{
  "MarketData": {
    "T4": { "ApiKey": "" },
    "AdapterKey": ""
  }
}
```

Fill the relevant value locally. T4 uses an API key authorized for the **simulator**.
The optional external adapter key must have 32–256 characters and differ from the dashboard
access key. Neither key is needed to save an incomplete setup. Do not commit the filled file.
Restrict file access to your OS user; this file is plaintext, not an encrypted secret store.

Equivalent environment names:

- `MarketData__T4__ApiKey`: T4 API-key authentication. Username/password and SSO are not implemented.
- `MarketData__AdapterKey`: authentication for an external data adapter's local HTTP requests.

Environment and command-line configuration take precedence over the optional local file.
Restart after changing credentials. The API exposes presence flags only. Nonsecret provider
settings are saved atomically to `data/dashboard/market-data.json` (or the configured data
directory). Test results stay in server memory and reset on settings changes or restart.
Corrupt settings fail startup rather than silently enabling another provider.

## What the T4 check does

The endpoint is fixed to `wss://wss-sim.t4login.com/v2`; the UI cannot select a live or custom
endpoint. Each test opens a fresh WebSocket, signs in using the server API key, requests
REAL price format, checks exchange entitlements, and subscribes to top-of-book data for
the selected markets. The client wire contract contains login and market subscription
messages only, with no account or order-routing messages.

The check needs a valid contract definition and a fresh, non-delayed, two-sided quote in
an open market. It rejects stale/future quotes, crossed books, invalid prices and recovery
flags. The diagnostic deliberately requires both sides in a single depth frame; it does
not reconstruct a book from partial updates. Closed markets or partial updates can therefore
leave the result incomplete even when credentials are valid. No prices reach the paper engine.

Connections are disposed after success/failure or a 15-second deadline, before T4's documented
20-second heartbeat interval. Message size/count are bounded. Raw provider errors, tokens,
account details and API keys are not returned or logged. Results distinguish authentication,
per-contract definition checks and observed quotes. `verified` always means **during that
diagnostic**, not a persistent data connection.

Local automated tests use scripted protocol frames. Actual T4 access, contract IDs, data
permissions and provider behavior still require validation with an authorized simulator account.

## External adapter

Select **External adapter**, enable the instruments, and set each exact expiring market ID.
Configure the separate server adapter key. An enabled key alone no longer enables ingress:
the provider and complete enabled contract bindings must also be saved in the dashboard.
Existing integrations must complete this configuration once after upgrading.

Send `X-Adapter-Key` to `POST /api/feed/quotes` and `POST /api/feed/bars` using the schemas in
[DASHBOARD.md](DASHBOARD.md#external-provider-adapter-contract). The feed's `contractId` is
the expiring market ID; for a T4-based external adapter, map T4 **`market_id`**, not
T4 `contract_id`, to this field. Disabled symbols and wrong expiries are rejected.
Choosing T4 simulator or Not configured disables external ingress, preventing mixed sources.

The adapter is responsible for provider login/entitlements, original event timestamps,
deduplication, reconnect, normalized decimal prices, and complete five-minute bars. Supply
at least 50 contiguous completed warmup bars while paused, then verify fresh quotes before
resuming. Never generate test prices for an actual session or retimestamp stale data.

## Local API

All configuration operations use `X-Dashboard-Key`, distinct from `X-Adapter-Key`.

| Endpoint | Purpose |
|---|---|
| `GET /api/market-data` | Saved settings, missing requirements, key-presence flags and latest diagnostic |
| `PUT /api/market-data` | Save settings while paused/flat, clear previous market history |
| `POST /api/market-data/test` | Run the bounded T4 simulator check using saved settings |

Example incomplete settings, with no fabricated contract IDs:

```json
{
  "provider": "t4-simulator",
  "instruments": [
    { "symbol": "MES", "enabled": true, "exchangeId": "", "productId": "", "marketId": "" },
    { "symbol": "MNQ", "enabled": true, "exchangeId": "", "productId": "", "marketId": "" }
  ]
}
```

Unknown fields (including browser-supplied API keys and endpoints), duplicate bindings,
unknown symbols, and malformed IDs are rejected. Missing credentials/settings block a test
before any provider connection. Test requests fail with 409 if another data operation is active.

## Remaining work for continuous T4 data

Implement sustained receive/heartbeat processing; bounded queues and backpressure; reconnect
with fresh authentication/resubscription; incremental book normalization and quote invalidation;
trade deduplication and completed-bar aggregation; historical warmup/backfill; instrument discovery;
calendar/expiry handling; and sustained load/outage tests. The current snapshot store is designed
for low-rate paper research, not an exchange tick stream. Provider certification and actual
simulator verification remain required before progressing toward real execution.

## Protocol references

The minimal `.proto` in `src/AutopilotQuant.T4/Protocol` is independently defined for wire
interoperability from the official T4 v2 field numbers/types. Generated C# is build output.

- [T4 v2 connection and authentication](https://docs.t4login.com/doku.php?id=developers:apiv2:connecting)
- [T4 v2 market-data messages](https://docs.t4login.com/doku.php?id=developers:apiv2:markets)
- [Official CTS protobuf schemas](https://github.com/CTS-Futures/t4-api-tools/tree/main/proto/t4/v2)
- [T4 instrument discovery](https://docs.t4login.com/doku.php?id=developers:markets)
- [T4 simulator certification](https://docs.t4login.com/doku.php?id=developers:apiv2:certification)
