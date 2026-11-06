using System;
using System.Threading;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using KillerScan.Controls;

/// <summary>
/// Enter opens the selected device in the browser, from the device and service grids. It used
/// to be caught at window level wherever focus was, so Enter on a focused button (Scan, the
/// Location banner's Switch, a rail button, a view tab) opened the browser instead of pressing it.
/// </summary>
internal static class ScanEnterKeyTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Body(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure is not null) throw failure;
        Console.WriteLine("PASS: Enter on a focused button, list row, menu item or text box belongs to that control, not the device shortcut.");
    }

    private static void Body()
    {
        foreach (var control in new System.Windows.IInputElement[] { new Button(), new ToggleButton(), new CheckBox(), new RadioButton(),
            new ComboBox(), new ListBoxItem(), new MenuItem(), new TextBox() })
            Require(ScanWorkspace.FocusOwnsEnter(control), control.GetType().Name + " keeps Enter for itself.");
        // The Devices, Services and Topology view tabs are focusable tab surfaces, not buttons,
        // with their own Enter handler.
        Require(ScanWorkspace.FocusOwnsEnter(new TabSurfaceBorder { Focusable = true }), "A view tab keeps Enter for itself.");
        Require(!ScanWorkspace.FocusOwnsEnter(new DataGridCell()), "A device grid cell leaves Enter to the browser shortcut.");
        Require(!ScanWorkspace.FocusOwnsEnter(new Border()), "A topology node leaves Enter to the browser shortcut.");
        Require(!ScanWorkspace.FocusOwnsEnter(null), "With nothing focused, Enter is the browser shortcut.");
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
