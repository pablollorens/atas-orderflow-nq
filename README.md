**English** · [Español](README.es.md)

# atas-orderflow-nq

Algorithmic **order flow** strategy for **ATAS v8** (C#, `ChartStrategy`), built
around the **A+** trigger: *Absorption → Stacked Imbalance in an extreme zone →
Price confirmation*. Designed for intraday index futures (MNQ / MES / NQ).

---

## Table of contents
- [What it is](#what-it-is)
- [The strategy: A+ setup](#the-strategy-a-setup)
- [Why the original version produced no orders](#why-the-original-version-produced-no-orders)
- [Repository layout](#repository-layout)
- [Requirements](#requirements)
- [Build (Windows + Zed)](#build-windows--zed)
- [Deploy to ATAS](#deploy-to-atas)
- [Test in Market Replay](#test-in-market-replay)
- [Parameters](#parameters)
- [Roadmap / TODO](#roadmap--todo)
- [Credits](#credits)

---

## What it is

ATAS lets you write C# strategies by inheriting from `ChartStrategy`. The method
`OnCalculate(int bar, decimal value)` runs on every historical bar and then on
every tick of the current bar; inside it you make decisions and send orders with
`OpenOrder`.

This repo contains a strategy that automates the A+ order flow pattern and keeps
the whole logic parameterized so it can be optimized later.

## The strategy: A+ setup

The trigger, over three candles:

```
Candle N-2:  ABSORPTION appears (a large participant absorbs the opposite side)
Candle N-1:  STACKED IMBALANCE in the candle's extreme zone
               · LONG  -> buy imbalance in the bottom 30%
               · SHORT -> sell imbalance in the top 30%
             (the candle makes no new extreme against N-2)
Candle N:    Price CONFIRMATION
               · LONG  -> closes above the high of N-1
               · SHORT -> closes below the low of N-1
             -> ENTRY at the close of N
```

**Management:** stop at `StopPoints`, take profit at `StopPoints × TargetRR`,
break-even at `BeTriggerR` (moves the stop to entry + `BeOffsetPoints`), forced
close at `ForceCloseR`. Daily circuit breakers by number of trades and by USD loss.

The 30% zone rule is computed like this:

```
range       = High - Low
relativePos = (imbalancePrice - Low) / range
relativePos <= 0.30  -> lower zone (bullish signal)
relativePos >= 0.70  -> upper zone (bearish signal)
```

## Why the original version produced no orders

The initial version instantiated the `StackedImbalance` / `Absorption` indicators,
called `Add()` and read their values via `DataSeries[0..3]` cast to `ValueDataSeries`.

The problem: those order flow indicators are **render-only** — their logic lives in
`OnRender` and they don't expose the signal as a numeric series. The read returned 0,
every signal stayed `false`, and no setup was ever evaluated -> **zero orders**. On
top of that, a `catch { return 0; }` swallowed any exception, so there wasn't even a
visible error. It was also missing an `OnOrderRegisterFailed` implementation, so any
order rejected by the exchange went unnoticed.

**Fix (v2):** signals are computed **directly from the candle's footprint**
(`GetAllPriceLevels`, `MaxVolumePriceInfo`, bid/ask volume per price level) instead
of reading other indicators. In addition:

- The 30% rule is implemented.
- Daily PnL is updated on every close (it was never summed before).
- `OnOrderRegisterFailed` is implemented (failures are now logged).
- `DebugMode` dumps daily counters to see where the chain breaks.

> ⚠️ **Pending verification.** The footprint calls marked with `// VERIFY`
> (`GetAllPriceLevels`, `GetPriceVolumeInfo`, `MaxVolumePriceInfo`, and the
> `.Bid / .Ask / .Volume / .Price` properties of `PriceVolumeInfo`) may be renamed
> across API versions. With `DebugMode = true`, the first Replay run confirms whether
> the values arrive (if the counters move, the names are correct).

## Repository layout

```
atas-orderflow-nq/
├── OrderFlowNQ.csproj          .NET 8 project, references the ATAS DLLs
├── README.md                   Default README (English)
├── README.es.md                Spanish version
├── .gitignore                  Excludes bin/obj and the platform DLLs
├── src/
│   └── OrderFlowNQ_v2_APlus.cs  Active version — the only one that compiles
└── reference/                  Original code (NOT compiled, read-only)
    ├── OrderFlowNQ_v1_Jun9.cs   Simplified version
    └── OrderFlowNQ_Jun8_full.cs Full version (4 setups) — quarry of ideas
```

Both versions under `reference/` declare the same `OrderFlowNQ` class, which is why
they're excluded from the build (`<Compile Remove="reference/**/*.cs" />`). They stay
as history and as the source of the extra setups (retest, false breakout, continuation).

## Requirements

- **Windows** (ATAS is Windows-only; don't use WSL for this).
- **ATAS v8** installed and signed in.
- **.NET 8 SDK** — https://dotnet.microsoft.com/download
- Editor: **Zed** (or Visual Studio / Rider if you want step-by-step debugging).

## Build (Windows + Zed)

Zed edits and gives C# autocompletion via LSP, but it doesn't build .NET by itself:
the build is done from the terminal.

1. Open the repo folder in Zed.
2. **Set `<AtasPath>`** in `OrderFlowNQ.csproj` to the folder containing
   `ATAS.Strategies.dll` (look for that file in your installation; it's usually under
   `%LOCALAPPDATA%\ATAS Platform\current`). This is the only mandatory change.
   If your ATAS is old (.NET Framework), change `net8.0-windows` to `net472`.
3. In Zed's integrated terminal:

   ```powershell
   dotnet build -c Release
   ```

   This produces `bin/Release/OrderFlowNQ.dll`.

## Deploy to ATAS

1. Copy `bin/Release/OrderFlowNQ.dll` to
   `C:\Users\<YOUR_USER>\Documents\ATAS\Strategies`
   (or uncomment the `DeployToAtas` target in the `.csproj` so it's copied
   automatically on build).
2. In ATAS, click the **refresh** button on the strategy list.
3. The strategy shows up as **`OrderFlowNQ_v2_APlus`**.

## Test in Market Replay

ATAS **has no historical tick backtester**: realistic simulation is done in
**Market Replay** (tick + DOM data from the cloud). For an order flow strategy it's
the only valid path, because the signals need real footprint.

1. Sign in to ATAS with your username/password.
2. Enable **Market Replay** -> *Ticks + Generated DOM* mode (up to 1 week) or
   *Ticks + DOM* (1 day, maximum precision). Set the dates and press Play.
3. Open a **Replay Account** — it's the `Portfolio` where simulated orders are
   executed. Without it, `OpenOrder` goes nowhere.
4. Open a **footprint / cluster** chart of the instrument (e.g. MNQ).
5. Add the strategy from *Chart Strategies*, with `DebugMode = true`.
6. Press Play and open the **Logs** window. Read the daily dump `[OFNQ][DAY ...]`:
   - `signals > 0` and trades on the chart -> the chain works.
   - `absBull/absBear` move but `siBull/siBear = 0` -> tune `ImbalanceRatio` / `MinStackedLevels`.
   - everything at 0 -> review the `// VERIFY` footprint names.
7. Check the **Trading Journal** (Account = Replay): profit factor, drawdown, etc.
   There's only one Replay account and each session resets the previous one -> export
   whatever you want to keep.

> Replay runs in real time (no session/candle skipping), so iterating over weeks is
> slow. Start with specific high-activity days and crank up the playback speed.

## Parameters

**Signal** (the ones you'll tweak most when optimizing)

| Parameter | Default | What it does |
|---|---|---|
| `ZonePct` | 0.30 | Candle's extreme zone to validate the SI |
| `ImbalanceRatio` | 3.0 | Diagonal ask/bid ratio to count an imbalance |
| `MinStackedLevels` | 3 | Number of consecutive imbalanced levels |
| `AbsorptionVolMin` | 200 | Minimum volume at the key level for absorption |
| `AbsorptionLookback` | 3 | Candles an absorption stays valid |
| `RequireAbsorption` | true | Require prior absorption for the A+ |
| `EnableSiDouble` | false | Enable the secondary setup (double SI) |

**Risk**

| Parameter | Default | What it does |
|---|---|---|
| `Quantity` | 1 | Contracts per entry |
| `StopPoints` | 3.0 | Stop in points |
| `TargetRR` | 2.0 | Risk/reward ratio of the TP |
| `BeTriggerR` | 1.0 | R at which break-even activates |
| `BeOffsetPoints` | 0.1 | Stop offset when moving to BE |
| `ForceCloseR` | 3.0 | Forced close at this R |
| `DollarsPerPoint` | 2.0 | MNQ=2, MES=5, NQ=20 |
| `MaxTrades` | 20 | Max trades/day |
| `MaxDailyLoss` | 5000 | Max daily loss (USD) |

**Session**: `UseSessionFilter`, `SessionStartHour`, `SessionEndHour`, `DebugMode`.

## Roadmap / TODO

- [ ] Close the footprint `// VERIFY` calls against the exact ATAS version.
- [ ] Port setups 2-4 (retest, false breakout, continuation) from the full version.
- [ ] Confirm the time zone of `candle.Time` for the session filter.
- [ ] Validate position management against real fills (vs the synthetic close-based stop).

## Credits

Original spec and base code by Juan (`juansanca1992`). Signal-layer rewrite,
parameterization, and diagnostic tooling in v2.
