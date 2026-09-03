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

        public int ScrollbackLimit { get; set; } = 5000;

        public int ScrollbackCount => _scrollback.Count;

        public int Version { get; private set; }

        public int CursorRow { get; private set; }
        public int CursorCol { get; private set; }
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

        public event Action<string>? Respond;

        public TerminalBuffer(int cols, int rows) => Resize(cols, rows);

        public Cell[] LineAt(int index)
        {
            if (index < 0) return _screen[0];
            if (index < _scrollback.Count) return _scrollback[index];
            int r = index - _scrollback.Count;
            return r < Rows ? _screen[r] : _screen[Rows - 1];
        }

        public int TotalLines => _scrollback.Count + Rows;

        public void Resize(int cols, int rows)
        {
            cols = Math.Max(1, cols);
            rows = Math.Max(1, rows);
            if (cols == Cols && rows == Rows) return;

            var old = _screen;
            int oldRows = Rows;

            _screen = New(cols, rows);
            for (int r = 0; r < Math.Min(oldRows, rows); r++)
                Array.Copy(old[r], _screen[r], Math.Min(Cols, cols));

            if (_altScreen != null)
            {
                var oldAlt = _altScreen;
                _altScreen = New(cols, rows);
                for (int r = 0; r < Math.Min(oldRows, rows); r++)
                    Array.Copy(oldAlt[r], _altScreen[r], Math.Min(Cols, cols));
            }

            Cols = cols;
            Rows = rows;
            _top = 0;
            _bottom = rows - 1;
            CursorRow = Math.Min(CursorRow, rows - 1);
            CursorCol = Math.Min(CursorCol, cols - 1);
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
            if (cp == 0) return;

            if (_pendingWrap)
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
            if (_insertMode && CursorCol < Cols - 1)
                Array.Copy(line, CursorCol, line, CursorCol + 1, Cols - CursorCol - 1);

            line[CursorCol].Ch = cp;
            line[CursorCol].Fg = _curFg;
            line[CursorCol].Bg = _curBg;
            line[CursorCol].Flags = _curFlags;

            if (CursorCol == Cols - 1) _pendingWrap = true;
            else CursorCol++;

            Version++;
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
            bool wholeScreen = _top == 0 && _bottom == Rows - 1 && _altScreen == null;
            for (int i = 0; i < n; i++)
            {
                var gone = _screen[_top];
                if (wholeScreen)
                {
                    _scrollback.Add(gone);
                    if (_scrollback.Count > ScrollbackLimit)
                        _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
                }
                for (int r = _top; r < _bottom; r++) _screen[r] = _screen[r + 1];
                _screen[_bottom] = wholeScreen ? NewLine(Cols) : Blank(gone);
            }
            Version++;
        }

        private void ScrollDown(int n)
        {
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

        private void FullReset()
        {
            _screen = New(Cols, Rows);
            _altScreen = null;
            _scrollback.Clear();
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
                        int prev = CursorCol > 0 ? _screen[CursorRow][CursorCol - 1].Ch : 0;
                        if (prev != 0) for (int i = 0; i < P(0); i++) Print(prev);
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
                case 0:
                    ClearRun(_screen[CursorRow], CursorCol, Cols - 1);
                    for (int r = CursorRow + 1; r < Rows; r++) ClearRun(_screen[r], 0, Cols - 1);
                    break;
                case 1:
                    for (int r = 0; r < CursorRow; r++) ClearRun(_screen[r], 0, Cols - 1);
                    ClearRun(_screen[CursorRow], 0, CursorCol);
                    break;
                default:
                    for (int r = 0; r < Rows; r++) ClearRun(_screen[r], 0, Cols - 1);

                    if (mode == 3) _scrollback.Clear();
                    break;
            }
            Version++;
        }

        private void EraseLine(int mode)
        {
            _pendingWrap = false;
            var line = _screen[CursorRow];
            if (mode == 0) ClearRun(line, CursorCol, Cols - 1);
            else if (mode == 1) ClearRun(line, 0, CursorCol);
            else ClearRun(line, 0, Cols - 1);
            Version++;
        }

        private void ClearRun(Cell[] line, int from, int to)
        {
            for (int c = Math.Max(0, from); c <= Math.Min(to, Cols - 1); c++)
            {
                line[c].Ch = 0;
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
            ClearRun(line, CursorCol, CursorCol + n - 1);
            Version++;
        }

        private void DeleteChars(int n)
        {
            var line = _screen[CursorRow];
            n = Math.Min(n, Cols - CursorCol);
            if (n <= 0) return;
            Array.Copy(line, CursorCol + n, line, CursorCol, Cols - CursorCol - n);
            ClearRun(line, Cols - n, Cols - 1);
            Version++;
        }

        private void EraseChars(int n)
        {
            ClearRun(_screen[CursorRow], CursorCol, CursorCol + Math.Max(1, n) - 1);
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
