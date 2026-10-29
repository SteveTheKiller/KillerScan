using System;
using System.Collections.Generic;

namespace KillerScan.Terminal
{
    [Flags]
    internal enum CellFlags : byte
    {
        None = 0, Bold = 1, Faint = 2, Italic = 4, Underline = 8,
        Blink = 16, Inverse = 32, Hidden = 64, Strike = 128,
    }

    internal struct Cell
    {
        public int Ch;          // codepoint; 0 is an empty cell
        public int Fg, Bg;      // see TerminalBuffer.DefaultColor / Rgb
        public CellFlags Flags;
        public string? Cluster;
        public byte Width;
        public string Text => Ch < 0 ? string.Empty : Cluster ?? (Ch == 0 ? " " : char.ConvertFromUtf32(Ch));
    }

    internal sealed class TerminalBuffer : IVtHandler
    {

        public const int DefaultColor = -1;

        public const int RgbFlag = 0x1000000;

        public static int Rgb(int r, int g, int b) => RgbFlag | (r << 16) | (g << 8) | b;

        private static readonly string Csi = ((char)0x1B).ToString() + "[";

        public int Cols { get; private set; }
        public int Rows { get; private set; }

        private Cell[][] _screen = [];
        private Cell[][]? _altScreen;                       // non-null while the alt buffer is up
        private readonly List<Cell[]> _scrollback = [];

        /// <summary>Lines of history kept above the screen. Bounded like Windows Terminal's 9001: a
        /// ping -t or a chatty session left open for days must not grow without end.</summary>
        public int ScrollbackLimit { get; set; } = 10000;

        /// <summary>Longest text one cell keeps for a base character and what joins it.</summary>
        private const int MaxClusterLength = 32;

        // Trims in batches, so a session at the limit is not a full list shift on every line.
        private void TrimScrollback()
        {
            // Written as a difference: ScrollbackLimit + 512 overflows when the limit is int.MaxValue.
            if (_scrollback.Count - ScrollbackLimit > 512)
                _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
        }

        public int ScrollbackCount => _scrollback.Count;

        public int Version { get; private set; }

        public int CursorRow { get; private set; }
        public int CursorCol { get; private set; }
        internal int InsertionCol => CursorCol + (_pendingWrap ? 1 : 0);
        public bool CursorVisible { get; private set; } = true;
        public bool CursorBlink { get; private set; } = true;
        public int CursorShape { get; private set; }        // 0 block, 1 underline, 2 bar

        private int _curFg = DefaultColor, _curBg = DefaultColor;
        private CellFlags _curFlags;

        private int _savedRow, _savedCol, _savedFg = DefaultColor, _savedBg = DefaultColor;
        private CellFlags _savedFlags;

        private int _top, _bottom;                          // scroll region, inclusive, 0 based
        private bool _autoWrap = true;
        private bool _originMode;
        private bool _insertMode;
        private bool _pendingWrap;                          // cursor is parked past the last column
        private readonly HashSet<int> _tabStops = [];

        public bool AppCursorKeys { get; private set; }

        public bool BracketedPaste { get; private set; }

        public bool AltScreen => _altScreen != null;

        public string Title { get; private set; } = string.Empty;

        public string WorkingDirectory { get; private set; } = string.Empty;

        public event Action<string>? TitleChanged;
        public event Action<string>? DirectoryChanged;
        public event Action? PromptReady;
        public event Action? SpeedTestRequested;

        public event Action<string>? Respond;

        public TerminalBuffer(int cols, int rows) => Resize(cols, rows);

        /// <summary>
        /// A random value this session's own prompt wrapper and speedtest command put in their
        /// private OSC 133 marks. Anything else printed to the terminal, such as a remote SSH host or
        /// a device banner, cannot know it, so it cannot pose as a ready prompt or ask for a speed test.
        /// </summary>
        internal string PromptToken { get; } = NewPromptToken();

        private static string NewPromptToken()
        {
            var bytes = new byte[16];
            using (var random = System.Security.Cryptography.RandomNumberGenerator.Create()) random.GetBytes(bytes);
            return BitConverter.ToString(bytes).Replace("-", "");
        }

        public Cell[] LineAt(int index)
        {
            if (index < 0) return _screen[0];
            if (index < _scrollback.Count) return _scrollback[index];
            int r = index - _scrollback.Count;
            return r < Rows ? _screen[r] : _screen[Rows - 1];
        }

        public int TotalLines => _scrollback.Count + Rows;

        public void ImportHistory(TerminalBuffer previous)
        {
            int last = previous.TotalLines - 1;
            while (last >= 0 && Array.TrueForAll(previous.LineAt(last), cell => cell.Ch == 0 || cell.Ch == ' ')) last--;
            for (int row = 0; row <= last; row++) _scrollback.Add((Cell[])previous.LineAt(row).Clone());
            TrimScrollback();
            Version++;
        }

        public void PreserveScreenInHistory()
        {
            int last = Rows - 1;
            while (last >= 0 && Array.TrueForAll(_screen[last], cell => cell.Ch == 0 || cell.Ch == ' ')) last--;
            for (int row = 0; row <= last; row++) _scrollback.Add((Cell[])_screen[row].Clone());
            TrimScrollback();
            Version++;
        }

        public void Resize(int cols, int rows)
        {
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            if (cols == Cols && rows == Rows) return;

            var old = _screen;
            int logicalCursor = CursorCol + (_pendingWrap ? 1 : 0);
            bool wasPendingWrap = _pendingWrap;
            int oldRows = Rows;
            int removed = Math.Max(0, oldRows - rows);
            if (_altScreen == null)
            {
                for (int r = 0; r < removed; r++) _scrollback.Add(old[r]);
                TrimScrollback();
            }
            _screen = New(cols, rows);
            for (int r = 0; r < Math.Min(oldRows, rows); r++)
            {
                var source = old[r + removed];
                _screen[r] = NewLine(Math.Max(cols, source.Length));
                Array.Copy(source, _screen[r], source.Length);
            }
            CursorRow = Math.Max(0, CursorRow - removed);

            if (_altScreen != null)
            {
                // The saved main screen (under vim, less or htop) resizes the way the main screen
                // does: anchored at the bottom, where the latest output and prompt are, with rows
                // that no longer fit moved into history and wide lines kept whole. Its saved cursor
                // moves with it.
                var oldMain = _altScreen;
                int mainRemoved = Math.Max(0, oldRows - rows);
                for (int r = 0; r < mainRemoved; r++) _scrollback.Add(oldMain[r]);
                TrimScrollback();
                _altScreen = New(cols, rows);
                for (int r = 0; r < Math.Min(oldRows, rows); r++)
                {
                    var source = oldMain[r + mainRemoved];
                    _altScreen[r] = NewLine(Math.Max(cols, source.Length));
                    Array.Copy(source, _altScreen[r], source.Length);
                }
                _savedRow = Math.Max(0, _savedRow - mainRemoved);
            }

            Cols = cols;
            Rows = rows;
            if (_altScreen != null)
                foreach (var line in _altScreen) NormalizeClusters(line);
            _top = 0;
            _bottom = rows - 1;
            CursorRow = Math.Min(CursorRow, rows - 1);
            CursorCol = Math.Min(logicalCursor, cols - 1);
            _pendingWrap = wasPendingWrap && logicalCursor >= cols;
            ResetTabs();
            Version++;
        }

        private static Cell[][] New(int cols, int rows)
        {
            var g = new Cell[rows][];
            for (int r = 0; r < rows; r++) g[r] = NewLine(cols);
            return g;
        }

        private static Cell[] NewLine(int cols)
        {
            var line = new Cell[cols];
            for (int c = 0; c < cols; c++) { line[c].Fg = DefaultColor; line[c].Bg = DefaultColor; }
            return line;
        }

        private void ResetTabs()
        {
            _tabStops.Clear();
            for (int c = 8; c < Cols; c += 8) _tabStops.Add(c);
        }

        public void Print(int cp)
        {
            if (cp <= 0 || cp > 0x10ffff || cp is >= 0xd800 and <= 0xdfff) return;

            var previousLine = _screen[CursorRow];
            int previous = _pendingWrap ? CursorCol : CursorCol - 1;
            if (previous >= 0 && previousLine[previous].Ch < 0) previous--;
            bool extension = TerminalWidth.IsExtension(cp);
            if (previous >= 0 && previousLine[previous].Ch > 0)
            {
                var cell = previousLine[previous];
                string cluster = cell.Text;
                bool join = TerminalWidth.IsEmoji(cell.Ch) && cluster.EndsWith("\u200d", StringComparison.Ordinal) && TerminalWidth.IsEmoji(cp);
                bool regional = TerminalWidth.IsRegional(cell.Ch) && TerminalWidth.IsRegional(cp) && cluster.Length == 2;
                if (extension || join || regional)
                {
                    // Real clusters (the longest family emoji) stay well under this. A stream of
                    // combining marks stops growing the cell here instead of costing a longer string
                    // copy and text layout per mark until the window stalls.
                    if (cluster.Length < MaxClusterLength) cell.Cluster = cluster + char.ConvertFromUtf32(cp);
                    int width = cell.Width;
                    if ((cp == 0xfe0f && TerminalWidth.IsEmoji(cell.Ch)) || join) width = 2;
                    if (width > cell.Width)
                    {
                        if (previous + width > Cols && _autoWrap && Cols > 1)
                        {
                            previousLine[previous] = new Cell { Fg = DefaultColor, Bg = _curBg };
                            CursorCol = 0; _pendingWrap = false; LineFeed();
                            previousLine = _screen[CursorRow]; previous = 0;
                        }
                        width = Math.Min(width, Cols - previous);
                        if (width == 2)
                        {
                            ClearCluster(previousLine, previous + 1);
                            previousLine[previous + 1] = new Cell { Ch = -1, Fg = cell.Fg, Bg = cell.Bg, Flags = cell.Flags };
                        }
                        CursorCol = Math.Min(Cols - 1, previous + width);
                        _pendingWrap = previous + width >= Cols;
                    }
                    cell.Width = (byte)width;
                    previousLine[previous] = cell;
                    Version++; return;
                }
            }
            // A standalone mark gets a visible base and consumes one column.
            string? standalone = extension ? "\u25cc" + char.ConvertFromUtf32(cp) : null;
            int cellWidth = extension ? 1 : Math.Min(Cols, TerminalWidth.Of(cp));

            if (_pendingWrap || (cellWidth == 2 && CursorCol == Cols - 1))
            {
                if (_autoWrap)
                {
                    CursorCol = 0;
                    LineFeed();
                }
                _pendingWrap = false;
            }

            if (CursorCol >= Cols) CursorCol = Cols - 1;

            var line = _screen[CursorRow];
            if (CursorCol + cellWidth > Cols) cellWidth = 1;
            if (_insertMode) InsertChars(cellWidth);
            ClearCluster(line, CursorCol);
            if (cellWidth == 2) ClearCluster(line, CursorCol + 1);

            line[CursorCol].Ch = extension ? 0x25cc : cp;
            line[CursorCol].Cluster = standalone;
            line[CursorCol].Width = (byte)cellWidth;
            line[CursorCol].Fg = _curFg;
            line[CursorCol].Bg = _curBg;
            line[CursorCol].Flags = _curFlags;

            if (cellWidth == 2)
                line[CursorCol + 1] = new Cell { Ch = -1, Fg = _curFg, Bg = _curBg, Flags = _curFlags };
            _pendingWrap = CursorCol + cellWidth >= Cols;
            CursorCol = Math.Min(Cols - 1, CursorCol + cellWidth);

            Version++;
        }

        private void ClearCluster(Cell[] line, int col)
        {
            if (col < 0 || col >= line.Length) return;
            if (line[col].Ch < 0 && col > 0) col--;
            int width = Math.Max(1, (int)line[col].Width);
            for (int c = col; c < Math.Min(line.Length, col + width); c++)
                line[c] = new Cell { Fg = DefaultColor, Bg = _curBg };
        }

        private void NormalizeClusters(Cell[] line)
        {
            for (int c = 0; c < Math.Min(Cols, line.Length); c++)
            {
                if (line[c].Ch < 0 && (c == 0 || line[c - 1].Width != 2))
                    line[c] = new Cell { Fg = DefaultColor, Bg = _curBg };
                else if (line[c].Width == 2 && (c + 1 >= Cols || line[c + 1].Ch != -1))
                    line[c] = new Cell { Fg = DefaultColor, Bg = _curBg };
            }
        }

        public void Execute(byte b)
        {
            switch (b)
            {
                case 0x07: break;                                   // BEL, deliberately silent
                case 0x08:                                          // BS
                    if (_pendingWrap) _pendingWrap = false;
                    else if (CursorCol > 0) CursorCol--;
                    break;
                case 0x09: Tab(); break;
                case 0x0A: case 0x0B: case 0x0C:                    // LF, VT, FF
                    _pendingWrap = false;
                    LineFeed();
                    break;
                case 0x0D: CursorCol = 0; _pendingWrap = false; break;
                default: return;
            }
            Version++;
        }

        private void Tab()
        {
            _pendingWrap = false;
            int c = CursorCol + 1;
            while (c < Cols - 1 && !_tabStops.Contains(c)) c++;
            CursorCol = Math.Min(c, Cols - 1);
        }

        private void LineFeed()
        {
            if (CursorRow == _bottom) ScrollUp(1);
            else if (CursorRow < Rows - 1) CursorRow++;
        }

        private void ScrollUp(int n)
        {
            // More than the region's height only adds blank lines; a remote ESC[65535S must not
            // turn eight bytes into tens of thousands of lines.
            n = Math.Min(n, _bottom - _top + 1);
            bool wholeScreen = _top == 0 && _bottom == Rows - 1 && _altScreen == null;
            for (int i = 0; i < n; i++)
            {
                var gone = _screen[_top];
                if (wholeScreen)
                {
                    _scrollback.Add(gone);
                    TrimScrollback();
                }
                for (int r = _top; r < _bottom; r++) _screen[r] = _screen[r + 1];
                _screen[_bottom] = wholeScreen ? NewLine(Cols) : Blank(gone);
            }
            Version++;
        }

        private void ScrollDown(int n)
        {
            n = Math.Min(n, _bottom - _top + 1);
            for (int i = 0; i < n; i++)
            {
                var gone = _screen[_bottom];
                for (int r = _bottom; r > _top; r--) _screen[r] = _screen[r - 1];
                _screen[_top] = Blank(gone);
            }
            Version++;
        }

        private Cell[] Blank(Cell[] reuse)
        {
            for (int c = 0; c < reuse.Length; c++)
            {
                reuse[c].Ch = 0;
                reuse[c].Cluster = null;
                reuse[c].Width = 0;
                reuse[c].Fg = DefaultColor;
                reuse[c].Bg = _curBg;      // so a colored background fills the new line too
                reuse[c].Flags = CellFlags.None;
            }
            return reuse;
        }

        public void EscDispatch(char final, char inter)
        {
            switch (final)
            {
                case '7': SaveCursor(); break;
                case '8': RestoreCursor(); break;
                case 'D': _pendingWrap = false; LineFeed(); break;
                case 'E': _pendingWrap = false; CursorCol = 0; LineFeed(); break;
                case 'M':                                            // reverse index
                    _pendingWrap = false;
                    if (CursorRow == _top) ScrollDown(1);
                    else if (CursorRow > 0) CursorRow--;
                    break;
                case 'H': _tabStops.Add(CursorCol); break;
                case 'c': FullReset(); break;
            }
            Version++;
        }

        private void SaveCursor()
        {
            _savedRow = CursorRow; _savedCol = CursorCol;
            _savedFg = _curFg; _savedBg = _curBg; _savedFlags = _curFlags;
        }

        private void RestoreCursor()
        {
            CursorRow = Math.Min(_savedRow, Rows - 1);
            CursorCol = Math.Min(_savedCol, Cols - 1);
            _curFg = _savedFg; _curBg = _savedBg; _curFlags = _savedFlags;
            _pendingWrap = false;
        }

        /// <summary>
        /// Preserves the screen in history, leaving the cursor at the top and every other
        /// mode alone. What `cls` gives you, without resetting the terminal underneath it.
        /// </summary>
        internal void ClearAll()
        {
            if (_altScreen == null) PreserveScreenInHistory();
            _screen = New(Cols, Rows);
            CursorRow = CursorCol = 0;
            Version++;
        }

        private void FullReset()
        {
            if (_altScreen == null) PreserveScreenInHistory();
            _screen = New(Cols, Rows);
            _altScreen = null;
            CursorRow = CursorCol = 0;
            _curFg = _curBg = DefaultColor;
            _curFlags = CellFlags.None;
            _top = 0; _bottom = Rows - 1;
            _autoWrap = true; _originMode = false; _insertMode = false;
            CursorVisible = true;
            AppCursorKeys = false; BracketedPaste = false;
            ResetTabs();
        }

        public void CsiDispatch(char final, int[] p, char prefix, char inter)
        {
            int P(int i, int def = 1)
            {
                int v = i < p.Length ? p[i] : 0;
                return v == 0 ? def : v;
            }

            if (prefix == '?')
            {
                if (final == 'h' || final == 'l') { PrivateMode(p, final == 'h'); Version++; }
                return;
            }

            switch (final)
            {
                case 'A': _pendingWrap = false; CursorRow = Math.Max(RowFloor(), CursorRow - P(0)); break;
                case 'B': _pendingWrap = false; CursorRow = Math.Min(RowCeil(), CursorRow + P(0)); break;
                case 'C': _pendingWrap = false; CursorCol = Math.Min(Cols - 1, CursorCol + P(0)); break;
                case 'D': _pendingWrap = false; CursorCol = Math.Max(0, CursorCol - P(0)); break;
                case 'E': _pendingWrap = false; CursorCol = 0; CursorRow = Math.Min(RowCeil(), CursorRow + P(0)); break;
                case 'F': _pendingWrap = false; CursorCol = 0; CursorRow = Math.Max(RowFloor(), CursorRow - P(0)); break;
                case 'G': case '`': _pendingWrap = false; CursorCol = Clamp(P(0) - 1, Cols); break;
                case 'd': _pendingWrap = false; CursorRow = Clamp(P(0) - 1, Rows); break;

                case 'H': case 'f':
                    _pendingWrap = false;
                    CursorRow = Clamp((_originMode ? _top : 0) + P(0) - 1, Rows);
                    CursorCol = Clamp(P(1) - 1, Cols);
                    break;

                case 'J': EraseDisplay(p.Length > 0 ? p[0] : 0); break;
                case 'K': EraseLine(p.Length > 0 ? p[0] : 0); break;

                case 'L': InsertLines(P(0)); break;
                case 'M': DeleteLines(P(0)); break;
                case 'P': DeleteChars(P(0)); break;
                case '@': InsertChars(P(0)); break;
                case 'X': EraseChars(P(0)); break;

                case 'S': ScrollUp(P(0)); break;
                case 'T': ScrollDown(P(0)); break;

                case 'b':                                            // repeat the last glyph
                    {
                        int col = _pendingWrap ? CursorCol : CursorCol - 1;
                        var line = _screen[CursorRow];
                        if (col >= 0 && line[col].Ch < 0) col--;
                        if (col >= 0 && line[col].Ch > 0)
                        {
                            string cluster = line[col].Text;
                            // At most a screenful: more repeats only overwrite what they just wrote.
                            for (int repeat = 0, times = Math.Min(P(0), Cols * Rows); repeat < times; repeat++)
                                for (int index = 0; index < cluster.Length; index++)
                                {
                                    int scalar = char.ConvertToUtf32(cluster, index);
                                    Print(scalar); if (scalar > 0xffff) index++;
                                }
                        }
                    }
                    break;

                case 'm': Sgr(p); break;

                case 'r':
                    _top = Clamp(P(0) - 1, Rows);
                    _bottom = Clamp(P(1, Rows) - 1, Rows);
                    if (_bottom <= _top) { _top = 0; _bottom = Rows - 1; }
                    CursorRow = _originMode ? _top : 0;
                    CursorCol = 0;
                    break;

                case 'g':
                    if (P(0, 0) == 3) _tabStops.Clear();
                    else _tabStops.Remove(CursorCol);
                    break;

                case 'n':                                            // device status report
                    if (P(0, 0) == 6)
                        Respond?.Invoke(Csi + (CursorRow + 1) + ";" + (CursorCol + 1) + "R");
                    else if (P(0, 0) == 5)
                        Respond?.Invoke(Csi + "0n");
                    break;

                case 'c':                                            // "I am a VT100 with AVO"
                    Respond?.Invoke(Csi + "?1;2c");
                    break;

                case 'h': if (P(0, 0) == 4) _insertMode = true;  break;
                case 'l': if (P(0, 0) == 4) _insertMode = false; break;

                case 'q':                                            // DECSCUSR, cursor shape
                    if (inter == ' ')
                    {
                        int s = P(0, 1);
                        CursorBlink = s == 0 || (s % 2) == 1;
                        CursorShape = s <= 2 ? 0 : s <= 4 ? 1 : 2;
                    }
                    break;
            }
            Version++;
        }

        private int RowFloor() => _originMode ? _top : 0;
        private int RowCeil()  => _originMode ? _bottom : Rows - 1;
        private static int Clamp(int v, int n) => v < 0 ? 0 : v >= n ? n - 1 : v;

        private void PrivateMode(int[] p, bool set)
        {
            foreach (int m in p)
            {
                switch (m)
                {
                    case 1:    AppCursorKeys = set; break;
                    case 6:    _originMode = set; CursorRow = set ? _top : 0; CursorCol = 0; break;
                    case 7:    _autoWrap = set; break;
                    case 12:   CursorBlink = set; break;
                    case 25:   CursorVisible = set; break;
                    case 2004: BracketedPaste = set; break;

                    case 47:
                    case 1047:
                    case 1049:
                        if (set && _altScreen == null)
                        {
                            if (m == 1049) SaveCursor();
                            _altScreen = _screen;
                            _screen = New(Cols, Rows);
                            CursorRow = CursorCol = 0;
                        }
                        else if (!set && _altScreen != null)
                        {
                            _screen = _altScreen;
                            _altScreen = null;
                            if (m == 1049) RestoreCursor();
                        }
                        break;
                }
            }
        }

        private void EraseDisplay(int mode)
        {
            _pendingWrap = false;
            switch (mode)
            {
                // A row can be wider than the pane after narrowing (its old cells stay reachable by
                // horizontal scroll), so "to the end" means the row's own end, not the visible width.
                case 0:
                    ClearRun(_screen[CursorRow], CursorCol, _screen[CursorRow].Length - 1);
                    for (int r = CursorRow + 1; r < Rows; r++) ClearRun(_screen[r], 0, _screen[r].Length - 1);
                    break;
                case 1:
                    for (int r = 0; r < CursorRow; r++) ClearRun(_screen[r], 0, _screen[r].Length - 1);
                    ClearRun(_screen[CursorRow], 0, CursorCol);
                    break;
                case 3:
                    // Session history remains available even when a shell requests ED 3.
                    break;
                default:
                    if (_altScreen == null) PreserveScreenInHistory();
                    for (int r = 0; r < Rows; r++) ClearRun(_screen[r], 0, _screen[r].Length - 1);
                    break;
            }
            Version++;
        }

        private void EraseLine(int mode)
        {
            _pendingWrap = false;
            var line = _screen[CursorRow];
            if (mode == 0) ClearRun(line, CursorCol, line.Length - 1);
            else if (mode == 1) ClearRun(line, 0, CursorCol);
            else ClearRun(line, 0, line.Length - 1);
            Version++;
        }

        private void ClearRun(Cell[] line, int from, int to, bool wholeClusters = true)
        {
            if (wholeClusters && from >= 0 && from < line.Length && line[from].Ch < 0) from--;
            if (wholeClusters && to >= 0 && to < line.Length && line[to].Width == 2) to++;
            for (int c = Math.Max(0, from); c <= Math.Min(to, line.Length - 1); c++)
            {
                line[c].Ch = 0;
                line[c].Cluster = null;
                line[c].Width = 0;
                line[c].Fg = DefaultColor;
                line[c].Bg = _curBg;
                line[c].Flags = CellFlags.None;
            }
        }

        private void InsertLines(int n)
        {
            if (CursorRow < _top || CursorRow > _bottom) return;
            n = Math.Min(n, _bottom - CursorRow + 1);
            for (int i = 0; i < n; i++)
            {
                var gone = _screen[_bottom];
                for (int r = _bottom; r > CursorRow; r--) _screen[r] = _screen[r - 1];
                _screen[CursorRow] = Blank(gone);
            }
            CursorCol = 0;
            Version++;
        }

        private void DeleteLines(int n)
        {
            if (CursorRow < _top || CursorRow > _bottom) return;
            n = Math.Min(n, _bottom - CursorRow + 1);
            for (int i = 0; i < n; i++)
            {
                var gone = _screen[CursorRow];
                for (int r = CursorRow; r < _bottom; r++) _screen[r] = _screen[r + 1];
                _screen[_bottom] = Blank(gone);
            }
            CursorCol = 0;
            Version++;
        }

        private void InsertChars(int n)
        {
            var line = _screen[CursorRow];
            n = Math.Min(n, Cols - CursorCol);
            if (n <= 0) return;
            Array.Copy(line, CursorCol, line, CursorCol + n, Cols - CursorCol - n);
            ClearRun(line, CursorCol, CursorCol + n - 1, false);
            NormalizeClusters(line);
            Version++;
        }

        private void DeleteChars(int n)
        {
            var line = _screen[CursorRow];
            n = Math.Min(n, Cols - CursorCol);
            if (n <= 0) return;
            Array.Copy(line, CursorCol + n, line, CursorCol, Cols - CursorCol - n);
            ClearRun(line, Cols - n, Cols - 1, false);
            NormalizeClusters(line);
            Version++;
        }

        private void EraseChars(int n)
        {
            ClearRun(_screen[CursorRow], CursorCol, Math.Min(CursorCol + Math.Max(1, n) - 1, Cols - 1));
            Version++;
        }

        private void Sgr(int[] p)
        {
            if (p.Length == 0) { _curFg = _curBg = DefaultColor; _curFlags = CellFlags.None; return; }

            for (int i = 0; i < p.Length; i++)
            {
                int v = p[i];
                switch (v)
                {
                    case 0: _curFg = _curBg = DefaultColor; _curFlags = CellFlags.None; break;
                    case 1: _curFlags |= CellFlags.Bold; break;
                    case 2: _curFlags |= CellFlags.Faint; break;
                    case 3: _curFlags |= CellFlags.Italic; break;
                    case 4: _curFlags |= CellFlags.Underline; break;
                    case 5: case 6: _curFlags |= CellFlags.Blink; break;
                    case 7: _curFlags |= CellFlags.Inverse; break;
                    case 8: _curFlags |= CellFlags.Hidden; break;
                    case 9: _curFlags |= CellFlags.Strike; break;
                    case 21: case 22: _curFlags &= ~(CellFlags.Bold | CellFlags.Faint); break;
                    case 23: _curFlags &= ~CellFlags.Italic; break;
                    case 24: _curFlags &= ~CellFlags.Underline; break;
                    case 25: _curFlags &= ~CellFlags.Blink; break;
                    case 27: _curFlags &= ~CellFlags.Inverse; break;
                    case 28: _curFlags &= ~CellFlags.Hidden; break;
                    case 29: _curFlags &= ~CellFlags.Strike; break;

                    case 39: _curFg = DefaultColor; break;
                    case 49: _curBg = DefaultColor; break;

                    case 38: i = ExtendedColor(p, i, ref _curFg); break;
                    case 48: i = ExtendedColor(p, i, ref _curBg); break;

                    default:
                        if (v >= 30 && v <= 37) _curFg = v - 30;
                        else if (v >= 40 && v <= 47) _curBg = v - 40;
                        else if (v >= 90 && v <= 97) _curFg = v - 90 + 8;      // bright
                        else if (v >= 100 && v <= 107) _curBg = v - 100 + 8;
                        break;
                }
            }
        }

        private static int ExtendedColor(int[] p, int i, ref int slot)
        {
            if (i + 1 >= p.Length) return i;
            int kind = p[i + 1];
            if (kind == 5 && i + 2 < p.Length)
            {
                slot = p[i + 2] & 0xFF;
                return i + 2;
            }
            if (kind == 2 && i + 4 < p.Length)
            {
                slot = Rgb(p[i + 2] & 0xFF, p[i + 3] & 0xFF, p[i + 4] & 0xFF);
                return i + 4;
            }
            return i + 1;
        }

        public void OscDispatch(int cmd, string data)
        {
            switch (cmd)
            {
                case 133:
                    if (data == "B;" + PromptToken) PromptReady?.Invoke();
                    else if (data == "KillerScan;SpeedTest;" + PromptToken) SpeedTestRequested?.Invoke();
                    break;
                case 0: case 2:
                    Title = data;
                    TitleChanged?.Invoke(data);
                    break;

                case 7:
                    SetDirFromUri(data);
                    break;
                case 9:
                    if (data.StartsWith("9;", StringComparison.Ordinal))
                        SetDir(data[2..].Trim());
                    break;
            }
        }

        private void SetDirFromUri(string data)
        {
            try
            {
                var u = new Uri(data);
                if (u.IsFile) SetDir(u.LocalPath);
            }
            catch { /* a malformed URL is not worth reporting */ }
        }

        private void SetDir(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || path == WorkingDirectory) return;
            WorkingDirectory = path;
            DirectoryChanged?.Invoke(path);
        }
    }
}
