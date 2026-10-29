using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace KillerScan.Terminal
{
    internal sealed partial class TerminalControl : FrameworkElement, IDisposable
    {
        private const double LeftInset = 4;

        private static readonly string Esc = ((char)0x1B).ToString();
        private static readonly string Csi = Esc + "[";
        private static readonly string Bs  = ((char)0x08).ToString();
        private static readonly string Del = ((char)0x7F).ToString();
        private static readonly string Nul = ((char)0x00).ToString();

        private readonly TerminalBuffer _buf;
        private readonly VtParser _parser;
        private ConPtySession? _pty;
        private TerminalInputWriter? _input;
        private TerminalPalette _palette;

        private readonly Queue<byte[]> _incoming = new();
        private readonly object _gate = new();
        private int _incomingBytes;
        private DispatcherTimer? _pump;
        private int _drawnVersion = -1;

        private GlyphTypeface? _glyphs;
        private double _cellW, _cellH, _baseline;
        private double _fontSize = 11;
        private string _fontFamily = string.Empty;
        private float _pixelsPerDip = 1f;

        private int _scroll;                 // lines scrolled back; 0 is live
        private int _horizontalScroll;
        private bool _cursorOn = true;
        private DispatcherTimer? _blink;

        private bool _closed;
        private Thread? _reader;

        public event Action<int>? Exited;
        public event Action<Exception>? StartFailed;
        public event Action<string>? ManagedInput;
        public event Action? Disposed;
        public event Action? PromptReady;
        public event Action? SpeedTestRequested;
        public bool IsManaged { get; private set; }
        private bool _atPrompt;
        private bool _speedTestRequested;
        public bool HasRunningCommand => IsManaged || (_pty != null && !_pty.HasExited && !_atPrompt);
        public bool HasShell => _pty != null && !_pty.HasExited;

        public void BeginManagedSession()
        {
            if (_closed || HasRunningCommand) throw new InvalidOperationException();
            IsManaged = true;
            _cursorOn = false;
        }

        public void WriteManaged(string text)
        {
            if (!_closed && IsManaged)
            {
                if (_bridge != null) { WriteShellManaged(text); return; }
                ApplySize();
                var bytes = Encoding.UTF8.GetBytes(text);
                _parser.Feed(bytes, bytes.Length);
                InvalidateVisual();
            }
        }

        public void EndManagedSession()
        {
            if (IsManaged) _buf.PreserveScreenInHistory();
            IsManaged = false;
            ManagedInput = null;
            _cursorOn = true;
            if (HasShell) Send("\u0003");
            InvalidateVisual();
        }

        public TerminalBuffer Buffer => _buf;

        public TerminalControl()
        {
            _palette = TerminalPalette.For(TerminalSkin.Default);
            _buf = new TerminalBuffer(80, 25);
            _parser = new VtParser(_buf);

            Focusable = true;
            FocusVisualStyle = null;
            ClipToBounds = true;

            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
            BuildContextMenu();

            _buf.Respond += Send;
            _buf.SpeedTestRequested += () => _speedTestRequested = true;
            _buf.PromptReady += () =>
            {
                _atPrompt = true;
                PromptReady?.Invoke();
                if (_speedTestRequested)
                {
                    _speedTestRequested = false;
                    SpeedTestRequested?.Invoke();
                }
            };

            LoadFontPreference();
            LoadFont();
            Loaded += (_, _) => Focus();
        }

        public double WidthForColumns(int cols) => LeftInset + cols * _cellW;

        public void SetFontSize(double size)
        {
            _fontSize = Math.Max(8, Math.Min(28, size));
            LoadFont();
            ApplySize();
            InvalidateVisual();

        }

        public void RefreshTheme()
        {
            _palette = TerminalPalette.For(_palette.Skin);
            Shell.MainWindow.WritePromptPalette();
            InvalidateVisual();
        }

        private static readonly string[] FontOrder =
        [
            "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console", "Courier New",
        ];

        private GlyphTypeface? _fallback;
        private double _fallbackScale = 1;

        public void ReloadFont()
        {
            LoadFont();
            ApplySize();
            InvalidateVisual();
        }

        private void LoadFont()
        {

            string[] order = Fonts.SystemFontFamilies.Any(f => f.Source == "ProFont IIx Nerd Font")
                ? ["ProFont IIx Nerd Font", .. FontOrder] : FontOrder;
            if (!string.IsNullOrEmpty(_fontFamily)) order = [_fontFamily, .. order];

            foreach (var name in order)
            {
                try
                {
                    var tf = new Typeface(new FontFamily(name), FontStyles.Normal,
                                          FontWeights.Normal, FontStretches.Normal);
                    if (!tf.TryGetGlyphTypeface(out var gt)) continue;
                    if (!gt.CharacterToGlyphMap.ContainsKey('M')) continue;

                    _glyphs = gt;
                    _fontFamily = name;

                    double ppd = _pixelsPerDip > 0 ? _pixelsPerDip : 1.0;
                    _cellW = Math.Max(1.0, Math.Round(gt.AdvanceWidths[gt.CharacterToGlyphMap['M']] * _fontSize * ppd)) / ppd;
                    _cellH = Math.Max(1.0, Math.Ceiling(gt.Height * _fontSize * ppd)) / ppd;
                    _baseline = Math.Round(gt.Baseline * _fontSize * ppd) / ppd;

                    _fallback = SystemFaceFor(0xE0B0);
                    _fallbackScale = 1;
                    if (_fallback != null
                        && _fallback.CharacterToGlyphMap.TryGetValue(0xE0B0, out ushort probe))
                    {
                        double own = _fallback.AdvanceWidths[probe] * _fontSize;
                        if (own > 0.01) _fallbackScale = Math.Round(_cellW / own, 4);
                    }
                    return;
                }
                catch { /* try the next face */ }
            }
        }

        public void Start(string commandLine, string workingDirectory)
        {
            if (_closed) throw new ObjectDisposedException(nameof(TerminalControl));
            if (_pty != null) throw new InvalidOperationException();
            ApplySize();
            try
            {
                _pty = ConPtySession.Start(commandLine, workingDirectory,
                    (short)_buf.Cols, (short)_buf.Rows);
                _input = new TerminalInputWriter(_pty.Input);
            }
            catch (Exception ex)
            {
                StartFailed?.Invoke(ex);
                return;
            }

            var session = _pty;
            var stream = session.Output;
            _reader = new Thread(() =>
            {
                var bytes = new byte[8192];
                try
                {
                    int count;
                    while ((count = stream.Read(bytes, 0, bytes.Length)) > 0)
                    {
                        lock (_gate)
                        {
                            while (!_closed && _incomingBytes >= 4 * 1024 * 1024)
                                Monitor.Wait(_gate);
                            if (_closed) continue;
                            var chunk = new byte[count];
                            Array.Copy(bytes, chunk, count);
                            _incoming.Enqueue(chunk);
                            _incomingBytes += count;
                        }
                    }
                }
                catch (System.IO.IOException) { }
                catch (ObjectDisposedException) { }
            }) { IsBackground = true, Name = "ConPTY output" };
            _reader.Start();

            session.Exited += code =>
            {
                // ClosePseudoConsole has completed, so the reader can drain the final output.
                _reader?.Join(2000);
                if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    if (_closed) return;
                    Drain(true);
                    _pump?.Stop();
                    _blink?.Stop();
                    _cursorOn = false;
                    InvalidateVisual();
                    Exited?.Invoke(code);
                }));
            };
            session.WatchForExit();

            _pump = new DispatcherTimer(DispatcherPriority.Render)
            { Interval = TimeSpan.FromMilliseconds(16) };
            _pump.Tick += (_, _) => Drain();
            _pump.Start();
            _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
            _blink.Tick += (_, _) =>
            {
                if (!_buf.CursorBlink) { _cursorOn = true; return; }
                _cursorOn = !_cursorOn;
                InvalidateVisual();
            };
            _blink.Start();
        }

        private void Drain(bool complete = false)
        {
            int previousHistory = _buf.ScrollbackCount;
            int budget = complete ? int.MaxValue : 262144;
            while (budget > 0)
            {
                byte[] chunk;
                lock (_gate)
                {
                    if (_incoming.Count == 0) break;
                    chunk = _incoming.Dequeue();
                    _incomingBytes -= chunk.Length;
                    Monitor.PulseAll(_gate);
                }
                _parser.Feed(chunk, chunk.Length);
                budget -= chunk.Length;
            }
            if (_scroll > 0) _scroll += _buf.ScrollbackCount - previousHistory;
            if (_buf.Version != _drawnVersion) InvalidateVisual();
        }

        /// <summary>
        /// Demo mode only. Renders scripted text as though a shell had printed it: no process is
        /// started, so a screenshot build shows the fabricated session rather than the machine it
        /// is running on. Typing goes nowhere, because there is nothing on the other end.
        /// </summary>
        public void ShowScript(string text)
        {
            if (_closed || _pty != null || string.IsNullOrEmpty(text)) return;
            ApplySize();
            _parser.Feed(Encoding.UTF8.GetBytes(text), Encoding.UTF8.GetByteCount(text));
            InvalidateVisual();
        }

        /// <summary>
        /// The whole session as plain text: scrollback and screen, colours and attributes dropped,
        /// trailing blank columns trimmed off each line. What you would have got by selecting it
        /// all and copying, without needing the session to still be running.
        /// </summary>
        public string GetText()
        {
            var text = new StringBuilder();
            var line = new StringBuilder();
            for (int i = 0; i < _buf.TotalLines; i++)
            {
                line.Clear();
                foreach (var cell in _buf.LineAt(i))
                    line.Append(cell.Text);
                text.AppendLine(line.ToString().TrimEnd());
            }
            // A terminal is mostly empty space below the cursor; keep the transcript, drop the rest.
            return text.ToString().TrimEnd() + Environment.NewLine;
        }

        public void Send(string s)
        {
            if (_closed) return;
            if (IsManaged) { ManagedInput?.Invoke(s); return; }
            if (_pty == null || _pty.HasExited || string.IsNullOrEmpty(s)) return;
            if (s.Contains("\r") || s.Contains("\n") || s.Contains("\u0003")) _atPrompt = false;
            // Written in the background and in order: a busy child that is not reading input can
            // make each write wait, which on this thread froze the window during large pastes.
            if (_input?.Enqueue(Encoding.UTF8.GetBytes(s)) == false) RefuseInput();
        }

        /// <summary>
        /// Raised when input could not be queued, because the shell has not read what is already
        /// waiting or a paste is larger than the queue holds. The input is not sent, so the host
        /// says so instead of letting it vanish.
        /// </summary>
        public event Action? InputRefused;

        private void RefuseInput()
        {
            System.Media.SystemSounds.Beep.Play();
            InputRefused?.Invoke();
        }

        public void Stop() => Dispose();

        public void Dispose()
        {
            if (_closed) return;
            lock (_gate)
            {
                _closed = true;
                _incoming.Clear();
                _incomingBytes = 0;
                Monitor.PulseAll(_gate);
            }
            _pump?.Stop();
            _blink?.Stop();
            SelectionMouseUp();
            _automationTimer?.Stop();
            Disposed?.Invoke();
            _bridge?.Dispose();
            ManagedInput = null;
            Disposed = null;
            _input?.Dispose();
            _input = null;
            var session = _pty;
            _pty = null;
            // Closing a pseudoconsole can block until its output is drained.
            if (session != null) ThreadPool.QueueUserWorkItem(_ => session.Dispose());
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);

            float ppd = (float)VisualTreeHelper.GetDpi(this).PixelsPerDip;
            if (Math.Abs(ppd - _pixelsPerDip) > 0.0001f)
            {
                _pixelsPerDip = ppd;
                LoadFont();
            }
            ApplySize();
        }

        private void ApplySize()
        {
            if (_cellW <= 0 || _cellH <= 0) return;

            if (ActualWidth <= LeftInset || ActualHeight <= 0) return;

            int cols = Math.Max(96, (int)(Math.Max(0, ActualWidth - LeftInset) / _cellW));
            int rows = Math.Max(1, (int)(ActualHeight / _cellH));

            if (cols != _buf.Cols || rows != _buf.Rows)
            {
                int previousHistory = _buf.ScrollbackCount;
                _buf.Resize(cols, rows);
                if (_scroll > 0) _scroll += _buf.ScrollbackCount - previousHistory;
                _pty?.Resize((short)cols, (short)rows);
                InvalidateVisual();
            }

        }

        protected override void OnRender(DrawingContext dc)
        {
            QueueAutomationUpdate();
            _drawnVersion = _buf.Version;

            var rect = new Rect(0, 0, ActualWidth, ActualHeight);
            var bg = new SolidColorBrush(_palette.Background);
            bg.Freeze();
            dc.DrawRectangle(bg, null, rect);

            if (TryFindResource("GrainTileBrush") is Brush grain)
            {
                double opacity = TryFindResource("GrainOpacity") is double d ? d : 0.2;
                dc.PushOpacity(opacity);
                dc.DrawRectangle(grain, null, rect);
                dc.Pop();
            }

            if (_glyphs == null) return;

            int first = Math.Max(0, _buf.ScrollbackCount - _scroll);
            int visibleColumns = Math.Max(1, (int)((ActualWidth - LeftInset) / _cellW));
            int visibleWidth = Enumerable.Range(0, _buf.Rows).Max(r => _buf.LineAt(first + r).Length);
            _horizontalScroll = Math.Min(_horizontalScroll, Math.Max(0, visibleWidth - visibleColumns));
            dc.PushTransform(new TranslateTransform(LeftInset - _horizontalScroll * _cellW, 0));

            for (int r = 0; r < _buf.Rows; r++)
            {
                var line = _buf.LineAt(first + r);
                double y = r * _cellH;
                DrawBackgrounds(dc, line, y);
                DrawGlyphs(dc, line, y);
            }

            DrawSelection(dc, first);   // TerminalSelection.cs
            DrawCursor(dc);
            dc.Pop();

            if (_palette.Scanlines) DrawScanlines(dc);
        }

        private void DrawBackgrounds(DrawingContext dc, Cell[] line, double y)
        {
            int c = 0;
            while (c < line.Length)
            {
                var color = CellBg(line[c]);
                int start = c;
                while (c < line.Length && CellBg(line[c]) == color) c++;
                if (color != _palette.Background)
                {
                    var b = new SolidColorBrush(color);
                    b.Freeze();
                    dc.DrawRectangle(b, null,
                        new Rect(start * _cellW, y, (c - start) * _cellW, _cellH));
                }
            }
        }

        private Color CellBg(Cell cell)
        {
            bool inv = (cell.Flags & CellFlags.Inverse) != 0;
            return _palette.Resolve(inv ? cell.Fg : cell.Bg, !inv);
        }

        private Color CellFg(Cell cell)
        {
            if ((cell.Flags & CellFlags.Hidden) != 0) return CellBg(cell);
            bool inv = (cell.Flags & CellFlags.Inverse) != 0;
            var fg = _palette.Resolve(inv ? cell.Bg : cell.Fg, inv);

            return _palette.Readable(fg, CellBg(cell));
        }

        private void DrawGlyphs(DrawingContext dc, Cell[] line, double y)
        {
            var gt = _glyphs!;
            int c = 0;
            while (c < line.Length)
            {
                if (line[c].Ch <= 0 || (line[c].Ch == ' ' && line[c].Cluster == null)) { c++; continue; }

                if (line[c].Cluster != null || line[c].Width == 2)
                {
                    var cell = line[c];
                    double clusterX = c * _cellW, width = Math.Max(1, (int)cell.Width) * _cellW;
                    var clusterBrush = new SolidColorBrush(CellFg(cell));
                    if ((cell.Flags & CellFlags.Faint) != 0) clusterBrush.Opacity = 0.6;
                    var typeface = new Typeface(new FontFamily(_fontFamily),
                        (cell.Flags & CellFlags.Italic) != 0 ? FontStyles.Italic : FontStyles.Normal,
                        (cell.Flags & CellFlags.Bold) != 0 ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
                    var text = new FormattedText(cell.Text, System.Globalization.CultureInfo.InvariantCulture,
                        FlowDirection.LeftToRight, typeface, _fontSize, clusterBrush, _pixelsPerDip);
                    dc.PushClip(new RectangleGeometry(new Rect(clusterX, y, width, _cellH)));
                    dc.DrawText(text, new Point(clusterX, y + _baseline - text.Baseline));
                    if ((cell.Flags & CellFlags.Underline) != 0)
                        dc.DrawRectangle(clusterBrush, null, new Rect(clusterX, y + _baseline + 1.5, width, 1));
                    if ((cell.Flags & CellFlags.Strike) != 0)
                        dc.DrawRectangle(clusterBrush, null, new Rect(clusterX, y + _baseline * 0.65, width, 1));
                    dc.Pop(); c++; continue;
                }

                var fg = CellFg(line[c]);
                var flags = line[c].Flags;
                int start = c;

                var face = FaceFor(gt, line[c].Ch);
                bool viaFallback = !ReferenceEquals(face, gt);

                var indices = new List<ushort>();
                var widths = new List<double>();

                while (c < line.Length && line[c].Ch > 0 && line[c].Ch != ' '
                       && line[c].Cluster == null && line[c].Width != 2
                       && CellFg(line[c]) == fg && line[c].Flags == flags
                       && ReferenceEquals(FaceFor(gt, line[c].Ch), face)

                       && (indices.Count == 0 || !viaFallback))
                {

                    int cp = line[c].Ch;
                    if (!face.CharacterToGlyphMap.TryGetValue(cp, out ushort gi))
                        face.CharacterToGlyphMap.TryGetValue(0x25A1, out gi);

                    indices.Add(gi);
                    widths.Add(_cellW);
                    c++;
                }

                if (indices.Count == 0) continue;

                double x = start * _cellW;
                var brush = new SolidColorBrush(
                    (flags & CellFlags.Faint) != 0
                        ? Color.FromArgb(0x99, fg.R, fg.G, fg.B)
                        : fg);
                brush.Freeze();

                bool stretch = ReferenceEquals(face, _fallback) && Math.Abs(_fallbackScale - 1) > 0.005;
                if (stretch) dc.PushTransform(new ScaleTransform(_fallbackScale, 1, x, 0));

                if (_palette.Glow > 0)
                {
                    var glow = new SolidColorBrush(
                        Color.FromArgb((byte)(70 * _palette.Glow), fg.R, fg.G, fg.B));
                    glow.Freeze();
                    var under = MakeRun(face, indices, widths, new Point(x, y + _baseline + 0.7));
                    if (under != null) dc.DrawGlyphRun(glow, under);
                }

                var run = MakeRun(face, indices, widths, new Point(x, y + _baseline));
                if (run != null) dc.DrawGlyphRun(brush, run);
                // Simulate bold within the fixed cell even when the chosen bitmap-style font
                // has no bold face. Keep column widths and the baseline unchanged.
                if (run != null && (flags & CellFlags.Bold) != 0)
                {
                    dc.PushTransform(new TranslateTransform(0.5 / _pixelsPerDip, 0));
                    dc.DrawGlyphRun(brush, run);
                    dc.Pop();
                }

                if (stretch) dc.Pop();

                double w = (c - start) * _cellW;
                if ((flags & CellFlags.Underline) != 0)
                    dc.DrawRectangle(brush, null, new Rect(x, y + _baseline + 1.5, w, 1));
                if ((flags & CellFlags.Strike) != 0)
                    dc.DrawRectangle(brush, null, new Rect(x, y + _baseline * 0.65, w, 1));
            }
        }

        private GlyphTypeface FaceFor(GlyphTypeface primary, int cp)
        {
            if (primary.CharacterToGlyphMap.ContainsKey(cp)) return primary;
            if (_fallback != null && _fallback.CharacterToGlyphMap.ContainsKey(cp)) return _fallback;
            return SystemFaceFor(cp) ?? primary;
        }

        private static readonly Dictionary<int, GlyphTypeface?> _sysFaceCache = [];

        // Every installed family's regular face, resolved once. Looking a codepoint up is then a map
        // check per face rather than building a Typeface for every font, inside OnRender, each time
        // output shows a codepoint not seen before.
        private static readonly Lazy<List<(string Name, GlyphTypeface Face)>> _systemFaces = new(() =>
        {
            var faces = new List<(string, GlyphTypeface)>();
            foreach (var fam in Fonts.SystemFontFamilies)
                try
                {
                    if (new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal).TryGetGlyphTypeface(out var face))
                        faces.Add((fam.Source ?? string.Empty, face));
                }
                catch { /* a face that will not resolve is just not a candidate */ }
            return faces;
        });

        private static GlyphTypeface? SystemFaceFor(int cp)
        {
            lock (_sysFaceCache)
                if (_sysFaceCache.TryGetValue(cp, out var cached)) return cached;

            GlyphTypeface? found = null;
            try
            {
                if (cp is >= 0xE000 and <= 0xF8FF)
                {

                    foreach (var (name, face) in _systemFaces.Value)
                    {
                        if (name.IndexOf("Nerd Font", StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("NerdFont", StringComparison.OrdinalIgnoreCase) < 0
                            && !name.EndsWith(" NF", StringComparison.OrdinalIgnoreCase)) continue;
                        if (face.CharacterToGlyphMap.ContainsKey(cp)) { found = face; break; }
                    }
                }
                else
                {

                    foreach (var name in new[] { "Segoe UI Symbol", "Segoe UI Emoji", "Segoe UI" })
                        if (TryFaceFor(new FontFamily(name), cp, out found)) break;

                    if (found == null)
                        foreach (var (_, face) in _systemFaces.Value)
                            if (face.CharacterToGlyphMap.ContainsKey(cp)) { found = face; break; }
                }
            }
            catch { found = null; }

            lock (_sysFaceCache)
            {
                // Bounded: output full of distinct rare codepoints must not grow it without end.
                if (_sysFaceCache.Count >= 8192) _sysFaceCache.Clear();
                _sysFaceCache[cp] = found;
            }
            return found;
        }

        private static bool TryFaceFor(FontFamily fam, int cp, out GlyphTypeface? face)
        {
            face = null;
            try
            {
                var tf = new Typeface(fam, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
                if (tf.TryGetGlyphTypeface(out var gt) && gt.CharacterToGlyphMap.ContainsKey(cp))
                {
                    face = gt;
                    return true;
                }
            }
            catch { /* a face that will not resolve is just not a candidate */ }
            return false;
        }

        private GlyphRun? MakeRun(GlyphTypeface gt, IList<ushort> indices, IList<double> widths, Point origin)
        {
            try
            {
                return new GlyphRun(gt, 0, false, _fontSize, _pixelsPerDip, indices, origin,
                                    widths, null, null, null, null, null, null);
            }
            catch { return null; }   // a face that refuses a run must not take the pane down
        }

        private void DrawCursor(DrawingContext dc)
        {
            if (!_buf.CursorVisible || !_cursorOn || _scroll != 0 || !IsKeyboardFocusWithin) return;

            var cursorLine = _buf.LineAt(_buf.ScrollbackCount + _buf.CursorRow);
            int cursorColumn = _buf.CursorCol;
            if (cursorLine[cursorColumn].Ch < 0 && cursorColumn > 0) cursorColumn--;
            var cell = cursorLine[cursorColumn];
            double cursorWidth = Math.Max(1, (int)cell.Width) * _cellW;
            double x = cursorColumn * _cellW;
            double y = _buf.CursorRow * _cellH;
            var b = new SolidColorBrush(_palette.Cursor);
            b.Freeze();

            switch (_buf.CursorShape)
            {
                case 1: dc.DrawRectangle(b, null, new Rect(x, y + _cellH - 2, cursorWidth, 2)); break;
                case 2: dc.DrawRectangle(b, null, new Rect(x, y, 2, _cellH)); break;
                default:
                    dc.DrawRectangle(b, null, new Rect(x, y, cursorWidth, _cellH));

                    if ((cell.Cluster != null || cell.Width == 2) && cell.Ch > 0)
                    {
                        var text = new FormattedText(cell.Text, System.Globalization.CultureInfo.InvariantCulture,
                            FlowDirection.LeftToRight, new Typeface(_fontFamily), _fontSize,
                            new SolidColorBrush(_palette.Background), _pixelsPerDip);
                        dc.PushClip(new RectangleGeometry(new Rect(x, y, cursorWidth, _cellH)));
                        dc.DrawText(text, new Point(x, y + _baseline - text.Baseline));
                        dc.Pop();
                    }
                    else if (cell.Ch > 0 && cell.Ch != ' ' && _glyphs != null)
                    {
                        var face = FaceFor(_glyphs, cell.Ch);
                        if (face.CharacterToGlyphMap.TryGetValue(cell.Ch, out ushort gi))
                        {
                            var hole = new SolidColorBrush(_palette.Background);
                            hole.Freeze();
                            var run = MakeRun(face, [gi], [_cellW], new Point(x, y + _baseline));
                            if (run != null) dc.DrawGlyphRun(hole, run);
                        }
                    }
                    break;
            }
        }

        private void DrawScanlines(DrawingContext dc)
        {
            var b = new SolidColorBrush(Color.FromArgb(0x30, 0, 0, 0));
            b.Freeze();
            for (double y = 0; y < ActualHeight; y += 2)
                dc.DrawRectangle(b, null, new Rect(0, y, ActualWidth, 1));
        }

        protected override void OnMouseWheel(MouseWheelEventArgs e)
        {
            if ((Keyboard.Modifiers & ModifierKeys.Shift) != 0)
            {
                int visible = Math.Max(1, (int)((ActualWidth - LeftInset) / _cellW));
                int widest = Enumerable.Range(0, _buf.TotalLines).Max(i => _buf.LineAt(i).Length);
                _horizontalScroll = Math.Max(0, Math.Min(Math.Max(0, widest - visible),
                    _horizontalScroll + (e.Delta > 0 ? -4 : 4)));
                InvalidateVisual();
                e.Handled = true;
                return;
            }

            if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            {
                SetFontSize(_fontSize + (e.Delta > 0 ? 1 : -1));
                e.Handled = true;
                return;
            }

            if (_buf.AltScreen)
            {
                string arrow = Csi + (e.Delta > 0 ? "A" : "B");
                Send(arrow + arrow + arrow);
                e.Handled = true;
                return;
            }

            int lines = Math.Max(1, SystemParameters.WheelScrollLines);
            _scroll = Math.Max(0, Math.Min(_buf.ScrollbackCount,
                                           _scroll + (e.Delta > 0 ? lines : -lines)));
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnMouseDown(MouseButtonEventArgs e)
        {
            Focus();

            if (e.ChangedButton == MouseButton.Middle) { Paste(); e.Handled = true; return; }

            if (e.ChangedButton == MouseButton.Left && !_hasSelection) ClearSelection();
            SelectionMouseDown(e);           // TerminalSelection.cs
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            SelectionMouseMove(e);
            base.OnMouseMove(e);
        }

        protected override void OnMouseUp(MouseButtonEventArgs e)
        {
            SelectionMouseUp();
            base.OnMouseUp(e);
        }

        protected override void OnTextInput(TextCompositionEventArgs e)
        {
            if (string.IsNullOrEmpty(e.Text)) return;

            if (e.Text.Length == 1 && e.Text[0] < 0x20) return;

            Send(e.Text);
            _scroll = 0;
            e.Handled = true;
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            var mods = Keyboard.Modifiers;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            bool ctrl = (mods & ModifierKeys.Control) != 0;
            bool shift = (mods & ModifierKeys.Shift) != 0;
            bool alt = (mods & ModifierKeys.Alt) != 0;

            if (HandleTerminalChord(key, ctrl, shift, alt)) { e.Handled = true; return; }

            if (ctrl && !shift && !alt && key == Key.C && CopySelection())
            {
                ClearSelection();
                e.Handled = true;
                return;
            }

            string? seq = Encode(key, ctrl, shift, alt);
            if (seq == null) return;

            ClearSelection();
            Send(seq);
            _scroll = 0;
            e.Handled = true;
        }

        private bool HandleTerminalChord(Key key, bool ctrl, bool shift, bool alt)
        {
            if (!ctrl && !alt && (key == Key.Apps && !shift || key == Key.F10 && shift))
            {
                ContextMenu.PlacementTarget = this;
                ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                ContextMenu.IsOpen = true;
                return true;
            }
            if (alt) return false;

            if (ctrl && shift)
            {
                switch (key)
                {
                    case Key.C: CopySelection(); ClearSelection(); return true;
                    case Key.V: Paste(); return true;
                    case Key.A: SelectAll(); return true;
                    case Key.Y: CopyAll(); return true;
                    case Key.L: ClearScreen(); return true;

                    case Key.Up:       ScrollBy(1); return true;
                    case Key.Down:     ScrollBy(-1); return true;
                    case Key.PageUp:   ScrollBy(_buf.Rows); return true;
                    case Key.PageDown: ScrollBy(-_buf.Rows); return true;
                    case Key.Home:     ScrollTo(_buf.ScrollbackCount); return true;
                    case Key.End:      ScrollTo(0); return true;
                }
                return false;
            }

            if (ctrl)
            {
                switch (key)
                {

                    case Key.OemPlus: case Key.Add:       SetFontSize(_fontSize + 1); return true;
                    case Key.OemMinus: case Key.Subtract: SetFontSize(_fontSize - 1); return true;
                    case Key.D0: case Key.NumPad0:        SetFontSize(11); return true;

                    case Key.V: Paste(); return true;
                    // Deliberately no Ctrl+A here. It belongs to the program on the other end as
                    // ^A: beginning-of-line in readline, and screen's command prefix. The terminal
                    // takes the Ctrl+Shift chords instead, as Windows Terminal does.
                }
            }

            if (shift && key == Key.Insert) { Paste(); return true; }

            return false;
        }

        private void ScrollBy(int lines) => ScrollTo(_scroll + lines);

        private void ScrollTo(int lines)
        {
            _scroll = Math.Max(0, Math.Min(_buf.ScrollbackCount, lines));
            InvalidateVisual();
        }

        private string? Encode(Key key, bool ctrl, bool shift, bool alt)
        {

            int mod = 1 + (shift ? 1 : 0) + (alt ? 2 : 0) + (ctrl ? 4 : 0);

            string mArrow = mod > 1 ? "1;" + mod : "";
            string mTilde = mod > 1 ? ";" + mod : "";
            string pre = mod > 1 ? Csi : (_buf.AppCursorKeys ? Esc + "O" : Csi);

            switch (key)
            {
                case Key.Up:    return pre + mArrow + "A";
                case Key.Down:  return pre + mArrow + "B";
                case Key.Right: return pre + mArrow + "C";
                case Key.Left:  return pre + mArrow + "D";
                case Key.Home:  return pre + mArrow + "H";
                case Key.End:   return pre + mArrow + "F";

                case Key.Enter:  return alt ? Esc + "\r" : "\r";
                case Key.Tab:    return shift ? Csi + "Z" : "\t";
                case Key.Escape: return Esc;

                case Key.Back:   return ctrl ? Bs : Del;

                case Key.Delete:   return Csi + "3" + mTilde + "~";
                case Key.Insert:   return Csi + "2" + mTilde + "~";
                case Key.PageUp:   return Csi + "5" + mTilde + "~";
                case Key.PageDown: return Csi + "6" + mTilde + "~";

                case Key.F1:  return Esc + "OP";
                case Key.F2:  return Esc + "OQ";
                case Key.F3:  return Esc + "OR";
                case Key.F4:  return Esc + "OS";
                case Key.F5:  return Csi + "15~";
                case Key.F6:  return Csi + "17~";
                case Key.F7:  return Csi + "18~";
                case Key.F8:  return Csi + "19~";
                case Key.F9:  return Csi + "20~";
                case Key.F10: return Csi + "21~";
                case Key.F11: return Csi + "23~";
                case Key.F12: return Csi + "24~";

                case Key.Space: return ctrl ? Nul : null;
            }

            if (ctrl && !alt && key >= Key.A && key <= Key.Z)
                return ((char)(key - Key.A + 1)).ToString();

            if (alt && !ctrl && key >= Key.A && key <= Key.Z)
                return Esc + (char)((shift ? 'A' : 'a') + (key - Key.A));

            return null;
        }

        private bool ConfirmMultiLinePaste(string text)
        {
            string Loc(string key, string fallback) => TryFindResource(key) as string ?? fallback;
            int lines = text.Replace("\r\n", "\n").TrimEnd('\n', '\r').Split('\n', '\r').Length;
            var dialog = new KillerScan.Controls.ConfirmDialog(Loc("Str_Term_PasteTitle", "Paste several lines?"),
                string.Format(Loc("Str_Term_PasteDetail", "This text has {0} lines. Pasted here, each line runs as soon as it arrives."), Math.Max(2, lines)),
                Loc("Str_Term_Paste", "Paste"), Loc("Str_Btn_Cancel", "Cancel")) { Owner = Window.GetWindow(this) };
            dialog.ShowDialog();
            return dialog.Confirmed;
        }

        private void Paste()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;

                string text = Clipboard.GetText();
                // Without bracketed paste every line break is Enter, so several lines would run at
                // once, unseen. Asked first, as Windows Terminal does.
                if (!_buf.BracketedPaste && text.IndexOfAny(['\r', '\n']) >= 0 && !ConfirmMultiLinePaste(text)) return;
                string framed = TerminalPaste.Frame(text, _buf.BracketedPaste);
                if (framed.Length > 0) Send(framed);
                _scroll = 0;
            }
            catch { /* another app holding the clipboard is not our problem */ }
        }

        protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnGotKeyboardFocus(e);
            InvalidateVisual();
        }

        protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
        {
            base.OnLostKeyboardFocus(e);
            InvalidateVisual();
        }
    }
}
