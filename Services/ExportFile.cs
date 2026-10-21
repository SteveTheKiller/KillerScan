using System.IO;
using System.Text;

namespace KillerScan.Services
{
    /// <summary>
    /// Writes user-chosen export files without truncating the old one first. The complete
    /// output is staged beside the target and swapped in only after it is flushed, so a failed
    /// encoder, full disk or locked target leaves the previous file exactly as it was.
    /// </summary>
    internal static class ExportFile
    {
        internal static void WriteText(string path, string text, Encoding encoding)
        {
            byte[] preamble = encoding.GetPreamble();
            byte[] body = encoding.GetBytes(text);
            Write(path, stream =>
            {
                stream.Write(preamble, 0, preamble.Length);
                stream.Write(body, 0, body.Length);
            });
        }

        internal static void Write(string path, System.Action<Stream> write) =>
            JsonPreferenceFile<object>.Publish(Path.GetFullPath(path), [], (stream, _) => write(stream));
    }
}
