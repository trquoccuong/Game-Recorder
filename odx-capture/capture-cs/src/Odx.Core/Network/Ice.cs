namespace Odx.Core;

/// <summary>
/// The ICE block cipher (Matthew Kwan, 1996; public domain: "no patents or copyrights, use is unrestricted"), ported from
/// the author's ANSI C reference. Level n uses an 8n-byte key and 16n rounds; level 0 ("Thin-ICE") 8 rounds. CS:GO
/// encrypts netchannel traffic with ICE-2.
/// </summary>
public sealed class IceKey
{
    private static readonly int[,] SMod = { { 333, 313, 505, 369 }, { 379, 375, 319, 391 }, { 361, 445, 451, 397 }, { 397, 425, 395, 505 } };
    private static readonly int[,] SXor = { { 0x83, 0x85, 0x9b, 0xcd }, { 0xcc, 0xa7, 0xad, 0x41 }, { 0x4b, 0x2e, 0xd4, 0x33 }, { 0xea, 0xcb, 0x2e, 0x04 } };

    private static readonly uint[] PBox =
    [
        0x00000001, 0x00000080, 0x00000400, 0x00002000, 0x00080000, 0x00200000, 0x01000000, 0x40000000,
        0x00000008, 0x00000020, 0x00000100, 0x00004000, 0x00010000, 0x00800000, 0x04000000, 0x20000000,
        0x00000004, 0x00000010, 0x00000200, 0x00008000, 0x00020000, 0x00400000, 0x08000000, 0x10000000,
        0x00000002, 0x00000040, 0x00000800, 0x00001000, 0x00040000, 0x00100000, 0x02000000, 0x80000000,
    ];

    private static readonly int[] KeyRotation = [0, 1, 2, 3, 2, 1, 3, 0, 1, 3, 2, 0, 3, 1, 0, 2];

    private static readonly uint[,] SBox = BuildSBoxes();

    private readonly int _size, _rounds;
    private readonly uint[,] _schedule;

    /// <param name="key">8·max(level, 1) bytes.</param>
    public IceKey(int level, ReadOnlySpan<byte> key)
    {
        (_size, _rounds) = level < 1 ? (1, 8) : (level, level * 16);
        if (key.Length != _size * 8) throw new ArgumentException($"ICE level {level} needs a {_size * 8}-byte key", nameof(key));
        _schedule = new uint[_rounds, 3];
        var kb = new ushort[4];
        if (_rounds == 8)
        {
            for (int i = 0; i < 4; i++) kb[3 - i] = (ushort)((key[i * 2] << 8) | key[i * 2 + 1]);
            BuildSchedule(kb, 0, 0);
            return;
        }
        for (int i = 0; i < _size; i++)
        {
            for (int j = 0; j < 4; j++) kb[3 - j] = (ushort)((key[i * 8 + j * 2] << 8) | key[i * 8 + j * 2 + 1]);
            BuildSchedule(kb, i * 8, 0);
            BuildSchedule(kb, _rounds - 8 - i * 8, 8);
        }
    }

    public void EncryptBlock(ReadOnlySpan<byte> plain, Span<byte> cipher)
    {
        uint l = Load(plain), r = Load(plain[4..]);
        for (int i = 0; i < _rounds; i += 2)
        {
            l ^= F(r, i);
            r ^= F(l, i + 1);
        }
        Store(r, cipher);
        Store(l, cipher[4..]);
    }

    public void DecryptBlock(ReadOnlySpan<byte> cipher, Span<byte> plain)
    {
        uint l = Load(cipher), r = Load(cipher[4..]);
        for (int i = _rounds - 1; i > 0; i -= 2)
        {
            l ^= F(r, i);
            r ^= F(l, i - 1);
        }
        Store(r, plain);
        Store(l, plain[4..]);
    }

    /// <summary>Every whole 8-byte block, in place (a trailing partial block is left as it is).</summary>
    public void Decrypt(Span<byte> data)
    {
        Span<byte> block = stackalloc byte[8];
        for (int o = 0; o + 8 <= data.Length; o += 8)
        {
            DecryptBlock(data.Slice(o, 8), block);
            block.CopyTo(data.Slice(o, 8));
        }
    }

    public void Encrypt(Span<byte> data)
    {
        Span<byte> block = stackalloc byte[8];
        for (int o = 0; o + 8 <= data.Length; o += 8)
        {
            EncryptBlock(data.Slice(o, 8), block);
            block.CopyTo(data.Slice(o, 8));
        }
    }

    private uint F(uint p, int round)
    {
        uint tl = ((p >> 16) & 0x3ff) | (((p >> 14) | (p << 18)) & 0xffc00);  // left half expansion
        uint tr = (p & 0x3ff) | ((p << 2) & 0xffc00);                         // right half expansion
        uint al = _schedule[round, 2] & (tl ^ tr);                             // salt permutation
        uint ar = al ^ tr;
        al ^= tl;
        al ^= _schedule[round, 0];
        ar ^= _schedule[round, 1];
        return SBox[0, al >> 10] | SBox[1, al & 0x3ff] | SBox[2, ar >> 10] | SBox[3, ar & 0x3ff];
    }

    /// <summary>Rounds [n, n+7] of the key schedule; rotation from offset 0 or 8 of the rotation table.</summary>
    private void BuildSchedule(ushort[] kb, int n, int rotationOffset)
    {
        for (int i = 0; i < 8; i++)
        {
            int kr = KeyRotation[rotationOffset + i];
            _schedule[n + i, 0] = _schedule[n + i, 1] = _schedule[n + i, 2] = 0;
            for (int j = 0; j < 15; j++)
            {
                for (int k = 0; k < 4; k++)
                {
                    int index = (kr + k) & 3;
                    int bit = kb[index] & 1;
                    _schedule[n + i, j % 3] = (_schedule[n + i, j % 3] << 1) | (uint)bit;
                    kb[index] = (ushort)((kb[index] >> 1) | ((bit ^ 1) << 15));
                }
            }
        }
    }

    private static uint[,] BuildSBoxes()
    {
        var s = new uint[4, 1024];
        for (int i = 0; i < 1024; i++)
        {
            int col = (i >> 1) & 0xff, row = (i & 0x1) | ((i & 0x200) >> 8);
            for (int b = 0; b < 4; b++) s[b, i] = Permute(GfExp7((uint)(col ^ SXor[b, row]), (uint)SMod[b, row]) << (24 - 8 * b));
        }
        return s;
    }

    private static uint GfMultiply(uint a, uint b, uint m)
    {
        uint result = 0;
        while (b != 0)
        {
            if ((b & 1) != 0) result ^= a;
            a <<= 1;
            b >>= 1;
            if (a >= 256) a ^= m;
        }
        return result;
    }

    private static uint GfExp7(uint b, uint m)
    {
        if (b == 0) return 0;
        uint x = GfMultiply(b, b, m);
        x = GfMultiply(b, x, m);
        x = GfMultiply(x, x, m);
        return GfMultiply(b, x, m);
    }

    private static uint Permute(uint x)
    {
        uint result = 0;
        for (int i = 0; x != 0; i++, x >>= 1)
            if ((x & 1) != 0) result |= PBox[i];
        return result;
    }

    private static uint Load(ReadOnlySpan<byte> b) => ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static void Store(uint v, Span<byte> b)
    {
        b[0] = (byte)(v >> 24);
        b[1] = (byte)(v >> 16);
        b[2] = (byte)(v >> 8);
        b[3] = (byte)v;
    }
}
