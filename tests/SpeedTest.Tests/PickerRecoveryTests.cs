using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerScan.Controls;
using KillerScan.Services;

/// <summary>
/// A typed folder that cannot be listed sends the picker back to the last folder that listed.
/// The user still has to learn why their folder did not open: the reason stays on the status line
/// after the recovery listing lands, instead of being replaced by that folder's item counts.
/// </summary>
internal static class PickerRecoveryTests
{
    private static readonly PickerListingResult Empty = new(Array.Empty<PickerEntry>(), true, true);
    private static readonly PickerListingResult Missing = new(Array.Empty<PickerEntry>(), false, false);

    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            // ThemeManager's fonts use pack URIs; reading the scheme registers it before the type loads.
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            try { Application.ResourceAssembly = typeof(FileDialog).Assembly; }
            catch (InvalidOperationException) { /* The runner already pointed it at the app. */ }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            // A Release build can run ThemeManager's static setup when a method that names it is
            // compiled, so everything that does lives in a method called only after the Application exists.
            failure = Scenario(app);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("Picker recovery scenario timed out.");
        if (failure != null) throw failure;
        Console.WriteLine("PASS: a typed folder that fails keeps its reason on screen after the picker recovers.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Exception? Scenario(Application app)
    {
            Exception? failure = null;
            var read = ThemeManager.GetSetting;
            var write = ThemeManager.SetSetting;
            try
            {
                ThemeManager.GetSetting = _ => null;
                ThemeManager.SetSetting = (_, _) => { };
                foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Strings/en-US.xaml" })
                    app.Resources.MergedDictionaries.Add(new ResourceDictionary
                    {
                        Source = new Uri("pack://application:,,,/KillerScan;component/" + resource)
                    });
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                string good = Path.GetFullPath(Path.GetTempPath()).TrimEnd('\\');
                string typed = Path.Combine(good, "Locked-" + Guid.NewGuid().ToString("N"));

                // Refused: the reader's reason survives the hop back.
                {
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (dir, _, _) => dir == typed ? throw new UnauthorizedAccessException("Access to the folder is denied.") : Empty;
                    _ = picker.NavigateDirectoryAsync(good);
                    Wait(() => picker.IsDirectoryListingAvailable, picker);
                    _ = picker.NavigateDirectoryAsync(typed);
                    Wait(() => picker.IsDirectoryListingAvailable && !picker.IsDirectoryListingBusy && PathText(picker) == good, picker);
                    Pump(200);
                    Require(PathText(picker) == good, "A refused typed folder goes back to the last folder that listed.");
                    Require(Status(picker).Contains("Access to the folder is denied."),
                        "The reason the typed folder failed stays on the status line after the recovery: " + Status(picker));
                    picker.Close();
                }

                // Missing: the no-such-folder message survives the hop back.
                {
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (dir, _, _) => dir == typed ? Missing : Empty;
                    _ = picker.NavigateDirectoryAsync(good);
                    Wait(() => picker.IsDirectoryListingAvailable, picker);
                    _ = picker.NavigateDirectoryAsync(typed);
                    Wait(() => picker.IsDirectoryListingAvailable && !picker.IsDirectoryListingBusy && PathText(picker) == good, picker);
                    Pump(200);
                    Require(Status(picker).Contains((string)app.FindResource("Str_Dlg_NoSuchFolder")),
                        "A typed folder that does not exist is reported after the recovery: " + Status(picker));
                    picker.Close();
                }

                // The next folder the user opens clears the old reason.
                {
                    var picker = new FileDialog(FileDialogMode.Open);
                    picker.DirectoryListingReader = (dir, _, _) => dir == typed ? throw new UnauthorizedAccessException("Access to the folder is denied.") : Empty;
                    _ = picker.NavigateDirectoryAsync(good);
                    Wait(() => picker.IsDirectoryListingAvailable, picker);
                    _ = picker.NavigateDirectoryAsync(typed);
                    Wait(() => picker.IsDirectoryListingAvailable && !picker.IsDirectoryListingBusy && PathText(picker) == good, picker);
                    Pump(200);
                    _ = picker.NavigateDirectoryAsync(good);
                    Wait(() => picker.IsDirectoryListingAvailable && !picker.IsDirectoryListingBusy, picker);
                    Pump(200);
                    Require(!Status(picker).Contains("Access to the folder is denied."), "A later successful listing drops the old reason.");
                    picker.Close();
                }
            }
            catch (Exception error) { failure = error; }
            finally
            {
                app.Shutdown();
                ThemeManager.GetSetting = read;
                ThemeManager.SetSetting = write;
            }
            return failure;
    }

    private static string PathText(FileDialog picker) => ((TextBox)picker.FindName("PathBox")).Text;
    private static string Status(FileDialog picker) => ((TextBlock)picker.FindName("SelMeta")).Text;

    private static void Pump(int milliseconds)
    {
        var end = DateTime.UtcNow.AddMilliseconds(milliseconds);
        while (DateTime.UtcNow < end)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
    }

    private static void Wait(Func<bool> done, FileDialog picker)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!done())
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("Listing state never settled: " + Status(picker));
            Pump(10);
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
