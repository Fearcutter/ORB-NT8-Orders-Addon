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
using System.Xml.Linq;
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
            // Only inject into NT's Control Center window.
            if (!(window is NinjaTrader.Gui.Tools.ControlCenter cc)) return;

            _ownerWindow = window;
            var newMenu = cc.MainMenu?.OfType<NTMenuItem>().FirstOrDefault(m =>
                (m.Header?.ToString() ?? "") == "New");
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
                if (window is NinjaTrader.Gui.Tools.ControlCenter cc)
                {
                    var newMenu = cc.MainMenu?.OfType<NTMenuItem>().FirstOrDefault(m =>
                        (m.Header?.ToString() ?? "") == "New");
                    newMenu?.Items.Remove(_menuItem);
                }
                _menuItem = null;
            }
            _ownerWindow = null;
        }

        private static void OnMenuClick(object sender, RoutedEventArgs e)
        {
            var factory = new PairedStopsTabFactory();
            var parent  = factory.CreateParentWindow();
            var tab     = factory.CreateTabContent() as NTTabPage;
            if (parent is NTWindow w && tab != null)
            {
                w.MainTabControl.AddNTTabPage(tab);
                w.Show();
            }
        }
    }

    // -------------------------------------------------------------------------
    // Tab factory + tab page
    // -------------------------------------------------------------------------
    public class PairedStopsTabFactory : INTTabFactory
    {
        public NTWindow CreateParentWindow() =>
            new NTWindow { Caption = "Paired Stops", Width = 420, Height = 380 };

        public NTTabPage CreateTabContent() => new PairedStopsTab();
    }

    public class PairedStopsTab : NTTabPage
    {
        private readonly PairedStopsSettings _settings;
        private readonly PairedStopsView     _view;

        public PairedStopsTab()
        {
            _settings = SettingsStore.Load();
            _view     = new PairedStopsView(_settings);
            Content   = _view;
        }

        public override void Cleanup()
        {
            SettingsStore.Save(_settings);
        }

        protected override string GetHeaderSubText() => "Paired Stops";
        protected override void RestoreFromXElement(XElement element) { }
        protected override void SaveToXElement(XElement element) { }
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
