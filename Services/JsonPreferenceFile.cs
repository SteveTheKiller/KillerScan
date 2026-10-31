using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;

namespace KillerScan.Services
{
    // Mutations are replayed over the latest committed file, never over another window's stale copy.
    internal sealed class JsonPreferenceFile<T> where T : class
    {
        private readonly string _path;
        private readonly Func<T> _create;
        private readonly Func<T, T> _normalize;
        private readonly Action<Stream, byte[]>? _write;
        private readonly List<Action<T>> _pending = [];
        // Protect pending changes and session state before entering the cross-process lease.
        // Location imports and the UI thread can both use one store at the same time.
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, object> Gates = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate;
        internal T Value { get; private set; }
        internal string? Error { get; private set; }

        internal JsonPreferenceFile(string path, Func<T> create, Func<T, T> normalize, Action<Stream, byte[]>? write = null)
        {
            _path = Path.GetFullPath(path); _create = create; _normalize = normalize; _write = write; Value = create();
            _gate = Gates.GetOrAdd(_path, _ => new object());
        }

        internal void Load()
        {
            lock (_gate)
            {
                try { Value = Read(); Error = null; }
                catch (Exception ex) when (IsUnreadable(ex))
                { Value = _create(); Error = Describe(ex); }
            }
        }

        /// <summary>Reads the file again. Unlike <see cref="Load"/>, a failed read keeps the value as it was.</summary>
        internal bool Refresh()
        {
            lock (_gate)
            {
                try { Value = Read(); Error = null; return true; }
                catch (Exception ex) when (IsUnreadable(ex)) { Error = Describe(ex); return false; }
            }
        }

        // InvalidDataException covers a file that parses but is not usable: a literal null, or a
        // normalizer refusing it, such as a newer format version. It is reported like any other
        // unreadable file instead of escaping to the caller and stopping the app.
        private static bool IsUnreadable(Exception ex) =>
            ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidDataException;

        internal bool Change(Action<T> mutation, bool memoryOnly = false)
        {
            lock (_gate)
            {
                mutation(Value);
                if (memoryOnly) return true;
                _pending.Add(mutation);
                return Save();
            }
        }

        /// <summary>
        /// Applies a change only if it reaches the file. Unlike <see cref="Change"/>, a failed
        /// save leaves the value as it was and does not keep the change for a later retry, so
        /// nothing half-done is shown now or written later. For stores where an entry that
        /// is listed but not saved would mislead, such as the Locations catalog.
        /// </summary>
        internal bool Commit(Action<T> mutation)
        {
            lock (_gate)
            {
                _pending.Add(mutation);
                if (Save()) return true;
                _pending.Remove(mutation);
                return false;
            }
        }

        internal bool Save()
        {
            lock (_gate) return SaveLocked();
        }

        private bool SaveLocked()
        {
            try
            {
                using var lease = ProfileLock.Acquire("Preferences", _path, TimeSpan.FromSeconds(2), "The preferences file is busy. Try again.");
                var latest = Read();
                foreach (var mutation in _pending) mutation(latest);
                Publish(_path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(latest, new JsonSerializerOptions { WriteIndented = true })), _write);
                Value = latest; _pending.Clear(); Error = null;
                return true;
            }
            catch (Exception ex) when (IsUnreadable(ex))
            { Error = Describe(ex); return false; }
        }

        // A file that cannot be read refuses every save, because overwriting it would destroy
        // whatever it held. Naming the file tells the user what to repair, restore or remove.
        private string Describe(Exception ex) => _path + Environment.NewLine + ex.Message;

        private T Read()
        {
            try
            {
                return _normalize(JsonSerializer.Deserialize<T>(File.ReadAllText(_path))
                    ?? throw new InvalidDataException("The preferences file contains no saved data."));
            }
            catch (FileNotFoundException) { return _create(); }
            catch (DirectoryNotFoundException) { return _create(); }
        }

        internal static void Publish(string path, byte[] bytes, Action<Stream, byte[]>? write = null)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            string backup = temporary + ".original";
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
                {
                    if (write == null) stream.Write(bytes, 0, bytes.Length);
                    else write(stream, bytes);
                    stream.Flush(true);
                }
                if (File.Exists(path))
                {
                    try { File.Replace(temporary, path, backup); }
                    catch
                    {
                        // A failed native replacement can move the original to its backup name.
                        if (!File.Exists(path) && File.Exists(backup)) File.Move(backup, path);
                        throw;
                    }
                    TryDelete(backup);
                }
                else File.Move(temporary, path);
            }
            finally { TryDelete(temporary); }
        }

        private static void TryDelete(string path)
        {
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
