# Get T4 simulator access

The official source is [CTS-Futures/t4-api-tools](https://github.com/CTS-Futures/t4-api-tools).
CTS links this repository from its [API v2 download page](https://docs.t4login.com/doku.php?id=developers:apiv2:download).
It provides protocol definitions, utilities and examples. Downloading source does not
create a simulator account, issue an API key or grant market-data permissions.

## Source downloaded for this project

A local reference checkout was fetched on 2026-10-04:

- Directory: `data/t4-api-tools/` (ignored by the project's Git repository).
- Upstream commit: [`505ecaf81267f4fdc230ef680169d3f05ea3f990`](https://github.com/CTS-Futures/t4-api-tools/commit/505ecaf81267f4fdc230ef680169d3f05ea3f990).
- Wire definitions: `proto/t4/v2/`.
- .NET example: `tools/dotNet/T4APIDemoV2/`.
- Additional tools: C++, Java, JavaScript, Python and Rust directories under `tools/`.

The examples have not been executed. The dashboard already implements the read-only V2
simulator data connection in `src/AutopilotQuant.T4`; the reference checkout is not a
runtime dependency and is not bundled into the website or committed to our repository.

## Account and credentials

1. Use [CTS simulator registration](https://cts.sim.t4login.com/register/Default_cts.aspx).
   The official page currently advertises a two-week simulator trial. Registration asks
   for your contact details, trader classification and acceptance of account/data terms.
2. Confirm simulator API access and MES/MNQ market-data permissions with CTS or your T4
   firm. Request an API key usable with API v2 at `wss://wss-sim.t4login.com/v2`. The
   [official authentication guide](https://docs.t4login.com/doku.php?id=developers:apiv2:connecting)
   describes API-key login and a separate username/password/application-license flow.
   This dashboard implements API-key login only; a trial login alone is not a configured key.
3. Save your issued key in the existing ignored `config/appsettings.Local.json` under
   `MarketData → T4 → ApiKey`, preserving `MarketData → Public` and other existing settings.
   Alternatively set `MarketData__T4__ApiKey` in the server environment. Restart the dashboard.
4. In **Market data → Futures data source & contracts**, select T4 simulator and enter the
   exact exchange, product and expiring market IDs for MES/MNQ supplied by T4. Do not use
   TradingView continuous symbols or invent IDs from contract names.
5. Run **Diagnostic test**, then **Connect feed**. Confirm source timestamps and contracts.
   Confirm the historical timezone before enabling backfill. Paper entries remain paused
   until explicitly resumed after fresh quotes and indicator history are available.

See [market-data setup and behavior](MARKET_DATA.md) for details. Simulator access and
authenticated quotes still need to be verified with an issued T4 key. The working
Public.com reference connection has its own credentials and supplies ETF context only.

## Signed into WebTrader but the dashboard is still incomplete

WebTrader's **Connected** indicator confirms that browser session only. It does not
authenticate AutopilotQuant or supply an API key to the local server. WebTrader's
Settings panel contains display preferences; API-key issuance was not exposed in the
panel inspected on 2026-10-04.

Use **Quotes → Add Contract → SIM:CME Equity Futures** to inspect the simulator
catalog. It includes **SIM:E-mini Micro S&P 500** and **SIM:E-mini Micro NASDAQ-100**.
Seeing those product names does not verify API permissions, current quotes or the
exact expiring market IDs required by this dashboard. Confirm those through the API
after CTS supplies access.

If you received only a simulator username and password, contact the API team listed
on the [official support page](https://docs.t4login.com/doku.php?id=developers:apiv2:download#support):
**T4.APISupport@Plus500.com** (verified 2026-10-04). You can send this request after
adding your simulator firm and username, without including your password:

> Subject: API v2 simulator access for AutopilotQuant
>
> I can sign into T4 WebTrader on the simulator. I am connecting a local application
> named AutopilotQuant for MES/MNQ market data and local paper execution. Please
> advise how to obtain an API key for wss://wss-sim.t4login.com/v2, confirm my MES/MNQ
> quote and historical-data permissions, and how to retrieve the exact exchange,
> product and expiring market IDs. Please confirm any trial limits or charges before
> enabling paid services. If API-key access is unavailable, please advise the
> application-name/application-license requirements for username/password login.

This is a request template only; the project does not send it. The current connector
accepts API keys. A CTS-issued application license would require adding the documented
alternate authentication flow; a WebTrader session cookie is not used for API login.
