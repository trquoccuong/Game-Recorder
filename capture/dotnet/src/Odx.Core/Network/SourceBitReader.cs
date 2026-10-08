using System.Text;

namespace Odx.Core;

/// <summary>The Source engine's bf_read: little-endian, least significant bit of each byte first.</summary>
public sealed class SourceBitReader(byte[] data, int startByte = 0)
{
    private int _bit = startByte * 8;

    public int BitsLeft => data.Length * 8 - _bit;

    /// <exception cref="EndOfStreamException">Past the end.</exception>
    public uint Bits(int count)
    {
        if (count > BitsLeft) throw new EndOfStreamException();
        uint value = 0;
        for (int i = 0; i < count; i++, _bit++)
            if ((data[_bit >> 3] & (1 << (_bit & 7))) != 0) value |= 1u << i;
        return value;
    }

    public bool Bit() => Bits(1) != 0;

    public int Int32() => (int)Bits(32);

    public byte[] Bytes(int count)
    {
        if (count * 8 > BitsLeft) throw new EndOfStreamException();
        var result = new byte[count];
        if ((_bit & 7) == 0)
        {
            Array.Copy(data, _bit >> 3, result, 0, count);
            _bit += count * 8;
        }
        else for (int i = 0; i < count; i++) result[i] = (byte)Bits(8);
        return result;
    }

    /// <summary>Source 2's message id: 6 bits, the top two of which say how many bits follow (0, 4, 8 or 28).</summary>
    public uint UBitVar()
    {
        uint value = Bits(6);
        return (value >> 4) switch
        {
            0 => value,
            1 => (value & 15) | (Bits(4) << 4),
            2 => (value & 15) | (Bits(8) << 4),
            _ => (value & 15) | (Bits(28) << 4),
        };
    }

    public uint VarInt32()
    {
        uint value = 0;
        for (int i = 0; i < 5; i++)
        {
            uint b = Bits(8);
            value |= (b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return value;
        }
        return value;
    }

    public string String()
    {
        var bytes = new List<byte>();
        for (byte b; (b = (byte)Bits(8)) != 0;) bytes.Add(b);
        return Encoding.UTF8.GetString(bytes.ToArray());
    }
}

