using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using KillerScan.Controls;   // Anim - the app's own fade, which this used to carry a copy of

namespace KillerScan.Controls
{
    /// <summary>Open or Save. Picked at construction; changes the accept button and the rules.</summary>
    public enum FileDialogMode { Open, Save }

    /// <summary>
    /// Themed stand-in for Microsoft.Win32.OpenFileDialog / SaveFileDialog. Chrome, places rail,
    /// view modes, sortable columns, a file name box and a filter combo, with the row styles
    /// shared from Controls.xaml.
    ///
    /// This is the ONLY picker in the app. There was a second one, FolderPickerDialog, for
    /// choosing a folder; it was deleted on 2026-08-07 because it duplicated all of the above and
    /// the two copies had drifted apart. A folder picker is this dialog with CheckFileExists off -
    /// the chosen path's directory IS the folder. Do not add a second one back.
    ///
    /// The property surface mirrors the Win32 dialogs on purpose - Title, Filter, FilterIndex,
    /// FileName, InitialDirectory, DefaultExt, AddExtension, OverwritePrompt, CheckFileExists -
    /// so adopting it at a call site is a one-word change:
    ///
    ///     var dlg = new FileDialog(FileDialogMode.Save) { Title = ..., Filter = ..., FileName = ... };
    ///     if (dlg.ShowDialog(owner) == true) Use(dlg.FileName);
    ///
    /// Multiselect is deliberately NOT implemented yet - nothing in the app needs it, and
    /// a half-working Multiselect is worse than an absent one. Add it when something wants it.
    /// </summary>
    public partial class FileDialog : Window
    {
        // ── Win32-compatible surface ─────────────────────────────────────────────

        /// <summary>Win32 filter syntax: "Desc|*.a;*.b|Other|*.c". Empty means every file.</summary>
        public string Filter { get; set; } = "";

        /// <summary>1-based, like the Win32 dialogs. Out of range is clamped.</summary>
        public int FilterIndex { get; set; } = 1;

        /// <summary>Seeded with a suggested name; on OK, the full chosen path.</summary>
        public string FileName { get; set; } = "";

        public string InitialDirectory { get; set; } = "";

        /// <summary>Appended on save when the typed name has no extension. No leading dot needed.</summary>
        public string DefaultExt { get; set; } = "";

        public bool AddExtension { get; set; } = true;

        /// <summary>Save mode: confirm before replacing an existing file.</summary>
        public bool OverwritePrompt { get; set; } = true;

        /// <summary>Open mode: refuse to return a path that does not exist.</summary>
        public bool CheckFileExists { get; set; } = true;

        /// <summary>Refuse a path whose DIRECTORY does not exist. On by default, matching the
        /// Win32 dialogs - the picker never creates folder trees on the user's behalf, so turning
        /// this off only means the caller has agreed to handle a missing directory itself.</summary>
        public bool CheckPathExists { get; set; } = true;

        /// <summary>Open mode only: let the user pick several files at once (Ctrl/Shift click, or
        /// a drag over the list). Ignored in Save mode, where "several names" is meaningless.
        /// Set it BEFORE ShowDialog - the list's selection mode is applied there.</summary>
        public bool Multiselect { get; set; }

        /// <summary>
        /// Shows the image preview pane on the right. Off by default: a picker choosing a data
        /// FOLDER has nothing to preview, and the column is 0-wide when off so the layout is
        /// byte-identical to before for every existing caller.
        /// </summary>
        public bool ShowPreview { get; set; }

        /// <summary>
        /// Drop the app-name prefix from the caption and show the Title alone. For a Title that
        /// already names the product - "Choose the KillerScan data folder" - the standard
        /// wordmark prefix makes the caption read "KillerScan  Choose the KillerScan data
        /// folder", saying it twice. Off by default: every other caller wants the wordmark.
        /// </summary>
        public bool TitleOnly { get; set; }


        /// <summary>Every path chosen. Always populated on success, so a caller can read this
        /// whether or not it asked for Multiselect - single selection yields one entry, matching
        /// the Win32 dialogs' FileNames. FileName remains the first of them.</summary>
        public string[] FileNames { get; private set; } = [];

        // ── internals ────────────────────────────────────────────────────────────

        private readonly FileDialogMode _mode;

        public ObservableCollection<PickerPlace> Places  { get; } = [];
        public ObservableCollection<PickerEntry> Entries { get; } = [];

        private readonly List<PickerEntry> _raw = [];
        private string _currentDir = string.Empty;
        private bool _navigating;
        private bool _built;                 // suppresses filter events during construction
        private int  _viewMode;              // 0 list, 1 icons, 2 details
        private int  _sortKey;               // 0 name, 1 size, 2 modified
        private bool _sortAsc = true;

        // Per-filter-entry patterns, parallel to FilterCombo's items. Empty list = show all.
        private readonly List<string[]> _filterPatterns = [];

        private static readonly string ArrowUp   = ((char)0xE70E).ToString();
        private static readonly string ArrowDown = ((char)0xE70D).ToString();

        // ── Pinned places / recents / hidden state ───────────────────────────────
        private bool _showHidden;

        private const string ShowHiddenKey = "FileDlgShowHidden";
        private const string RecentsKey    = "FileDlgRecents";
        private const string PinnedKey     = "FileDlgPinned";
        private const string LastOpenKey   = "FileDlgLastOpenDir";
        private const string LastSaveKey   = "FileDlgLastSaveDir";
        private const int    RecentsMax    = 12;

        // Guards the fade-then-close re-entry below. Without it OnClosing would cancel forever.
        private bool _fadingOut;

        /// <summary>The result Accept wants, held until the window is actually allowed to close.
        /// Null means cancel (the X, Escape, the Cancel button - none of them set it).</summary>
        private bool? _pendingResult;

        /// <summary>Fades the dialog out before it actually closes. The first pass cancels the
        /// close and runs the fade; the second sees the flag and lets it through.
        ///
        /// The result CANNOT be assigned before the fade. Assigning Window.DialogResult is itself a
        /// close request, so it lands in this handler, which cancels that close - and WPF resets
        /// DialogResult to null whenever a close is canceled. Accept therefore records what it
        /// wants in _pendingResult and the assignment happens in the fade's completion callback,
        /// where nothing will cancel it. Assigning DialogResult there also closes the window, which
        /// is why that branch does not call Close() as well.</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (!_fadingOut)
            {
                _fadingOut = true;
                e.Cancel = true;
                Anim.FadeOut(RootFade, () =>
                {
                    if (_pendingResult.HasValue) DialogResult = _pendingResult;   // this closes it
                    else Close();                                                 // cancel path
                });
                return;
            }
            base.OnClosing(e);
        }

        public FileDialog(FileDialogMode mode = FileDialogMode.Open)
        {
            _mode = mode;
            InitializeComponent();
            Loaded += (_, _) => Anim.FadeIn(RootFade);

            // Size and placement remembered separately from the folder picker: this dialog is a
            // different shape and sharing the keys would make each one fight the other.
            try
            {
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                if (double.TryParse(App.GetSetting("FileDlgW"),
                        System.Globalization.NumberStyles.Float, ci, out double w) &&
                    double.TryParse(App.GetSetting("FileDlgH"),
                        System.Globalization.NumberStyles.Float, ci, out double h))
                {
                    Width  = Math.Max(MinWidth,  Math.Min(w, SystemParameters.WorkArea.Width));
                    Height = Math.Max(MinHeight, Math.Min(h, SystemParameters.WorkArea.Height));
                }
                if (double.TryParse(App.GetSetting("FileDlgX"),
                        System.Globalization.NumberStyles.Float, ci, out double x) &&
                    double.TryParse(App.GetSetting("FileDlgY"),
                        System.Globalization.NumberStyles.Float, ci, out double y))
                {
                    var wa = SystemParameters.WorkArea;
                    if (x > wa.Left - Width + 80 && x < wa.Right - 80 &&
                        y > wa.Top - 20 && y < wa.Bottom - 80)
                    {
                        WindowStartupLocation = WindowStartupLocation.Manual;
                        Left = x;
                        Top  = y;
                    }
                }
            }
            catch { /* registry unavailable - defaults are fine */ }

            _showHidden = App.GetSetting(ShowHiddenKey) == "1";
            ApplyShowHiddenButton();

            Closing += (_, _) =>
            {
                try
                {
                    var ci = System.Globalization.CultureInfo.InvariantCulture;
                    App.SetSetting("FileDlgW", ActualWidth.ToString(ci));
                    App.SetSetting("FileDlgH", ActualHeight.ToString(ci));
                    App.SetSetting("FileDlgX", Left.ToString(ci));
                    App.SetSetting("FileDlgY", Top.ToString(ci));
                }
                catch { /* not worth failing the close */ }
            };

            // NO DwmChrome calls, deliberately. On an AllowsTransparency window the DWM corner
            // preference makes DWM composite its own rounded frame around the WINDOW rect - the
            // transparent 10px halo included - and SetThemeBorder tints it: that WAS the gray
            // band. The other four dialogs are AllowsTransparency with no DWM calls and have
            // never shown one; the card draws its own border and shadow, so DWM has nothing to
            // add. The WM_ERASEBKGND hook that lived here went too - a layered window is rendered
            // via UpdateLayeredWindow and never receives it. (2026-07-30, fifth attempt)
        }

        /// <summary>
        /// Sets the owner and shows modally. Everything that depends on Filter / FileName /
        /// InitialDirectory is wired HERE rather than in the constructor, because callers set
        /// those as object-initializer properties after construction.
        /// </summary>
        public bool? ShowDialog(Window? owner)
        {
            if (owner != null && owner.IsVisible) Owner = owner;

            // The caller's Title is the caption SUBTITLE now, to the right of the wordmark, the
            // same shape SketchPad / Databases / Dictation use. It used to be a heading in the
            // BODY, which read as part of the file list rather than as the dialog's name. The
            // blurred shadow copy has to carry the same text or the drop shadow stops halfway
            // across the caption.
            // TitleOnly blanks the wordmark runs rather than hiding the block, so the layout and
            // the shadow copy stay in step and the subtitle simply starts at the left edge. The
            // leading gap goes with it - with no wordmark in front there is nothing to sit after.
            if (TitleOnly)
            {
                TitleWordA.Text = TitleWordB.Text = "";
                TitleShadowA.Text = TitleShadowB.Text = "";
                TitlePlainA.Text = TitlePlainB.Text = "";
            }
            string sub = string.IsNullOrWhiteSpace(Title) ? ""
                       : (TitleOnly ? Title : "  " + Title);
            TitleSub.Text       = sub;
            TitleSubShadow.Text = sub;
            // The plain-caption twin, shown instead of the wordmark on a theme with a Win98-style
            // title bar. It has to carry the same subtitle or that caption reads just "KillerScan"
            // with no indication of what the dialog is for.
            TitlePlainSub.Text  = sub;
            HeadingText.Text    = "";   // collapsed placeholder; keeps the grid row indices stable
            AcceptButton.Content = Loc(_mode == FileDialogMode.Save ? "Str_Btn_Save" : "Str_Btn_Open");
            // Extended, not Multiple: Extended is the Explorer behavior (plain click replaces the
            // selection, Ctrl adds, Shift ranges). Multiple toggles on every click, which feels
            // broken to anyone who has used a file dialog before.
            FileList.SelectionMode = Multiselect && _mode == FileDialogMode.Open
                ? SelectionMode.Extended
                : SelectionMode.Single;

            // The preview column is 0-wide unless the caller asked for it, so every existing
            // call site lays out exactly as it did before.
            if (ShowPreview)
            {
                PreviewPane.Visibility = Visibility.Visible;
                PreviewGapCol.Width = new GridLength(8);
                PreviewCol.Width    = new GridLength(220);
                UpdatePreview();
            }

            // Open mode has nothing to name, so the box is for typing/filtering a path, not a
            // new file. It stays visible: typing an exact name is faster than hunting for it.
            BuildFilters();
            BuildPlaces();
            PlacesList.ItemsSource = Places;
            FileList.ItemsSource   = Entries;
            InitPlacesFades();
            ApplyView();

            // A seeded FileName can be a bare name ("export.ics"), a full path, or empty.
            string startDir = InitialDirectory;
            string seedName = "";
            if (!string.IsNullOrWhiteSpace(FileName))
            {
                if (FileName.IndexOfAny(['\\', '/']) >= 0)
                {
                    var d = Path.GetDirectoryName(FileName);
                    if (!string.IsNullOrEmpty(d) && Directory.Exists(d)) startDir = d!;
                    seedName = Path.GetFileName(FileName);
                }
                else seedName = FileName;
            }
            if (string.IsNullOrWhiteSpace(startDir) || !Directory.Exists(startDir))
            {
                string? remembered = App.GetSetting(_mode == FileDialogMode.Open ? LastOpenKey : LastSaveKey);
                startDir = !string.IsNullOrWhiteSpace(remembered) && Directory.Exists(remembered)
                    ? remembered!
                    : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }

            _built = true;
            NavigateTo(startDir);
            FileNameBox.Text = seedName;

            // Save: preselect the stem so typing replaces the name but keeps the extension
            // visible. Open: caret at the end.
            FileNameBox.Focus();
            if (_mode == FileDialogMode.Save && seedName.Length > 0)
            {
                int dot = seedName.LastIndexOf('.');
                FileNameBox.Select(0, dot > 0 ? dot : seedName.Length);
            }
            else FileNameBox.CaretIndex = FileNameBox.Text.Length;

            return ShowDialog();
        }

        // ── Filters ──────────────────────────────────────────────────────────────

        /// <summary>
        /// Parses Win32 filter syntax into the combo plus a parallel pattern list. A malformed
        /// filter (odd number of segments) degrades to "all files" rather than throwing - a bad
        /// filter string should not stop someone opening a file.
        /// </summary>
        private void BuildFilters()
        {
            FilterCombo.Items.Clear();
            _filterPatterns.Clear();

            var parts = (Filter ?? "").Split('|');
            for (int i = 0; i + 1 < parts.Length; i += 2)
            {
                var label = parts[i].Trim();
                var pats  = parts[i + 1].Split(';')
                                        .Select(p => p.Trim())
                                        .Where(p => p.Length > 0)
                                        .ToArray();
                if (label.Length == 0 || pats.Length == 0) continue;
                FilterCombo.Items.Add(label);
                _filterPatterns.Add(pats);
            }

            if (FilterCombo.Items.Count == 0)
            {
                FilterCombo.Items.Add(Loc("Str_Dlg_AllFiles"));
                _filterPatterns.Add(["*.*"]);
            }

            int idx = FilterIndex - 1;
            FilterCombo.SelectedIndex = idx >= 0 && idx < FilterCombo.Items.Count ? idx : 0;
            FilterLabel.Visibility = FilterCombo.Visibility =
                FilterCombo.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Filter_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!_built) return;
            FilterIndex = FilterCombo.SelectedIndex + 1;

            // Save mode follows the Win32 dialogs: switching the type swaps the typed name's
            // extension - but only when the current one belongs to another entry of THIS filter.
            // An extension the user typed by hand is theirs and is left alone.
            if (_mode == FileDialogMode.Save)
            {
                string? newExt = ActiveFilterExt();
                string  name   = FileNameBox.Text?.Trim() ?? "";
                string  cur    = name.Length == 0 ? "" : Path.GetExtension(name);
                if (newExt != null && cur.Length > 0 &&
                    !cur.Equals(newExt, StringComparison.OrdinalIgnoreCase) &&
                    AllFilterExts().Contains(cur, StringComparer.OrdinalIgnoreCase))
                {
                    FileNameBox.Text = Path.ChangeExtension(name, newExt);
                }
            }

            ApplySort();
        }

        /// <summary>
        /// The active filter entry's own extension (".csv"), or null when its first pattern is a
        /// wildcard-any or a multi-pattern catch-all that names no single extension.
        /// </summary>
        private string? ActiveFilterExt()
        {
            int i = FilterCombo.SelectedIndex;
            if (i < 0 || i >= _filterPatterns.Count) return null;
            string p = _filterPatterns[i][0];
            if (p.Length > 2 && p.StartsWith("*.") && p.IndexOfAny(['*', '?'], 2) < 0)
                return p[1..];
            return null;
        }

        /// <summary>Every concrete extension the filter list names, for the swap test above.</summary>
        private IEnumerable<string> AllFilterExts()
        {
            foreach (var pats in _filterPatterns)
                foreach (var p in pats)
                    if (p.Length > 2 && p.StartsWith("*.") && p.IndexOfAny(['*', '?'], 2) < 0)
                        yield return p[1..];
        }

        /// <summary>True when the name passes the active filter. Folders are never filtered out.</summary>
        private bool PassesFilter(PickerEntry en)
        {
            if (en.IsFolder) return true;
            int i = FilterCombo.SelectedIndex;
            if (i < 0 || i >= _filterPatterns.Count) return true;
            var pats = _filterPatterns[i];
            return pats.Any(p => p == "*.*" || p == "*" || WildcardMatch(en.Name, p));
        }

        /// <summary>Case-insensitive glob. Anchored, so "*.ics" does not match "a.icsx".</summary>
        private static bool WildcardMatch(string name, string pattern)
        {
            var rx = "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(name, rx, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        // ── Quick places (pinned + drives) ───────────────────────────────────────

        /// <summary>
        /// Pinned folders first (persisted, user-editable via right-click), then the ready
        /// drives. Drives are enumerated live every build - they come and go with USB sticks -
        /// and are not pinned, so they carry no remove menu.
        /// </summary>
        private void BuildPlaces()
        {
            Places.Clear();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var p in PinnedPaths())
                if (added.Add(p.TrimEnd('\\'))) AddPlace(LabelFor(p), p, pinned: true);

            foreach (var place in ExplorerQuickAccessPlaces())
                if (added.Add(place.Path.TrimEnd('\\'))) AddPlace(place.Label, place.Path);

            foreach (var d in DriveInfo.GetDrives().Where(d => d.IsReady))
            {
                string label;
                try { label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? d.DriveType.ToString() : d.VolumeLabel.Trim(); }
                catch { label = d.DriveType.ToString(); }
                if (added.Add(d.RootDirectory.FullName.TrimEnd('\\')))
                    AddPlace($"{d.Name.TrimEnd('\\')}  {label}", d.RootDirectory.FullName);
            }
        }

        private static IEnumerable<(string Label, string Path)> ExplorerQuickAccessPlaces()
        {
            const string QuickAccess = "shell:::{679f85cb-0220-4080-b29b-5540cc05aab6}";
            object? shell = null, folder = null, items = null;
            try
            {
                var type = Type.GetTypeFromProgID("Shell.Application");
                if (type == null) yield break;
                shell = Activator.CreateInstance(type);
                folder = ((dynamic)shell!).NameSpace(QuickAccess);
                if (folder == null) yield break;
                items = ((dynamic)folder).Items();
                int count = ((dynamic)items).Count;
                for (int i = 0; i < count; i++)
                {
                    object? item = null;
                    try
                    {
                        item = ((dynamic)items).Item(i);
                        if (item == null) continue;
                        dynamic quickItem = item;
                        if (!Convert.ToBoolean(quickItem.IsFolder)) continue;
                        string path = Convert.ToString(quickItem.Path) ?? "";
                        string name = Convert.ToString(quickItem.Name) ?? "";
                        if (Directory.Exists(path)) yield return (name.Length > 0 ? name : LabelFor(path), path);
                    }
                    finally { if (item != null && Marshal.IsComObject(item)) Marshal.FinalReleaseComObject(item); }
                }
            }
            finally
            {
                if (items != null && Marshal.IsComObject(items)) Marshal.FinalReleaseComObject(items);
                if (folder != null && Marshal.IsComObject(folder)) Marshal.FinalReleaseComObject(folder);
                if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
            }
        }

        /// <summary>
        /// The persisted pin list. First run (key absent, null) seeds the five standard folders;
        /// an EMPTY stored value means the user unpinned everything and must stay empty.
        /// </summary>
        private static List<string> PinnedPaths()
        {
            string? saved = App.GetSetting(PinnedKey);
            if (saved != null)
                return [.. saved.Split('|').Where(s => s.Length > 0)];

            return [.. new List<string>
            {
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
                Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            }.Where(p => !string.IsNullOrEmpty(p))];
        }

        /// <summary>Localized label for the five standard folders, plain folder name otherwise.</summary>
        private static string LabelFor(string path)
        {
            string p = path.TrimEnd('\\');
            bool Is(string other) => other.Length > 0 &&
                p.Equals(other.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)))  return Loc("Str_QA_Home");
            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.Desktop)))      return Loc("Str_QA_Desktop");
            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)))  return Loc("Str_QA_Documents");
            if (Is(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")))
                                                                                       return Loc("Str_QA_Downloads");
            if (Is(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)))   return Loc("Str_QA_Pictures");

            var name = Path.GetFileName(p);
            return name.Length == 0 ? p : name;
        }

        private void AddPlace(string label, string path, bool pinned = false)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                Places.Add(new PickerPlace(label, path, pinned));
        }

        private void PinPlace(string path)
        {
            var list = PinnedPaths();
            if (list.Any(p => p.TrimEnd('\\').Equals(path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)))
                return;
            list.Add(path);
            App.SetSetting(PinnedKey, string.Join("|", list));
            BuildPlaces();
            SyncPlacesSelection();
        }

        private PickerPlace? _placesMenuPlace;

        private void Places_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _placesMenuPlace = ItemUnder<PickerPlace>(e.OriginalSource as DependencyObject);
            // Drives are dynamic, not pinned - nothing to remove; empty space likewise.
            if (_placesMenuPlace is not { Pinned: true }) e.Handled = true;
        }

        private void UnpinPlace_Click(object sender, RoutedEventArgs e)
        {
            if (_placesMenuPlace is not { Pinned: true } pl) return;
            var list = PinnedPaths()
                .Where(p => !p.TrimEnd('\\').Equals(pl.Path.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                .ToList();
            App.SetSetting(PinnedKey, string.Join("|", list));
            BuildPlaces();
            SyncPlacesSelection();
        }

        private PickerEntry? _filesMenuEntry;

        private void Files_ContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            _filesMenuEntry = ItemUnder<PickerEntry>(e.OriginalSource as DependencyObject);
            if (_filesMenuEntry is not { IsFolder: true }) e.Handled = true;   // only folders pin
        }

        private void FilePin_Click(object sender, RoutedEventArgs e)
        {
            if (_filesMenuEntry is { IsFolder: true } en) PinPlace(en.FullPath);
        }

        /// <summary>Marks the place matching the current folder, or clears the marker.</summary>
        private void SyncPlacesSelection()
        {
            bool was = _navigating;
            _navigating = true;
            PlacesList.SelectedItem = _currentDir.Length == 0 ? null : Places.FirstOrDefault(p =>
                p.Path.TrimEnd('\\').Equals(_currentDir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            _navigating = was;
        }

        /// <summary>The row model under a right-click, resolved by walking up to the ListBoxItem.</summary>
        private static T? ItemUnder<T>(DependencyObject? d) where T : class
        {
            while (d != null)
            {
                if (d is ListBoxItem lbi) return lbi.DataContext as T;
                d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                    ? System.Windows.Media.VisualTreeHelper.GetParent(d)
                    : LogicalTreeHelper.GetParent(d);
            }
            return null;
        }

        private static string Loc(string key)
            => Application.Current.TryFindResource(key) as string ?? key;

        // ── Navigation ───────────────────────────────────────────────────────────

        private void NavigateTo(string dir)
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) return;

            _navigating = true;
            _currentDir  = dir;
            PathBox.Text = dir;
            _raw.Clear();

            try
            {
                // The toggle gates two things together: attribute Hidden/System AND leading-dot
                // names - the Unix convention is all over a Windows home folder (.gradle, .ssh)
                // and those carry no Hidden attribute. Same gate in the folder tree (FolderTree.cs).
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    DirectoryInfo info;
                    try { info = new DirectoryInfo(sub); } catch { continue; }
                    if (!_showHidden)
                    {
                        if ((info.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (info.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    }
                    _raw.Add(new PickerEntry(info.Name, sub, true, 0, SafeTime(() => info.LastWriteTime)));
                }
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    FileInfo fi;
                    try { fi = new FileInfo(file); } catch { continue; }
                    if (!_showHidden)
                    {
                        if ((fi.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                        if (fi.Name.StartsWith(".", StringComparison.Ordinal)) continue;
                    }
                    _raw.Add(new PickerEntry(fi.Name, file, false, SafeLen(fi), SafeTime(() => fi.LastWriteTime)));
                }
            }
            catch { /* unauthorized / unreadable - show what we have */ }

            ApplySort();
            UpButton.IsEnabled = Directory.GetParent(dir) != null;
            UpdateInfoSummary();
            SyncPlacesSelection();
            _navigating = false;

            RecordRecent(dir);
        }

        private static DateTime SafeTime(Func<DateTime> get)
        {
            try { return get(); } catch { return DateTime.MinValue; }
        }

        private static long SafeLen(FileInfo fi)
        {
            try { return fi.Length; } catch { return 0; }
        }

        private void Up_Click(object sender, RoutedEventArgs e)
        {
            var parent = Directory.GetParent(_currentDir);
            if (parent != null) NavigateTo(parent.FullName);
        }

        private void Places_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (PlacesList.SelectedItem is PickerPlace p) NavigateTo(p.Path);
        }

        // ── Places list ─────────────────────────────────────────────────────────
        //
        // There was a drive/folder TREE above this, ported from KillerShell. It came out on
        // 2026-08-07: it duplicated the folder rows the file list already shows, took the whole
        // panel, and left the bookmarks - the part people actually use - as a cramped drawer
        // underneath. Folders are navigated in the list; folders worth keeping are right-clicked
        // and pinned here.

        private void InitPlacesFades()
        {
            // Edge fades follow the scroll position (KillerShell TreePanel.cs). ScrollChanged is
            // handled at the ListBox rather than dug out of its template: it bubbles, so the
            // inner ScrollViewer is reached without needing to have found it first. Loaded and
            // SizeChanged cover the passes where nothing scrolled but the extent moved. No
            // scrollbar lift: horizontal scrolling is disabled on this list.
            PlacesList.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler((_, _) => SyncPlacesEdgeFades()));
            PlacesList.SizeChanged += (_, _) => SyncPlacesEdgeFades();
            PlacesList.Loaded      += (_, _) => SyncPlacesEdgeFades();
        }

        /// <summary>Places-list twin of SyncTreeEdgeFades: same ramp, same rules.</summary>
        private void SyncPlacesEdgeFades()
        {
            var sv = FindDescendant<ScrollViewer>(PlacesList);
            if (sv == null) return;

            PlacesFadeTop.Opacity    = Ramp(sv.VerticalOffset, PlacesFadeTop.Height, 18);
            PlacesFadeBottom.Opacity = Ramp(sv.ExtentHeight - sv.ViewportHeight - sv.VerticalOffset,
                                            PlacesFadeBottom.Height, 22);
        }

        // Height is NaN until the border has been laid out, hence the fallback.
        private static double Ramp(double distance, double height, double fallback)
        {
            double h = double.IsNaN(height) || height <= 0 ? fallback : height;
            return Math.Min(1, Math.Max(0, distance) / h);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is T hit) return hit;
                var deeper = FindDescendant<T>(c);
                if (deeper != null) return deeper;
            }
            return null;
        }

        // FindDescendant takes the FIRST match of a type, and a ScrollViewer has two scrollbars,
        // so the orientation has to be checked rather than assumed.
        private static System.Windows.Controls.Primitives.ScrollBar? FindHorizontalBar(DependencyObject root)
        {
            int n = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < n; i++)
            {
                var c = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
                if (c is System.Windows.Controls.Primitives.ScrollBar sb &&
                    sb.Orientation == Orientation.Horizontal) return sb;
                var deeper = FindHorizontalBar(c);
                if (deeper != null) return deeper;
            }
            return null;
        }

        // ── Recent locations ─────────────────────────────────────────────────────

        private static List<string> LoadRecents()
            => [.. (App.GetSetting(RecentsKey) ?? "")
               .Split('|').Where(s => s.Length > 0)];

        private static void RecordRecent(string dir)
        {
            var list = LoadRecents();
            list.RemoveAll(p => p.TrimEnd('\\').Equals(dir.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase));
            list.Insert(0, dir);
            if (list.Count > RecentsMax) list.RemoveRange(RecentsMax, list.Count - RecentsMax);
            App.SetSetting(RecentsKey, string.Join("|", list));
        }

        private void RecentsBtn_Click(object sender, RoutedEventArgs e)
        {
            // Stale entries (unplugged drive, deleted folder) are filtered at open rather than
            // scrubbed from the store - the drive may be back tomorrow.
            var list = LoadRecents().Where(Directory.Exists).ToList();
            if (list.Count == 0) return;

            _navigating = true;              // rebinding must not raise a navigation
            RecentsList.ItemsSource = list;
            RecentsList.SelectedItem = null;
            _navigating = false;
            RecentsPopup.IsOpen = true;
        }

        private void RecentsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (RecentsList.SelectedItem is not string dir) return;
            RecentsPopup.IsOpen = false;
            NavigateTo(dir);
        }

        // ── Hidden / dot files ───────────────────────────────────────────────────

        private void ShowHidden_Click(object sender, RoutedEventArgs e)
        {
            _showHidden = !_showHidden;
            App.SetSetting(ShowHiddenKey, _showHidden ? "1" : "0");
            ApplyShowHiddenButton();

            if (_currentDir.Length > 0) NavigateTo(_currentDir);
        }

        private void ApplyShowHiddenButton()
        {
            // E7B3 eye at rest, E890 while showing - KillerShell's build-proven pair
            // (ViewOptions.cs). Codepoints, never literal PUA glyphs (family rule).
            ShowHiddenBtn.Content = ((char)(_showHidden ? 0xE890 : 0xE7B3)).ToString();
            ShowHiddenBtn.Tag     = _showHidden ? "on" : null;
        }

        private void Files_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_navigating) return;
            if (FileList.SelectedItem is PickerEntry en)
            {
                // Selecting a FILE fills the name box - that is the value being chosen. Selecting
                // a folder does not: it is a navigation target, and overwriting the typed name
                // with a folder name would lose what the user was in the middle of typing.
                if (!en.IsFolder) FileNameBox.Text = en.Name;
                SelName.Text = en.Name;
                SelMeta.Text = en.IsFolder ? en.ModifiedLabel : $"{en.SizeLabel}  |  {en.ModifiedLabel}";
            }
            else UpdateInfoSummary();
            UpdatePreview();
        }

        private void Files_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is not PickerEntry en) return;
            if (en.IsFolder) NavigateTo(en.FullPath);
            else Accept();
        }

        private void PathBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            var typed = PathBox.Text?.Trim();
            if (!string.IsNullOrEmpty(typed) && Directory.Exists(typed)) NavigateTo(typed!);
            e.Handled = true;
        }

        private void FileNameBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;

            var typed = FileNameBox.Text?.Trim() ?? "";

            // A directory typed into the name box navigates instead of accepting - matches the
            // Win32 dialogs, and is how people paste a path in.
            if (typed.Length > 0)
            {
                var asDir = Path.IsPathRooted(typed) ? typed : Path.Combine(_currentDir, typed);
                if (Directory.Exists(asDir)) { NavigateTo(asDir); FileNameBox.Clear(); return; }
            }

            // A wildcard retargets the listing rather than naming a file.
            if (typed.IndexOfAny(['*', '?']) >= 0)
            {
                _filterPatterns.Insert(0, [typed]);
                FilterCombo.Items.Insert(0, typed);
                FilterCombo.SelectedIndex = 0;
                FileNameBox.Clear();
                return;
            }

            Accept();
        }

        private void UpdateInfoSummary()
        {
            int folders = _raw.Count(x => x.IsFolder);
            int shown   = Entries.Count(x => !x.IsFolder);
            var leaf    = Path.GetFileName(_currentDir.TrimEnd('\\'));
            SelName.Text = leaf.Length == 0 ? _currentDir : leaf;
            SelMeta.Text = string.Format(Loc("Str_Sum_Counts"), folders, shown);
        }

        // ── View modes ───────────────────────────────────────────────────────────

        private void ViewList_Click(object sender, RoutedEventArgs e)    => SetView(0);
        private void ViewIcons_Click(object sender, RoutedEventArgs e)   => SetView(1);
        private void ViewDetails_Click(object sender, RoutedEventArgs e) => SetView(2);

        private void SetView(int mode)
        {
            _viewMode = mode;
            ApplyView();
        }

        /// <summary>
        /// The three views differ in panel, template AND scroll direction - that last one is the
        /// part that is easy to miss. List view wraps into columns and scrolls sideways, which only
        /// works if vertical scrolling is DISABLED: an enabled vertical ScrollViewer hands the panel
        /// infinite height, so a vertical WrapPanel never wraps and you get one tall column.
        /// </summary>
        private void ApplyView()
        {
            switch (_viewMode)
            {
                case 1:  // icons: grid, wraps across, scrolls down
                    FileList.ItemsPanel   = (ItemsPanelTemplate)FindResource("PanelIconGrid");
                    FileList.ItemTemplate = (DataTemplate)FindResource("IconTemplate");
                    ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Disabled);
                    ScrollViewer.SetVerticalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
                    break;

                case 2:  // details: one row per entry, scrolls down
                    FileList.ItemsPanel   = (ItemsPanelTemplate)FindResource("PanelStack");
                    FileList.ItemTemplate = (DataTemplate)FindResource("DetailsTemplate");
                    ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Disabled);
                    ScrollViewer.SetVerticalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
                    break;

                default: // list: columns of small icons, scrolls RIGHT
                    FileList.ItemsPanel   = (ItemsPanelTemplate)FindResource("PanelListCols");
                    FileList.ItemTemplate = (DataTemplate)FindResource("RowTemplate");
                    ScrollViewer.SetHorizontalScrollBarVisibility(FileList, ScrollBarVisibility.Auto);
                    ScrollViewer.SetVerticalScrollBarVisibility(FileList, ScrollBarVisibility.Disabled);
                    break;
            }

            DetailsHeader.Visibility = _viewMode == 2 ? Visibility.Visible : Visibility.Collapsed;
            ViewListBtn.Tag    = _viewMode == 0 ? "on" : null;
            ViewIconsBtn.Tag   = _viewMode == 1 ? "on" : null;
            ViewDetailsBtn.Tag = _viewMode == 2 ? "on" : null;
        }

        /// <summary>
        /// List view wraps entries into columns and scrolls horizontally. A normal mouse wheel
        /// only asks WPF to scroll vertically, which is disabled in this view, so translate the
        /// wheel delta to the horizontal scrollbar. Icon and details views remain vertical.
        /// </summary>
        private void FileList_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (_viewMode != 0) return;
            var sv = FindDescendant<ScrollViewer>(FileList);
            if (sv is null) return;
            sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta);
            e.Handled = true;
        }

        // ── Sorting ──────────────────────────────────────────────────────────────

        private void SortName_Click(object sender, RoutedEventArgs e)     => SetSort(0);
        private void SortSize_Click(object sender, RoutedEventArgs e)     => SetSort(1);
        private void SortModified_Click(object sender, RoutedEventArgs e) => SetSort(2);

        private void SetSort(int key)
        {
            if (_sortKey == key) _sortAsc = !_sortAsc;
            else { _sortKey = key; _sortAsc = true; }
            ApplySort();
        }

        /// <summary>
        /// Rebuilds Entries from _raw: filter applied, folders always before files, then the
        /// active sort key. Folders-first is not a sort key - it is the frame the sort runs in.
        /// </summary>
        private void ApplySort()
        {
            var visible = _raw.Where(PassesFilter);

            IOrderedEnumerable<PickerEntry> ordered = _sortKey switch
            {
                1 => _sortAsc ? visible.OrderBy(x => !x.IsFolder).ThenBy(x => x.SizeBytes)
                              : visible.OrderBy(x => !x.IsFolder).ThenByDescending(x => x.SizeBytes),
                2 => _sortAsc ? visible.OrderBy(x => !x.IsFolder).ThenBy(x => x.Modified)
                              : visible.OrderBy(x => !x.IsFolder).ThenByDescending(x => x.Modified),
                _ => _sortAsc ? visible.OrderBy(x => !x.IsFolder).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
                              : visible.OrderBy(x => !x.IsFolder).ThenByDescending(x => x.Name, StringComparer.OrdinalIgnoreCase),
            };

            Entries.Clear();
            foreach (var e in ordered) Entries.Add(e);

            NameArrow.Text = _sortKey == 0 ? (_sortAsc ? ArrowUp : ArrowDown) : "";
            SizeArrow.Text = _sortKey == 1 ? (_sortAsc ? ArrowUp : ArrowDown) : "";
            ModArrow.Text  = _sortKey == 2 ? (_sortAsc ? ArrowUp : ArrowDown) : "";

            EmptyHint.Visibility = Entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        // ── Accept / cancel ──────────────────────────────────────────────────────

        private void OK_Click(object sender, RoutedEventArgs e) => Accept();

        /// <summary>
        /// Resolves the name box to a full path and applies the mode's rules. Anything that fails
        /// leaves the dialog OPEN with focus back in the name box - a file dialog that closes on a
        /// bad name and makes you start over is the worst outcome.
        /// </summary>
        private void Accept()
        {
            // Multiselect: several files highlighted wins outright. The name box shows a quoted
            // list in that state and is not worth re-parsing - the selection IS the answer. A
            // single highlighted file falls through to the normal path so every rule below
            // (extension, existence) still applies.
            if (Multiselect && _mode == FileDialogMode.Open)
            {
                var picked = FileList.SelectedItems.OfType<PickerEntry>()
                                                   .Where(x => !x.IsFolder)
                                                   .Select(x => x.FullPath)
                                                   .ToArray();
                if (picked.Length > 1)
                {
                    FileNames = picked;
                    FileName  = picked[0];
                    RememberAcceptedDirectory();
                    _pendingResult = true;   // applied after the fade - see OnClosing
                    Close();
                    return;
                }
            }

            var typed = FileNameBox.Text?.Trim().Trim('"') ?? "";
            if (typed.Length == 0)
            {
                // Nothing typed but a file is highlighted: take that.
                if (FileList.SelectedItem is PickerEntry sel && !sel.IsFolder) typed = sel.Name;
                else { FileNameBox.Focus(); return; }
            }

            var full = Path.IsPathRooted(typed) ? typed : Path.Combine(_currentDir, typed);

            if (_mode == FileDialogMode.Save)
            {
                // The extension follows the ACTIVE filter, so picking "CSV files" in the type
                // combo is enough to get a .csv - DefaultExt only decides when the filter names
                // no single extension (a wildcard or a multi-pattern entry).
                if (AddExtension && string.IsNullOrEmpty(Path.GetExtension(full)))
                {
                    string? ext = ActiveFilterExt();
                    if (ext == null && !string.IsNullOrEmpty(DefaultExt))
                        ext = DefaultExt.StartsWith(".") ? DefaultExt : "." + DefaultExt;
                    if (ext != null) full += ext;
                }

                // The directory must exist; we do not silently create trees on the user's behalf.
                var dir = Path.GetDirectoryName(full);
                if (CheckPathExists && (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)))
                {
                    SelMeta.Text = Loc("Str_Dlg_NoSuchFolder");
                    FileNameBox.Focus();
                    return;
                }

                if (OverwritePrompt && File.Exists(full))
                {
                    // The caption said "KillerPDF" - visibly, in a KillerScan dialog - because
                    // this file was carried across as foreign source. It is this app's now.
                    var answer = MessageBox.Show(this,
                        string.Format(Loc("Str_Dlg_OverwriteMsg"), Path.GetFileName(full)),
                        "KillerScan", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                    if (answer != MessageBoxResult.Yes) { FileNameBox.Focus(); return; }
                }
            }
            else
            {
                if (CheckFileExists && !File.Exists(full))
                {
                    SelMeta.Text = Loc("Str_Dlg_NoSuchFile");
                    FileNameBox.Focus();
                    FileNameBox.SelectAll();
                    return;
                }
            }

            FileName = full;
            FileNames = [full];   // always populated on success, so callers can read either
            RememberAcceptedDirectory();
            _pendingResult = true;   // applied after the fade - see OnClosing
            Close();
        }

        private void RememberAcceptedDirectory()
        {
            if (_currentDir.Length > 0 && Directory.Exists(_currentDir))
                App.SetSetting(_mode == FileDialogMode.Open ? LastOpenKey : LastSaveKey, _currentDir);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            // Handled, or the press bubbles on to Resize_MouseDown AFTER DragMove's modal loop
            // returns - by then the button is UP, and WM_NCLBUTTONDOWN with no button held puts
            // Windows into its sticky keyboard-style size loop: the window chases the mouse,
            // resizing, until a click. (2026-07-30)
            e.Handled = true;
            DragMove();
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.Key == Key.Escape) { Close(); e.Handled = true; return; }
            base.OnKeyDown(e);
        }

        // ---- edge resize, done by hand ----
        //
        // This dialog carries no shell:WindowChrome - on an AllowsTransparency window it fills its
        // own non-client area and that paints as a flat band around the card. So the 10px halo does
        // the job instead: work out which edge the pointer is in and hand the drag to Windows with
        // WM_NCLBUTTONDOWN, exactly as Shell/Chrome.cs does for the main window's corner grip.
        // Windows then runs its own resize loop, so this gets the real snapping and live preview
        // rather than a hand-rolled approximation. (2026-07-30)

        private const int WM_NCLBUTTONDOWN = 0x00A1;
        private const int HTLEFT = 10, HTRIGHT = 11, HTTOP = 12, HTTOPLEFT = 13,
                          HTTOPRIGHT = 14, HTBOTTOM = 15, HTBOTTOMLEFT = 16, HTBOTTOMRIGHT = 17;

        /// <summary>Width of the grab band, matching the ResizeBorderThickness WindowChrome used.</summary>
        private const double ResizeEdge = 8;

        /// <summary>Which edge the pointer is in, or 0 for none.</summary>
        private int HitTestEdge(Point p)
        {
            bool left   = p.X <= ResizeEdge;
            bool right  = p.X >= ActualWidth  - ResizeEdge;
            bool top    = p.Y <= ResizeEdge;
            bool bottom = p.Y >= ActualHeight - ResizeEdge;

            if (top && left)     return HTTOPLEFT;
            if (top && right)    return HTTOPRIGHT;
            if (bottom && left)  return HTBOTTOMLEFT;
            if (bottom && right) return HTBOTTOMRIGHT;
            if (left)            return HTLEFT;
            if (right)           return HTRIGHT;
            if (top)             return HTTOP;
            if (bottom)          return HTBOTTOM;
            return 0;
        }

        private void Resize_MouseMove(object sender, MouseEventArgs e)
        {
            Cursor = HitTestEdge(e.GetPosition(this)) switch
            {
                HTLEFT or HTRIGHT           => Cursors.SizeWE,
                HTTOP or HTBOTTOM           => Cursors.SizeNS,
                HTTOPLEFT or HTBOTTOMRIGHT  => Cursors.SizeNWSE,
                HTTOPRIGHT or HTBOTTOMLEFT  => Cursors.SizeNESW,
                _                           => Cursors.Arrow,
            };
        }

        private void Resize_MouseDown(object sender, MouseButtonEventArgs e)
        {
            // Only a press on the halo Grid ITSELF may start a resize. Every press on the card
            // bubbles up here too (with OriginalSource somewhere in the card's tree), and a stale
            // bubbled press must never reach WM_NCLBUTTONDOWN - see TitleBar_MouseLeftButtonDown.
            if (!ReferenceEquals(e.OriginalSource, sender)) return;
            int ht = HitTestEdge(e.GetPosition(this));
            if (ht == 0) return;
            e.Handled = true;
            SendMessage(new System.Windows.Interop.WindowInteropHelper(this).Handle,
                        WM_NCLBUTTONDOWN, new IntPtr(ht), IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>
        /// Point the preview at the current selection. Shows the hint instead when the selection is
        /// a folder, a non-image, or an image that will not decode - Preview returns null for all
        /// three, so one null check covers them.
        /// </summary>
        private void UpdatePreview()
        {
            if (!ShowPreview || PreviewImage is null) return;
            var src = (FileList.SelectedItem as PickerEntry)?.Preview;
            PreviewImage.Source = src;
            PreviewImage.Visibility = src is null ? Visibility.Collapsed : Visibility.Visible;
            PreviewHint.Visibility  = src is null ? Visibility.Visible  : Visibility.Collapsed;
        }
}
}
