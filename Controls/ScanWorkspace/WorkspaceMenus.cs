using System.Windows.Controls;

namespace KillerScan.Controls
{
    public partial class ScanWorkspace
    {
        private void AddWorkspaceDeviceMenus()
        {
            var external = new MenuItem { Icon = MenuGlyph.Create(0xE8A7) };
            external.SetResourceReference(MenuItem.HeaderProperty, "Str_Workspace_OpenExternal");
            foreach (var action in new[]
            {
                ("PingExternal", "Str_Ctx_Ping"), ("SshExternal", "Str_Ctx_Ssh"),
                ("SshAsExternal", "Str_Ctx_SshAs")
            })
            {
                var item = new MenuItem { Icon = MenuGlyph.Create(action.Item1 == "PingExternal" ? 0xE704 : 0xE756) };
                item.SetResourceReference(MenuItem.HeaderProperty, action.Item2);
                item.Click += (_, _) => RaiseDeviceAction(action.Item1, false);
                external.Items.Add(item);
            }
            ResultsGrid.ContextMenu.Items.Add(external);
        }
    }
}
