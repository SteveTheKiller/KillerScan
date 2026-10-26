using System.Linq;
using System.Windows;

namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private const string RailOrderSetting = "RailOrder";

        /// <summary>Theme, language and shortcuts, from the rail's bottom edge.</summary>
        internal static readonly string[] RailEdgeNames = ["ThemeButton", "LangButton", "ShortcutsButton"];

        /// <summary>The rail buttons that can be dragged into any order, in their default order.</summary>
        internal static readonly string[] RailMovableNames = ["SpeedTestButton", "LocationsButton", "HistoryButton"];

        // The family rail order (RailOrder): theme, language and shortcuts hold the rail's edge,
        // and the speed test, saved scans and history buttons can be dragged into any order above them.
        private void InitRailOrder()
            => RailOrder.Attach(RailButtons,
                RailEdgeNames.Select(name => (FrameworkElement)FindName(name)).ToList(),
                RailMovableNames.Select(name => (FrameworkElement)FindName(name)).ToList(),
                // The saved-scans button became Locations; a saved order keeps its place.
                App.GetSetting(RailOrderSetting)?.Replace("ProfilesButton", "LocationsButton"), value => App.SetSetting(RailOrderSetting, value));
    }
}
