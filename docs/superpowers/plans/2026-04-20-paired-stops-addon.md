# Paired Stops AddOn Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a NinjaTrader 8 AddOn that lets a trader place a synchronized pair of stop orders (one buy above, one sell below market) with one click, auto-sync them on chart drag, and auto-cancel the partner on fill.

**Architecture:** Single-file NinjaScript AddOn (`PairedStopsAddOn.cs`) registered via `AddOnBase`, opens as a tabbed window in NT8 via `INTTabFactory` → `NTTabPage` → WPF `UserControl`. A `PairManager` owns the active pair state, subscribes to `Account.OrderUpdate`, handles place/track/OCO/session-reset, and enforces a ping-pong guard on drag-sync. Live orders use NT's native Chart Trader lines. Optional ghost preview draws chart `DrawObjects` + shows an inline confirmation strip. JSON settings persist to the NT user folder.

**Tech Stack:** C#, .NET Framework 4.8 (NT8's target), NinjaScript AddOn API, WPF (for the tab UI), `System.Windows.Threading.DispatcherTimer`, `System.Text.Json` or `DataContractSerializer` for settings persistence.

**Dev workflow note:** NT8 runs only on Windows, so the compile/test cycle requires a Windows machine with NT8 installed. Typical loop: write on macOS → `git push` → pull on the Windows NT8 box → copy `PairedStopsAddOn.cs` into `Documents\NinjaTrader 8\bin\Custom\AddOns\` → compile in the NinjaScript Editor (press F5) → test in Sim101. The plan's "verify" steps all assume access to that Windows environment.

**Reference spec:** `docs/superpowers/specs/2026-04-20-paired-stops-addon-design.md`.

---

## Task 1: Capture NT8 AddOn API Reference

**Goal:** Clone two open-source NT8 AddOns as sidecar references and write a short `docs/nt8-api-notes.md` that pins down the exact class names, method signatures, and event shapes used throughout the rest of the plan. This protects against API drift between the plan's pseudo-code and what NT8 actually exposes.

**Files:**
- Create: `~/ORB-NT8-Orders-Addon/docs/nt8-api-notes.md`
- Reference (clone outside the repo): `~/nt8-refs/allankk-addons`, `~/nt8-refs/djq99-addon-client`

- [ ] **Step 1: Clone the reference AddOns**

```bash
mkdir -p ~/nt8-refs
cd ~/nt8-refs
[ -d allankk-addons ]     || git clone https://github.com/allankk/ninjatrader-addons.git allankk-addons
[ -d djq99-addon-client ] || git clone https://github.com/djq99/ninjatrader-addon-client.git djq99-addon-client
```

- [ ] **Step 2: Extract API patterns**

Read through these files and extract the exact signatures and idioms:

- `~/nt8-refs/djq99-addon-client/Addon/` — look for a file with `: AddOnBase`, and note:
  - `OnWindowCreated(Window window)` / `OnWindowDestroyed(Window window)` overrides
  - How `NTMenuItem` is constructed and added to the "New" menu
  - `INTTabFactory.CreateParentWindow()` and `CreateTabContent()`
  - `NTTabPage` overrides: `Icon`, `TabName`, `SaveToXElement`, `RestoreFromXElement`
- Search both repos for `Account.OrderUpdate`, `CreateOrder`, `Submit(`, `Cancel(`, `ChangeOrder(` — capture the exact argument lists.
- Search for `MasterInstrument.RoundToTickSize` — confirm whether it's a static utility or instance method and its signature.
- Note whether `Account.Submit` takes `new[] { order }` (array) or a single `Order`.
- Note how `OrderEventArgs` exposes the order and state (`e.Order`, `e.Order.OrderState`, `e.OrderState`).

- [ ] **Step 3: Write `docs/nt8-api-notes.md`**

The notes file should be 1–2 pages of concise, copy-paste-ready signatures. Template:

```markdown
# NT8 API Reference Notes

Captured 2026-04-20 from allankk/ninjatrader-addons and djq99/ninjatrader-addon-client.

## AddOn entry point
```csharp
public class <MyAddOn> : NinjaTrader.NinjaScript.AddOns.AddOnBase
{
    protected override void OnStateChange() { /* set Name in State.SetDefaults */ }
    protected override void OnWindowCreated(Window window) { /* inject NTMenuItem */ }
    protected override void OnWindowDestroyed(Window window) { /* unhook */ }
}
```

## Menu item registration
<exact code pattern copied from reference>

## Tab factory + tab page
<exact code patterns>

## Account / Order API
<exact signatures for CreateOrder, Submit, Cancel, ChangeOrder, OrderUpdate event>

## Misc
- `Instrument.MasterInstrument.RoundToTickSize(double)` — returns double (verify)
- `Instrument.MarketData.Last.Price` — may be null pre-open; fall back to `.Bid`/`.Ask`
- Market-data + order events fire on a non-UI thread; marshal to WPF `Dispatcher` for UI updates.
```

Fill in the `<...>` sections with actual code from the reference repos. If a pattern differs between the two repos, note both and pick the simpler one.

- [ ] **Step 4: Commit**

```bash
cd ~/ORB-NT8-Orders-Addon
git add docs/nt8-api-notes.md
git commit -m "Add NT8 AddOn API reference notes

Captured exact signatures for AddOnBase, NTMenuItem, INTTabFactory,
NTTabPage, Account order submission, and Account.OrderUpdate from
two open-source NT8 AddOns. Used as the canonical reference for the
PairedStopsAddOn implementation."
```

---

## Task 2: Project Scaffold — AddOn, Menu Item, Empty Tab

**Goal:** Get an empty Paired Stops tab opening from the NT8 `New` menu. No logic yet. This task proves the AddOn registration plumbing works end-to-end.

**Files:**
- Create: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Write the scaffold**

Use the signatures from `docs/nt8-api-notes.md` to fill in the `<verify>` tags below. If anything conflicts with the notes, the notes win.

```csharp
// PairedStopsAddOn.cs
// Paired Stops AddOn for NinjaTrader 8.
// See docs/superpowers/specs/2026-04-20-paired-stops-addon-design.md for the design.

#region Using declarations
using System;
using System.Windows;
using System.Windows.Controls;
using System.Xml.Linq;
using NinjaTrader.Cbi;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript.AddOns;
#endregion

namespace NinjaTrader.NinjaScript.AddOns.PairedStops
{
    public class PairedStopsAddOn : AddOnBase
    {
        private const string MenuHeader = "Paired Stops";

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Paired Stops";
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            // Only add the menu item to NT's Control Center "New" menu.
            // <verify> - exact window-type check and menu-path from reference
            if (!(window is NinjaTrader.Gui.Tools.NTWindow)) return;
            var newMenu = NinjaTrader.Gui.Tools.MenuItemProvider.GetNewMenu(window);
            if (newMenu == null) return;

            var menuItem = new NTMenuItem { Header = MenuHeader, Style = Application.Current.TryFindResource("MainMenuItem") as Style };
            menuItem.Click += (s, e) => OpenTab(window);
            newMenu.Items.Add(menuItem);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            // Cleanup is handled by NT when the parent window closes; nothing to do here.
        }

        private static void OpenTab(Window parentWindow)
        {
            // <verify> - confirm factory-based tab creation path
            var factory = new PairedStopsTabFactory();
            var tabWindow = factory.CreateParentWindow();
            var tab = factory.CreateTabContent() as NTTabPage;
            if (tab != null)
            {
                tabWindow.MainTabControl.AddNTTabPage(tab);
                tabWindow.Show();
            }
        }
    }

    public class PairedStopsTabFactory : INTTabFactory
    {
        public NTWindow CreateParentWindow() => new NTWindow { Caption = "Paired Stops" };
        public NTTabPage CreateTabContent() => new PairedStopsTab();
    }

    public class PairedStopsTab : NTTabPage
    {
        public PairedStopsTab()
        {
            // Placeholder content — replaced by PairedStopsView in Task 4.
            Content = new TextBlock
            {
                Text = "Paired Stops — coming soon",
                Margin = new Thickness(16),
                FontSize = 14
            };
        }

        public override void Cleanup() { /* nothing yet */ }
        protected override string GetHeaderSubText() => "Paired Stops";
        protected override void RestoreFromXElement(XElement element) { }
        protected override void SaveToXElement(XElement element) { }
    }
}
```

- [ ] **Step 2: Reconcile with the API notes**

Open `docs/nt8-api-notes.md` and check every `<verify>` tag in the scaffold against the captured signatures. Correct the scaffold so it compiles against the real NT8 API — the structure above is correct but class/member names like `MenuItemProvider.GetNewMenu`, `MainTabControl.AddNTTabPage`, `GetHeaderSubText` are placeholders that the reference repos should confirm or replace.

- [ ] **Step 3: Compile-verify in NT8 (Windows)**

1. Copy `PairedStopsAddOn.cs` into `Documents\NinjaTrader 8\bin\Custom\AddOns\`.
2. Open the NinjaScript Editor (NT Control Center → New → NinjaScript Editor).
3. Press F5 to compile. Expected: zero errors, zero warnings.
4. Open the NT Control Center → `New` menu. Expected: a "Paired Stops" item appears.
5. Click it. Expected: a new window opens with the placeholder "Paired Stops — coming soon" text.
6. Close the window.

If compile fails, the error message will point at the specific API mismatch. Fix against `docs/nt8-api-notes.md` and retry.

- [ ] **Step 4: Commit**

```bash
cd ~/ORB-NT8-Orders-Addon
git add PairedStopsAddOn.cs
git commit -m "Scaffold PairedStopsAddOn with menu item and empty tab

Registers AddOnBase, adds a 'Paired Stops' entry under NT's New menu,
and opens an empty NTTabPage. No order logic yet."
```

---

## Task 3: Settings Model + JSON Persistence

**Goal:** Define the `PairedStopsSettings` POCO, load/save to a JSON file, expose via `SettingsStore`. No UI wiring yet — just the data layer.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs` (append new types)

- [ ] **Step 1: Add `PairedStopsSettings`**

Insert this class inside the namespace (above `PairedStopsAddOn`):

```csharp
public class PairedStopsSettings
{
    public double OffsetPoints { get; set; } = 10.0;
    public int    Quantity     { get; set; } = 1;

    public string AccountName    { get; set; } = "";   // empty = auto-pick first
    public string InstrumentName { get; set; } = "NQ 06-26"; // user can override

    public bool GhostPreviewEnabled { get; set; } = false;

    // Colors stored as ARGB hex so JSON stays primitive.
    public string PreviewBuyColorArgb  { get; set; } = "#FF00C853"; // green
    public string PreviewSellColorArgb { get; set; } = "#FFD50000"; // red
    public double PreviewLineWidth     { get; set; } = 2.0;
    public string PreviewDashStyle     { get; set; } = "Dashed";    // "Dashed" | "Dotted" | "DashDot"

    public string PairTagPrefix { get; set; } = "PAIRSTOP_";

    public bool AudibleDragSync { get; set; } = false;
}
```

- [ ] **Step 2: Add `SettingsStore`**

```csharp
public static class SettingsStore
{
    private static readonly string SettingsDir = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "NinjaTrader 8", "bin", "Custom", "AddOns", "PairedStops");

    private static readonly string SettingsPath = System.IO.Path.Combine(SettingsDir, "settings.json");

    public static PairedStopsSettings Load()
    {
        try
        {
            if (!System.IO.File.Exists(SettingsPath)) return new PairedStopsSettings();
            var json = System.IO.File.ReadAllText(SettingsPath);
            return System.Text.Json.JsonSerializer.Deserialize<PairedStopsSettings>(json)
                   ?? new PairedStopsSettings();
        }
        catch (Exception ex)
        {
            NinjaTrader.Code.Output.Process(
                $"[PairedStops] Settings load failed: {ex.Message}. Using defaults.",
                PrintTo.OutputTab1);
            return new PairedStopsSettings();
        }
    }

    public static void Save(PairedStopsSettings settings)
    {
        try
        {
            System.IO.Directory.CreateDirectory(SettingsDir);
            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var json = System.Text.Json.JsonSerializer.Serialize(settings, options);
            System.IO.File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            NinjaTrader.Code.Output.Process(
                $"[PairedStops] Settings save failed: {ex.Message}",
                PrintTo.OutputTab1);
        }
    }
}
```

Add `using NinjaTrader.Code;` to the using block if not already present (for `Output.Process` and `PrintTo`).

If `System.Text.Json` is not available on NT8's .NET Framework 4.8 build (it usually is via `System.Text.Json` NuGet, but NT's custom folder may not reference it), fall back to `DataContractJsonSerializer` in `System.Runtime.Serialization`. Check the captured API notes; if uncertain, try `System.Text.Json` first and swap if the compiler rejects it.

- [ ] **Step 3: Quick smoke check in NT8**

1. Copy updated file into `bin\Custom\AddOns\`, compile. Expected: zero errors.
2. There's nothing user-visible to test yet — this is just a data-layer task.
3. After Task 4 wires the UI, round-trip persistence will be exercised.

- [ ] **Step 4: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Add PairedStopsSettings POCO and SettingsStore

JSON load/save to Documents/NinjaTrader 8/bin/Custom/AddOns/PairedStops/
settings.json. All 10 user-configurable fields with sensible defaults.
Not yet wired to the UI."
```

---

## Task 4: View Skeleton — Input Panel

**Goal:** Build the `PairedStopsView` `UserControl` with all input fields, buttons, and the status strip. Wire it to the settings model with two-way binding. No order logic yet — Place/Cancel buttons are no-ops that log to the status strip.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs` (add `PairedStopsView` class, update `PairedStopsTab` to use it)

- [ ] **Step 1: Add `PairedStopsView`**

Build the UI in code-behind (no separate XAML file to keep this a single-file AddOn). Insert before `PairedStopsTab`:

```csharp
public class PairedStopsView : UserControl
{
    public PairedStopsSettings Settings { get; }

    // Exposed buttons and status so PairManager wiring (Task 6+) can attach handlers.
    public Button PlaceButton  { get; }
    public Button CancelButton { get; }
    public TextBlock StatusText { get; }

    // Ghost-preview confirmation strip (hidden until Task 13).
    public StackPanel PreviewStrip { get; }
    public Button PreviewConfirmButton { get; }
    public Button PreviewCancelButton  { get; }
    public TextBlock PreviewText { get; }

    // Expose the inputs for PairManager to read current values.
    public ComboBox AccountCombo { get; }
    public TextBox  InstrumentBox { get; }
    public TextBox  OffsetBox { get; }
    public TextBox  QuantityBox { get; }
    public CheckBox GhostToggle { get; }
    public CheckBox AudibleToggle { get; }

    public PairedStopsView(PairedStopsSettings settings)
    {
        Settings = settings;
        DataContext = settings;

        var root = new Grid { Margin = new Thickness(12) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // inputs
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // buttons
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // preview strip
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // spacer
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // status

        // --- Inputs grid ---
        var inputs = new Grid();
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        for (int i = 0; i < 6; i++)
            inputs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        int row = 0;
        AccountCombo  = AddRow(inputs, ref row, "Account", new ComboBox());
        InstrumentBox = AddRow(inputs, ref row, "Instrument", new TextBox { Text = settings.InstrumentName });
        OffsetBox     = AddRow(inputs, ref row, "Offset (pts)", new TextBox { Text = settings.OffsetPoints.ToString("0.##") });
        QuantityBox   = AddRow(inputs, ref row, "Quantity", new TextBox { Text = settings.Quantity.ToString() });
        GhostToggle   = AddRow(inputs, ref row, "Ghost preview", new CheckBox { IsChecked = settings.GhostPreviewEnabled });
        AudibleToggle = AddRow(inputs, ref row, "Beep on drag-sync", new CheckBox { IsChecked = settings.AudibleDragSync });

        Grid.SetRow(inputs, 0);
        root.Children.Add(inputs);

        // --- Buttons row ---
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
        PlaceButton  = new Button { Content = "Place Paired Stops", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
        CancelButton = new Button { Content = "Cancel Pair", Padding = new Thickness(12, 6, 12, 6) };
        buttons.Children.Add(PlaceButton);
        buttons.Children.Add(CancelButton);
        Grid.SetRow(buttons, 1);
        root.Children.Add(buttons);

        // --- Preview strip (hidden until needed) ---
        PreviewStrip = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0), Visibility = Visibility.Collapsed };
        PreviewText = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        PreviewConfirmButton = new Button { Content = "Confirm", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 0) };
        PreviewCancelButton  = new Button { Content = "Cancel",  Padding = new Thickness(8, 4, 8, 4) };
        PreviewStrip.Children.Add(PreviewText);
        PreviewStrip.Children.Add(PreviewConfirmButton);
        PreviewStrip.Children.Add(PreviewCancelButton);
        Grid.SetRow(PreviewStrip, 2);
        root.Children.Add(PreviewStrip);

        // --- Status ---
        StatusText = new TextBlock { Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(StatusText, 4);
        root.Children.Add(StatusText);

        Content = root;

        // Populate account list from NT.
        foreach (var acct in Account.All)
            AccountCombo.Items.Add(acct.Name);
        if (!string.IsNullOrEmpty(settings.AccountName) && AccountCombo.Items.Contains(settings.AccountName))
            AccountCombo.SelectedItem = settings.AccountName;
        else if (AccountCombo.Items.Count > 0)
            AccountCombo.SelectedIndex = 0;

        // Write-back on change.
        AccountCombo.SelectionChanged += (s, e) => { settings.AccountName = AccountCombo.SelectedItem as string ?? ""; SettingsStore.Save(settings); };
        InstrumentBox.LostFocus        += (s, e) => { settings.InstrumentName = InstrumentBox.Text.Trim(); SettingsStore.Save(settings); };
        OffsetBox.LostFocus            += (s, e) => { if (double.TryParse(OffsetBox.Text, out var v) && v > 0) { settings.OffsetPoints = v; SettingsStore.Save(settings); } };
        QuantityBox.LostFocus          += (s, e) => { if (int.TryParse(QuantityBox.Text, out var v) && v > 0) { settings.Quantity = v; SettingsStore.Save(settings); } };
        GhostToggle.Checked            += (s, e) => { settings.GhostPreviewEnabled = true;  SettingsStore.Save(settings); };
        GhostToggle.Unchecked          += (s, e) => { settings.GhostPreviewEnabled = false; SettingsStore.Save(settings); };
        AudibleToggle.Checked          += (s, e) => { settings.AudibleDragSync = true;  SettingsStore.Save(settings); };
        AudibleToggle.Unchecked        += (s, e) => { settings.AudibleDragSync = false; SettingsStore.Save(settings); };

        // Placeholder handlers until Task 6 wires PairManager.
        PlaceButton.Click  += (s, e) => SetStatus("Place clicked — not yet wired.");
        CancelButton.Click += (s, e) => SetStatus("Cancel clicked — not yet wired.");
    }

    public void SetStatus(string message, bool isError = false)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            StatusText.Text = message;
            StatusText.Foreground = isError ? System.Windows.Media.Brushes.Red : System.Windows.Media.Brushes.DimGray;
        }));
    }

    private static T AddRow<T>(Grid grid, ref int row, string label, T control) where T : UIElement
    {
        var lbl = new TextBlock { Text = label, Margin = new Thickness(0, 4, 12, 4), VerticalAlignment = VerticalAlignment.Center };
        Grid.SetRow(lbl, row); Grid.SetColumn(lbl, 0);
        Grid.SetRow(control, row); Grid.SetColumn(control, 1);
        (control as FrameworkElement).Margin = new Thickness(0, 4, 0, 4);
        grid.Children.Add(lbl);
        grid.Children.Add(control);
        row++;
        return control;
    }
}
```

- [ ] **Step 2: Update `PairedStopsTab` to host the view**

Replace the `PairedStopsTab` class body with:

```csharp
public class PairedStopsTab : NTTabPage
{
    private readonly PairedStopsSettings _settings;
    private readonly PairedStopsView _view;

    public PairedStopsTab()
    {
        _settings = SettingsStore.Load();
        _view     = new PairedStopsView(_settings);
        Content   = _view;
    }

    public override void Cleanup() { SettingsStore.Save(_settings); }
    protected override string GetHeaderSubText() => "Paired Stops";
    protected override void RestoreFromXElement(XElement element) { }
    protected override void SaveToXElement(XElement element) { }
}
```

- [ ] **Step 3: Compile-verify in NT8**

1. Copy updated file, compile. Expected: zero errors.
2. Open Paired Stops tab. Expected: form with Account dropdown (populated), Instrument textbox, Offset, Quantity, Ghost preview toggle, Audible toggle, Place + Cancel buttons, status area.
3. Change a value (e.g. Offset to 15), click out of the field. Close and reopen the tab. Expected: the 15 persists.
4. Click Place. Expected: status shows "Place clicked — not yet wired."
5. Click Cancel. Expected: status shows "Cancel clicked — not yet wired."

- [ ] **Step 4: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Build PairedStopsView input panel with settings round-trip

Code-behind WPF UserControl with account/instrument/offset/qty inputs,
ghost-preview + audible toggles, Place/Cancel buttons, status strip, and
hidden preview-confirmation strip. Inputs auto-persist on blur/change."
```

---

## Task 5: PriceMath Helper

**Goal:** Extract the tick-rounding math into a pure static class so it can be reasoned about (and potentially tested later) without touching NT types.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Add `PriceMath`**

Insert in the namespace:

```csharp
public static class PriceMath
{
    /// <summary>Rounds a raw price to the nearest tick-size multiple. Always returns a positive price.</summary>
    public static double RoundToTick(double price, double tickSize)
    {
        if (tickSize <= 0) throw new ArgumentOutOfRangeException(nameof(tickSize), "Tick size must be positive.");
        return Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize;
    }

    /// <summary>Computes the buy/sell stop prices around a reference price, both rounded to tick size.</summary>
    public static (double buyStop, double sellStop) ComputePair(double reference, double offsetPoints, double tickSize)
    {
        var buy  = RoundToTick(reference + offsetPoints, tickSize);
        var sell = RoundToTick(reference - offsetPoints, tickSize);
        return (buy, sell);
    }

    /// <summary>True when two prices are equal within half a tick — the right way to compare NT order prices.</summary>
    public static bool PricesEqual(double a, double b, double tickSize) => Math.Abs(a - b) < tickSize * 0.5;
}
```

- [ ] **Step 2: Sanity-check by inspection**

Mentally walk through:
- `RoundToTick(21010.1, 0.25)` → 21010.00? `21010.1 / 0.25 = 84040.4` → round to 84040 → `* 0.25 = 21010.0`. ✓
- `RoundToTick(21010.13, 0.25)` → `84040.52` → 84041 → `21010.25`. ✓
- `ComputePair(21000, 10.1, 0.25)` → buy: `21010.1 → 21010.0`, sell: `20989.9 → 20990.0`. ✓
- `PricesEqual(21000.001, 21000, 0.25)` → diff 0.001 < 0.125 → true. ✓

If any of those feel wrong, fix before proceeding. These are the invariants every downstream task relies on.

- [ ] **Step 3: Compile-verify**

1. Copy file, compile. Expected: zero errors, zero warnings.
2. No user-visible change.

- [ ] **Step 4: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Add PriceMath static helpers for tick rounding and comparison

Pure, NT-independent tick-rounding math. Isolated so the arithmetic core
of the pair-pricing logic can be reasoned about without touching NT types."
```

---

## Task 6: PairManager Skeleton + Account Subscription

**Goal:** Introduce `PairManager` and `PairState`, wire the view's Account selection to a live `Account.OrderUpdate` subscription. Nothing fires yet — this establishes the subscription plumbing and unsubscribes cleanly on account change / tab close.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Add `PairState`**

```csharp
internal sealed class PairState
{
    public Guid   PairId         { get; }
    public Order  Buy            { get; }
    public Order  Sell           { get; }
    public double ExpectedSpread { get; }   // buyPrice - sellPrice at placement
    public DateTime CreatedUtc   { get; }

    public PairState(Guid pairId, Order buy, Order sell, double expectedSpread)
    {
        PairId = pairId; Buy = buy; Sell = sell;
        ExpectedSpread = expectedSpread;
        CreatedUtc = DateTime.UtcNow;
    }

    public Order PartnerOf(Order o) => o == Buy ? Sell : (o == Sell ? Buy : null);
    public bool  Contains(Order o)  => o == Buy || o == Sell;
}
```

- [ ] **Step 2: Add `PairManager` skeleton**

```csharp
internal sealed class PairManager : IDisposable
{
    private readonly PairedStopsView _view;
    private readonly PairedStopsSettings _settings;
    private readonly object _sync = new object();
    private bool _programmatic;
    private PairState _state;
    private Account _subscribedAccount;

    public PairManager(PairedStopsView view, PairedStopsSettings settings)
    {
        _view = view;
        _settings = settings;

        // Subscribe now to whatever account is currently selected.
        ResubscribeToSelectedAccount();

        // Rebind on every account-combo change.
        _view.AccountCombo.SelectionChanged += (s, e) => ResubscribeToSelectedAccount();

        // Wire the buttons (placeholders — filled in later tasks).
        _view.PlaceButton.Click  += (s, e) => OnPlaceClicked();
        _view.CancelButton.Click += (s, e) => OnCancelClicked();
    }

    private void ResubscribeToSelectedAccount()
    {
        if (_subscribedAccount != null)
        {
            _subscribedAccount.OrderUpdate -= OnAccountOrderUpdate;
            _subscribedAccount = null;
        }

        var name = _view.AccountCombo.SelectedItem as string;
        if (string.IsNullOrEmpty(name)) return;

        var account = Account.All.FirstOrDefault(a => a.Name == name);
        if (account == null) return;

        _subscribedAccount = account;
        _subscribedAccount.OrderUpdate += OnAccountOrderUpdate;
    }

    private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
    {
        // Task 7+ fills this in.
    }

    // Placeholders — Task 7/8 fill these in.
    private void OnPlaceClicked()  { _view.SetStatus("Place: stub — implemented in Task 7."); }
    private void OnCancelClicked() { _view.SetStatus("Cancel: stub — implemented in Task 8."); }

    public void Dispose()
    {
        if (_subscribedAccount != null)
        {
            _subscribedAccount.OrderUpdate -= OnAccountOrderUpdate;
            _subscribedAccount = null;
        }
    }
}
```

Add `using System.Linq;` to the using block (for `Account.All.FirstOrDefault`).

- [ ] **Step 3: Wire `PairManager` into the tab**

Update `PairedStopsTab`:

```csharp
public class PairedStopsTab : NTTabPage
{
    private readonly PairedStopsSettings _settings;
    private readonly PairedStopsView _view;
    private readonly PairManager _manager;

    public PairedStopsTab()
    {
        _settings = SettingsStore.Load();
        _view     = new PairedStopsView(_settings);
        _manager  = new PairManager(_view, _settings);
        Content   = _view;
    }

    public override void Cleanup() { _manager.Dispose(); SettingsStore.Save(_settings); }
    protected override string GetHeaderSubText() => "Paired Stops";
    protected override void RestoreFromXElement(XElement element) { }
    protected override void SaveToXElement(XElement element) { }
}
```

Also remove the old placeholder `PlaceButton.Click`/`CancelButton.Click` handlers from the `PairedStopsView` constructor — they're now owned by `PairManager`.

- [ ] **Step 4: Compile-verify in NT8**

1. Copy file, compile. Expected: zero errors.
2. Open the tab, click Place. Expected: status shows "Place: stub — implemented in Task 7."
3. Click Cancel. Expected: corresponding stub message.
4. Change the Account in the dropdown. Expected: no visible change, but internally the OrderUpdate subscription migrated.
5. Close the tab. Expected: NT remains stable (no lingering handler on the previous account).

- [ ] **Step 5: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Add PairManager skeleton + PairState with Account.OrderUpdate plumbing

Subscribes to the selected account's OrderUpdate event, resubscribes on
account change, cleans up on tab close. Place/Cancel buttons now route
through PairManager but still log stubs. No order logic yet."
```

---

## Task 7: PlacePair — Atomic Submit (no ghost preview)

**Goal:** Implement the happy-path place flow: read last price, compute and tick-round the two stop prices, submit both orders atomically, store the pair state. If either leg fails, cancel the other. Ghost preview is added in Task 13 — for now, Place is always one-click.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Replace `OnPlaceClicked` with the real implementation**

Replace the stub in `PairManager` with:

```csharp
private void OnPlaceClicked()
{
    lock (_sync)
    {
        if (_state != null) { _view.SetStatus("Pair already active — cancel first.", isError: true); return; }
        if (_subscribedAccount == null) { _view.SetStatus("No account selected.", isError: true); return; }

        var instrument = Instrument.GetInstrument(_view.InstrumentBox.Text.Trim());
        if (instrument == null || instrument.MasterInstrument == null)
        { _view.SetStatus($"Instrument '{_view.InstrumentBox.Text}' not found.", isError: true); return; }

        if (!double.TryParse(_view.OffsetBox.Text, out var offset) || offset <= 0)
        { _view.SetStatus("Invalid offset.", isError: true); return; }

        if (!int.TryParse(_view.QuantityBox.Text, out var qty) || qty <= 0)
        { _view.SetStatus("Invalid quantity.", isError: true); return; }

        var tickSize = instrument.MasterInstrument.TickSize;
        var last = instrument.MarketData?.Last?.Price ?? 0.0;
        if (last <= 0)
        {
            var bid = instrument.MarketData?.Bid?.Price ?? 0.0;
            var ask = instrument.MarketData?.Ask?.Price ?? 0.0;
            if (bid > 0 && ask > 0) last = (bid + ask) * 0.5;
            else { _view.SetStatus("No market data — cannot compute prices.", isError: true); return; }
        }

        var (buyPx, sellPx) = PriceMath.ComputePair(last, offset, tickSize);
        if (buyPx <= sellPx) { _view.SetStatus($"Invalid prices: buy {buyPx} <= sell {sellPx}.", isError: true); return; }

        SubmitPair(instrument, qty, buyPx, sellPx);
    }
}

private void SubmitPair(Instrument instrument, int qty, double buyPx, double sellPx)
{
    var pairId = Guid.NewGuid();
    var tag    = _settings.PairTagPrefix + pairId.ToString("N").Substring(0, 8);

    Order buyOrder = null, sellOrder = null;
    try
    {
        buyOrder = _subscribedAccount.CreateOrder(
            instrument, OrderAction.Buy, OrderType.StopMarket,
            OrderEntry.Manual, TimeInForce.Day, qty,
            limitPrice: 0, stopPrice: buyPx,
            oco: string.Empty, name: tag + "_BUY",
            gtd: Core.Globals.MaxDate, customOrder: null);
        _subscribedAccount.Submit(new[] { buyOrder });

        sellOrder = _subscribedAccount.CreateOrder(
            instrument, OrderAction.SellShort, OrderType.StopMarket,
            OrderEntry.Manual, TimeInForce.Day, qty,
            limitPrice: 0, stopPrice: sellPx,
            oco: string.Empty, name: tag + "_SELL",
            gtd: Core.Globals.MaxDate, customOrder: null);
        _subscribedAccount.Submit(new[] { sellOrder });
    }
    catch (Exception ex)
    {
        if (buyOrder != null && (buyOrder.OrderState == OrderState.Accepted || buyOrder.OrderState == OrderState.Working))
        {
            try { _subscribedAccount.Cancel(new[] { buyOrder }); } catch { /* swallow cleanup errors */ }
        }
        _view.SetStatus($"Place failed: {ex.Message}", isError: true);
        NinjaTrader.Code.Output.Process($"[PairedStops] Place failed: {ex}", PrintTo.OutputTab1);
        return;
    }

    _state = new PairState(pairId, buyOrder, sellOrder, buyPx - sellPx);
    _view.SetStatus($"Pair active: buy @ {buyPx}, sell @ {sellPx}.");
}
```

**Verify against `docs/nt8-api-notes.md`:** the `CreateOrder` argument list above is the most common NT8 unmanaged shape, but some NT8 versions use a different overload (fewer args, or `stopPrice`/`limitPrice` in a different order). If the notes show a different shape, adjust the call here. `Core.Globals.MaxDate` may also be named differently — the notes file is the source of truth.

Add `using NinjaTrader.Core;` if not already present.

- [ ] **Step 2: Compile-verify in NT8 (Sim101)**

1. Copy file, compile. Expected: zero errors.
2. Open tab, connect to Sim101. Select Sim101 account, instrument `NQ XX-XX` (active front-month).
3. Offset 10, quantity 1. Click Place.
4. Expected: status shows "Pair active: buy @ <price>, sell @ <price>". Open Chart Trader on the same instrument — two native stop lines appear, one above and one below current price, spread = 20 points (rounded to 0.25 ticks).
5. Try Place again without cancelling. Expected: "Pair already active — cancel first."
6. Click Cancel. Expected: stub message (Task 8 wires real cancel). Manually cancel both orders via Chart Trader before moving on.

- [ ] **Step 3: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Implement PairManager.PlacePair with atomic submit

Reads last price (falling back to bid/ask mid), tick-rounds buy/sell stops,
submits both legs unmanaged, cancels first leg if second throws. Tags
orders with PAIRSTOP_<guid> for pair-identification."
```

---

## Task 8: CancelPair

**Goal:** Cancel both legs when the user clicks Cancel.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Replace `OnCancelClicked`**

```csharp
private void OnCancelClicked()
{
    lock (_sync)
    {
        if (_state == null) { _view.SetStatus("No active pair to cancel."); return; }
        var toCancel = new List<Order>();
        if (_state.Buy.OrderState == OrderState.Working  || _state.Buy.OrderState == OrderState.Accepted)  toCancel.Add(_state.Buy);
        if (_state.Sell.OrderState == OrderState.Working || _state.Sell.OrderState == OrderState.Accepted) toCancel.Add(_state.Sell);
        if (toCancel.Count > 0)
        {
            try { _subscribedAccount.Cancel(toCancel.ToArray()); }
            catch (Exception ex)
            {
                _view.SetStatus($"Cancel failed: {ex.Message}", isError: true);
                NinjaTrader.Code.Output.Process($"[PairedStops] Cancel failed: {ex}", PrintTo.OutputTab1);
            }
        }
        _state = null;
        _view.SetStatus("Pair cancelled.");
    }
}
```

Add `using System.Collections.Generic;` to the using block.

- [ ] **Step 2: Compile-verify in NT8 (Sim101)**

1. Copy file, compile. Expected: zero errors.
2. Place pair. Click Cancel. Expected: both orders disappear from Chart Trader within ~1s; status shows "Pair cancelled."
3. Click Cancel again. Expected: "No active pair to cancel."

- [ ] **Step 3: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Implement PairManager.CancelPair

Cancels whichever legs are still working/accepted, clears state, updates
status strip. No-op when no pair is active."
```

---

## Task 9: OCO on Fill

**Goal:** When one leg fills, cancel the partner. The trader's ATM takes over the filled position — the tool does nothing beyond cancelling the partner.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Extend `OnAccountOrderUpdate`**

Replace the empty body with:

```csharp
private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
{
    PairState snapshot;
    lock (_sync)
    {
        if (_state == null || !_state.Contains(e.Order)) return;
        snapshot = _state;
    }

    // OCO on fill.
    if (e.Order.OrderState == OrderState.Filled)
    {
        var partner = snapshot.PartnerOf(e.Order);
        if (partner != null &&
            (partner.OrderState == OrderState.Working || partner.OrderState == OrderState.Accepted))
        {
            try { _subscribedAccount.Cancel(new[] { partner }); }
            catch (Exception ex)
            { NinjaTrader.Code.Output.Process($"[PairedStops] OCO cancel failed: {ex}", PrintTo.OutputTab1); }
        }
        lock (_sync) { if (_state == snapshot) _state = null; }
        _view.SetStatus($"{(e.Order == snapshot.Buy ? "Buy" : "Sell")} stop filled — partner cancelled.");
        return;
    }
}
```

- [ ] **Step 2: Compile-verify in NT8 (Sim101)**

This one requires market movement. Use Market Replay or live Sim connection.

1. Connect Sim101 to a live data feed or a Market Replay session for NQ.
2. Set offset small enough that price will hit one leg within a reasonable timeframe (e.g., 3–5 points) — or force it by choosing a reference moment near a known breakout.
3. Place pair. Watch for one side to fill.
4. Expected: within ~1s of the fill, the partner disappears from Chart Trader. Status strip reads "Buy stop filled — partner cancelled." (or Sell).
5. Confirm only the filled-side position exists on the account.

- [ ] **Step 3: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Implement OCO on fill

When one leg reports OrderState.Filled, cancel the partner if still
working/accepted and clear pair state. Trader's ATM handles the filled
position from here."
```

---

## Task 10: Manual-Cancel & Rejection Propagation

**Goal:** If the user manually cancels one leg via Chart Trader (or if the exchange rejects one), cancel the partner and clear state.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Extend `OnAccountOrderUpdate`**

After the Filled block added in Task 9, add:

```csharp
    if (e.Order.OrderState == OrderState.Cancelled || e.Order.OrderState == OrderState.Rejected)
    {
        var partner = snapshot.PartnerOf(e.Order);
        if (partner != null &&
            (partner.OrderState == OrderState.Working || partner.OrderState == OrderState.Accepted))
        {
            try { _subscribedAccount.Cancel(new[] { partner }); }
            catch (Exception ex)
            { NinjaTrader.Code.Output.Process($"[PairedStops] Partner cancel-after-cancel failed: {ex}", PrintTo.OutputTab1); }
        }
        lock (_sync) { if (_state == snapshot) _state = null; }
        var reason = e.Order.OrderState == OrderState.Rejected
            ? $"Order rejected: {e.Order.ErrorCode} {e.Order.NativeError}"
            : "One leg cancelled — partner cancelled to preserve pair integrity.";
        _view.SetStatus(reason, isError: e.Order.OrderState == OrderState.Rejected);
        return;
    }
```

**Verify against `docs/nt8-api-notes.md`:** the exact error-property names (`ErrorCode`, `NativeError`) vary across NT8 versions. Match the notes.

- [ ] **Step 2: Compile-verify in NT8 (Sim101)**

1. Place pair. Via Chart Trader, right-click one of the stop lines → Cancel Order. Expected: the other disappears within ~1s; status shows manual-cancel message.
2. Place pair with an offset that the exchange will reject (e.g., 0.1 points — smaller than a tick after rounding may still go through; as an alternative, temporarily change tag prefix to something the broker's risk rules will reject, or submit with absurdly large quantity). Expected: both legs ultimately gone, status shows rejection reason.

- [ ] **Step 3: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Propagate manual cancel and rejection to partner leg

Any Cancelled/Rejected state on one leg triggers partner cancel (if still
working) and clears tracking state. Rejection surfaces broker error to
the status strip."
```

---

## Task 11: Drag-Sync with Ping-Pong Guard

**Goal:** When the trader drags one leg on the chart, move the partner to preserve the spread. Use both the `_programmatic` flag and a price-equality short-circuit to prevent ping-pong loops across thread boundaries.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Extend `OnAccountOrderUpdate` with the sync path**

Add before the Filled block (so drag events on Working orders are handled first):

```csharp
    // Drag-sync path. Only react to Working updates where the price has actually drifted.
    if (e.Order.OrderState == OrderState.Working)
    {
        double tickSize = e.Order.Instrument.MasterInstrument.TickSize;
        double newPartnerPx;
        Order partner;

        lock (_sync)
        {
            if (_programmatic) return;
            if (_state == null || !_state.Contains(e.Order)) return;

            partner = snapshot.PartnerOf(e.Order);
            if (partner == null) return;

            double expectedPartnerPx = e.Order == snapshot.Buy
                ? e.Order.StopPrice - snapshot.ExpectedSpread
                : e.Order.StopPrice + snapshot.ExpectedSpread;
            expectedPartnerPx = PriceMath.RoundToTick(expectedPartnerPx, tickSize);

            if (PriceMath.PricesEqual(partner.StopPrice, expectedPartnerPx, tickSize))
                return;   // partner is already where it should be — no drift to sync

            newPartnerPx = expectedPartnerPx;
            _programmatic = true;
        }

        try
        {
            _subscribedAccount.ChangeOrder(new[] { partner }, partner.Quantity, 0, newPartnerPx);
            if (_settings.AudibleDragSync) System.Media.SystemSounds.Asterisk.Play();
            _view.SetStatus($"Synced partner to {newPartnerPx}.");
        }
        catch (Exception ex)
        {
            _view.SetStatus($"Sync failed: {ex.Message}. Pair is now unlinked.", isError: true);
            NinjaTrader.Code.Output.Process($"[PairedStops] Sync failed: {ex}", PrintTo.OutputTab1);
            lock (_sync) { if (_state == snapshot) _state = null; }
        }
        finally
        {
            lock (_sync) { _programmatic = false; }
        }

        return;
    }
```

**Verify against `docs/nt8-api-notes.md`:** `Account.ChangeOrder` may take `(Order[], qty, limitPx, stopPx)` or `(Order, qty, limitPx, stopPx)` — adjust per the notes.

- [ ] **Step 2: Compile-verify in NT8 (Sim101)**

1. Place pair with offset 10 points (spread 20).
2. Grab the buy-stop line in Chart Trader and drag it up 5 points. Expected: sell-stop line follows up 5 points within ~1s. Spread remains 20.
3. Drag the sell-stop line down 3 points. Expected: buy-stop line follows down 3 points.
4. Drag the buy-stop rapidly back and forth (3-4 quick moves). Expected: no runaway order modifications; each drag settles cleanly.
5. Toggle Audible on. Drag a leg. Expected: an asterisk-style beep on each sync.
6. Cancel pair when done.

- [ ] **Step 3: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Implement drag-sync with ping-pong guard

Either leg's drag on the chart triggers a ChangeOrder on the partner to
preserve the placement spread. _programmatic flag suppresses synchronous
echoes; PricesEqual short-circuit handles the threaded echo race. Sync
failure leaves orders live but unlinks tracking. Optional beep on sync."
```

---

## Task 12: Session Reset at 18:00 ET

**Goal:** Clear tracking state at the CME session rollover so yesterday's dangling pair state doesn't affect today. Live broker-side orders are not cancelled — they survive the rollover.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Add session-reset timer to `PairManager`**

Add fields at the top of `PairManager`:

```csharp
private readonly System.Windows.Threading.DispatcherTimer _sessionTimer;
private DateTime _lastSessionTickEt = DateTime.MinValue;
```

In the constructor, after subscribing to account events, add:

```csharp
_sessionTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
_sessionTimer.Tick += OnSessionTimerTick;
_sessionTimer.Start();
_lastSessionTickEt = NowEt();
```

Add helper methods on `PairManager`:

```csharp
private static DateTime NowEt()
{
    var etZone = TimeZoneInfo.FindSystemTimeZoneById(
        Environment.OSVersion.Platform == PlatformID.Unix ? "America/New_York" : "Eastern Standard Time");
    return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
}

private void OnSessionTimerTick(object sender, EventArgs e)
{
    var nowEt = NowEt();
    var todayBoundary = nowEt.Date.AddHours(18);

    if (_lastSessionTickEt < todayBoundary && nowEt >= todayBoundary)
    {
        lock (_sync) { _state = null; }
        _view.SetStatus("Session rollover — tracking state cleared.");
    }
    _lastSessionTickEt = nowEt;
}
```

In `Dispose`, add:

```csharp
_sessionTimer.Stop();
_sessionTimer.Tick -= OnSessionTimerTick;
```

- [ ] **Step 2: Compile-verify in NT8**

1. Temporarily change `nowEt.Date.AddHours(18)` to `nowEt.Date.AddHours(nowEt.Hour).AddMinutes(nowEt.Minute + 2)` so you don't have to wait until 6 PM. (Revert after testing.)
2. Place a pair. Wait past the fake boundary.
3. Expected: status shows "Session rollover — tracking state cleared." Live broker-side orders remain open in Chart Trader (spec-intentional — manually cancel them).
4. Click Cancel. Expected: "No active pair to cancel."
5. Revert the boundary change back to `AddHours(18)`. Recompile. Confirm the real 18:00 ET logic is intact.

- [ ] **Step 3: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Add 18:00 ET session-reset timer

DispatcherTimer ticks every minute; when ET wall-clock crosses 18:00,
tracking state is cleared. Live broker-side orders are not cancelled
(they survive the CME session rollover). Disposed cleanly with the tab."
```

---

## Task 13: Ghost Preview — Panel Confirmation Strip

**Goal:** When the Ghost Preview toggle is on, clicking Place shows the inline confirmation strip in the tab (numbers only) and waits for Confirm/Cancel. The chart lines come in Task 14.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Refactor `OnPlaceClicked` into compute + commit**

Split the existing `OnPlaceClicked` body. Extract the "computed prices → SubmitPair" call into a pending-state helper:

```csharp
private (Instrument instrument, int qty, double buyPx, double sellPx)? _pendingPair;

private void OnPlaceClicked()
{
    lock (_sync)
    {
        if (_state != null) { _view.SetStatus("Pair already active — cancel first.", isError: true); return; }
        if (_pendingPair != null) { _view.SetStatus("Preview already pending — confirm or cancel.", isError: true); return; }
        if (_subscribedAccount == null) { _view.SetStatus("No account selected.", isError: true); return; }

        var instrument = Instrument.GetInstrument(_view.InstrumentBox.Text.Trim());
        if (instrument == null || instrument.MasterInstrument == null)
        { _view.SetStatus($"Instrument '{_view.InstrumentBox.Text}' not found.", isError: true); return; }

        if (!double.TryParse(_view.OffsetBox.Text, out var offset) || offset <= 0)
        { _view.SetStatus("Invalid offset.", isError: true); return; }

        if (!int.TryParse(_view.QuantityBox.Text, out var qty) || qty <= 0)
        { _view.SetStatus("Invalid quantity.", isError: true); return; }

        var tickSize = instrument.MasterInstrument.TickSize;
        var last = instrument.MarketData?.Last?.Price ?? 0.0;
        if (last <= 0)
        {
            var bid = instrument.MarketData?.Bid?.Price ?? 0.0;
            var ask = instrument.MarketData?.Ask?.Price ?? 0.0;
            if (bid > 0 && ask > 0) last = (bid + ask) * 0.5;
            else { _view.SetStatus("No market data — cannot compute prices.", isError: true); return; }
        }

        var (buyPx, sellPx) = PriceMath.ComputePair(last, offset, tickSize);
        if (buyPx <= sellPx) { _view.SetStatus($"Invalid prices: buy {buyPx} <= sell {sellPx}.", isError: true); return; }

        if (_settings.GhostPreviewEnabled)
        {
            _pendingPair = (instrument, qty, buyPx, sellPx);
            ShowPreviewStrip(buyPx, sellPx);
            return;
        }

        SubmitPair(instrument, qty, buyPx, sellPx);
    }
}
```

- [ ] **Step 2: Add preview-strip helpers**

```csharp
private void ShowPreviewStrip(double buyPx, double sellPx)
{
    _view.Dispatcher.BeginInvoke(new Action(() =>
    {
        _view.PreviewText.Text = $"Place buy stop @ {buyPx}, sell stop @ {sellPx} — ";
        _view.PreviewStrip.Visibility = Visibility.Visible;
    }));
}

private void HidePreviewStrip()
{
    _view.Dispatcher.BeginInvoke(new Action(() =>
    {
        _view.PreviewStrip.Visibility = Visibility.Collapsed;
        _view.PreviewText.Text = "";
    }));
}
```

- [ ] **Step 3: Wire Confirm/Cancel buttons**

In the `PairManager` constructor, after wiring `PlaceButton`/`CancelButton`:

```csharp
_view.PreviewConfirmButton.Click += (s, e) => OnPreviewConfirm();
_view.PreviewCancelButton.Click  += (s, e) => OnPreviewCancel();
```

Add the two handlers:

```csharp
private void OnPreviewConfirm()
{
    (Instrument instrument, int qty, double buyPx, double sellPx)? pending;
    lock (_sync) { pending = _pendingPair; _pendingPair = null; }
    HidePreviewStrip();
    if (pending == null) return;
    lock (_sync) { SubmitPair(pending.Value.instrument, pending.Value.qty, pending.Value.buyPx, pending.Value.sellPx); }
}

private void OnPreviewCancel()
{
    lock (_sync) { _pendingPair = null; }
    HidePreviewStrip();
    _view.SetStatus("Preview cancelled.");
}
```

- [ ] **Step 4: Compile-verify in NT8**

1. Compile. Expected: zero errors.
2. Toggle Ghost Preview OFF. Click Place. Expected: orders submit immediately (Task 7 behavior).
3. Cancel that pair.
4. Toggle Ghost Preview ON. Click Place. Expected: preview strip appears showing "Place buy stop @ X, sell stop @ Y —" with Confirm/Cancel buttons. No orders submitted yet.
5. Click Confirm. Expected: strip hides, orders submit, status shows active pair.
6. Cancel pair. Click Place again. Click the preview Cancel. Expected: strip hides, no orders submitted, status "Preview cancelled."

- [ ] **Step 5: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Add inline ghost-preview confirmation strip

When ghost-preview is enabled, Place computes prices and shows an inline
Confirm/Cancel strip with the proposed prices. Confirm commits; Cancel
aborts without submitting. One pending preview at a time."
```

---

## Task 14: Ghost Preview — Chart Draw Objects

**Goal:** When preview is on AND a chart exists for the instrument, also draw two dashed horizontal lines on the chart at the proposed prices. Remove them on Confirm or Cancel. Silent no-op if no chart is open.

**Files:**
- Modify: `~/ORB-NT8-Orders-Addon/PairedStopsAddOn.cs`

- [ ] **Step 1: Add `GhostPreview` helper**

```csharp
internal sealed class GhostPreview
{
    private readonly PairedStopsSettings _settings;
    private NinjaTrader.NinjaScript.DrawingTools.HorizontalLine _buyLine;
    private NinjaTrader.NinjaScript.DrawingTools.HorizontalLine _sellLine;
    private NinjaTrader.Gui.Chart.ChartControl _chart;

    public GhostPreview(PairedStopsSettings settings) { _settings = settings; }

    public void Show(string instrumentName, double buyPx, double sellPx)
    {
        Hide(); // defensive

        _chart = FindChart(instrumentName);
        if (_chart == null) return;   // silent — panel confirmation carries the UX

        var buyBrush  = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(_settings.PreviewBuyColorArgb);
        var sellBrush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter().ConvertFromString(_settings.PreviewSellColorArgb);

        _chart.Dispatcher.BeginInvoke(new Action(() =>
        {
            _buyLine  = NinjaTrader.NinjaScript.DrawingTools.Draw.HorizontalLine(_chart.ChartPanel.OwnerChart, "PS_GHOST_BUY",  buyPx,  buyBrush);
            _sellLine = NinjaTrader.NinjaScript.DrawingTools.Draw.HorizontalLine(_chart.ChartPanel.OwnerChart, "PS_GHOST_SELL", sellPx, sellBrush);
            // Style: dashed, configured width.
            if (_buyLine  != null) { _buyLine.Stroke.DashStyleHelper  = NinjaTrader.Gui.DashStyleHelper.Dash; _buyLine.Stroke.Width  = (float)_settings.PreviewLineWidth; }
            if (_sellLine != null) { _sellLine.Stroke.DashStyleHelper = NinjaTrader.Gui.DashStyleHelper.Dash; _sellLine.Stroke.Width = (float)_settings.PreviewLineWidth; }
        }));
    }

    public void Hide()
    {
        if (_chart == null) return;
        _chart.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (_buyLine  != null) NinjaTrader.NinjaScript.DrawingTools.Draw.Remove(_chart.ChartPanel.OwnerChart, _buyLine.Tag);
            if (_sellLine != null) NinjaTrader.NinjaScript.DrawingTools.Draw.Remove(_chart.ChartPanel.OwnerChart, _sellLine.Tag);
            _buyLine = null; _sellLine = null;
        }));
        _chart = null;
    }

    private static NinjaTrader.Gui.Chart.ChartControl FindChart(string instrumentName)
    {
        // Enumerate all open windows, find those hosting a ChartControl whose instrument matches.
        // Prefer the currently focused one; fall back to the first.
        NinjaTrader.Gui.Chart.ChartControl match = null;
        NinjaTrader.Gui.Chart.ChartControl focused = null;

        Application.Current.Dispatcher.Invoke(() =>
        {
            foreach (Window w in Application.Current.Windows)
            {
                foreach (var chart in FindVisualChildren<NinjaTrader.Gui.Chart.ChartControl>(w))
                {
                    if (chart.Instrument != null && chart.Instrument.FullName == instrumentName)
                    {
                        match = match ?? chart;
                        if (w.IsActive) focused = chart;
                    }
                }
            }
        });

        return focused ?? match;
    }

    private static System.Collections.Generic.IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent == null) yield break;
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is T hit) yield return hit;
            foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
        }
    }
}
```

**Verify against `docs/nt8-api-notes.md`:** `Draw.HorizontalLine(...)` is normally called from a Strategy/Indicator context; calling it from an AddOn requires that the first argument is an `IChartBars`-backed owner. If the exact overload in the reference AddOns differs (some use `Draw.HorizontalLine(chart, tag, price, brush)` with different positional args, or require a `NinjaScriptBase` receiver via reflection), substitute the correct form. If no clean API is available, fall back to adding a simple `System.Windows.Shapes.Line` overlay to `chart.ChartPanel` — that path always works.

- [ ] **Step 2: Integrate `GhostPreview` with `PairManager`**

Add a field:

```csharp
private readonly GhostPreview _ghost;
```

In the constructor:

```csharp
_ghost = new GhostPreview(_settings);
```

Update `ShowPreviewStrip` to also show chart lines:

```csharp
private void ShowPreviewStrip(double buyPx, double sellPx)
{
    var instName = _view.InstrumentBox.Text.Trim();
    _ghost.Show(instName, buyPx, sellPx);
    _view.Dispatcher.BeginInvoke(new Action(() =>
    {
        _view.PreviewText.Text = $"Place buy stop @ {buyPx}, sell stop @ {sellPx} — ";
        _view.PreviewStrip.Visibility = Visibility.Visible;
    }));
}

private void HidePreviewStrip()
{
    _ghost.Hide();
    _view.Dispatcher.BeginInvoke(new Action(() =>
    {
        _view.PreviewStrip.Visibility = Visibility.Collapsed;
        _view.PreviewText.Text = "";
    }));
}
```

- [ ] **Step 3: Compile-verify in NT8 (Sim101)**

1. Compile. Expected: zero errors.
2. Open NT chart for NQ front-month. Toggle Ghost Preview ON in the AddOn tab.
3. Click Place. Expected: two dashed horizontal lines appear on the chart (green above, red below current price); inline strip also appears.
4. Click Confirm. Expected: dashed lines disappear; real stop orders submit; Chart Trader shows the two native order lines.
5. Cancel pair. Click Place again. Click the preview Cancel. Expected: dashed lines disappear; no orders submitted.
6. Close all NQ charts. Click Place (still preview-on). Expected: inline strip appears; no chart lines (no chart available); confirming still submits normally.

- [ ] **Step 4: Commit**

```bash
git add PairedStopsAddOn.cs
git commit -m "Add chart draw-object overlay for ghost preview

Finds a ChartControl matching the instrument (focused preferred, else
first), adds two dashed HorizontalLine draw objects at the proposed
prices, removes them on confirm/cancel. Silent no-op when no chart is open."
```

---

## Task 15: README

**Goal:** Write the `README.md` the spec calls for: install steps, opening the tool, settings overview, known limitations, and the Sim101 verification checklist.

**Files:**
- Create: `~/ORB-NT8-Orders-Addon/README.md`

- [ ] **Step 1: Write the README**

```markdown
# ORB NT8 Orders AddOn — Paired Stops

NinjaTrader 8 AddOn that places a linked pair of stop orders (one buy above market, one sell below) with one click, keeps them synchronized when you drag either one on the chart, and auto-cancels the partner when one fills.

Trader-assistive tool — every action is initiated by the trader. Not an autonomous strategy.

## Install

1. Copy `PairedStopsAddOn.cs` into `Documents\NinjaTrader 8\bin\Custom\AddOns\`.
2. Open the NinjaScript Editor in NT8.
3. Press **F5** to compile. Confirm zero errors.
4. Restart NT8 if the new menu item does not appear.

## Open

From the Control Center: **New → Paired Stops**. A new window opens with the AddOn's tab.

## Settings

| Setting | Default | Notes |
|---|---|---|
| Account | first available | Dropdown populated from NT accounts. |
| Instrument | `NQ 06-26` | Full NT instrument name. |
| Offset (points) | 10.0 | Applied as ± from last traded price. Tick-rounded. |
| Quantity | 1 | Contracts per leg. |
| Ghost preview | Off | When on, shows dashed chart lines + inline Confirm/Cancel before submitting. |
| Preview buy line color | Green | Applied to ghost preview only. |
| Preview sell line color | Red | Applied to ghost preview only. |
| Preview line width | 2 | |
| Preview dash style | Dashed | |
| Pair tag prefix | `PAIRSTOP_` | Used to identify paired orders internally. |
| Audible drag-sync | Off | Plays a short sound each time the tool auto-syncs the partner. |

Settings persist to `Documents\NinjaTrader 8\bin\Custom\AddOns\PairedStops\settings.json`.

## What it does

- **Place:** Reads last traded price (falls back to bid/ask mid if no last), computes `buy = last + offset`, `sell = last - offset`, tick-rounds both, submits atomically. If one leg fails, the other is cancelled.
- **Drag-sync:** When you drag either leg on the chart, the partner moves to preserve the original spread.
- **OCO on fill:** When one leg fills, the partner is cancelled. Your ATM strategy handles the filled position from there — the AddOn does not submit TP/SL.
- **Manual cancel:** If you cancel one leg from Chart Trader, the partner is cancelled too.
- **Session reset:** At 18:00 ET the AddOn clears its internal tracking state. Live broker-side orders survive the rollover; the AddOn simply stops treating yesterday's pair as its own.

## What it does NOT do

- Does not place take-profit or stop-loss orders (ATM does that).
- Does not decide when to place orders (you click the button).
- Does not close positions.
- Does not run without your supervision.

## Verification in Sim101

Before deploying to a funded account, run through these in a Sim101 account:

1. Place pair on NQ → drag buy stop up 5 points → sell stop follows up 5 points (spread preserved).
2. Place pair on NQ → drag sell stop down 3 points → buy stop follows down 3 points.
3. Let price hit the buy stop → sell stop cancels automatically within ~1s.
4. Place pair → manually cancel one order via Chart Trader → partner cancels.
5. Tick-size rounding: offset 10.1 → order prices snap to 0.25 increments.
6. Ghost preview on → Place → lines appear → Confirm → orders submit. Place → Cancel → nothing submitted.
7. Run across session rollover (18:00 ET) → tracking state clears.

## Known limitations

- Ghost preview requires an open chart for the instrument to show chart lines; the inline panel confirmation always works.
- If NT restarts while a pair is active, the AddOn does not attempt to re-adopt the orders. They remain on the broker, tagged `PAIRSTOP_<guid>_BUY|SELL` — cancel them manually via Chart Trader.
- Live order lines use NT's native Chart Trader rendering. The line-color/style settings apply to the ghost preview only.
- Account disconnects clear tracking state on reconnect. Live orders survive the disconnect broker-side.

## Prop firm compliance

Designed as a trader-assistive helper. You initiate every action (button click, order drag); the AddOn only syncs two already-user-placed orders. It does not enter, exit, or manage positions autonomously.

Verify compatibility with your prop firm's rules before using — do not use with prop firms that prohibit any form of automation.
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "Add README with install, settings, and Sim101 verification checklist

Covers install steps, the New > Paired Stops menu path, all 10 settings,
known limitations, the seven verification scenarios from the spec, and a
prop-firm compliance note."
```

---

## Task 16: Final End-to-End Sim Walkthrough

**Goal:** Run the full seven-step verification from the spec in Sim101 on a Windows NT8 machine. This is a checkpoint, not a coding task — no code changes unless a bug is found.

- [ ] **Step 1: Walk through all seven verifications**

On the Windows NT8 machine, with latest `main` pulled and `PairedStopsAddOn.cs` deployed + compiled:

- [ ] 1. Place pair on NQ → drag buy stop up 5 points → confirm sell stop follows up 5 points (spread preserved).
- [ ] 2. Place pair on NQ → drag sell stop down 3 points → confirm buy stop follows down 3 points.
- [ ] 3. Let price hit the buy stop (use Market Replay if needed) → confirm sell stop cancels automatically within 1 second.
- [ ] 4. Place pair → manually cancel one order via Chart Trader → confirm the partner cancels.
- [ ] 5. Tick-size rounding: offset 10.1 points → confirm order prices snap to 0.25 increments.
- [ ] 6. Ghost preview on: Place → lines appear → Confirm → orders submit. Place → Cancel → nothing submitted.
- [ ] 7. Run across session rollover (18:00 ET) → confirm pair state clears.

- [ ] **Step 2: Fix any bugs found**

For each failed scenario, open an issue-style commit:

```bash
git commit -m "Fix <bug>: <short description>

<What failed in which verification step, and what the fix was.>"
```

- [ ] **Step 3: Final commit + tag**

Once all seven scenarios pass cleanly:

```bash
git tag -a v0.1.0 -m "Initial release: Paired Stops AddOn

All seven Sim101 verifications passing. See README for install."
git push origin main --tags
```

---

## Self-Review

_(plan author's checklist, done before handoff)_

**Spec coverage:**
- Pair lifecycle (place, track, fill, reset) → Tasks 7, 11, 9, 12 ✓
- User controls (Place, Cancel, settings, status) → Tasks 4, 7, 8 ✓
- Ghost preview (toggle, chart lines, panel) → Tasks 13, 14 ✓
- All 10 config settings → Task 3 (model) + Task 4 (UI binding) ✓
- Tick rounding → Task 5 + used in Tasks 7, 11 ✓
- Ping-pong guard → Task 11 ✓
- Atomic placement with rollback → Task 7 ✓
- Pair ID tagging → Task 7 ✓
- OCO on fill → Task 9 ✓
- Session reset at 18:00 ET → Task 12 ✓
- Manual cancel / rejection propagation → Task 10 ✓
- Tabbed NT window UI shell → Task 2 ✓
- Settings persistence → Task 3 ✓
- Live orders use native Chart Trader (no custom render) → by omission — no live-render task ✓
- Non-goals (no TP/SL, no auto-close) → respected by omission ✓
- README with install + verification → Task 15 ✓
- Manual Sim101 verification → Task 16 ✓

**Placeholder scan:** No `TBD`/`TODO` in code blocks. `<verify>` tags are deliberate — they point the implementer at `docs/nt8-api-notes.md` (Task 1 deliverable) for the specific NT8 API shapes. That's the right place for version-sensitive details.

**Type consistency:** `PairState`, `PairManager`, `PairedStopsSettings`, `SettingsStore`, `PairedStopsView`, `PairedStopsTab`, `PairedStopsAddOn`, `PairedStopsTabFactory`, `GhostPreview`, `PriceMath` — all used with stable names across tasks. Methods: `SubmitPair`, `ComputePair`, `RoundToTick`, `PricesEqual`, `ResubscribeToSelectedAccount`, `OnAccountOrderUpdate`, `OnPlaceClicked`, `OnCancelClicked`, `ShowPreviewStrip`/`HidePreviewStrip`, `OnPreviewConfirm`/`OnPreviewCancel`, `NowEt`, `OnSessionTimerTick` — consistent across tasks.
