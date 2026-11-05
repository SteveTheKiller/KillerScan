using System.ComponentModel;
using System.Windows.Media;

namespace KillerScan.Controls
{
    /// <summary>
    /// One check and its answer inside a Keep Alive card. Notifies, because a card lays its
    /// rows out reading "Checking" and fills each in as its result lands rather than replacing
    /// the row, so the table has its final shape from the first frame.
    /// </summary>
    internal sealed class CheckRow : INotifyPropertyChanged
    {
        private string _result = string.Empty;

        public string Check { get; init; } = string.Empty;

        public string Result
        {
            get => _result;
            set
            {
                if (_result == value) return;
                _result = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Result)));
            }
        }

        /// <summary>The answer as the exports write it. Differs from Result only for values that depend on the region.</summary>
        public string ExportResult => ExportOverride ?? _result;

        public string? ExportOverride { get; init; }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>One logged state change for a card: when it happened and what it became.</summary>
    internal sealed class CardEvent
    {
        public string Time { get; init; } = string.Empty;
        /// <summary>The time as the exports write it: 24-hour with colons on every Windows region.</summary>
        public string ExportTime { get; init; } = string.Empty;
        public string State { get; init; } = string.Empty;
        public Brush StateBrush { get; init; } = Brushes.Gray;
        /// <summary>Which state this line recorded, so it can be re-worded in another language.</summary>
        public bool Up { get; init; }
    }
}
