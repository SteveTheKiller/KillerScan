using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;

/// <summary>
/// A dialog's confirm button (Unlock) keeps hinted text: an effect on its caption would
/// rasterize it and blur it at rest. Loads the shipped Black theme and kit styles.
/// </summary>
internal static class OutlineButtonCaptionTests
{
    // Its own process: a pack URI load fixes the process's resource assembly, which the later
    // in-process view checks set for themselves.
    public static async Task RunIsolated()
    {
        using var process = Process.Start(new ProcessStartInfo(typeof(OutlineButtonCaptionTests).Assembly.Location, "--outline-caption")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => process.WaitForExit(60000))) throw new InvalidOperationException("Outline caption check finishes within a minute.");
        Console.Write(await output);
        if (process.ExitCode != 0) throw new InvalidOperationException("Isolated outline caption check: " + await output + await error);
    }

    public static Task Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { Check(); }
            catch (Exception ex) { failure = ex; }
            finally { System.Windows.Threading.Dispatcher.CurrentDispatcher.InvokeShutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
        Console.WriteLine("PASS: outline button captions are not rasterized (1 selected).");
        return Task.CompletedTask;
    }

    private static void Check()
    {
        var root = new Border { Width = 300 };
        foreach (string path in new[] { "Themes/Black.xaml", "Controls/Controls.xaml" })
            root.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/KillerScan;component/" + path, UriKind.Absolute),
            });
        var button = new Button { Content = "Unlock", Style = (Style)root.FindResource("OutlineButton") };
        root.Child = button;
        root.Measure(new Size(300, double.PositiveInfinity));
        root.Arrange(new Rect(root.DesiredSize));
        root.UpdateLayout();

        if (button.Template.FindName("label", button) is not ContentPresenter label)
            throw new InvalidOperationException("OutlineButton's template has no ContentPresenter named label.");
        if (label.Effect != null)
            throw new InvalidOperationException("OutlineButton's caption carries an effect (" + label.Effect.GetType().Name
                + "), which rasterizes the text and blurs it at rest.");
    }
}
