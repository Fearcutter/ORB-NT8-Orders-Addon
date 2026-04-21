# ORB NT8 Orders AddOn — Paired Stops

NinjaTrader 8 AddOn that places a linked pair of stop orders (one buy above market, one sell below) with one click, keeps them synchronized when you drag either one on the chart, auto-cancels the partner when one fills, and automatically submits the TP/SL exit pair using your ATM template.

Trader-assistive tool — every action is initiated by the trader. Not an autonomous strategy.

## Install

1. Copy `PairedStopsAddOn.cs` into `Documents\NinjaTrader 8\bin\Custom\AddOns\`.
2. Open the NinjaScript Editor in NT8.
3. Press **F5** to compile. Confirm zero errors.
4. Restart NT8 if the new menu item does not appear.

## Open

From the Control Center: **New → Paired Stops**. A floating NT window opens with the form.

## Settings

| Setting | Default | Notes |
|---|---|---|
| Account | first available | Dropdown populated from NT accounts. |
| Instrument | `NQ 06-26` | Full NT instrument name (edit as the front-month rolls). |
| Offset (points) | 10.0 | Applied as ± from last traded price. Tick-rounded. |
| Quantity | 1 | Contracts per leg. |
| ATM template | `IMBA` | Name of an existing NT8 ATM template. Its XML is read on fill to derive TP/SL. |

Settings persist to `Documents\NinjaTrader 8\bin\Custom\AddOns\PairedStops\settings.json`.

## What it does

- **Place.** One click. Reads last traded price (falls back to bid/ask mid if no last), computes `buy = last + offset` and `sell = last - offset`, tick-rounds both, submits atomically as unmanaged stop-market orders tagged `PAIRSTOP_<id>_BUY|SELL`. If one leg fails, the other is cancelled automatically.
- **Drag-sync.** When you drag either leg on the chart, the partner moves to preserve the original spread. Implemented as cancel-and-recreate — you'll see a brief (<1s) flicker on the partner line while it's replaced at the new price.
- **OCO + auto-TP/SL on fill.** When one leg fills, the partner is cancelled. The tool then reads `Documents\NinjaTrader 8\templates\AtmStrategy\<AtmTemplate>.xml`, pulls the `StopLoss` and `Target` bracket values, and submits a TP limit + SL stop as an OCO pair on the filled side. NT's broker-side OCO auto-cancels the losing exit when the other fills.
- **Manual cancel propagation.** If you cancel one leg from Chart Trader, the partner is cancelled too.
- **Session reset.** At 18:00 ET the AddOn clears its internal tracking state. Live broker-side orders survive the rollover; the AddOn simply stops treating yesterday's pair as its own.

## What it does NOT do

- Does not trade autonomously — you click Place.
- Does not scale out, trail, or move the SL to breakeven. Only the fixed TP/SL values from your ATM template are honored; any trailing/BE logic in your template is NOT replicated.
- Does not re-adopt orders across NT restarts.

## Verification in Sim101

Before deploying to a funded account:

1. Place pair on NQ → drag buy stop up 5 points → sell stop follows up 5 points (spread preserved).
2. Place pair on NQ → drag sell stop down 3 points → buy stop follows down 3 points.
3. Let price hit one leg (Market Replay works) → partner cancels within ~1s; TP limit + SL stop appear with prices matching your ATM template's Target/StopLoss distances.
4. Let TP or SL fill → the other exit auto-cancels (broker-side OCO).
5. Place pair → manually cancel one leg via Chart Trader → partner cancels.
6. Tick-size rounding: offset `10.1` → order prices snap to 0.25 increments.
7. Tool across 18:00 ET rollover → tracking state clears silently.

## Known limitations

- **Only fixed TP/SL.** If your ATM template uses trailing stops, breakeven triggers, scale-outs, or chase logic, those behaviors are not replicated — the tool reads only the first `<Bracket>`'s `StopLoss` and `Target`. Don't use this tool with complex ATM templates.
- **Do NOT also engage ATM manually.** The tool submits TP/SL itself on fill. If you also select the same ATM in Chart Trader, you'll get double exits.
- **Drag-sync flickers.** The partner leg is cancel-and-recreated (not modified in place) because NT8's in-place order modification API isn't cleanly callable from AddOn context. During the sub-second swap, only one side of the pair is live broker-side.
- **NT restart while pair is active.** Tracking state is not persisted. The broker-side orders remain, tagged `PAIRSTOP_<id>_BUY|SELL` — cancel them manually via Chart Trader after restart.
- **Account disconnect.** On reconnect, tracking state is cleared. Live orders survive broker-side.
- **Multiple Paired Stops windows on the same account.** Each window has its own independent tracking — use one at a time.
- **NQ 06-26 default.** Update the Instrument field as the front-month rolls.

## Prop-firm compliance

Designed as a trader-assistive helper. You initiate every action (button click, order drag); the AddOn only syncs two already-user-placed orders and submits the template-derived TP/SL the moment NT reports your chosen entry filled.

Verify compatibility with your prop firm's rules before using — do not use with prop firms that prohibit any form of automation.

## Files

- `PairedStopsAddOn.cs` — the AddOn (single-file, drop into `bin\Custom\AddOns\`).
- `docs/superpowers/specs/2026-04-20-paired-stops-addon-design.md` — original design spec (note: UI is a floating NTWindow in the shipped build, not an NTTabPage as the spec anticipated).
- `docs/superpowers/plans/2026-04-20-paired-stops-addon.md` — task-by-task implementation plan.
