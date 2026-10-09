# Public.com ETF market view

When Public.com is configured and no futures feed is configured, unlocking the dashboard
opens **Overview → SPY · ETF** and loads actual API quotes and historical candles. Switch
to **QQQ · ETF** for Nasdaq ETF research, or **MES / MNQ** for the separate futures chart.
If a futures feed is configured, the overview continues to default to its first enabled
instrument. Changing the displayed instrument never changes the paper strategy's instruments.

The ETF chart uses the provider's past-week, five-minute, regular-session OHLCV data.
It supports zoom, crosshair, candle/line display, display aggregation and EMA overlays.
Provider bar timestamps are plotted directly; they do not use the end-of-bar convention
of the futures strategy. The latest candle may be forming. Quotes show last-trade, bid
and ask freshness separately; Friday snapshots remain marked stale on the weekend.

The initial ETF overview enables 15-second quote refresh while visible and unlocked.
Turn it off with the checkbox. Historical data refreshes at most once per minute per
symbol. Hiding the tab, locking or navigating away from both the ETF overview and Market
data stops automatic refresh; returning does not silently re-enable it.

The **Market data → Public.com market context** panel retrieves SPY/QQQ ETF quote snapshots
through the official Public.com API. **Refresh Public.com quotes** authenticates and loads
the latest available last, bid and ask with each price's source timestamp. The optional
15-second refresh runs while Market data or the ETF overview is visible and unlocked. It stops
when the tab is hidden, another page is selected, the workspace locks, or the provider reports an
error. A server-wide 15-second minimum request interval also applies across tabs.

Public.com's [documented quote types](https://public.com/api/docs/resources/market-data/get-quotes)
do not include CME futures. These ETF prices do not stand in for MES/MNQ, enter the paper
engine, satisfy its quote-readiness checks or change its account marks. The futures feed
still requires [T4 or a separate authorized adapter](MARKET_DATA.md). Public.com order routing
is not implemented in this integration.

## Local configuration

Put your personal secret in the Git-ignored `config/appsettings.Local.json`:

```json
{
  "MarketData": {
    "Public": {
      "Secret": "",
      "AccountId": ""
    }
  }
}
```

Preserve existing settings when editing that file. Alternatively, use
`MarketData__Public__Secret` and optional `MarketData__Public__AccountId` environment
variables. Restart the dashboard after changes. Never commit the secret or paste it into
client-side code. The API exposes presence only; account labels show just the final four
characters of the account identifier.

The client selects the sole `BROKERAGE` account returned by Public.com. If there is just
one account overall, it selects that account. Multiple brokerage accounts require an
explicit server-side `AccountId`; it must belong to the returned account list. Other
accounts, balances, holdings, transactions and orders are not queried.

## Authentication and request flow

The [personal secret is exchanged for an access token](https://public.com/api/docs/resources/authorization/create-personal-access-token)
before calling authenticated endpoints. The quote documentation's bearer placeholder is
filled with that access token, not the long-lived personal secret.

Only these operations are implemented, all against `https://api.public.com`:

1. `POST /userapiauthservice/personal/access-tokens`: request a 15-minute token, cached in
   server memory and refreshed after 14 minutes.
2. `GET /userapigateway/trading/account`: identify the quote-access account. See the
   [official quickstart](https://public.com/api/docs/quickstart).
3. `POST /userapigateway/marketdata/{accountId}/quotes`: request `SPY` and `QQQ` with type
   `EQUITY`, using `Authorization: Bearer <access token>`.
4. `GET /userapigateway/historicdata/EQUITY/{symbol}/WEEK/FIVE_MINUTES?tradingSessionToggle=REGULAR_HOURS`:
   fetch [historical candles](https://public.com/api/docs/resources/market-data/get-bars-v2-with-aggregation)
   for only `SPY` or `QQQ`. History shares the in-memory authentication token; it does not
   need an account or portfolio request.

There is no order endpoint, balance/position query or dependency on `PaperSession` in this
client. Tokens and snapshots stay in server memory. Requests have a 20-second overall
timeout, quote/auth responses are bounded to 128 KiB and history to 2 MiB, redirects are disabled and provider errors are
sanitized. HTTP 429 honors `Retry-After` with a minimum 15-second pause. Authentication
failure clears the cached token/account for the next explicit retry. Rate-limit cooldowns
are shared by quote and historical requests.

History requires matching symbol/period, explicit timestamp offsets, unique timestamps,
valid positive OHLC prices and bounded nonnegative volume, preserving fractional shares.
Responses over 2,000 bars are rejected;
the chart receives the latest 512 bars in timestamp order. Synthetic leading-fill metadata
and other trading-session series are ignored. A failed history refresh clears that chart's
cached bars while quote access remains independent. History never enters `PaperSession`.

Last, bid and ask freshness are calculated separately from provider timestamps, never the
HTTP receipt time. Values older than 30 seconds, more than two seconds in the future or
without timestamps are marked stale. Freshness updates as time passes. Closed sessions
can return old snapshots even after a successful HTTP response. Missing values remain
empty; a failed refresh clears displayed prices. Unexpected instruments, duplicates,
crossed books, malformed timestamps and invalid prices fail validation.

## Local endpoints

- `GET /api/market-data/public`: cached status and reference snapshots; no provider request.
- `POST /api/market-data/public/refresh`: bounded, throttled provider request.
- `GET /api/market-data/public/history/{symbol}`: cached history, no provider request.
- `POST /api/market-data/public/history/{symbol}/refresh`: bounded, throttled history request.
- `GET /api/state`: includes the same cached data in `publicData`.

All require the local operator key. The external adapter key cannot read or refresh this
data. Quotes cannot be injected or credentials set through these endpoints.

## MCP link

`https://mcp.public.com/mcp` is Public.com's separate hosted endpoint for compatible AI
clients. Its [official setup guide](https://public.com/api/docs/templates/hosted-mcp) covers
those connections. This website uses the REST quote API; no MCP connection is installed
or authenticated by the project, and the server secret is not sent to the MCP host.

## Verification

Automated tests exercise token exchange/cache/renewal, account selection, fixed request
paths, secret redaction, per-price freshness, throttling, provider failures and validation.
HTTP smoke tests explicitly clear the Public secret so CI never calls a real account.
Local read-only verification on 2026-10-08 successfully authenticated and retrieved SPY/QQQ
snapshots; their source timestamps were older than the freshness window. Both dashboard
history endpoints returned 395 five-minute candles, preserving fractional share volume.
The futures paper session stayed paused with no positions or futures quotes. Tests cover history validation,
throttling, fixed endpoints and the dashboard controller's ETF/futures switching and refresh
lifecycle. Visual browser verification remains a separate manual check.
