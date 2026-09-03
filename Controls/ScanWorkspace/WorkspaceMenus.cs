using System.Windows;
using System.Windows.Controls;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private void AddWorkspaceDeviceMenus()
        {
            var actions = new[]
            {
                ("Ping", "Str_Ctx_Ping", "Ctrl+P"), ("Ssh", "Str_Ctx_Ssh", "Ctrl+S"),
                ("SshAs", "Str_Ctx_SshAs", "Ctrl+Shift+S"),
                ("Watch", "Str_Watch_Title", "F2"), ("Diagnose", "Str_Diag_Title", "F3")
            };
            foreach (var placement in new[]
            {
                ("Str_Workspace_OpenTab", false, false),
                ("Str_Workspace_OpenBeside", true, false),
                ("Str_Workspace_OpenExternal", false, true)
            })
            {
                var parent = new MenuItem();
                parent.SetResourceReference(MenuItem.HeaderProperty, placement.Item1);
                foreach (var action in actions)
                {
                    if (placement.Item3 && (action.Item1 == "Watch" || action.Item1 == "Diagnose")) continue;
                    string name = action.Item1 + (placement.Item3 ? "External" : "");
                    bool beside = placement.Item2;
                    var item = new MenuItem();
                    item.SetResourceReference(MenuItem.HeaderProperty, action.Item2);
                    if (!placement.Item2 && !placement.Item3) item.InputGestureText = action.Item3;
                    item.Click += (_, _) => RaiseDeviceAction(name, beside);
                    parent.Items.Add(item);
                }
                ResultsGrid.ContextMenu.Items.Add(parent);
            }
        }
    }
}
