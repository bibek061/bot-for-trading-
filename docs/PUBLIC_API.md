# Public.com reference quotes

The **Market data → Public.com market context** panel retrieves SPY/QQQ ETF quote snapshots
through the official Public.com API. **Refresh Public.com quotes** authenticates and loads
the latest available last, bid and ask with each price's source timestamp. The optional
15-second refresh runs only while this dashboard page is visible and unlocked. It stops
when the tab is hidden, the page changes, the workspace locks, or the provider reports an
error. A server-wide 15-second minimum request interval also applies across tabs.

Public.com's [documented quote types](https://public.com/api/docs/resources/market-data/get-quotes)
do not include CME futures. These ETF prices do not stand in for MES/MNQ, enter the paper
engine, satisfy its quote-readiness checks or change its account marks. The futures feed
still requires [T4 or a separate authorized adapter](MARKET_DATA.md). Historical Public.com
bars and order routing are not implemented in this integration.

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

There is no order endpoint, balance/position query or dependency on `PaperSession` in this
client. Tokens and snapshots stay in server memory. Requests have a 20-second overall
timeout, responses are bounded to 128 KiB, redirects are disabled and provider errors are
sanitized. HTTP 429 honors `Retry-After` with a minimum 15-second pause. Authentication
failure clears the cached token/account for the next explicit retry.

Last, bid and ask freshness are calculated separately from provider timestamps, never the
HTTP receipt time. Values older than 30 seconds, more than two seconds in the future or
without timestamps are marked stale. Freshness updates as time passes. Closed sessions
can return old snapshots even after a successful HTTP response. Missing values remain
empty; a failed refresh clears displayed prices. Unexpected instruments, duplicates,
crossed books, malformed timestamps and invalid prices fail validation.

## Local endpoints

- `GET /api/market-data/public`: cached status and reference snapshots; no provider request.
- `POST /api/market-data/public/refresh`: bounded, throttled provider request.
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
Local read-only verification on 2026-10-04 successfully authenticated and retrieved SPY/QQQ
snapshots; their source timestamps were older than the freshness window. Visual browser
verification remains a separate manual check.
