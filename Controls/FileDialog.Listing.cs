using System;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace KillerScan.Controls
{
    public partial class FileDialog
    {
        private readonly PickerListingWork _listingWork = new PickerListingWork();
        private CancellationTokenSource? _listingRows;
        private bool _listingClosed;
        private string? _listingError;
        public Func<string, bool, CancellationToken, PickerListingResult> DirectoryListingReader { get; set; } = PickerDirectoryListing.Read;
        public Task DirectoryListingTask { get; private set; } = Task.CompletedTask;
        public bool IsDirectoryListingBusy { get; private set; }
        public bool IsDirectoryListingAvailable { get; private set; }
        public bool IsDirectoryListingComplete { get; private set; }

        public Task NavigateDirectoryAsync(string directory)
        {
            if (_listingClosed || string.IsNullOrWhiteSpace(directory)) return Task.CompletedTask;
            _listingRows?.Cancel();
            UpdateImagePreview(null);
            IsDirectoryListingBusy = true;
            IsDirectoryListingAvailable = false;
            IsDirectoryListingComplete = false;
            AcceptButton.IsEnabled = false;
            UpButton.IsEnabled = false;
            _raw.Clear();
            MutateListingRows(Entries.Clear);
            SelMeta.Text = "\u2026";
            bool showHidden = _showHidden;
            var reader = DirectoryListingReader;
            return DirectoryListingTask = _listingWork.RunAsync(token => reader(directory, showHidden, token), async (result, token) =>
            {
                if (!result.Exists)
                {
                    IsDirectoryListingBusy = false;
                    SelMeta.Text = Loc("Str_Dlg_NoSuchFolder");
                    return;
                }
                IsDirectoryListingComplete = result.Complete && result.Entries.Count <= PickerDirectoryListing.MaximumEntries;
                _listingError = result.Error ?? (IsDirectoryListingComplete ? null : new Win32Exception(234).Message);
                MutateListingRows(() =>
                {
                    _currentDir = directory;
                    PathBox.Text = directory;
                    _raw.AddRange(result.Entries.Take(PickerDirectoryListing.MaximumEntries));
                });
                await ApplySortAsync();
                token.ThrowIfCancellationRequested();
                IsDirectoryListingBusy = false;
                IsDirectoryListingAvailable = true;
                AcceptButton.IsEnabled = true;
                UpButton.IsEnabled = System.IO.Directory.GetParent(directory) != null;
                UpdateInfoSummary();
                MutateListingRows(SyncPlacesSelection);
                RecordRecent(directory);
                _ = RevealInTree(directory);
            }, error =>
            {
                IsDirectoryListingBusy = false;
                _listingError = error.Message;
                SelMeta.Text = error.Message;
            });
        }

        private void CloseDirectoryListing()
        {
            _listingClosed = true;
            _listingWork.Dispose();
            _listingRows?.Cancel();
            IsDirectoryListingBusy = false;
        }

        private async Task<bool> PublishListingRowsAsync(PickerEntry[] rows)
        {
            _listingRows?.Cancel();
            var request = _listingRows = new CancellationTokenSource();
            try
            {
                await PickerListingWork.PublishAsync(rows, () => MutateListingRows(Entries.Clear), row => MutateListingRows(() => Entries.Add(row)),
                    async () => { await Dispatcher.Yield(DispatcherPriority.Background); }, request.Token);
                return true;
            }
            catch (OperationCanceledException) { return false; }
            finally { if (ReferenceEquals(_listingRows, request)) _listingRows = null; request.Dispose(); }
        }

        private void MutateListingRows(Action change)
        {
            bool previous = _navigating;
            _navigating = true;
            try { change(); }
            finally { _navigating = previous; }
        }
    }
}
