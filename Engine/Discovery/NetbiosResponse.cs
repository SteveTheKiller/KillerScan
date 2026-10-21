using System.Text;

namespace KillerScan.Engine
{
    /// <summary>
    /// Bounded NBSTAT (RFC 1002 node status) reply decoding. Matching a reply to its query is
    /// correlation, not authentication: any host on the path can still answer for the target.
    /// </summary>
    internal static class NetbiosResponse
    {
        private const int HeaderLength = 12;
        private const int NodeNameLength = 18;

        internal static bool TryDecode(byte[] bytes, int transactionId, out string name)
        {
            name = string.Empty;
            if (bytes == null || bytes.Length < HeaderLength) return false;
            int id = (bytes[0] << 8) | bytes[1];
            int flags = (bytes[2] << 8) | bytes[3];
            int answers = (bytes[6] << 8) | bytes[7];
            // Response bit set, opcode 0 (query), rcode 0, exactly one answer.
            if (id != transactionId || (flags & 0x8000) == 0 || (flags & 0x7800) != 0 ||
                (flags & 0x000f) != 0 || answers != 1)
                return false;

            int offset = HeaderLength;
            if (!SkipName(bytes, ref offset) || offset + 10 > bytes.Length) return false;
            int type = (bytes[offset] << 8) | bytes[offset + 1];
            int klass = (bytes[offset + 2] << 8) | bytes[offset + 3];
            int length = (bytes[offset + 8] << 8) | bytes[offset + 9];
            offset += 10;
            if (type != 0x21 || klass != 1 || length < 1 || offset + length > bytes.Length) return false;

            int count = bytes[offset];
            int names = offset + 1;
            if (names + count * NodeNameLength > offset + length) return false;
            for (int i = 0; i < count; i++)
            {
                int entry = names + i * NodeNameLength;
                byte suffix = bytes[entry + 15];
                bool group = (bytes[entry + 16] & 0x80) != 0;
                if (group || (suffix != 0x00 && suffix != 0x20)) continue;
                string candidate = Encoding.ASCII.GetString(bytes, entry, 15).TrimEnd(' ', '\0');
                if (candidate.Length > 0 && candidate.All(c => c >= 0x21 && c < 0x7f || c == ' '))
                {
                    name = candidate;
                    return true;
                }
            }
            return false;
        }

        // Labels or a single compression pointer; anything else is malformed.
        private static bool SkipName(byte[] bytes, ref int offset)
        {
            for (int labels = 0; labels < 64 && offset < bytes.Length; labels++)
            {
                byte length = bytes[offset];
                if (length == 0) { offset++; return true; }
                if ((length & 0xc0) == 0xc0) { offset += 2; return offset <= bytes.Length; }
                if ((length & 0xc0) != 0) return false;
                offset += 1 + length;
            }
            return false;
        }
    }
}
