using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Threading;
using KillerScan.Controls;
using KillerScan.Services;

/// <summary>
/// The picker's new-folder box names, checks and creates the folder off the UI thread. A share that stops
/// answering costs at most the probe timeout: the window keeps pumping, the box gets the plain name, and
/// Enter reports the timeout instead of freezing. Every refusal is announced to a screen reader.
/// </summary>
internal static class NewFolderTests
{
    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            _ = System.IO.Packaging.PackUriHelper.UriSchemePack;
            try { Application.ResourceAssembly = typeof(FileDialog).Assembly; }
            catch (InvalidOperationException) { /* The runner already pointed it at the app. */ }
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            failure = Scenario(app);
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(60))) throw new TimeoutException("New folder scenario timed out.");
        if (failure != null) throw failure;
        Console.WriteLine("PASS: the new-folder box works off the UI thread, survives a hung share and announces refusals.");
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static Exception? Scenario(Application app)
    {
        Exception? failure = null;
        var read = ThemeManager.GetSetting;
        var write = ThemeManager.SetSetting;
        string root = Path.Combine(Path.GetTempPath(), "ks-newfolder-" + Guid.NewGuid().ToString("N"));
        using var gate = new ManualResetEventSlim(false);
        try
        {
            ThemeManager.GetSetting = _ => null;
            ThemeManager.SetSetting = (_, _) => { };
            foreach (string resource in new[] { "Themes/Dark.xaml", "Controls/Controls.xaml", "Strings/en-US.xaml" })
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/KillerScan;component/" + resource) });
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            Directory.CreateDirectory(root);
            string baseName = (string)app.FindResource("Str_FD_NewFolder");
            var picker = new FileDialog(FileDialogMode.Open);
            var error = (TextBlock)picker.FindName("NewFolderError");
            Require(AutomationProperties.GetLiveSetting(error) == AutomationLiveSetting.Assertive, "The new-folder message is an assertive live region from the start.");

            // On a working disk: the first free name, the folder itself, then the name already taken.
            Require(Await(picker.FirstFreeFolderNameAsync(root)) == baseName, "An empty folder offers the plain name.");
            var made = Await(picker.CreateNewFolderAsync(root, baseName));
            Require(made.Failure == null && made.Problem == FolderNameProblem.None && made.Created == Path.Combine(root, baseName) && Directory.Exists(made.Created!), "Enter creates the folder.");
            Require(picker.ShowNewFolderOutcome(made), "A created folder is opened, not reported.");
            Require(Await(picker.FirstFreeFolderNameAsync(root)) == baseName + " (2)", "The next offer skips the taken name.");
            var taken = Await(picker.CreateNewFolderAsync(root, baseName));
            Require(taken.Problem == FolderNameProblem.Exists && taken.Created == null, "A taken name is refused.");
            Require(Await(picker.CreateNewFolderAsync(root, "a:b")).Problem == FolderNameProblem.InvalidCharacters, "Invalid characters are refused.");
            Require(Await(picker.FirstFreeFolderNameAsync(Path.Combine(root, "gone"))) == null, "A folder that is gone opens no box.");

            // A refusal is shown and announced.
            Require(!picker.ShowNewFolderOutcome(taken), "A refusal keeps the box open.");
            Require(error.Visibility == Visibility.Visible && error.Text == (string)app.FindResource("Str_FD_FolderExists"), "The refusal is on screen: " + error.Text);
            Require(AutomationProperties.GetLiveSetting(error) == AutomationLiveSetting.Assertive, "The refusal is announced.");

            // A share that never answers: the UI thread keeps pumping and the wait ends at the timeout.
            picker.NewFolderProbe = (_, _) => { gate.Wait(); return false; };
            picker.NewFolderTimeout = TimeSpan.FromMilliseconds(400);
            int ticks = 0;
            var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Normal, (_, _) => ticks++, Dispatcher.CurrentDispatcher);
            var started = DateTime.UtcNow;
            string? offered = Await(picker.FirstFreeFolderNameAsync(root));
            var hung = Await(picker.CreateNewFolderAsync(root, "Hung"));
            double seconds = (DateTime.UtcNow - started).TotalSeconds;
            timer.Stop();
            Require(offered == baseName, "A hung share still gets the box, with the plain name.");
            string timedOut = new System.ComponentModel.Win32Exception(1460).Message;
            Require(hung.Failure == timedOut && hung.Created == null, "Enter on a hung share reports the timeout: " + hung.Failure);
            Require(seconds < 3, $"Both waits end at the timeout ({seconds:0.0} s).");
            Require(ticks >= 8, $"The window kept working while the share hung ({ticks} timer ticks).");
            Require(!Directory.Exists(Path.Combine(root, "Hung")), "Nothing was created on the hung share.");
            Require(!picker.ShowNewFolderOutcome(hung) && error.Text.Contains(timedOut), "The timeout is shown: " + error.Text);
            picker.Close();
        }
        catch (Exception problem) { failure = problem; }
        finally
        {
            gate.Set();
            app.Shutdown();
            ThemeManager.GetSetting = read;
            ThemeManager.SetSetting = write;
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
        return failure;
    }

    // Pumps the dispatcher until the task finishes, the way the dialog's own window would.
    private static T Await<T>(Task<T> task)
    {
        var end = DateTime.UtcNow.AddSeconds(15);
        while (!task.IsCompleted)
        {
            if (DateTime.UtcNow > end) throw new TimeoutException("The new-folder call never finished.");
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            Thread.Sleep(5);
        }
        return task.GetAwaiter().GetResult();
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
