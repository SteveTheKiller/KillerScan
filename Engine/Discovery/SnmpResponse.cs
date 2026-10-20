using System.Text;

namespace KillerScan.Engine
{
    internal static class SnmpResponse
    {
        private static readonly byte[] Community = Encoding.ASCII.GetBytes("public");
        private static readonly byte[] SysDescr = [0x2b, 6, 1, 2, 1, 1, 1, 0];

        internal static bool TryDecode(byte[] bytes, int requestId, out string description)
        {
            description = string.Empty;
            var packet = new Reader(bytes, 0, bytes.Length);
            if (!packet.Read(0x30, out var message) || !packet.Empty ||
                !message.Integer(out int version) || version != 0 || !message.EqualsBytes(0x04, Community) ||
                !message.Read(0xa2, out var response) || !message.Empty ||
                !response.Integer(out int id) || id != requestId ||
                !response.Integer(out int error) || error != 0 || !response.Integer(out int index) || index != 0 ||
                !response.Read(0x30, out var bindings) || !response.Empty ||
                !bindings.Read(0x30, out var binding) || !bindings.Empty ||
                !binding.EqualsBytes(0x06, SysDescr) || !binding.Read(0x04, out var value) || !binding.Empty)
                return false;
            description = Encoding.UTF8.GetString(bytes, value._position, value._end - value._position).Trim();
            return true;
        }

        private sealed class Reader(byte[] bytes, int start, int length)
        {
            internal int _position = start;
            internal readonly int _end = start + length;
            internal bool Empty => _position == _end;
            internal bool Read(byte tag, out Reader value)
            {
                value = null!;
                if (_end - _position < 2 || bytes[_position++] != tag) return false;
                int first = bytes[_position++];
                long size = first;
                if ((first & 0x80) != 0)
                {
                    int count = first & 0x7f;
                    if (count == 0 || count > 4 || count > _end - _position) return false;
                    size = 0;
                    for (int i = 0; i < count; i++) size = (size << 8) | bytes[_position++];
                }
                if (size > _end - _position) return false;
                value = new Reader(bytes, _position, (int)size);
                _position += (int)size;
                return true;
            }
            internal bool Integer(out int result)
            {
                result = 0;
                if (!Read(0x02, out var value)) return false;
                int count = value._end - value._position;
                if (count < 1 || count > 4) return false;
                result = (bytes[value._position] & 0x80) == 0 ? 0 : -1;
                while (value._position < value._end) result = (result << 8) | bytes[value._position++];
                return true;
            }
            internal bool EqualsBytes(byte tag, byte[] expected)
            {
                if (!Read(tag, out var value) || value._end - value._position != expected.Length) return false;
                for (int i = 0; i < expected.Length; i++) if (bytes[value._position + i] != expected[i]) return false;
                return true;
            }
        }
    }
}
