// =============================================================================
// Paired Stops AddOn for NinjaTrader 8
// -----------------------------------------------------------------------------
// Places a linked pair of stop orders (buy above / sell below market), keeps
// them synchronized when either is dragged on the chart, and auto-cancels the
// partner when one fills.
//
// Design spec:  docs/superpowers/specs/2026-04-20-paired-stops-addon-design.md
// Impl plan:    docs/superpowers/plans/2026-04-20-paired-stops-addon.md
//
// Trader-assistive tool. Every action is initiated by the trader; the tool only
// handles mechanical sync. Does NOT place TP/SL, does NOT close positions,
// does NOT trade autonomously.
// =============================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NinjaTrader.Cbi;
using NinjaTrader.Code;
using NinjaTrader.Core;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Tools;
using NinjaTrader.NinjaScript;
#endregion

namespace NinjaTrader.NinjaScript.AddOns.PairedStops
{
    // -------------------------------------------------------------------------
    // Settings model + persistence
    // -------------------------------------------------------------------------
    public class PairedStopsSettings
    {
        public double OffsetPoints { get; set; } = 10.0;
        public int    Quantity     { get; set; } = 1;

        public string AccountName    { get; set; } = "";           // empty = auto-pick first
        public string InstrumentName { get; set; } = "NQ 06-26";   // user overrides per session

        public bool GhostPreviewEnabled { get; set; } = false;

        // Colors stored as ARGB hex to keep JSON primitive.
        public string PreviewBuyColorArgb  { get; set; } = "#FF00C853";
        public string PreviewSellColorArgb { get; set; } = "#FFD50000";
        public double PreviewLineWidth     { get; set; } = 2.0;
        public string PreviewDashStyle     { get; set; } = "Dashed";

        public string PairTagPrefix { get; set; } = "PAIRSTOP_";

        public bool AudibleDragSync { get; set; } = false;
    }

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
                return SimpleJson.Deserialize(json) ?? new PairedStopsSettings();
            }
            catch (Exception ex)
            {
                Output.Process($"[PairedStops] Settings load failed: {ex.Message}. Using defaults.",
                               PrintTo.OutputTab1);
                return new PairedStopsSettings();
            }
        }

        public static void Save(PairedStopsSettings settings)
        {
            try
            {
                System.IO.Directory.CreateDirectory(SettingsDir);
                System.IO.File.WriteAllText(SettingsPath, SimpleJson.Serialize(settings));
            }
            catch (Exception ex)
            {
                Output.Process($"[PairedStops] Settings save failed: {ex.Message}", PrintTo.OutputTab1);
            }
        }
    }

    // Minimal JSON serializer for PairedStopsSettings — avoids taking a dependency on
    // System.Text.Json or Newtonsoft, neither of which are guaranteed-available in the
    // NT8 custom AddOn assembly. The schema is fixed and small, so hand-rolled is fine.
    internal static class SimpleJson
    {
        public static string Serialize(PairedStopsSettings s)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("{\n");
            AppendNum(sb, "OffsetPoints", s.OffsetPoints, isLast: false);
            AppendNum(sb, "Quantity", s.Quantity, isLast: false);
            AppendStr(sb, "AccountName", s.AccountName, isLast: false);
            AppendStr(sb, "InstrumentName", s.InstrumentName, isLast: false);
            AppendBool(sb, "GhostPreviewEnabled", s.GhostPreviewEnabled, isLast: false);
            AppendStr(sb, "PreviewBuyColorArgb", s.PreviewBuyColorArgb, isLast: false);
            AppendStr(sb, "PreviewSellColorArgb", s.PreviewSellColorArgb, isLast: false);
            AppendNum(sb, "PreviewLineWidth", s.PreviewLineWidth, isLast: false);
            AppendStr(sb, "PreviewDashStyle", s.PreviewDashStyle, isLast: false);
            AppendStr(sb, "PairTagPrefix", s.PairTagPrefix, isLast: false);
            AppendBool(sb, "AudibleDragSync", s.AudibleDragSync, isLast: true);
            sb.Append("}\n");
            return sb.ToString();
        }

        public static PairedStopsSettings Deserialize(string json)
        {
            if (string.IsNullOrWhiteSpace(json)) return new PairedStopsSettings();
            var s = new PairedStopsSettings();
            foreach (var (k, v) in ExtractFields(json))
            {
                switch (k)
                {
                    case "OffsetPoints":        if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var o)) s.OffsetPoints = o; break;
                    case "Quantity":            if (int.TryParse(v, out var q)) s.Quantity = q; break;
                    case "AccountName":         s.AccountName = Unquote(v); break;
                    case "InstrumentName":      s.InstrumentName = Unquote(v); break;
                    case "GhostPreviewEnabled": s.GhostPreviewEnabled = (v == "true"); break;
                    case "PreviewBuyColorArgb": s.PreviewBuyColorArgb = Unquote(v); break;
                    case "PreviewSellColorArgb":s.PreviewSellColorArgb = Unquote(v); break;
                    case "PreviewLineWidth":    if (double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)) s.PreviewLineWidth = w; break;
                    case "PreviewDashStyle":    s.PreviewDashStyle = Unquote(v); break;
                    case "PairTagPrefix":       s.PairTagPrefix = Unquote(v); break;
                    case "AudibleDragSync":     s.AudibleDragSync = (v == "true"); break;
                }
            }
            return s;
        }

        private static void AppendStr (System.Text.StringBuilder sb, string k, string v, bool isLast) => sb.Append("  \"").Append(k).Append("\": \"").Append(Escape(v ?? "")).Append('"').Append(isLast ? "\n" : ",\n");
        private static void AppendNum (System.Text.StringBuilder sb, string k, double v, bool isLast) => sb.Append("  \"").Append(k).Append("\": ").Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(isLast ? "\n" : ",\n");
        private static void AppendNum (System.Text.StringBuilder sb, string k, int    v, bool isLast) => sb.Append("  \"").Append(k).Append("\": ").Append(v.ToString(System.Globalization.CultureInfo.InvariantCulture)).Append(isLast ? "\n" : ",\n");
        private static void AppendBool(System.Text.StringBuilder sb, string k, bool   v, bool isLast) => sb.Append("  \"").Append(k).Append("\": ").Append(v ? "true" : "false").Append(isLast ? "\n" : ",\n");
        private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        private static string Unquote(string v) { v = v.Trim(); if (v.StartsWith("\"") && v.EndsWith("\"")) v = v.Substring(1, v.Length - 2); return v.Replace("\\\"", "\"").Replace("\\\\", "\\"); }

        private static IEnumerable<(string key, string value)> ExtractFields(string json)
        {
            // Dumb but sufficient for our flat schema: split by top-level commas/newlines, then key/value on first ':'.
            int depth = 0, i = 0;
            bool inString = false;
            var chunks = new List<string>();
            var current = new System.Text.StringBuilder();
            for (; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '"' && (i == 0 || json[i - 1] != '\\')) inString = !inString;
                if (!inString)
                {
                    if (c == '{' || c == '[') depth++;
                    else if (c == '}' || c == ']') depth--;
                    if ((c == ',' || c == '\n') && depth <= 1)
                    {
                        if (current.Length > 0) chunks.Add(current.ToString()); current.Clear(); continue;
                    }
                }
                current.Append(c);
            }
            if (current.Length > 0) chunks.Add(current.ToString());

            foreach (var raw in chunks)
            {
                var trimmed = raw.Trim().TrimStart('{').TrimEnd('}').Trim();
                if (string.IsNullOrEmpty(trimmed)) continue;
                int colon = trimmed.IndexOf(':');
                if (colon < 0) continue;
                var key   = trimmed.Substring(0, colon).Trim().Trim('"');
                var value = trimmed.Substring(colon + 1).Trim();
                yield return (key, value);
            }
        }
    }

    // -------------------------------------------------------------------------
    // Pure arithmetic helpers — no NT dependencies, trivially reasoned about
    // -------------------------------------------------------------------------
    public static class PriceMath
    {
        /// <summary>Rounds a raw price to the nearest tick-size multiple.</summary>
        public static double RoundToTick(double price, double tickSize)
        {
            if (tickSize <= 0)
                throw new ArgumentOutOfRangeException(nameof(tickSize), "Tick size must be positive.");
            return Math.Round(price / tickSize, MidpointRounding.AwayFromZero) * tickSize;
        }

        /// <summary>Computes buy/sell stop prices around a reference, both tick-rounded.</summary>
        public static void ComputePair(double reference, double offsetPoints, double tickSize,
                                       out double buyStop, out double sellStop)
        {
            buyStop  = RoundToTick(reference + offsetPoints, tickSize);
            sellStop = RoundToTick(reference - offsetPoints, tickSize);
        }

        /// <summary>True when two order prices agree within half a tick.</summary>
        public static bool PricesEqual(double a, double b, double tickSize)
            => Math.Abs(a - b) < tickSize * 0.5;
    }

    // -------------------------------------------------------------------------
    // AddOn entry point
    // -------------------------------------------------------------------------
    public class PairedStopsAddOn : AddOnBase
    {
        private const string MenuHeader = "Paired Stops";
        private NTMenuItem _menuItem;
        private Window _ownerWindow;

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Name = "Paired Stops";
            }
        }

        protected override void OnWindowCreated(Window window)
        {
            // The Control Center is the main NT window; detect by type name rather
            // than by type import to stay independent of internal namespace moves.
            if (window == null || window.GetType().Name != "ControlCenter") return;

            _ownerWindow = window;
            var newMenu = FindNewMenu(window);
            if (newMenu == null) return;

            _menuItem = new NTMenuItem
            {
                Header = MenuHeader,
                Style  = Application.Current.TryFindResource("MainMenuItem") as Style
            };
            _menuItem.Click += OnMenuClick;
            newMenu.Items.Add(_menuItem);
        }

        protected override void OnWindowDestroyed(Window window)
        {
            if (window != _ownerWindow) return;
            if (_menuItem != null)
            {
                _menuItem.Click -= OnMenuClick;
                var newMenu = FindNewMenu(window);
                newMenu?.Items.Remove(_menuItem);
                _menuItem = null;
            }
            _ownerWindow = null;
        }

        private static MenuItem FindNewMenu(Window window)
        {
            foreach (var menu in FindVisualChildren<Menu>(window))
            {
                var candidate = menu.Items.OfType<MenuItem>().FirstOrDefault(m =>
                    (m.Header?.ToString() ?? "") == "New");
                if (candidate != null) return candidate;
            }
            return null;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) yield return hit;
                foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
            }
        }

        private static void OnMenuClick(object sender, RoutedEventArgs e)
        {
            // Open a plain NTWindow hosting the view directly — simpler and
            // more portable than registering an NTTabPage/INTTabFactory, whose
            // abstract member shapes vary across NT8 builds.
            var settings = SettingsStore.Load();
            var view     = new PairedStopsView(settings);
            var manager  = new PairManager(view, settings);

            var window = new NTWindow
            {
                Caption = "Paired Stops",
                Content = view,
                Width   = 440,
                Height  = 400
            };
            window.Closed += (s, args) =>
            {
                manager.Dispose();
                SettingsStore.Save(settings);
            };
            window.Show();
        }
    }

    // -------------------------------------------------------------------------
    // GhostPreview — optional chart-overlay lines for the Confirm/Cancel step
    // -------------------------------------------------------------------------
    //
    // Draws two dashed horizontal lines on a chart showing the instrument, at
    // the proposed buy / sell stop prices. Uses plain WPF Line shapes over the
    // chart's visual tree rather than NT's Draw.HorizontalLine API — the
    // Draw.* methods require a NinjaScriptBase context that AddOns don't have,
    // and reflecting one out is more fragile than just overlaying Lines.
    //
    // This overlay is static: it does not update as the user zooms or scrolls.
    // Fine for a short-lived preview (the user is about to click Confirm or
    // Cancel within a few seconds). Silent no-op if no matching chart is open.
    //
    internal sealed class GhostPreview
    {
        private readonly PairedStopsSettings _settings;

        private Canvas _overlay;
        private NinjaTrader.Gui.Chart.ChartControl _chart;
        private System.Windows.Shapes.Line _buyLine;
        private System.Windows.Shapes.Line _sellLine;

        public GhostPreview(PairedStopsSettings settings) { _settings = settings; }

        public void Show(string instrumentName, double buyPx, double sellPx)
        {
            Hide(); // defensive — clears any stale lines

            _chart = FindChart(instrumentName);
            if (_chart == null) return; // silent — panel confirmation carries the UX

            try
            {
                _chart.Dispatcher.Invoke(() =>
                {
                    _overlay = FindVisualChildren<Canvas>(_chart).FirstOrDefault();
                    if (_overlay == null) return;

                    var buyBrush  = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter()
                                       .ConvertFromString(_settings.PreviewBuyColorArgb);
                    var sellBrush = (System.Windows.Media.SolidColorBrush)new System.Windows.Media.BrushConverter()
                                       .ConvertFromString(_settings.PreviewSellColorArgb);

                    _buyLine  = BuildLine(buyPx,  buyBrush);
                    _sellLine = BuildLine(sellPx, sellBrush);
                    if (_buyLine  != null) _overlay.Children.Add(_buyLine);
                    if (_sellLine != null) _overlay.Children.Add(_sellLine);
                });
            }
            catch (Exception ex)
            {
                Output.Process($"[PairedStops] Ghost preview render failed: {ex.Message}", PrintTo.OutputTab1);
            }
        }

        public void Hide()
        {
            if (_chart == null || _overlay == null) { _chart = null; _overlay = null; return; }
            try
            {
                _chart.Dispatcher.Invoke(() =>
                {
                    if (_buyLine  != null) _overlay.Children.Remove(_buyLine);
                    if (_sellLine != null) _overlay.Children.Remove(_sellLine);
                    _buyLine  = null;
                    _sellLine = null;
                });
            }
            catch { /* chart was closed; nothing to clean up */ }
            _chart   = null;
            _overlay = null;
        }

        private System.Windows.Shapes.Line BuildLine(double price, System.Windows.Media.Brush brush)
        {
            double? y = YForPrice(price);
            if (y == null) return null;

            return new System.Windows.Shapes.Line
            {
                X1                = 0,
                X2                = _overlay.ActualWidth,
                Y1                = y.Value,
                Y2                = y.Value,
                Stroke            = brush,
                StrokeThickness   = _settings.PreviewLineWidth,
                StrokeDashArray   = new System.Windows.Media.DoubleCollection(new[] { 6.0, 4.0 }),
                IsHitTestVisible  = false,
                SnapsToDevicePixels = true
            };
        }

        private double? YForPrice(double price)
        {
            try
            {
                // ChartScale is the price-to-pixel mapping on the chart's active panel.
                // Accessed via ChartPanel.ChartScale in most NT8 builds.
                var chartScaleProp = _chart.GetType().GetProperty("ChartScale");
                var chartScale     = chartScaleProp?.GetValue(_chart);
                if (chartScale == null) return null;

                var getY = chartScale.GetType().GetMethod("GetYByValue", new[] { typeof(double) });
                if (getY == null) return null;

                var y = getY.Invoke(chartScale, new object[] { price });
                return y is float f ? (double?)f : y is double d ? (double?)d : null;
            }
            catch
            {
                return null;
            }
        }

        private static NinjaTrader.Gui.Chart.ChartControl FindChart(string instrumentName)
        {
            NinjaTrader.Gui.Chart.ChartControl match   = null;
            NinjaTrader.Gui.Chart.ChartControl focused = null;

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (Window w in Application.Current.Windows)
                {
                    foreach (var chart in FindVisualChildren<NinjaTrader.Gui.Chart.ChartControl>(w))
                    {
                        if (chart.Instrument != null && chart.Instrument.FullName == instrumentName)
                        {
                            if (match == null) match = chart;
                            if (w.IsActive)   focused = chart;
                        }
                    }
                }
            });

            return focused ?? match;
        }

        private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
        {
            if (parent == null) yield break;
            int count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T hit) yield return hit;
                foreach (var nested in FindVisualChildren<T>(child)) yield return nested;
            }
        }
    }

    // -------------------------------------------------------------------------
    // PairState + PairManager
    // -------------------------------------------------------------------------
    internal sealed class PairState
    {
        public Guid     PairId         { get; }
        public Order    Buy            { get; }
        public Order    Sell           { get; }
        public double   ExpectedSpread { get; }   // buyPx - sellPx at placement
        public DateTime CreatedUtc     { get; }

        public PairState(Guid pairId, Order buy, Order sell, double expectedSpread)
        {
            PairId         = pairId;
            Buy            = buy;
            Sell           = sell;
            ExpectedSpread = expectedSpread;
            CreatedUtc     = DateTime.UtcNow;
        }

        public Order PartnerOf(Order o) => o == Buy ? Sell : (o == Sell ? Buy : null);
        public bool  Contains (Order o) => o == Buy || o == Sell;
    }

    internal sealed class PairManager : IDisposable
    {
        private sealed class PendingPair
        {
            public Instrument Instrument;
            public int        Quantity;
            public double     BuyPx;
            public double     SellPx;
        }

        private readonly PairedStopsView     _view;
        private readonly PairedStopsSettings _settings;
        private readonly object _sync = new object();

        private bool       _programmatic;
        private PairState  _state;
        private PendingPair _pendingPair;
        private Account    _subscribedAccount;

        private readonly GhostPreview     _ghost;
        private readonly DispatcherTimer _sessionTimer;
        private DateTime _lastSessionTickEt;

        public PairManager(PairedStopsView view, PairedStopsSettings settings)
        {
            _view     = view;
            _settings = settings;
            _ghost    = new GhostPreview(settings);

            ResubscribeToSelectedAccount();

            _view.AccountCombo.SelectionChanged += (s, e) => ResubscribeToSelectedAccount();

            _view.PlaceButton.Click         += (s, e) => OnPlaceClicked();
            _view.CancelButton.Click        += (s, e) => OnCancelClicked();
            _view.PreviewConfirmButton.Click += (s, e) => OnPreviewConfirm();
            _view.PreviewCancelButton.Click  += (s, e) => OnPreviewCancel();

            _sessionTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
            _sessionTimer.Tick += OnSessionTimerTick;
            _sessionTimer.Start();
            _lastSessionTickEt = NowEt();
        }

        private static DateTime NowEt()
        {
            try
            {
                var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, etZone);
            }
            catch (TimeZoneNotFoundException)
            {
                // Fallback for non-Windows TZ database (unlikely on NT8 but safe).
                return DateTime.UtcNow.AddHours(-5);
            }
        }

        private void OnSessionTimerTick(object sender, EventArgs e)
        {
            var nowEt        = NowEt();
            var todayBoundary = nowEt.Date.AddHours(18); // 18:00 ET — CME session start

            if (_lastSessionTickEt < todayBoundary && nowEt >= todayBoundary)
            {
                lock (_sync) { _state = null; }
                _view.SetStatus("Session rollover — tracking state cleared.");
                // Live broker-side orders are left alone; they survive the rollover.
            }
            _lastSessionTickEt = nowEt;
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

            Account account = null;
            try { account = Account.All.FirstOrDefault(a => a.Name == name); }
            catch (Exception ex)
            {
                Output.Process($"[PairedStops] Account lookup failed: {ex.Message}", PrintTo.OutputTab1);
                return;
            }
            if (account == null) return;

            _subscribedAccount = account;
            _subscribedAccount.OrderUpdate += OnAccountOrderUpdate;
        }

        private void OnAccountOrderUpdate(object sender, OrderEventArgs e)
        {
            // Diagnostic: log every OrderUpdate received. Remove once drag-sync is verified working.
            try
            {
                Output.Process(
                    $"[PairedStops] OrderUpdate: name={e.Order?.Name ?? "(null)"} " +
                    $"state={e.Order?.OrderState} stop={e.Order?.StopPrice} " +
                    $"tracked={(_state != null && e.Order != null && _state.Contains(e.Order))} " +
                    $"programmatic={_programmatic}",
                    PrintTo.OutputTab1);
            }
            catch { /* logging must never break the handler */ }

            PairState snapshot;
            lock (_sync)
            {
                if (_programmatic) return;                      // our own ChangeOrder echoing back
                if (_state == null || !_state.Contains(e.Order)) return;
                snapshot = _state;
            }

            // Drag-sync: a Working update whose price has drifted from the expected spread.
            if (e.Order.OrderState == OrderState.Working)
            {
                double tickSize = e.Order.Instrument.MasterInstrument.TickSize;
                Order  partner  = snapshot.PartnerOf(e.Order);
                if (partner == null) return;

                double expectedPartnerPx = e.Order == snapshot.Buy
                    ? e.Order.StopPrice - snapshot.ExpectedSpread
                    : e.Order.StopPrice + snapshot.ExpectedSpread;
                expectedPartnerPx = PriceMath.RoundToTick(expectedPartnerPx, tickSize);

                try { Output.Process($"[PairedStops] Drag-sync check: moved={e.Order.Name} movedStop={e.Order.StopPrice} partnerStop={partner.StopPrice} expected={expectedPartnerPx}", PrintTo.OutputTab1); } catch { }

                // Second line of defense against the threaded echo: if the partner is
                // already at the expected price, there's nothing to sync.
                if (PriceMath.PricesEqual(partner.StopPrice, expectedPartnerPx, tickSize))
                {
                    try { Output.Process("[PairedStops] Drag-sync short-circuit: prices already equal.", PrintTo.OutputTab1); } catch { }
                    return;
                }

                bool acquired = false;
                try
                {
                    lock (_sync) { _programmatic = true; acquired = true; }

                    // Cancel-and-recreate: the safest way to move an unmanaged stop
                    // in NT8. We hold _programmatic = true across the whole sequence
                    // so the cancel echo, the new-order Accepted event, and the new
                    // Working event all get suppressed in our handler.
                    var instrument      = partner.Instrument;
                    var qty             = partner.Quantity;
                    var movedIsBuy      = e.Order == snapshot.Buy;
                    var partnerIsBuy    = !movedIsBuy;
                    var partnerAction   = partnerIsBuy ? OrderAction.Buy : OrderAction.SellShort;
                    var partnerName     = partner.Name; // preserve the PAIRSTOP_<guid>_{BUY|SELL} tag

                    _subscribedAccount.Cancel(new[] { partner });

                    var newPartner = _subscribedAccount.CreateOrder(
                        instrument,
                        partnerAction,
                        OrderType.StopMarket,
                        OrderEntry.Manual,
                        TimeInForce.Day,
                        qty,
                        0,
                        expectedPartnerPx,
                        string.Empty,
                        partnerName,
                        Core.Globals.MaxDate,
                        null);
                    _subscribedAccount.Submit(new[] { newPartner });

                    // Swap the partner reference in tracking state so subsequent
                    // events on the old partner are ignored and new events on the
                    // new partner are recognized.
                    lock (_sync)
                    {
                        if (_state == snapshot)
                        {
                            _state = movedIsBuy
                                ? new PairState(snapshot.PairId, snapshot.Buy, newPartner, snapshot.ExpectedSpread)
                                : new PairState(snapshot.PairId, newPartner, snapshot.Sell, snapshot.ExpectedSpread);
                        }
                    }

                    if (_settings.AudibleDragSync)
                    {
                        try { System.Media.SystemSounds.Asterisk.Play(); } catch { /* no audio device — ignore */ }
                    }
                    _view.SetStatus($"Synced partner to {expectedPartnerPx}.");
                }
                catch (Exception ex)
                {
                    _view.SetStatus($"Sync failed: {ex.Message}. Pair is now unlinked.", isError: true);
                    Output.Process($"[PairedStops] Sync failed: {ex}", PrintTo.OutputTab1);
                    lock (_sync) { if (_state == snapshot) _state = null; }
                }
                finally
                {
                    if (acquired) lock (_sync) { _programmatic = false; }
                }

                return;
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
                    {
                        Output.Process($"[PairedStops] OCO cancel failed: {ex}", PrintTo.OutputTab1);
                    }
                }
                lock (_sync) { if (_state == snapshot) _state = null; }
                var side = e.Order == snapshot.Buy ? "Buy" : "Sell";
                _view.SetStatus($"{side} stop filled — partner cancelled.");
                return;
            }

            // Manual cancel (via Chart Trader) or exchange rejection on one leg —
            // cancel the partner and clear state so the pair stays all-or-nothing.
            if (e.Order.OrderState == OrderState.Cancelled || e.Order.OrderState == OrderState.Rejected)
            {
                var partner = snapshot.PartnerOf(e.Order);
                if (partner != null &&
                    (partner.OrderState == OrderState.Working || partner.OrderState == OrderState.Accepted))
                {
                    try { _subscribedAccount.Cancel(new[] { partner }); }
                    catch (Exception ex)
                    {
                        Output.Process($"[PairedStops] Partner cancel after cancel/reject failed: {ex}", PrintTo.OutputTab1);
                    }
                }
                lock (_sync) { if (_state == snapshot) _state = null; }

                bool isReject = e.Order.OrderState == OrderState.Rejected;
                string msg    = isReject
                    ? $"Order rejected: {e.Error}"
                    : "One leg cancelled — partner cancelled to preserve pair integrity.";
                _view.SetStatus(msg, isError: isReject);
                return;
            }
        }

        // -------------------------------------------------------------------
        // Place
        // -------------------------------------------------------------------
        private void OnPlaceClicked()
        {
            lock (_sync)
            {
                if (_state != null)             { _view.SetStatus("Pair already active — cancel first.",        isError: true); return; }
                if (_pendingPair != null)       { _view.SetStatus("Preview already pending — confirm or cancel.", isError: true); return; }
                if (_subscribedAccount == null) { _view.SetStatus("No account selected.",                         isError: true); return; }

                var instrumentName = _view.InstrumentBox.Text.Trim();
                var instrument     = Instrument.GetInstrument(instrumentName);
                if (instrument == null || instrument.MasterInstrument == null)
                {
                    _view.SetStatus($"Instrument '{instrumentName}' not found.", isError: true);
                    return;
                }

                if (!double.TryParse(_view.OffsetBox.Text, System.Globalization.NumberStyles.Float,
                                     System.Globalization.CultureInfo.InvariantCulture, out var offset) || offset <= 0)
                {
                    _view.SetStatus("Invalid offset.", isError: true);
                    return;
                }

                if (!int.TryParse(_view.QuantityBox.Text, out var qty) || qty <= 0)
                {
                    _view.SetStatus("Invalid quantity.", isError: true);
                    return;
                }

                var tickSize = instrument.MasterInstrument.TickSize;
                var last     = instrument.MarketData?.Last?.Price ?? 0.0;
                if (last <= 0)
                {
                    var bid = instrument.MarketData?.Bid?.Price ?? 0.0;
                    var ask = instrument.MarketData?.Ask?.Price ?? 0.0;
                    if (bid > 0 && ask > 0) last = (bid + ask) * 0.5;
                    else
                    {
                        _view.SetStatus("No market data — cannot compute prices.", isError: true);
                        return;
                    }
                }

                PriceMath.ComputePair(last, offset, tickSize, out var buyPx, out var sellPx);
                if (buyPx <= sellPx)
                {
                    _view.SetStatus($"Invalid prices: buy {buyPx} <= sell {sellPx}.", isError: true);
                    return;
                }

                if (_settings.GhostPreviewEnabled)
                {
                    _pendingPair = new PendingPair
                    {
                        Instrument = instrument,
                        Quantity   = qty,
                        BuyPx      = buyPx,
                        SellPx     = sellPx
                    };
                    ShowPreviewStrip(buyPx, sellPx);
                    return;
                }

                SubmitPair(instrument, qty, buyPx, sellPx);
            }
        }

        // -------------------------------------------------------------------
        // Ghost preview — inline panel confirmation strip
        // -------------------------------------------------------------------
        private void ShowPreviewStrip(double buyPx, double sellPx)
        {
            _ghost.Show(_view.InstrumentBox.Text.Trim(), buyPx, sellPx);
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
                _view.PreviewText.Text        = "";
            }));
        }

        private void OnPreviewConfirm()
        {
            PendingPair pending;
            lock (_sync)
            {
                pending      = _pendingPair;
                _pendingPair = null;
            }
            HidePreviewStrip();
            if (pending == null) return;
            lock (_sync) { SubmitPair(pending.Instrument, pending.Quantity, pending.BuyPx, pending.SellPx); }
        }

        private void OnPreviewCancel()
        {
            lock (_sync) { _pendingPair = null; }
            HidePreviewStrip();
            _view.SetStatus("Preview cancelled.");
        }

        private void SubmitPair(Instrument instrument, int qty, double buyPx, double sellPx)
        {
            var pairId = Guid.NewGuid();
            var tag    = _settings.PairTagPrefix + pairId.ToString("N").Substring(0, 8);

            Order buyOrder = null, sellOrder = null;
            try
            {
                buyOrder = _subscribedAccount.CreateOrder(
                    instrument,
                    OrderAction.Buy,
                    OrderType.StopMarket,
                    OrderEntry.Manual,
                    TimeInForce.Day,
                    qty,
                    /* limitPrice */ 0,
                    /* stopPrice  */ buyPx,
                    /* oco        */ string.Empty,
                    /* name       */ tag + "_BUY",
                    /* gtd        */ Core.Globals.MaxDate,
                    /* customOrder*/ null);
                _subscribedAccount.Submit(new[] { buyOrder });

                sellOrder = _subscribedAccount.CreateOrder(
                    instrument,
                    OrderAction.SellShort,
                    OrderType.StopMarket,
                    OrderEntry.Manual,
                    TimeInForce.Day,
                    qty,
                    0,
                    sellPx,
                    string.Empty,
                    tag + "_SELL",
                    Core.Globals.MaxDate,
                    null);
                _subscribedAccount.Submit(new[] { sellOrder });
            }
            catch (Exception ex)
            {
                // Roll back the first leg if it made it through.
                if (buyOrder != null &&
                    (buyOrder.OrderState == OrderState.Accepted || buyOrder.OrderState == OrderState.Working))
                {
                    try { _subscribedAccount.Cancel(new[] { buyOrder }); }
                    catch { /* swallow cleanup errors — nothing we can do */ }
                }
                _view.SetStatus($"Place failed: {ex.Message}", isError: true);
                Output.Process($"[PairedStops] Place failed: {ex}", PrintTo.OutputTab1);
                return;
            }

            _state = new PairState(pairId, buyOrder, sellOrder, buyPx - sellPx);
            _view.SetStatus($"Pair active: buy @ {buyPx}, sell @ {sellPx}.");
        }

        // -------------------------------------------------------------------
        // Cancel
        // -------------------------------------------------------------------
        private void OnCancelClicked()
        {
            lock (_sync)
            {
                if (_state == null)
                {
                    _view.SetStatus("No active pair to cancel.");
                    return;
                }

                var toCancel = new List<Order>();
                if (_state.Buy.OrderState  == OrderState.Working || _state.Buy.OrderState  == OrderState.Accepted) toCancel.Add(_state.Buy);
                if (_state.Sell.OrderState == OrderState.Working || _state.Sell.OrderState == OrderState.Accepted) toCancel.Add(_state.Sell);

                if (toCancel.Count > 0)
                {
                    try { _subscribedAccount.Cancel(toCancel.ToArray()); }
                    catch (Exception ex)
                    {
                        _view.SetStatus($"Cancel failed: {ex.Message}", isError: true);
                        Output.Process($"[PairedStops] Cancel failed: {ex}", PrintTo.OutputTab1);
                    }
                }

                _state = null;
                _view.SetStatus("Pair cancelled.");
            }
        }

        public void Dispose()
        {
            _sessionTimer.Stop();
            _sessionTimer.Tick -= OnSessionTimerTick;

            if (_subscribedAccount != null)
            {
                _subscribedAccount.OrderUpdate -= OnAccountOrderUpdate;
                _subscribedAccount = null;
            }
        }
    }

    // -------------------------------------------------------------------------
    // The view (WPF UserControl, all code-behind)
    // -------------------------------------------------------------------------
    public class PairedStopsView : UserControl
    {
        public PairedStopsSettings Settings { get; }

        public ComboBox AccountCombo  { get; }
        public TextBox  InstrumentBox { get; }
        public TextBox  OffsetBox     { get; }
        public TextBox  QuantityBox   { get; }
        public CheckBox GhostToggle   { get; }
        public CheckBox AudibleToggle { get; }

        public Button PlaceButton  { get; }
        public Button CancelButton { get; }

        public StackPanel PreviewStrip         { get; }
        public TextBlock  PreviewText          { get; }
        public Button     PreviewConfirmButton { get; }
        public Button     PreviewCancelButton  { get; }

        public TextBlock StatusText { get; }

        public PairedStopsView(PairedStopsSettings settings)
        {
            Settings    = settings;
            DataContext = settings;

            // Root grid — 5 rows: inputs, buttons, preview strip, spacer, status.
            var root = new Grid { Margin = new Thickness(12) };
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // --- Inputs ---
            var inputs = new Grid();
            inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            inputs.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
            for (int i = 0; i < 6; i++)
                inputs.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            int row = 0;
            AccountCombo  = AddRow(inputs, ref row, "Account",           new ComboBox());
            InstrumentBox = AddRow(inputs, ref row, "Instrument",        new TextBox { Text = settings.InstrumentName });
            OffsetBox     = AddRow(inputs, ref row, "Offset (pts)",      new TextBox { Text = settings.OffsetPoints.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture) });
            QuantityBox   = AddRow(inputs, ref row, "Quantity",          new TextBox { Text = settings.Quantity.ToString(System.Globalization.CultureInfo.InvariantCulture) });
            GhostToggle   = AddRow(inputs, ref row, "Ghost preview",     new CheckBox { IsChecked = settings.GhostPreviewEnabled });
            AudibleToggle = AddRow(inputs, ref row, "Beep on drag-sync", new CheckBox { IsChecked = settings.AudibleDragSync });

            Grid.SetRow(inputs, 0);
            root.Children.Add(inputs);

            // --- Buttons ---
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 0) };
            PlaceButton  = new Button { Content = "Place Paired Stops", Padding = new Thickness(12, 6, 12, 6), Margin = new Thickness(0, 0, 8, 0) };
            CancelButton = new Button { Content = "Cancel Pair",        Padding = new Thickness(12, 6, 12, 6) };
            buttons.Children.Add(PlaceButton);
            buttons.Children.Add(CancelButton);
            Grid.SetRow(buttons, 1);
            root.Children.Add(buttons);

            // --- Preview strip (hidden until ghost preview is triggered) ---
            PreviewStrip = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin      = new Thickness(0, 12, 0, 0),
                Visibility  = Visibility.Collapsed
            };
            PreviewText          = new TextBlock { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
            PreviewConfirmButton = new Button { Content = "Confirm", Padding = new Thickness(8, 4, 8, 4), Margin = new Thickness(0, 0, 4, 0) };
            PreviewCancelButton  = new Button { Content = "Cancel",  Padding = new Thickness(8, 4, 8, 4) };
            PreviewStrip.Children.Add(PreviewText);
            PreviewStrip.Children.Add(PreviewConfirmButton);
            PreviewStrip.Children.Add(PreviewCancelButton);
            Grid.SetRow(PreviewStrip, 2);
            root.Children.Add(PreviewStrip);

            // --- Status ---
            StatusText = new TextBlock
            {
                Margin       = new Thickness(0, 12, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground   = System.Windows.Media.Brushes.DimGray
            };
            Grid.SetRow(StatusText, 4);
            root.Children.Add(StatusText);

            Content = root;

            PopulateAccounts();
            HookPersistence();
        }

        public void SetStatus(string message, bool isError = false)
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                StatusText.Text       = message;
                StatusText.Foreground = isError
                    ? System.Windows.Media.Brushes.Red
                    : System.Windows.Media.Brushes.DimGray;
            }));
        }

        private void PopulateAccounts()
        {
            try
            {
                foreach (var acct in Account.All)
                    AccountCombo.Items.Add(acct.Name);
            }
            catch (Exception ex)
            {
                Output.Process($"[PairedStops] Failed to enumerate accounts: {ex.Message}", PrintTo.OutputTab1);
            }

            if (!string.IsNullOrEmpty(Settings.AccountName) && AccountCombo.Items.Contains(Settings.AccountName))
                AccountCombo.SelectedItem = Settings.AccountName;
            else if (AccountCombo.Items.Count > 0)
                AccountCombo.SelectedIndex = 0;
        }

        private void HookPersistence()
        {
            AccountCombo.SelectionChanged += (s, e) =>
            {
                Settings.AccountName = AccountCombo.SelectedItem as string ?? "";
                SettingsStore.Save(Settings);
            };
            InstrumentBox.LostFocus += (s, e) =>
            {
                Settings.InstrumentName = InstrumentBox.Text.Trim();
                SettingsStore.Save(Settings);
            };
            OffsetBox.LostFocus += (s, e) =>
            {
                if (double.TryParse(OffsetBox.Text, System.Globalization.NumberStyles.Float,
                                    System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0)
                {
                    Settings.OffsetPoints = v;
                    SettingsStore.Save(Settings);
                }
            };
            QuantityBox.LostFocus += (s, e) =>
            {
                if (int.TryParse(QuantityBox.Text, out var v) && v > 0)
                {
                    Settings.Quantity = v;
                    SettingsStore.Save(Settings);
                }
            };
            GhostToggle.Checked   += (s, e) => { Settings.GhostPreviewEnabled = true;  SettingsStore.Save(Settings); };
            GhostToggle.Unchecked += (s, e) => { Settings.GhostPreviewEnabled = false; SettingsStore.Save(Settings); };
            AudibleToggle.Checked   += (s, e) => { Settings.AudibleDragSync = true;  SettingsStore.Save(Settings); };
            AudibleToggle.Unchecked += (s, e) => { Settings.AudibleDragSync = false; SettingsStore.Save(Settings); };
        }

        private static T AddRow<T>(Grid grid, ref int row, string label, T control) where T : FrameworkElement
        {
            var lbl = new TextBlock
            {
                Text              = label,
                Margin            = new Thickness(0, 4, 12, 4),
                VerticalAlignment = VerticalAlignment.Center
            };
            Grid.SetRow(lbl,     row); Grid.SetColumn(lbl,     0);
            Grid.SetRow(control, row); Grid.SetColumn(control, 1);
            control.Margin = new Thickness(0, 4, 0, 4);
            grid.Children.Add(lbl);
            grid.Children.Add(control);
            row++;
            return control;
        }
    }
}
