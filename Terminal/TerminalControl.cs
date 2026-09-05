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
        private TerminalPalette _palette;

        private readonly Queue<byte[]> _incoming = new();
        private readonly object _gate = new();
        private int _incomingBytes;
        private DispatcherTimer? _pump;
        private int _drawnVersion = -1;

        private GlyphTypeface? _glyphs;
        private double _cellW, _cellH, _baseline;
        private double _fontSize = 13;
        private float _pixelsPerDip = 1f;

        private int _scroll;                 // lines scrolled back; 0 is live
        private bool _cursorOn = true;
        private DispatcherTimer? _blink;

        private bool _closed;
        private Thread? _reader;

        public event Action<int>? Exited;
        public event Action<Exception>? StartFailed;

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

            foreach (var name in order)
            {
                try
                {
                    var tf = new Typeface(new FontFamily(name), FontStyles.Normal,
                                          FontWeights.Normal, FontStretches.Normal);
                    if (!tf.TryGetGlyphTypeface(out var gt)) continue;
                    if (!gt.CharacterToGlyphMap.ContainsKey('M')) continue;

                    _glyphs = gt;

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
            bool any = false;
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
                any = true;
            }
            if (any) _scroll = 0;               // new output jumps back to the bottom
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
                    line.Append(cell.Ch == 0 ? ' ' : char.ConvertFromUtf32(cell.Ch));
                text.AppendLine(line.ToString().TrimEnd());
            }
            // A terminal is mostly empty space below the cursor; keep the transcript, drop the rest.
            return text.ToString().TrimEnd() + Environment.NewLine;
        }

        public void Send(string s)
        {
            if (_pty == null || _pty.HasExited || string.IsNullOrEmpty(s)) return;
            try
            {
                var b = Encoding.UTF8.GetBytes(s);
                _pty.Input.Write(b, 0, b.Length);
                _pty.Input.Flush();
            }
            catch { /* the shell went away between keystroke and write */ }
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

            int cols = Math.Max(1, (int)(Math.Max(0, ActualWidth - LeftInset) / _cellW));
            int rows = Math.Max(1, (int)(ActualHeight / _cellH));

            if (cols != _buf.Cols || rows != _buf.Rows)
            {
                _buf.Resize(cols, rows);
                _pty?.Resize((short)cols, (short)rows);
                InvalidateVisual();
            }

        }

        protected override void OnRender(DrawingContext dc)
        {
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
            dc.PushTransform(new TranslateTransform(LeftInset, 0));

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
                if (line[c].Ch == 0 || line[c].Ch == ' ') { c++; continue; }

                var fg = CellFg(line[c]);
                var flags = line[c].Flags;
                int start = c;

                var face = FaceFor(gt, line[c].Ch);
                bool viaFallback = !ReferenceEquals(face, gt);

                var indices = new List<ushort>();
                var widths = new List<double>();

                while (c < line.Length && line[c].Ch != 0 && line[c].Ch != ' '
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

        private static GlyphTypeface? SystemFaceFor(int cp)
        {
            lock (_sysFaceCache)
                if (_sysFaceCache.TryGetValue(cp, out var cached)) return cached;

            GlyphTypeface? found = null;
            try
            {
                if (cp is >= 0xE000 and <= 0xF8FF)
                {

                    foreach (var fam in Fonts.SystemFontFamilies)
                    {
                        string name = fam.Source ?? string.Empty;
                        if (name.IndexOf("Nerd Font", StringComparison.OrdinalIgnoreCase) < 0
                            && name.IndexOf("NerdFont", StringComparison.OrdinalIgnoreCase) < 0
                            && !name.EndsWith(" NF", StringComparison.OrdinalIgnoreCase)) continue;
                        if (TryFaceFor(fam, cp, out found)) break;
                    }
                }
                else
                {

                    foreach (var name in new[] { "Segoe UI Symbol", "Segoe UI Emoji", "Segoe UI" })
                        if (TryFaceFor(new FontFamily(name), cp, out found)) break;

                    if (found == null)
                        foreach (var fam in Fonts.SystemFontFamilies)
                            if (TryFaceFor(fam, cp, out found)) break;
                }
            }
            catch { found = null; }

            lock (_sysFaceCache) _sysFaceCache[cp] = found;
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

            double x = _buf.CursorCol * _cellW;
            double y = _buf.CursorRow * _cellH;
            var b = new SolidColorBrush(_palette.Cursor);
            b.Freeze();

            switch (_buf.CursorShape)
            {
                case 1: dc.DrawRectangle(b, null, new Rect(x, y + _cellH - 2, _cellW, 2)); break;
                case 2: dc.DrawRectangle(b, null, new Rect(x, y, 2, _cellH)); break;
                default:
                    dc.DrawRectangle(b, null, new Rect(x, y, _cellW, _cellH));

                    var cell = _buf.LineAt(_buf.ScrollbackCount + _buf.CursorRow)[_buf.CursorCol];
                    if (cell.Ch != 0 && cell.Ch != ' ' && _glyphs != null)
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
            if (alt) return false;

            if (ctrl && shift)
            {
                switch (key)
                {
                    case Key.C: CopySelection(); ClearSelection(); return true;
                    case Key.V: Paste(); return true;
                    case Key.A: SelectAll(); return true;

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
                    case Key.D0: case Key.NumPad0:        SetFontSize(13); return true;

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

        private void Paste()
        {
            try
            {
                if (!Clipboard.ContainsText()) return;

                string text = Clipboard.GetText().Replace("\r\n", "\r").Replace('\n', '\r');

                Send(_buf.BracketedPaste ? Csi + "200~" + text + Csi + "201~" : text);
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
