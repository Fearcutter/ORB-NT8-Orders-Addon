# Paired Stops AddOn — Design

**Date:** 2026-04-20
**Target platform:** NinjaTrader 8, .NET Framework, C#, NinjaScript AddOn
**Primary instruments:** NQ, MNQ (generalizes to any futures via configuration)

## Purpose

Trader-assistive NT8 AddOn that lets the trader place a linked pair of stop orders — one buy stop above market, one sell stop below market — with one click, and automatically keeps them synchronized whenever the trader drags either one on the chart. When one fills, the other auto-cancels. Every action is initiated by the trader; the tool only handles the mechanical task of keeping the pair in sync.

This is not a strategy and does not trade autonomously. It does not place TP/SL orders (the trader's ATM does that after fill), does not close positions, and does not act on signals.

## Core Behavior

### Pair lifecycle

1. **Place.** Trader clicks *Place Paired Stops*. Tool reads last traded price, computes `buyPx = round(last + offset)` and `sellPx = round(last - offset)` (tick-rounded), and submits both orders atomically, tagged with a shared pair ID.
2. **Track (linked drag).** When live, dragging either order fires `Account.OrderUpdate`. The tool detects the modification and submits a `ChangeOrder` on the partner to preserve the original spread. A guard flag + price-equality check prevent ping-pong loops.
3. **Fill (OCO).** When one leg fills, the other is cancelled. The trader's ATM strategy takes over the filled position; the tool does not manage it further.
4. **Session reset.** At 18:00 ET (CME futures session start), the tool clears its tracking state so yesterday's pair does not affect today's setup. Live broker-side orders are not cancelled.

### User controls (all in the AddOn tab)

- Account selector (dropdown)
- Instrument selector (defaults to active chart's instrument if available, else user-selected)
- Offset (points)
- Contract quantity
- Ghost-preview toggle
- Preview buy-line color, preview sell-line color, preview line width, preview dash style (ghost preview only — live orders use NT's native Chart Trader lines and are not restyled)
- Pair-tag prefix (default `PAIRSTOP_`)
- Audible-confirmation toggle (beep on drag-sync)
- **Place Paired Stops** button
- **Cancel Pair** button
- Status strip (errors, drag-sync confirmation)
- Inline Confirm / Cancel strip (shown only when ghost preview is on and awaiting confirmation)

### Configuration defaults

| Setting | Default |
|---|---|
| Offset (points) | 10.0 |
| Contract quantity | 1 |
| Ghost-line preview | Off |
| Preview buy line color | Green |
| Preview sell line color | Red |
| Preview line width | 2 |
| Preview dash style | Dashed |
| Pair tag prefix | `PAIRSTOP_` |
| Audible confirmation | Off |

## Architecture

### UI shell

A tabbed NT window — registered via `AddOnBase` so the user opens it through `New → Paired Stops`. The tab docks in the NT main window like any other tool, state persists with workspaces, and the Instrument/Account fields bind naturally to the tab's context.

Rejected alternatives:
- Floating WPF window — doesn't survive workspace reloads, floats over charts.
- Chart Trader extension — most ergonomic but fragile across NT versions and significantly more code.

### Live order chart rendering

**None.** Live orders display via NT's native Chart Trader lines. The tool does not draw its own overlay for live orders — avoids visual duplication and the complexity of hooking `OnRender` on every chart showing the instrument. The `Line color / width / dash` configuration applies only to the ghost preview.

### Ghost preview rendering

**Both chart lines and inline panel confirmation** (when preview toggle is on):

- Chart path: `GhostPreview.Show(buyPx, sellPx)` locates a `ChartControl` showing the instrument (focused chart preferred, else first match), adds two `HorizontalLine` draw objects (dashed, configured colors), and removes them on Confirm/Cancel. No-ops gracefully if no chart is open.
- Panel path: the tab shows an inline confirmation strip (`Place buy stop @ X, sell stop @ Y — [Confirm] [Cancel]`) always — this is the reliable fallback when no chart is open.

### Type layout (single `PairedStopsAddOn.cs`, one namespace)

| Type | Role |
|---|---|
| `PairedStopsAddOn : AddOnBase` | Entry point. Registers the menu item and tab type. |
| `PairedStopsTab : NTTabPage` | Hosts the WPF `UserControl`. Tab-level lifecycle: load settings, instantiate `PairManager`, clean up on close. |
| `PairedStopsView : UserControl` | The UI. Pure view — no order logic. All mutations marshal to the dispatcher. |
| `PairManager` | Owns the active `PairState`, subscribes to `Account.OrderUpdate`, handles place/track/OCO/session reset, enforces the ping-pong guard. Thread-safe via a `lock` + bool flag. |
| `PairState` | Record: `Guid PairId`, `Order Buy`, `Order Sell`, `double ExpectedSpread`, `DateTime CreatedUtc`. |
| `GhostPreview` | Chart discovery + draw-object management. |
| `SettingsStore` | JSON load/save. |
| `PriceMath` (static) | Pure tick-rounding math. Isolated to make it trivially testable. |

### Persistence

- **Settings:** JSON at `Documents/NinjaTrader 8/bin/Custom/AddOns/PairedStops/settings.json`. Loaded on tab open, saved on change.
- **Tracking state:** *not* persisted across NT restarts. On startup the `PairManager` starts clean. Re-adopting orphaned broker-side orders is explicitly out of scope; the pair-ID tags (`PAIRSTOP_{guid}_BUY` / `_SELL`) make stragglers easy to identify and cancel manually.

## Data Flow

### Place — one-click path (ghost preview off)

1. View gathers `{account, instrument, offset, qty}` and calls `PairManager.PlacePair(...)`.
2. Read `instrument.MarketData.Last.Price`; fall back to bid/ask mid if Last is stale or zero.
3. Compute `buyPx = RoundToTickSize(last + offset)` and `sellPx = RoundToTickSize(last - offset)`.
4. Reject if a pair is already active on this account (blocking default).
5. Generate `Guid pairId`. Build and submit the **buy** stop via `account.CreateOrder(...)` + `account.Submit(...)` with name `PAIRSTOP_{pairId}_BUY`. On success, submit the **sell** the same way.
6. On any exception or immediate rejection of either leg: cancel whichever was accepted, clear state, surface the error.
7. Store both orders and `ExpectedSpread = buyPx - sellPx` in `PairState`.

### Place — ghost-preview path

1. Steps 1–3 as above.
2. `GhostPreview.Show(buyPx, sellPx)` draws chart lines (if a chart is found) and the view shows the inline confirmation strip.
3. **Confirm** → continue from step 4. **Cancel** → `GhostPreview.Hide()`, nothing submitted.

### Track — linked drag and the ping-pong guard

`PairManager` holds `private readonly object _sync = new(); private bool _programmatic = false;`

```
OnAccountOrderUpdate(OrderEventArgs e):
  lock (_sync):
    if (_programmatic) return                    // our own ChangeOrder echoed — ignore
    if (!IsTrackedPairOrder(e.Order)) return
    if (e.Order.OrderState != OrderState.Working) return
    if (PriceMatchesExpected(e.Order)) return     // no drift — ignore spurious events
    partner = PartnerOf(e.Order)
    newPartnerPx = e.Order == state.Buy
                     ? RoundToTick(e.Order.StopPrice - state.ExpectedSpread)
                     : RoundToTick(e.Order.StopPrice + state.ExpectedSpread)
    _programmatic = true
    try:
      account.ChangeOrder(partner, partner.Quantity, 0, newPartnerPx)
    finally:
      _programmatic = false
```

Two lines of defense:
1. The `_programmatic` flag suppresses the synchronous echo.
2. NT fires `OrderUpdate` on a background thread; the echo may arrive *after* the flag is cleared. The `PriceMatchesExpected` short-circuit handles that race — by then the partner's price already matches the expected spread, so we do nothing.

Optional beep on drag-sync fires here (post-ChangeOrder, in the dispatcher) when the audible-confirmation toggle is on.

### Fill — OCO

```
OnAccountOrderUpdate(e):
  if (e.OrderState == Filled && IsTrackedPairOrder(e.Order)):
    partner = PartnerOf(e.Order)
    if (partner.OrderState is Working or Accepted):
      account.Cancel(new[]{partner})
    ClearPair()
```

The trader's ATM takes over the filled position. The tool does not submit TP or SL.

### Manual cancel / rejection

- `OrderState == Cancelled` on one leg (trader cancelled via Chart Trader): cancel the partner, clear state.
- `OrderState == Rejected` on either leg: cancel the partner if working, clear state, surface the rejection reason.

### Session reset

A `DispatcherTimer` ticks once per minute; when wall-clock ET crosses 18:00 since the last tick, `PairManager.ResetSession()` clears tracking state. Live broker-side orders are not cancelled — they survive the session rollover.

## Threading

- `Account.OrderUpdate` fires on NT's market-data thread.
- All UI mutations (status strip, confirmation strip, inputs) marshal via `Dispatcher.BeginInvoke`.
- `PairManager` is thread-safe internally via a `lock`.
- The view never touches order state directly — all interaction goes through `PairManager`.

## Error Handling

| Condition | Behavior |
|---|---|
| Last price unavailable (pre-market, instrument not subscribed) | Reject place; status: "No market data — cannot compute prices" |
| Offset doesn't produce a valid tick-rounded price both sides | Reject place; status includes rounded prices so user understands |
| Pair already active for account | Reject place; status: "Pair already active — cancel first" |
| First-leg submit throws | Status: submit error message; no second leg sent |
| First-leg accepted, second-leg throws or rejects | Auto-cancel first leg; status: "Second leg failed — first leg cancelled" |
| Drag-sync `ChangeOrder` throws | Status: "Sync failed — pair is now unlinked"; state cleared (do not cancel — orders are still legitimate) |
| Account disconnect during active pair | Status: "Account disconnected"; on reconnect, clear tracking state and status |
| No chart found for ghost preview | Silent — panel confirmation carries the UX |

Detailed lines are also written to NT's Output window for diagnostics.

## Edge Cases

- **Place while pair active:** blocked with the "Pair already active" message. Auto-cancel-and-replace was considered as an alternative behavior and deferred — if it proves useful in practice it can be added as a configurable toggle.
- **Instrument change on the chart mid-pair:** the pair stays bound to its original instrument; the tool does not migrate it.
- **NT restart mid-pair:** tracking state is not restored. Orphaned broker-side orders keep their pair-ID tags and can be cancelled manually.

## File Layout

```
~/ORB-NT8-Orders-Addon/
├── .git/
├── .gitignore                    # excludes bin/, obj/, .vs/
├── README.md                     # install, open, settings, limitations
├── PairedStopsAddOn.cs           # all types, single namespace
└── docs/
    └── superpowers/specs/
        └── 2026-04-20-paired-stops-addon-design.md   (this file)
```

## Testing

Manual verification in a Sim101 account before deploying to a funded account. Automated unit tests are out of scope (NT's `Account`/`Order`/`Instrument` types are not trivially mockable and the value-per-hour is poor for a single-file AddOn). The `PriceMath` tick-rounding helper is isolated as a static pure function so a harness could be added later.

Verification steps (to be restated in the README):

1. Place pair on NQ → drag buy stop up 5 points → confirm sell stop follows up 5 points (spread preserved).
2. Place pair on NQ → drag sell stop down 3 points → confirm buy stop follows down 3 points.
3. Let price hit the buy stop → confirm sell stop cancels automatically within 1 second.
4. Place pair → manually cancel one order via Chart Trader → confirm the partner cancels.
5. Tick-size rounding: try offset 10.1 → confirm order prices snap to 0.25 increments.
6. Ghost-line toggle: enable → Place → lines appear → Confirm → orders submit. Cancel path: Place → Cancel → nothing submitted.
7. Run across session rollover (18:00 ET) → confirm tracking state clears.

## Non-Goals

- No take-profit or stop-loss orders (ATM handles that post-fill).
- No deciding when to place orders (trader clicks the button).
- No position closing.
- No signal-driven or scheduled trading.
- No unsupervised operation — this is a trader-in-the-loop helper, not an autonomous system.

## Prop-Firm Compliance Note

Designed as a trader-assistive helper intended to comply with prop firms that permit semi-automated trading. The trader initiates every action (button click, order drag); the tool only syncs two already-user-placed orders. It does not enter, exit, or manage positions autonomously. Traders should verify compatibility with their prop firm's rules before deploying — do not use it with prop firms that prohibit any form of automation.
