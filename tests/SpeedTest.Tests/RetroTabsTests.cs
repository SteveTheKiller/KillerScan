using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using KillerScan.Shell;


/// <summary>
/// The 98SE view tabs, rendered without a window: a band with three tabs, the middle one
/// selected, above and below the page. Checks the pixels Windows 98 puts where: the selected
/// tab's page face overhanging its neighbours and running into the page, the others a step
/// shorter in the darker face, and the page line across the strip broken by the selected tab.
/// </summary>
internal static class RetroTabsTests
{
    private const int Width = 304, Height = 30;   // three 100px cells inside the 2px insets
    private static readonly Color Light = Colors.White, Shade = Color.FromRgb(0x80, 0x80, 0x80),
        Dark = Colors.Black, Page = Color.FromRgb(0xc0, 0xc0, 0xc0), Behind = Color.FromRgb(0x9f, 0x9f, 0x9f);

    internal static void Run()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                Check(bottom: false);
                Check(bottom: true);
            }
            catch (Exception ex) { failure = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (failure != null) throw failure;
        Console.WriteLine("PASS: the 98SE view tabs render as Windows 98 tabs above and below the page.");
    }

    private static void Check(bool bottom)
    {
        var band = new Grid { Width = Width, Height = Height, UseLayoutRounding = true, SnapsToDevicePixels = true };
        band.Resources["BevelLightBrush"] = new SolidColorBrush(Light);
        band.Resources["BevelDarkBrush"] = new SolidColorBrush(Shade);
        band.Resources["BevelOuterDarkBrush"] = new SolidColorBrush(Dark);
        band.Resources["BackgroundBrush"] = new SolidColorBrush(Page);
        band.Resources["TabInactiveBrush"] = new SolidColorBrush(Behind);
        band.Resources["TextBrush"] = new SolidColorBrush(Dark);
        var strip = new UniformGrid { Rows = 1, UseLayoutRounding = true, SnapsToDevicePixels = true };
        band.Children.Add(strip);
        var flip = new ScaleTransform(1, -1);
        var tabs = new ViewTab[3];
        for (int i = 0; i < 3; i++)
        {
            tabs[i] = new ViewTab(() => { }, () => false) { Child = new Grid(), RenderTransformOrigin = new Point(0.5, 0.5) };
            tabs[i].RenderTransform = bottom ? flip : Transform.Identity;
            strip.Children.Add(tabs[i]);
        }
        RetroTabs.ApplyBand(band, strip, retro: true, bottom);
        for (int i = 0; i < 3; i++) RetroTabs.StyleTab(tabs[i], retro: true, active: i == 1, bottom);
        band.Measure(new Size(Width, Height));
        band.Arrange(new Rect(0, 0, Width, Height));
        var bitmap = new RenderTargetBitmap(Width, Height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(band);
        var pixels = new byte[Width * Height * 4];
        bitmap.CopyPixels(pixels, Width * 4, 0);
        string where = bottom ? "below the page" : "above the page";
        void At(int x, int y, Color expected, string what)
        {
            int o = (y * Width + x) * 4;
            var actual = Color.FromArgb(pixels[o + 3], pixels[o + 2], pixels[o + 1], pixels[o]);
            Require(actual == expected, $"Tabs {where}: {what} at ({x},{y}) is {expected}, got {actual}.");
        }
        var clear = Color.FromArgb(0, 0, 0, 0);
        // Cells: tab 0 at x 2..101, tab 1 (selected) at 102..201 overhanging to 100..203, tab 2 at 202..301.
        At(150, 15, Page, "the selected tab's face");
        At(50, 15, Behind, "an inactive tab's face");
        At(250, 15, Behind, "the other inactive tab's face");
        At(100, 15, Light, "the selected tab's white left edge, two pixels into its neighbour");
        At(202, 15, Shade, "the selected tab's #808080 right edge");
        At(203, 15, Dark, "the selected tab's black right edge, two pixels into its neighbour");
        At(2, 15, Light, "the first tab's white left edge, two pixels in from the band");
        At(301, 15, Dark, "the last tab's black right edge");
        if (!bottom)
        {
            At(50, 2, Light, "an inactive tab's top highlight, a step below the selected tab's");
            At(50, 1, clear, "the strip above an inactive tab");
            At(150, 0, Light, "the selected tab's top highlight on the band's first row");
            At(150, 28, Page, "the selected tab running into the page over the page line");
            At(150, 29, Page, "the selected tab running into the page");
            At(50, 28, Light, "the page's white top line under an inactive tab");
            At(50, 29, clear, "the page row below the line, outside every tab");
            At(50, 27, Behind, "an inactive tab stopping above the page line");
            At(2, 0, clear, "the first tab's cut top corner");
            At(102, 29, Page, "the selected tab's foot is open, no edge");
        }
        else
        {
            At(50, 0, Shade, "the page's #808080 shade on the band's first row, over an inactive tab");
            At(50, 1, Dark, "the page's black foot against an inactive tab");
            At(150, 0, Page, "the selected tab breaking the page foot");
            At(150, 1, Page, "the selected tab breaking the page foot");
            At(150, 28, Shade, "the selected tab's #808080 foot");
            At(150, 29, Dark, "the selected tab's black foot on the band's last row");
            At(50, 26, Shade, "an inactive tab's #808080 foot, a step up");
            At(50, 27, Dark, "an inactive tab's black foot, a step up");
            At(50, 29, clear, "the strip below an inactive tab");
            At(100, 0, Light, "the selected tab's white left edge reaching the page");
        }
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
