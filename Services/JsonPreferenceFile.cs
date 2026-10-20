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
        internal T Value { get; private set; }
        internal string? Error { get; private set; }

        internal JsonPreferenceFile(string path, Func<T> create, Func<T, T> normalize, Action<Stream, byte[]>? write = null)
        {
            _path = Path.GetFullPath(path); _create = create; _normalize = normalize; _write = write; Value = create();
        }

        internal void Load()
        {
            try { Value = Read(); Error = null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { Value = _create(); Error = ex.Message; }
        }

        internal bool Change(Action<T> mutation, bool memoryOnly = false)
        {
            mutation(Value);
            if (memoryOnly) return true;
            _pending.Add(mutation);
            return Save();
        }

        internal bool Save()
        {
            try
            {
                using var hash = SHA256.Create();
                string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(_path.ToUpperInvariant()))).Replace("-", "");
                using var mutex = new Mutex(false, "Global\\KillerScan.Preferences." + key);
                bool owned = false;
                try
                {
                    try { owned = mutex.WaitOne(TimeSpan.FromSeconds(2)); }
                    catch (AbandonedMutexException) { owned = true; }
                    if (!owned) throw new IOException("The preferences file is busy. Try again.");
                    var latest = Read();
                    foreach (var mutation in _pending) mutation(latest);
                    Publish(_path, Encoding.UTF8.GetBytes(JsonSerializer.Serialize(latest, new JsonSerializerOptions { WriteIndented = true })), _write);
                    Value = latest; _pending.Clear(); Error = null;
                    return true;
                }
                finally { if (owned) mutex.ReleaseMutex(); }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            { Error = ex.Message; return false; }
        }

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
