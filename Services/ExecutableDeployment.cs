using System.IO;

namespace KillerScan.Services
{
    internal static class ExecutableDeployment
    {
        internal static void Install(string source, string destination)
        {
            source = Path.GetFullPath(source);
            destination = Path.GetFullPath(destination);
            if (string.Equals(source, destination, StringComparison.OrdinalIgnoreCase)) return;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            string staged = destination + "." + Guid.NewGuid().ToString("N") + ".new";
            try
            {
                using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read))
                using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    input.CopyTo(output);
                    output.Flush(true);
                }
                // Replace only after the complete payload is on the destination volume.
                // A failed copy or a locked target leaves the installed executable intact.
                if (File.Exists(destination)) File.Replace(staged, destination, null);
                else File.Move(staged, destination);
            }
            finally
            {
                try { if (File.Exists(staged)) File.Delete(staged); } catch { }
            }
        }
    }
}
