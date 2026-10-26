using System;
using System.Security.Cryptography;
using System.Text;

namespace KillerScan.Services
{
    /// <summary>
    /// Names the mutexes that serialize writes to one user's settings, history and Locations files
    /// across app instances. They live in the session namespace: a machine-wide name is predictable
    /// from the profile path, so another account on a shared computer could create it first and
    /// hold it, leaving every save to report that the file is busy.
    /// </summary>
    internal static class ProfileLock
    {
        internal static string Name(string kind, string path)
        {
            using var hash = SHA256.Create();
            string key = BitConverter.ToString(hash.ComputeHash(Encoding.UTF8.GetBytes(path.ToUpperInvariant()))).Replace("-", "");
            return "Local\\KillerScan." + kind + "." + key;
        }
    }
}
