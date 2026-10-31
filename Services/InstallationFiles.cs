using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace KillerScan.Services
{
    /// <summary>Removes the single file owned by the portable installer.</summary>
    internal static class InstallationFiles
    {
        internal static void Remove(string directory)
        {
            directory = Path.GetFullPath(directory);
            if (Path.GetDirectoryName(directory) == null)
                throw new UnauthorizedAccessException("Cleanup cannot remove a volume root.");
            using (PinDirectory(directory))
            {
                string image = Path.Combine(directory, "KillerScan.exe");
                if (File.Exists(image))
                {
                    if ((File.GetAttributes(image) & FileAttributes.ReparsePoint) != 0)
                        throw new UnauthorizedAccessException("Cleanup cannot remove a linked installation image: " + image);
                    File.Delete(image);
                }
            }
            RemoveEmpty(directory);
        }

        internal static void RemoveEmpty(string directory)
        {
            string full = Path.GetFullPath(directory);
            var parent = Path.GetDirectoryName(full);
            if (parent == null) throw new UnauthorizedAccessException("Cleanup cannot remove a volume root.");
            using (PinDirectory(parent))
            {
                if (!Directory.Exists(full)) return;
                if ((File.GetAttributes(full) & FileAttributes.ReparsePoint) != 0)
                    throw new UnauthorizedAccessException("Cleanup cannot traverse a linked directory: " + full);
                if (Directory.GetFileSystemEntries(full).Length != 0)
                {
                    Console.Error.WriteLine("Unrelated contents were preserved in: " + full);
                    return;
                }
                // Nonrecursive deletion never removes a late arrival or a link's target.
                Directory.Delete(full, recursive: false);
            }
        }

        internal static void RemoveShortcut(string path)
        {
            string full = Path.GetFullPath(path);
            if (!string.Equals(Path.GetFileName(full), "KillerScan.lnk", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Cleanup only owns the KillerScan shortcut.", nameof(path));
            using (PinDirectory(Path.GetDirectoryName(full)!)) File.Delete(full);
        }

        /// <summary>Keep every existing directory ancestor immovable during owned-file cleanup. With
        /// <paramref name="mustExist"/>, a missing ancestor fails instead of being skipped, as the
        /// update paths require (Killendar's UpdatePathSafety).</summary>
        internal static IDisposable PinDirectory(string directory, bool mustExist = false)
        {
            var lease = new DirectoryLease();
            try
            {
                var ancestors = new Stack<string>();
                for (string? item = Path.GetFullPath(directory); item != null; item = Path.GetDirectoryName(item))
                    ancestors.Push(item);
                foreach (string item in ancestors)
                {
                    // Deny delete sharing so existing ancestors cannot be renamed out from
                    // under cleanup. Write sharing permits our owned child-file renames.
                    var handle = CreateFile(item, 0x80000000, 3, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
                    if (handle.IsInvalid)
                    {
                        int code = Marshal.GetLastWin32Error();
                        handle.Dispose();
                        if ((code == 2 || code == 3) && !mustExist) continue;
                        if (code == 2 || code == 3) throw new DirectoryNotFoundException(item);
                        throw new Win32Exception(code);
                    }
                    lease.Handles.Add(handle);
                    FileInformation information;
                    if (!GetFileInformationByHandle(handle, out information))
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    if ((information.Attributes & 0x400) != 0 || (information.Attributes & 0x10) == 0)
                        throw new UnauthorizedAccessException("Cleanup cannot traverse a linked installation path: " + item);
                }
                return lease;
            }
            catch { lease.Dispose(); throw; }
        }

        private sealed class DirectoryLease : IDisposable
        {
            internal readonly List<SafeFileHandle> Handles = new List<SafeFileHandle>();
            public void Dispose()
            {
                for (int index = Handles.Count - 1; index >= 0; index--) Handles[index].Dispose();
                Handles.Clear();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FileInformation
        {
            internal uint Attributes;
            internal System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
            internal uint Volume, SizeHigh, SizeLow, Links, IndexHigh, IndexLow;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateFile(string path, uint access, uint share,
            IntPtr security, uint disposition, uint flags, IntPtr template);
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    }
}
