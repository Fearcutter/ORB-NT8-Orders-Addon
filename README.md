# ORB NT8 Orders AddOn — Paired Stops

NinjaTrader 8 AddOn that places a linked pair of stop orders (one buy above market, one sell below) with one click, keeps them synchronized when you drag either one on the chart, and auto-cancels the partner when one fills.

Trader-assistive tool — every action is initiated by the trader. Not an autonomous strategy.

## Install

1. Copy `PairedStopsAddOn.cs` into `Documents\NinjaTrader 8\bin\Custom\AddOns\`.
2. Open the NinjaScript Editor in NT8.
3. Press **F5** to compile. Confirm zero errors.
4. Restart NT8 if the new menu item does not appear.

## Open

From the Control Center: **New → Paired Stops**. A new window opens hosting the AddOn's tab.

## Settings

| Setting | Default | Notes |
|---|---|---|
| Account | first available | Dropdown populated from NT accounts. |
| Instrument | `NQ 06-26` | Full NT instrument name (edit as the front-month rolls). |
| Offset (points) | 10.0 | Applied as ± from last traded price. Tick-rounded. |
| Quantity | 1 | Contracts per leg. |
| Ghost preview | Off | When on, shows dashed chart lines + inline Confirm/Cancel before submitting. |
| Preview buy line color | Green | Applied to ghost preview only. Live orders use NT's native Chart Trader lines. |
| Preview sell line color | Red | Applied to ghost preview only. |
| Preview line width | 2 | |
| Preview dash style | Dashed | |
| Pair tag prefix | `PAIRSTOP_` | Used to identify paired orders internally. |
| Audible drag-sync | Off | Plays a short sound when the tool auto-syncs the partner. |

Settings persist to `Documents\NinjaTrader 8\bin\Custom\AddOns\PairedStops\settings.json`.

## What it does

- **Place.** Reads last traded price (falls back to bid/ask mid if no last), computes `buy = last + offset` and `sell = last - offset`, tick-rounds both, submits atomically. If one leg fails, the other is cancelled automatically.
- **Drag-sync.** When you drag either leg on the chart, the partner moves to preserve the original spread.
- **OCO on fill.** When one leg fills, the partner is cancelled. Your ATM strategy handles the filled position from there — the AddOn does not submit TP/SL.
- **Manual cancel.** If you cancel one leg from Chart Trader, the partner is cancelled too.
- **Session reset.** At 18:00 ET the AddOn clears its internal tracking state. Live broker-side orders survive the rollover; the AddOn simply stops treating yesterday's pair as its own.

## What it does NOT do

- Does not place take-profit or stop-loss orders. Your ATM handles that.
- Does not decide when to place orders. You click the button.
- Does not close positions.
- Does not run without your supervision.

## Verification in Sim101

Before deploying to a funded account, run through these in a Sim101 account:

1. Place pair on NQ → drag buy stop up 5 points → sell stop follows up 5 points (spread preserved).
2. Place pair on NQ → drag sell stop down 3 points → buy stop follows down 3 points.
3. Let price hit the buy stop (use Market Replay if needed) → sell stop cancels automatically within ~1 second.
4. Place pair → manually cancel one order via Chart Trader → partner cancels.
5. Tick-size rounding: offset 10.1 → order prices snap to 0.25 increments.
6. Ghost preview on → Place → lines appear → Confirm → orders submit. Place → Cancel → nothing submitted.
7. Run across session rollover (18:00 ET) → tracking state clears.

## Known limitations

- Ghost preview chart lines are static — they don't track chart zoom/scroll. They exist only for the few seconds between Place and Confirm/Cancel.
- If NT restarts while a pair is active, the AddOn does not attempt to re-adopt the orders. They remain on the broker, tagged `PAIRSTOP_<guid>_BUY|SELL` — cancel them manually via Chart Trader.
- Live order lines use NT's native Chart Trader rendering. The line-color/style settings apply to the ghost preview only.
- Account disconnects clear tracking state on reconnect. Live orders survive the disconnect broker-side.
- Pair tracking is per-tab. If you open multiple Paired Stops tabs on the same account you'll get independent (and potentially conflicting) managers — use one tab at a time.

## Prop-firm compliance

Designed as a trader-assistive helper. You initiate every action (button click, order drag); the AddOn only syncs two already-user-placed orders. It does not enter, exit, or manage positions autonomously.

Verify compatibility with your prop firm's rules before using — do not use with prop firms that prohibit any form of automation.

## Files

- `PairedStopsAddOn.cs` — the AddOn (single-file, drop into `bin\Custom\AddOns\`).
- `docs/superpowers/specs/2026-04-20-paired-stops-addon-design.md` — design spec.
- `docs/superpowers/plans/2026-04-20-paired-stops-addon.md` — task-by-task implementation plan.
