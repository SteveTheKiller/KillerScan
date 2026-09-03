using System.Windows.Controls;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private void AddWorkspaceDeviceMenus()
        {
            var external = new MenuItem();
            external.SetResourceReference(MenuItem.HeaderProperty, "Str_Workspace_OpenExternal");
            foreach (var action in new[]
            {
                ("PingExternal", "Str_Ctx_Ping"), ("SshExternal", "Str_Ctx_Ssh"),
                ("SshAsExternal", "Str_Ctx_SshAs")
            })
            {
                var item = new MenuItem();
                item.SetResourceReference(MenuItem.HeaderProperty, action.Item2);
                item.Click += (_, _) => RaiseDeviceAction(action.Item1, false);
                external.Items.Add(item);
            }
            ResultsGrid.ContextMenu.Items.Add(external);
        }
    }
}
