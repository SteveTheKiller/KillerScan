namespace KillerScan.Shell
{
    public partial class MainWindow
    {
        private Controls.SpeedTestView? _speedTestView;

        private void SpeedTestButton_Click(object sender, System.Windows.RoutedEventArgs e)
        {
            if (_speedTestView == null)
            {
                _speedTestView = new Controls.SpeedTestView();
                _speedTestView.StatusChanged += () =>
                {
                    if (_workspaceView == "speedtest") UpdateWorkspaceStatus();
                };
                RegisterViewToolbar("speedtest", new System.Windows.Controls.Grid());
            }
            _speedTestView.LayoutTransform = new System.Windows.Media.ScaleTransform(_appScale, _appScale);
            ShowWorkspaceContent(_speedTestView, "speedtest");
            _speedTestView.FocusInput();
        }
    }
}
