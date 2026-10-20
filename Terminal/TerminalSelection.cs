using System;
using System.Text;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace KillerScan.Terminal
{
    internal sealed partial class TerminalControl
    {
        private bool _selecting;
        private bool _hasSelection;
        private int _selAnchorLine, _selAnchorCol;      // where the drag began
        private int _selHeadLine, _selHeadCol;          // where it is now

        private void CellAt(Point p, out int line, out int col)
        {
            int first = Math.Max(0, _buf.ScrollbackCount - _scroll);
            int row = _cellH > 0 ? (int)(p.Y / _cellH) : 0;
            line = first + Math.Max(0, Math.Min(_buf.Rows - 1, row));

            col = _cellW > 0 ? (int)Math.Round((p.X - LeftInset) / _cellW) + _horizontalScroll : 0;
            col = Math.Max(0, Math.Min(_buf.LineAt(line).Length, col));
        }

        private void SelectionMouseDown(MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            var p = e.GetPosition(this);

            if (e.ClickCount == 2) { SelectWordAt(p); return; }
            if (e.ClickCount >= 3) { SelectLineAt(p); return; }

            CellAt(p, out _selAnchorLine, out _selAnchorCol);
            _selHeadLine = _selAnchorLine;
            _selHeadCol = _selAnchorCol;
            _selecting = true;
            _hasSelection = false;
            CaptureMouse();
            InvalidateVisual();
        }

        private void SelectionMouseMove(MouseEventArgs e)
        {
            if (!_selecting || e.LeftButton != MouseButtonState.Pressed) return;

            CellAt(e.GetPosition(this), out int line, out int column);
            SelectCells(_selAnchorLine, _selAnchorCol, line, column);
        }

        internal void SelectCells(int anchorLine, int anchorColumn, int headLine, int headColumn)
        {
            _selAnchorLine = Math.Max(0, Math.Min(_buf.TotalLines - 1, anchorLine));
            _selHeadLine = Math.Max(0, Math.Min(_buf.TotalLines - 1, headLine));
            _selAnchorCol = Math.Max(0, Math.Min(_buf.LineAt(_selAnchorLine).Length, anchorColumn));
            _selHeadCol = Math.Max(0, Math.Min(_buf.LineAt(_selHeadLine).Length, headColumn));
            _hasSelection = _selHeadLine != _selAnchorLine || _selHeadCol != _selAnchorCol;
            InvalidateVisual();
        }

        private void SelectionMouseUp()
        {
            if (!_selecting) return;
            _selecting = false;
            if (IsMouseCaptured) ReleaseMouseCapture();
            InvalidateVisual();
        }

        private void SelectWordAt(Point p)
        {
            CellAt(p, out int line, out int col);
            var cells = _buf.LineAt(line);
            col = Math.Min(col, cells.Length - 1);
            if (col < 0) return;

            int a = col, b = col;
            while (a > 0 && IsWordChar(cells[a - 1].Ch)) a--;
            while (b < cells.Length - 1 && IsWordChar(cells[b + 1].Ch)) b++;

            _selAnchorLine = _selHeadLine = line;
            _selAnchorCol = a;
            _selHeadCol = b + 1;
            _hasSelection = true;
            _selecting = false;
            InvalidateVisual();
        }

        private static bool IsWordChar(int cp)
        {
            if (cp < 0) return true;
            if (cp == 0 || cp == ' ') return false;
            char c = (char)cp;
            return char.IsLetterOrDigit(c) || "_-.\\/:~@+=%#".IndexOf(c) >= 0;
        }

        private void SelectLineAt(Point p)
        {
            CellAt(p, out int line, out _);
            _selAnchorLine = _selHeadLine = line;
            _selAnchorCol = 0;
            _selHeadCol = _buf.Cols;
            _hasSelection = true;
            _selecting = false;
            InvalidateVisual();
        }

        internal void SelectAll()
        {
            _selAnchorLine = 0;
            _selAnchorCol = 0;
            _selHeadLine = _buf.TotalLines - 1;
            _selHeadCol = _buf.Cols;
            _hasSelection = true;
            InvalidateVisual();
        }

        internal void ClearSelection()
        {
            if (!_hasSelection) return;
            _hasSelection = false;
            InvalidateVisual();
        }

        internal bool HasSelection => _hasSelection;

        private void Ordered(out int l1, out int c1, out int l2, out int c2)
        {
            if (_selAnchorLine < _selHeadLine ||
                (_selAnchorLine == _selHeadLine && _selAnchorCol <= _selHeadCol))
            {
                l1 = _selAnchorLine; c1 = _selAnchorCol;
                l2 = _selHeadLine;   c2 = _selHeadCol;
            }
            else
            {
                l1 = _selHeadLine;   c1 = _selHeadCol;
                l2 = _selAnchorLine; c2 = _selAnchorCol;
            }
            var first = _buf.LineAt(l1);
            var last = _buf.LineAt(l2);
            if (c1 > 0 && c1 < first.Length && first[c1].Ch < 0) c1--;
            if (c2 > 0 && c2 < last.Length && last[c2].Ch < 0) c2++;
        }

        private void DrawSelection(DrawingContext dc, int firstVisible)
        {
            if (!_hasSelection) return;

            Ordered(out int l1, out int c1, out int l2, out int c2);

            var brush = new SolidColorBrush(_palette.Selection);
            brush.Freeze();

            for (int r = 0; r < _buf.Rows; r++)
            {
                int line = firstVisible + r;
                if (line < l1 || line > l2) continue;

                int from = line == l1 ? c1 : 0;
                int to   = line == l2 ? c2 : _buf.Cols;
                var cells = _buf.LineAt(line);
                if (from > 0 && from < cells.Length && cells[from].Ch < 0) from--;
                if (to > 0 && to < cells.Length && cells[to].Ch < 0) to++;
                if (to <= from) continue;

                dc.DrawRectangle(brush, null,
                    new Rect(from * _cellW, r * _cellH, (to - from) * _cellW, _cellH));
            }
        }

        internal bool CopySelection()
        {
            if (!_hasSelection) return false;

            string text = GetSelectionText();
            if (text.Length == 0) return false;
            try { Clipboard.SetText(text); }
            catch { return false; }
            return true;
        }

        internal string GetSelectionText()
        {
            if (!_hasSelection) return string.Empty;

            Ordered(out int l1, out int c1, out int l2, out int c2);
            var sb = new StringBuilder();

            for (int line = l1; line <= l2 && line < _buf.TotalLines; line++)
            {
                var cells = _buf.LineAt(line);
                int from = line == l1 ? c1 : 0;
                int to   = Math.Min(line == l2 ? c2 : cells.Length, cells.Length);
                if (from > 0 && from < cells.Length && cells[from].Ch < 0) from--;

                var row = new StringBuilder();
                for (int c = from; c < to; c++)
                    row.Append(cells[c].Text);

                sb.Append(row.ToString().TrimEnd());
                if (line < l2) sb.Append("\r\n");
            }

            return sb.ToString();
        }
    }
}
