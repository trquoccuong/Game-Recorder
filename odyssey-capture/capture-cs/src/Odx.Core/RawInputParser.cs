using System.Buffers.Binary;

namespace Odx.Core;

/// <summary>
/// Decodes a RAWINPUT buffer (as returned by GetRawInputData) into input events. Pure byte parsing so it can be
/// tested off Windows. Layout (x64): RAWINPUTHEADER is 24 bytes (dwType, dwSize, hDevice, wParam).
/// </summary>
public static class RawInputParser
{
    public const int TypeMouse = 0, TypeKeyboard = 1;
    private const ushort MouseMoveAbsolute = 0x01;
    private const ushort KeyBreak = 0x01;
    private const ushort FakeVirtualKey = 0xFF;

    private static readonly (ushort Down, ushort Up, string Name)[] Buttons =
    [
        (0x0001, 0x0002, "Left"), (0x0004, 0x0008, "Right"), (0x0010, 0x0020, "Middle"),
        (0x0040, 0x0080, "X1"), (0x0100, 0x0200, "X2"),
    ];

    public static int HeaderSize(int pointerSize) => 8 + 2 * pointerSize;

    public static IEnumerable<InputEvent> Parse(ReadOnlySpan<byte> buffer, int pointerSize, double tQpcMs)
    {
        var events = new List<InputEvent>();
        int type = BinaryPrimitives.ReadInt32LittleEndian(buffer);
        var data = buffer[HeaderSize(pointerSize)..];

        if (type == TypeKeyboard)
        {
            // RAWKEYBOARD: MakeCode u16 @0, Flags u16 @2, Reserved u16 @4, VKey u16 @6
            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(data[2..]);
            ushort vk = BinaryPrimitives.ReadUInt16LittleEndian(data[6..]);
            if (vk != FakeVirtualKey)
                events.Add(new InputEvent(tQpcMs, (flags & KeyBreak) != 0 ? InputKind.KeyUp : InputKind.KeyDown,
                    KeyNames.FromVirtualKey(vk)));
        }
        else if (type == TypeMouse)
        {
            // RAWMOUSE: usFlags u16 @0, usButtonFlags u16 @4, usButtonData u16 @6, ulRawButtons u32 @8,
            //           lLastX i32 @12, lLastY i32 @16
            ushort moveFlags = BinaryPrimitives.ReadUInt16LittleEndian(data);
            ushort buttonFlags = BinaryPrimitives.ReadUInt16LittleEndian(data[4..]);
            int dx = BinaryPrimitives.ReadInt32LittleEndian(data[12..]);
            int dy = BinaryPrimitives.ReadInt32LittleEndian(data[16..]);
            if ((moveFlags & MouseMoveAbsolute) == 0 && (dx != 0 || dy != 0))
                events.Add(new InputEvent(tQpcMs, InputKind.MouseMove, "", dx, dy));
            foreach (var (down, up, name) in Buttons)
            {
                if ((buttonFlags & down) != 0) events.Add(new InputEvent(tQpcMs, InputKind.ButtonDown, name));
                if ((buttonFlags & up) != 0) events.Add(new InputEvent(tQpcMs, InputKind.ButtonUp, name));
            }
        }
        return events;
    }
}
