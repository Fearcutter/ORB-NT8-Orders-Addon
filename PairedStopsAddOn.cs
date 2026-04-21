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
        public PairedStopsTab()
        {
            // Placeholder — replaced by PairedStopsView in Task 4.
            Content = new TextBlock
            {
                Text     = "Paired Stops — coming soon",
                Margin   = new Thickness(16),
                FontSize = 14
            };
        }

        public override void Cleanup() { }
        protected override string GetHeaderSubText() => "Paired Stops";
        protected override void RestoreFromXElement(XElement element) { }
        protected override void SaveToXElement(XElement element) { }
    }
}
