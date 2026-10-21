using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace KillerScan.Services
{
    internal static class ImmutablePowerShell
    {
        internal static string Arguments(string script)
        {
            using var buffer = new MemoryStream();
            byte[] bytes = Encoding.UTF8.GetBytes(script);
            using (var gzip = new GZipStream(buffer, CompressionLevel.Optimal, leaveOpen: true))
                gzip.Write(bytes, 0, bytes.Length);
            string bootstrap = "$stream=[IO.MemoryStream]::new([Convert]::FromBase64String('"
                + Convert.ToBase64String(buffer.ToArray())
                + "'));$reader=[IO.StreamReader]::new([IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress));"
                + "try{&([scriptblock]::Create($reader.ReadToEnd()))}finally{$reader.Dispose();$stream.Dispose()}";
            string arguments = "-NoLogo -NoProfile -NonInteractive -EncodedCommand "
                + Convert.ToBase64String(Encoding.Unicode.GetBytes(bootstrap));
            if (arguments.Length > 30000) throw new IOException("The immutable helper exceeds the Windows command limit.");
            return arguments;
        }
    }
}
