using System.IO;
using System.Text.Json;

namespace KillerScan.Services
{
    /// <summary>
    /// Remembers the SSH username to use for a device in one Location (logins.json), keyed the
    /// same way device-type overrides are: by MAC address, so it survives a DHCP lease change.
    ///
    /// The point of storing it at all is that the username is per DEVICE, not per network. Windows'
    /// ssh client falls back to the logged-on account name when no user is given, which is right
    /// almost nowhere: a UniFi box wants "ui", a switch wants "admin", an appliance wants whatever
    /// its vendor chose. Guessing produced a failed login every time and no way to say otherwise.
    ///
    /// A device seen with no MAC (routed, or an ARP entry that never resolved) falls back to its IP
    /// as the key. That is weaker - an address can be reassigned - but it is better than refusing to
    /// remember anything, and the entry is only ever used to pre-fill a prompt the user confirms.
    ///
    /// Only a username is stored. There is deliberately no password or key field: KillerScan hands
    /// off to the system ssh client and lets it do the authenticating, so there is nothing here
    /// worth stealing.
    /// </summary>
    internal sealed class DeviceLoginStore
    {
        private readonly JsonPreferenceFile<Dictionary<string, string>> _store;
        private readonly bool _memoryOnly;
        private Dictionary<string, string> _logins => _store.Value;
        public string? SaveError => _store.Error;
        private bool MemoryOnly => _memoryOnly || DemoData.Enabled;

        internal DeviceLoginStore(string path, bool memoryOnly = false)
        {
            _memoryOnly = memoryOnly;
            _store = new(path, () => new(StringComparer.OrdinalIgnoreCase), value => new(value, StringComparer.OrdinalIgnoreCase));
        }

        public void Load()
        {
            if (!MemoryOnly) _store.Load();
        }

        public bool Save() => MemoryOnly || _store.Save();

        /// <summary>MAC when there is one, otherwise the IP. Empty when neither is known, which the
        /// callers treat as "do not remember this one".</summary>
        private static string KeyFor(string? mac, string? ip) =>
            !string.IsNullOrEmpty(mac) ? mac!.ToUpperInvariant()
            : !string.IsNullOrEmpty(ip) ? ip!
            : string.Empty;

        /// <summary>The remembered username, or null if this device has never been asked about.</summary>
        public string? Get(string? mac, string? ip)
        {
            string key = KeyFor(mac, ip);
            if (key.Length == 0) return null;
            return _logins.TryGetValue(key, out var user) ? user : null;
        }

        /// <summary>Remembers a username for the device. An empty or null username REMOVES the entry,
        /// so clearing the box in the prompt goes back to plain `ssh &lt;ip&gt;` rather than storing a
        /// blank that would look like a remembered answer.</summary>
        public bool Set(string? mac, string? ip, string? user)
        {
            string key = KeyFor(mac, ip);
            if (key.Length == 0) return true;
            return _store.Change(values =>
            {
                if (string.IsNullOrWhiteSpace(user)) values.Remove(key);
                else values[key] = user!.Trim();
            }, MemoryOnly);
        }
    }

    /// <summary>The app-wide login store, until every caller works through a Location.</summary>
    internal static class DeviceLogins
    {
        private static readonly string FilePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "KillerScan", "logins.json");
        internal static DeviceLoginStore Store { get; private set; } = new(FilePath);

        public static string? SaveError => Store.SaveError;
        public static void Load() { Store = new(FilePath); Store.Load(); }
        public static bool Save() => Store.Save();
        public static string? Get(string? mac, string? ip) => Store.Get(mac, ip);
        public static bool Set(string? mac, string? ip, string? user) => Store.Set(mac, ip, user);
    }
}
