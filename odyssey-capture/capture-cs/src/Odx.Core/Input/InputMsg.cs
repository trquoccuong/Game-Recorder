using System.Buffers.Binary;
using System.Text;

namespace Odx.Core;

/// <summary>
/// Wire format of the mailslot message read by the C++ extractor DLL (extractor/src/win/common.hpp, InputMsg):
/// packed { u8 kind; f64 t_ms; f32 dx; f32 dy; char value[32] } = 49 bytes, little-endian.
/// </summary>
public static class InputMsg
{
    public const int Size = 49;
    private const int ValueSize = 32;

    public static byte[] Encode(InputEvent e)
    {
        var b = new byte[Size];
        b[0] = (byte)e.Kind;  // enum order matches odx::win::MsgKind
        BinaryPrimitives.WriteDoubleLittleEndian(b.AsSpan(1), e.TQpcMs);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(9), (float)e.Dx);
        BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(13), (float)e.Dy);
        var value = Encoding.UTF8.GetBytes(e.Value);
        value.AsSpan(0, Math.Min(value.Length, ValueSize - 1)).CopyTo(b.AsSpan(17));  // keep a NUL terminator
        return b;
    }

    public static string MailslotName(int pid) => $@"\\.\mailslot\odx_input_{pid}";
}
