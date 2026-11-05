using System;
using System.IO;

namespace KillerScan.Services
{
    internal static class RemovalTransaction
    {
        /// <summary>The registry and shortcut boundary participates before owned bytes are discarded.</summary>
        internal static void Run(string directory, Action removeRegistration, Action restoreRegistration, Action? notifyEnvironment = null)
        {
            using (InstallationFiles.PinDirectory(directory))
            {
                string image = Path.Combine(directory, AppInfo.ExeName);
                string retired = image + ".uninstall-" + Guid.NewGuid().ToString("N");
                bool moved = false, metadataStarted = false;
                try
                {
                    if (File.Exists(image))
                    {
                        if ((File.GetAttributes(image) & FileAttributes.ReparsePoint) != 0)
                            throw new UnauthorizedAccessException("Uninstall cannot remove a linked image: " + image);
                        File.Move(image, retired);
                        moved = true;
                    }
                    metadataStarted = true;
                    removeRegistration();
                    // A mapped image can be renamed but not deleted. Access denied on the renamed copy
                    // means a copy is still running, most likely in another user's session that could
                    // not be inspected, so it is reported as busy (retry later, 1618), not as denied.
                    if (moved)
                        try { File.Delete(retired); }
                        catch (UnauthorizedAccessException) { throw new IOException("KillerTools is still running in another session.", unchecked((int)0x80070020)); }
                    if (File.Exists(image) || File.Exists(retired))
                        throw new IOException("Owned installation files remain. Removal was not completed.");
                    // Backups and update working files are ours too, whatever their age: nothing will
                    // recover from them once the app is gone. A missed one only keeps the folder; it
                    // never fails a removal that already succeeded. Copies marked to keep stay.
                    foreach (string leftover in ExecutableDeployment.UninstallLeftovers(directory, AppInfo.ExeName))
                        try { File.Delete(Path.Combine(directory, leftover)); }
                        catch (IOException) { }
                        catch (UnauthorizedAccessException) { }
                }
                catch (Exception failure)
                {
                    Exception? recovery = null;
                    try
                    {
                        if (moved && File.Exists(retired))
                        {
                            if (File.Exists(image)) throw new IOException("A replacement image occupies the installation path.");
                            File.Move(retired, image);
                        }
                    }
                    catch (Exception error) { recovery = error; }
                    try { if (metadataStarted) restoreRegistration(); }
                    catch (Exception error) { recovery = recovery == null ? error : new AggregateException(recovery, error); }
                    if (metadataStarted) Notify(notifyEnvironment);
                    if (recovery != null)
                        throw new IOException("Uninstall failed and recovery is incomplete. Retain: " + image + " and " + retired,
                            new AggregateException(failure, recovery));
                    throw;
                }
            }
            Notify(notifyEnvironment);
        }

        private static void Notify(Action? notifyEnvironment)
        {
            try { notifyEnvironment?.Invoke(); }
            catch (Exception error) { Console.Error.WriteLine("Environment notification failed: " + error.Message); }
        }
    }
}
