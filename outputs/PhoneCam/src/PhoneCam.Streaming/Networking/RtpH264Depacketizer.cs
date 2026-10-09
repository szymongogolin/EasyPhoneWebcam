using System.IO;

namespace PhoneCam.Streaming.Networking;

/// <summary>Minimal RFC 6184 receiver for single NAL, STAP-A and FU-A packets (non-interleaved mode).</summary>
public sealed class RtpH264Depacketizer
{
    private readonly List<byte[]> _nalUnits = [];
    private readonly MemoryStream _fragment = new();
    private ushort? _expectedSequence;
    private uint? _timestamp;
    private bool _damaged;

    public long CompletedAccessUnits { get; private set; }
    public long LostPackets { get; private set; }

    public byte[]? Push(ReadOnlySpan<byte> packet)
    {
        if (!TryReadRtpPayload(packet, out var sequence, out var timestamp, out var marker, out var payload))
            return null;

        if (_expectedSequence is { } expected && sequence != expected)
        {
            ushort gap = unchecked((ushort)(sequence - expected));
            if (gap >= 0x8000)
                return null; // late/duplicate packet; do not count it as a large forward loss
            LostPackets += gap;
            _damaged = true;
            _nalUnits.Clear();
            _fragment.SetLength(0);
        }
        _expectedSequence = unchecked((ushort)(sequence + 1));

        if (_timestamp is { } prior && timestamp != prior)
        {
            // The previous access unit did not have an RTP marker; discard it rather than
            // allowing a partial frame to contaminate the next one.
            _nalUnits.Clear();
            _fragment.SetLength(0);
            _damaged = false;
        }
        _timestamp = timestamp;

        if (!_damaged && !AppendPayload(payload))
        {
            _damaged = true;
            _nalUnits.Clear();
            _fragment.SetLength(0);
        }

        if (!marker)
            return null;

        byte[]? accessUnit = null;
        if (!_damaged && _fragment.Length == 0 && _nalUnits.Count > 0)
        {
            using var output = new MemoryStream();
            foreach (byte[] nal in _nalUnits)
            {
                output.Write([0, 0, 0, 1]);
                output.Write(nal);
            }
            accessUnit = output.ToArray();
            CompletedAccessUnits++;
        }

        _nalUnits.Clear();
        _fragment.SetLength(0);
        _damaged = false;
        _timestamp = null;
        return accessUnit;
    }

    private bool AppendPayload(ReadOnlySpan<byte> payload)
    {
        if (payload.IsEmpty)
            return false;

        int type = payload[0] & 0x1f;
        if (type is >= 1 and <= 23)
        {
            if (_fragment.Length != 0)
                return false;
            _nalUnits.Add(payload.ToArray());
            return true;
        }

        if (type == 24) // STAP-A
        {
            if (_fragment.Length != 0)
                return false;
            int offset = 1;
            while (offset + 2 <= payload.Length)
            {
                int length = (payload[offset] << 8) | payload[offset + 1];
                offset += 2;
                if (length == 0 || offset + length > payload.Length)
                    return false;
                _nalUnits.Add(payload.Slice(offset, length).ToArray());
                offset += length;
            }
            return offset == payload.Length && _nalUnits.Count > 0;
        }

        if (type == 28) // FU-A
        {
            if (payload.Length < 3)
                return false;
            bool start = (payload[1] & 0x80) != 0;
            bool end = (payload[1] & 0x40) != 0;
            if (start)
            {
                if (_fragment.Length != 0)
                    return false;
                byte nalHeader = (byte)((payload[0] & 0xe0) | (payload[1] & 0x1f));
                _fragment.WriteByte(nalHeader);
            }
            else if (_fragment.Length == 0)
            {
                return false;
            }

            _fragment.Write(payload[2..]);
            if (end)
            {
                _nalUnits.Add(_fragment.ToArray());
                _fragment.SetLength(0);
            }
            return true;
        }

        // Other packetization modes (including interleaved mode) are outside this LAN MVP.
        return false;
    }

    private static bool TryReadRtpPayload(ReadOnlySpan<byte> packet, out ushort sequence, out uint timestamp, out bool marker, out ReadOnlySpan<byte> payload)
    {
        sequence = 0;
        timestamp = 0;
        marker = false;
        payload = default;
        if (packet.Length < 12 || (packet[0] >> 6) != 2)
            return false;

        sequence = (ushort)((packet[2] << 8) | packet[3]);
        timestamp = ((uint)packet[4] << 24) | ((uint)packet[5] << 16) | ((uint)packet[6] << 8) | packet[7];
        marker = (packet[1] & 0x80) != 0;
        int offset = 12 + (packet[0] & 0x0f) * 4;
        if (offset > packet.Length)
            return false;

        if ((packet[0] & 0x10) != 0)
        {
            if (offset + 4 > packet.Length)
                return false;
            int extensionLength = ((packet[offset + 2] << 8) | packet[offset + 3]) * 4;
            offset += 4 + extensionLength;
            if (offset > packet.Length)
                return false;
        }

        int end = packet.Length;
        if ((packet[0] & 0x20) != 0)
        {
            int paddingLength = packet[^1];
            if (paddingLength == 0 || paddingLength > end - offset)
                return false;
            end -= paddingLength;
        }

        if (end <= offset)
            return false;
        payload = packet[offset..end];
        return true;
    }
}
