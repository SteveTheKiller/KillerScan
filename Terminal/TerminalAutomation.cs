using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Automation.Text;
using System.Windows.Threading;

namespace KillerScan.Terminal
{
    internal sealed partial class TerminalControl
    {
        private TerminalAutomationPeer? _automationPeer;
        private DispatcherTimer? _automationTimer;
        private int _automationVersion = -1;
        private string _automationSelection = string.Empty;

        protected override AutomationPeer OnCreateAutomationPeer() =>
            _automationPeer ??= new TerminalAutomationPeer(this);

        // Output and mouse selection can change faster than a reader can consume events.
        private void QueueAutomationUpdate()
        {
            if (_automationPeer == null || _closed) return;
            if (_automationTimer == null)
            {
                _automationTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher)
                    { Interval = TimeSpan.FromMilliseconds(200) };
                _automationTimer.Tick += (_, _) =>
                {
                    _automationTimer.Stop();
                    if (_closed) return;
                    if (_automationVersion != _buf.Version)
                    {
                        _automationVersion = _buf.Version;
                        _automationPeer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextChanged);
                    }
                    string selection = AutomationSelection.Start + ":" + AutomationSelection.End;
                    if (_automationSelection != selection)
                    {
                        _automationSelection = selection;
                        _automationPeer.RaiseAutomationEvent(AutomationEvents.TextPatternOnTextSelectionChanged);
                    }
                };
            }
            if (!_automationTimer.IsEnabled) _automationTimer.Start();
        }

        // Text offset where each row starts, plus the total at the end, for one buffer version.
        // Rebuilt at most once per change: a screen reader asks for offsets on every 200 ms tick
        // and on every query, and a long scrollback made each of those a full rebuild.
        private int[] _rowStarts = Array.Empty<int>();
        private int _rowStartsVersion = -1;

        private int[] RowStarts()
        {
            if (_rowStartsVersion == _buf.Version && _rowStarts.Length == _buf.TotalLines + 1) return _rowStarts;
            var starts = new int[_buf.TotalLines + 1];
            for (int r = 0; r < _buf.TotalLines; r++) starts[r + 1] = starts[r] + RowTextLength(_buf.LineAt(r)) + Environment.NewLine.Length;
            _rowStarts = starts;
            _rowStartsVersion = _buf.Version;
            return starts;
        }

        private static int RowTextLength(Cell[] cells) => string.Concat(cells.Select(c => c.Text)).TrimEnd().Length;

        internal int TextOffset(int row, int col)
        {
            var starts = RowStarts();
            if (row >= _buf.TotalLines) return starts[_buf.TotalLines];
            if (row < 0) return 0;
            var cells = _buf.LineAt(row);
            int prefix = 0;
            for (int c = 0; c < Math.Min(Math.Max(0, col), cells.Length); c++) prefix += cells[c].Text.Length;
            return starts[row] + Math.Min(prefix, starts[row + 1] - starts[row] - Environment.NewLine.Length);
        }

        internal (int Start, int End) AutomationSelection
        {
            get
            {
                if (!_hasSelection)
                {
                    int caret = TextOffset(_buf.ScrollbackCount + _buf.CursorRow, _buf.InsertionCol);
                    return (caret, caret);
                }
                Ordered(out int a, out int b, out int c, out int d);
                return (TextOffset(a, b), TextOffset(c, d));
            }
        }

        internal (int Row, int Col) CellFromOffset(int offset)
        {
            offset = Math.Max(0, offset);
            if (_buf.TotalLines == 0) return (-1, 0);
            var starts = RowStarts();
            // The last row whose start is at or before the offset; the final row takes any overflow.
            int r = Array.BinarySearch(starts, 0, _buf.TotalLines, offset);
            if (r < 0) r = ~r - 1;
            while (r + 1 < _buf.TotalLines && starts[r + 1] == offset) r++;
            r = Math.Max(0, Math.Min(r, _buf.TotalLines - 1));
            offset -= starts[r];
            var cells = _buf.LineAt(r);
            int used = 0;
            for (int c = 0; c < cells.Length; c++)
            {
                if (cells[c].Ch < 0) continue;
                if (used >= offset) return (r, c);
                used += cells[c].Text.Length;
            }
            return (r, cells.Length);
        }

        internal void AutomationSelect(int start, int end)
        {
            var a = CellFromOffset(start); var b = CellFromOffset(end);
            _selAnchorLine = a.Row; _selAnchorCol = a.Col;
            _selHeadLine = b.Row; _selHeadCol = b.Col;
            _hasSelection = start != end;
            InvalidateVisual(); QueueAutomationUpdate();
        }

        internal void AutomationScroll(int offset, bool top)
        {
            var cell = CellFromOffset(offset);
            _scroll = Math.Max(0, _buf.ScrollbackCount - cell.Row + (top ? 0 : _buf.Rows - 1));
            InvalidateVisual();
        }

        internal double[] AutomationBounds(int start, int end)
        {
            if (!IsVisible || PresentationSource.FromVisual(this) == null) return Array.Empty<double>();
            var result = new List<double>();
            var a = CellFromOffset(start); var b = CellFromOffset(end);
            int first = Math.Max(0, _buf.ScrollbackCount - _scroll);
            for (int row = Math.Max(first, a.Row); row <= Math.Min(first + _buf.Rows - 1, b.Row); row++)
            {
                int left = row == a.Row ? a.Col : 0, right = row == b.Row ? b.Col : _buf.Cols;
                double x = Math.Max(0, LeftInset + (left - _horizontalScroll) * _cellW);
                double width = Math.Min(ActualWidth, LeftInset + (right - _horizontalScroll) * _cellW) - x;
                if (width <= 0) continue;
                var point = PointToScreen(new Point(x, (row - first) * _cellH));
                var bottom = PointToScreen(new Point(x + width, (row - first + 1) * _cellH));
                result.AddRange(new[] { point.X, point.Y, bottom.X - point.X, bottom.Y - point.Y });
            }
            return result.ToArray();
        }

        internal int AutomationOffsetAt(Point screen)
        {
            var point = PointFromScreen(screen);
            CellAt(point, out int row, out int col);
            return TextOffset(row, col);
        }

        internal (int Start, int End) VisibleTextOffsets
        {
            get
            {
                int first = Math.Max(0, _buf.ScrollbackCount - _scroll);
                return (TextOffset(first, 0), TextOffset(Math.Min(_buf.TotalLines, first + _buf.Rows), 0));
            }
        }

        internal HashSet<int> CharacterStarts(int length)
        {
            var starts = new HashSet<int> { 0 };
            int offset = 0;
            for (int row = 0; row < _buf.TotalLines && offset < length; row++)
            {
                var cells = _buf.LineAt(row);
                int content = string.Concat(cells.Select(cell => cell.Text)).TrimEnd().Length;
                int used = 0;
                foreach (var cell in cells)
                {
                    if (cell.Ch < 0) continue;
                    if (used >= content) break;
                    starts.Add(offset + used); used += cell.Text.Length;
                }
                if (offset + content < length) starts.Add(offset + content);
                offset += content + Environment.NewLine.Length;
            }
            starts.Add(length);
            return starts;
        }
    }

    internal sealed class TerminalAutomationPeer(TerminalControl terminal) : FrameworkElementAutomationPeer(terminal), ITextProvider
    {
        protected override string GetClassNameCore() => "Terminal";
        protected override string GetNameCore() => terminal.TryFindResource("Str_Workspace_Terminal") as string ?? "Terminal";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
        protected override bool IsControlElementCore() => true;
        protected override bool IsContentElementCore() => true;
        public override object? GetPattern(PatternInterface patternInterface) => patternInterface == PatternInterface.Text ? this : base.GetPattern(patternInterface);
        internal IRawElementProviderSimple Provider => ProviderFromPeer(this);
        private TerminalControl Terminal => terminal;
        private int _textVersion = -1;
        private string _text = string.Empty;
        internal string Text
        {
            get
            {
                if (_textVersion != terminal.Buffer.Version)
                { _text = terminal.GetText(); _textVersion = terminal.Buffer.Version; }
                return _text;
            }
        }
        public SupportedTextSelection SupportedTextSelection => SupportedTextSelection.Single;
        public ITextRangeProvider DocumentRange => new Range(this, 0, Text.Length);
        public ITextRangeProvider[] GetSelection()
        {
            var selected = terminal.AutomationSelection;
            return new ITextRangeProvider[] { new Range(this, selected.Start, selected.End) };
        }
        public ITextRangeProvider[] GetVisibleRanges()
        {
            var visible = terminal.VisibleTextOffsets;
            return new ITextRangeProvider[] { new Range(this, visible.Start, Math.Min(visible.End, Text.Length)) };
        }
        public ITextRangeProvider RangeFromChild(IRawElementProviderSimple childElement) => throw new ArgumentException("Terminal text has no child elements.", nameof(childElement));
        public ITextRangeProvider RangeFromPoint(Point screenLocation)
        { int offset = terminal.AutomationOffsetAt(screenLocation); return new Range(this, offset, offset); }

        private sealed class Range(TerminalAutomationPeer peer, int start, int end) : ITextRangeProvider
        {
            private int _start = start, _end = end;
            private void Clamp() { _start = Math.Max(0, Math.Min(_start, peer.Text.Length)); _end = Math.Max(_start, Math.Min(_end, peer.Text.Length)); }
            private Range Other(ITextRangeProvider value) => value is Range range && ReferenceEquals(range.Peer, peer) ? range : throw new ArgumentException("Range belongs to another terminal.");
            private TerminalAutomationPeer Peer => peer;
            public ITextRangeProvider Clone() { Clamp(); return new Range(peer, _start, _end); }
            public bool Compare(ITextRangeProvider range) { Clamp(); var other = Other(range); other.Clamp(); return _start == other._start && _end == other._end; }
            public int CompareEndpoints(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
            { Clamp(); var other = Other(targetRange); other.Clamp(); return (endpoint == TextPatternRangeEndpoint.Start ? _start : _end) - (targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end); }
            private List<int> Boundaries(TextUnit unit)
            {
                if (unit == TextUnit.Format) unit = TextUnit.Word;
                string text = peer.Text;
                var result = new List<int> { 0 };
                var elements = unit == TextUnit.Character ? peer.Terminal.CharacterStarts(text.Length) : null;
                for (int i = 1; i < text.Length; i++)
                {
                    bool boundary = unit == TextUnit.Character ? elements!.Contains(i) :
                        unit == TextUnit.Word ? char.IsWhiteSpace(text[i - 1]) != char.IsWhiteSpace(text[i]) :
                        unit == TextUnit.Line || unit == TextUnit.Paragraph ? text[i - 1] == '\n' : false;
                    if (boundary) result.Add(i);
                }
                if (text.Length > 0) result.Add(text.Length);
                return result;
            }
            public void ExpandToEnclosingUnit(TextUnit unit)
            {
                Clamp(); var bounds = Boundaries(unit);
                int index = Math.Max(0, bounds.FindLastIndex(x => x <= _start));
                if (index == bounds.Count - 1 && index > 0) index--;
                _start = bounds[index]; _end = bounds[Math.Min(index + 1, bounds.Count - 1)];
            }
            public ITextRangeProvider? FindAttribute(int attributeId, object value, bool backward) => null;
            public ITextRangeProvider? FindText(string text, bool backward, bool ignoreCase)
            {
                if (string.IsNullOrEmpty(text)) throw new ArgumentException("Search text is required.", nameof(text));
                string range = GetText(-1);
                var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                int at = backward ? range.LastIndexOf(text, comparison) : range.IndexOf(text, comparison);
                return at < 0 ? null : new Range(peer, _start + at, _start + at + text.Length);
            }
            public object GetAttributeValue(int attributeId) => attributeId == TextPatternIdentifiers.IsReadOnlyAttribute.Id ? true : AutomationElementIdentifiers.NotSupported;
            public double[] GetBoundingRectangles() { Clamp(); return peer.Terminal.AutomationBounds(_start, _end); }
            public IRawElementProviderSimple GetEnclosingElement() => peer.Provider;
            public string GetText(int maxLength)
            {
                if (maxLength < -1) throw new ArgumentOutOfRangeException(nameof(maxLength));
                Clamp(); return peer.Text.Substring(_start, maxLength < 0 ? _end - _start : Math.Min(maxLength, _end - _start));
            }
            public int Move(TextUnit unit, int count)
            {
                Clamp(); if (count == 0) return 0;
                bool empty = _start == _end;
                if (!empty) ExpandToEnclosingUnit(unit);
                var bounds = Boundaries(unit);
                int from = Math.Max(0, bounds.FindLastIndex(x => x <= _start));
                int to = Math.Max(0, Math.Min(bounds.Count - (empty ? 1 : 2), from + count));
                _start = bounds[to]; _end = empty ? _start : bounds[Math.Min(to + 1, bounds.Count - 1)];
                return to - from;
            }
            public int MoveEndpointByUnit(TextPatternRangeEndpoint endpoint, TextUnit unit, int count)
            {
                Clamp(); var bounds = Boundaries(unit); int position = endpoint == TextPatternRangeEndpoint.Start ? _start : _end;
                int from = Math.Max(0, bounds.FindLastIndex(x => x <= position));
                if (count < 0 && bounds[from] < position) from++;
                int to = Math.Max(0, Math.Min(bounds.Count - 1, from + count));
                if (endpoint == TextPatternRangeEndpoint.Start) { _start = bounds[to]; _end = Math.Max(_start, _end); }
                else { _end = bounds[to]; _start = Math.Min(_start, _end); }
                return to - from;
            }
            public void MoveEndpointByRange(TextPatternRangeEndpoint endpoint, ITextRangeProvider targetRange, TextPatternRangeEndpoint targetEndpoint)
            {
                var other = Other(targetRange); other.Clamp(); int position = targetEndpoint == TextPatternRangeEndpoint.Start ? other._start : other._end;
                if (endpoint == TextPatternRangeEndpoint.Start) { _start = position; _end = Math.Max(_start, _end); }
                else { _end = position; _start = Math.Min(_start, _end); }
            }
            public void Select() { Clamp(); peer.Terminal.AutomationSelect(_start, _end); }
            public void AddToSelection() => throw new InvalidOperationException("Terminal supports one selection.");
            public void RemoveFromSelection() => peer.Terminal.ClearSelection();
            public void ScrollIntoView(bool alignToTop) { Clamp(); peer.Terminal.AutomationScroll(alignToTop ? _start : _end, alignToTop); }
            public IRawElementProviderSimple[] GetChildren() => Array.Empty<IRawElementProviderSimple>();
        }
    }
}
