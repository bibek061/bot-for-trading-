# Autopilot Quant

Hands-off adaptive futures trading system for Plus500US / T4.

## Current scope
- Windows + .NET 10
- MES and MNQ
- Long-only initially
- Short exposure locked until the rolling 30-day gate passes:
  - >= 12 closed trades
  - >= 60% win rate
  - >= 1.15 profit factor
- India NSE/BSE architecture reserved for a later, separately validated broker adapter
- DryRun by default. Live order routing is intentionally disabled.
- MIT-licensed; see [LICENSE](./LICENSE).
- Public project website: https://bibek061.github.io/bot-for-trading-/

## Intended flow
T4 market data -> regime detection -> strategies -> learning/allocation -> risk engine -> simulated/live broker -> performance feedback.

## Current status
The repository includes contract metadata, configurable risk checks, feature gate logic, a 5-minute-bar market regime classifier, a long-only EMA20 pullback/reclaim signal evaluator, and a paper-only coordinator that connects completed-bar signals to simulated orders. The coordinator processes each instrument's completed bars once, ignores out-of-order bars, marks existing positions to market, and enforces a configurable open-position limit. The initial regime baseline uses ATR(14) / close >= 1% for risk-off; otherwise, EMA(20) relative to EMA(50) and the latest EMA(20) slope classify uptrend, downtrend, or chop. The classifier returns insufficient-data until 50 bars are available. The signal evaluator emits a BUY candidate only when an uptrend's preceding completed bar closed at/below EMA20 and the current completed bar closes above EMA20. These thresholds and rules are initial paper-research assumptions and are not validated trading advice.

Each paper entry is tracked as an independent position and must be closed by its position ID. Short entries remain locked by default. The simulator tracks mark-to-market unrealized P/L, closed-trade realized P/L, and entry/exit fees; it does not persist state between runs. Regime classification and signals are not yet connected to live market data. T4 market data and live order routing are not connected or enabled.

## Next milestones
1. Validate regime thresholds and EMA20 pullback/reclaim rules on real historical/replay data
2. Add breakout strategy
3. T4 live quote connection and reconnect handling
4. Contract discovery/rollover
5. Persistent storage for orders, positions, and trades
6. Performance/learning allocator
7. Dry-run forward test
8. Only after validation: explicit live execution enablement
9. VPS deployment, monitoring, backups, alerts

## Run

From PowerShell:

    cd C:\Projects\AutopilotQuant
    dotnet restore
    dotnet test
    dotnet run --project src\AutopilotQuant.Runner

Historical paper replay expects CSV columns `Symbol,Timestamp,Open,High,Low,Close,Volume`, with timestamps as ISO 8601 values including an explicit timezone offset:

    dotnet run --project src\AutopilotQuant.Runner -- replay C:\data\MES_5m.csv --report-json data\replay-report.json

Try the included deterministic synthetic fixture:

    dotnet run --project src\AutopilotQuant.Runner -- replay data\sample_mes_5m_synthetic.csv --report-json data\sample-replay-report.json

The sample is fabricated to exercise one entry/exit path; it is not real market data and must not be used to assess strategy performance. Replay orders are simulated only. Open positions are closed at each replay calendar-day boundary and at the end of the input, using the last available bar close and configured paper slippage/fees. The replay sorts bars chronologically and rejects duplicate symbol/timestamp pairs.

The JSON report contains run-level counts and performance metrics plus each closed trade's entry/exit prices, fees, gross P/L, and net P/L. Win rate uses net trade P/L. Profit factor uses net winning and losing trade P/L and is `null` when there are no losing trades because the ratio is undefined.

## Release and research disclaimer

This is an experimental paper-trading/research prototype, not financial advice, a brokerage service, or a recommendation to trade. Strategies, thresholds, and synthetic replay outputs have not established profitability. The sample CSV is fabricated, not exchange data. Live T4 quotes and order routing are not implemented. Paper replay simplifies execution and does not model queue position, partial fills, market impact, or all fees and outages. Historical results, if later tested, do not guarantee future performance.
