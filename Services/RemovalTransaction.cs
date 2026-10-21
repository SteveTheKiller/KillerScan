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
                string image = Path.Combine(directory, "KillerScan.exe");
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
                    if (moved) File.Delete(retired);
                    if (File.Exists(image) || File.Exists(retired))
                        throw new IOException("Owned installation files remain. Removal was not completed.");
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
