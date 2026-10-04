using System.Buffers.Binary;
using System.Text;
using Odx.Core;
using Xunit;

namespace Odx.Core.Tests;

public class InputLogTests
{
    [Fact]
    public void Writer_output_matches_shared_fixture()
    {
        var sw = new StringWriter();
        using (var log = new InputLogWriter(sw))
        {
            log.Write(new InputEvent(1000.5, InputKind.KeyDown, "W"));
            log.Write(new InputEvent(1001.25, InputKind.MouseMove, "", 3, -1));
            log.Write(new InputEvent(1010, InputKind.ButtonDown, "Left"));
            log.Write(new InputEvent(1020, InputKind.KeyUp, "W"));
            log.Write(new InputEvent(1030, InputKind.ButtonUp, "Left"));
            log.Flush();
            var expected = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "input_events.csv"));
            Assert.Equal(expected, sw.ToString());
        }
    }

    [Fact]
    public void Clock_samples_have_header_and_full_precision()
    {
        var sw = new StringWriter();
        var clock = new ClockSampleWriter(sw);
        clock.Write(0.1 + 0.2, 1_790_000_000_000);
        Assert.Equal("t_qpc_ms,utc_unix_ms\n0.30000000000000004,1790000000000\n", sw.ToString());
    }
}

public class KeyNamesTests
{
    [Theory]
    [InlineData('W', "W")]
    [InlineData('7', "7")]
    [InlineData(0x70, "F1")]
    [InlineData(0x87, "F24")]
    [InlineData(0x65, "Num5")]
    [InlineData(0x20, "Space")]
    [InlineData(0x25, "ArrowLeft")]
    [InlineData(0xBC, "Comma")]
    [InlineData(0xE7, "VK_E7")]
    public void Maps_virtual_keys_to_stable_names(int vk, string name) => Assert.Equal(name, KeyNames.FromVirtualKey(vk));

    [Fact]
    public void Names_never_contain_delimiters()
    {
        for (int vk = 0; vk < 256; vk++)
        {
            var name = KeyNames.FromVirtualKey(vk);
            Assert.DoesNotContain(",", name);
            Assert.DoesNotContain("|", name);
        }
    }
}

public class RawInputParserTests
{
    private const int Ptr = 8;  // x64

    private delegate void Fill(Span<byte> data);

    private static byte[] Raw(int type, Fill fill)
    {
        var b = new byte[RawInputParser.HeaderSize(Ptr) + 24];
        BinaryPrimitives.WriteInt32LittleEndian(b, type);
        fill(b.AsSpan(RawInputParser.HeaderSize(Ptr)));
        return b;
    }

    [Fact]
    public void Keyboard_make_and_break()
    {
        var down = Raw(RawInputParser.TypeKeyboard, d => BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 'W'));
        var up = Raw(RawInputParser.TypeKeyboard, d =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d[2..], 0x01);  // RI_KEY_BREAK
            BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 'W');
        });
        Assert.Equal([new InputEvent(5, InputKind.KeyDown, "W")], RawInputParser.Parse(down, Ptr, 5));
        Assert.Equal([new InputEvent(6, InputKind.KeyUp, "W")], RawInputParser.Parse(up, Ptr, 6));
    }

    [Fact]
    public void Fake_virtual_key_is_ignored()
    {
        var fake = Raw(RawInputParser.TypeKeyboard, d => BinaryPrimitives.WriteUInt16LittleEndian(d[6..], 0xFF));
        Assert.Empty(RawInputParser.Parse(fake, Ptr, 0));
    }

    [Fact]
    public void Mouse_relative_move_and_buttons()
    {
        var raw = Raw(RawInputParser.TypeMouse, d =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d[4..], 0x0001 | 0x0008);  // left down, right up
            BinaryPrimitives.WriteInt32LittleEndian(d[12..], 4);
            BinaryPrimitives.WriteInt32LittleEndian(d[16..], -2);
        });
        Assert.Equal(
            [
                new InputEvent(1, InputKind.MouseMove, "", 4, -2),
                new InputEvent(1, InputKind.ButtonDown, "Left"),
                new InputEvent(1, InputKind.ButtonUp, "Right"),
            ],
            RawInputParser.Parse(raw, Ptr, 1));
    }

    [Fact]
    public void Absolute_mouse_moves_are_not_deltas()
    {
        var raw = Raw(RawInputParser.TypeMouse, d =>
        {
            BinaryPrimitives.WriteUInt16LittleEndian(d, 0x01);  // MOUSE_MOVE_ABSOLUTE
            BinaryPrimitives.WriteInt32LittleEndian(d[12..], 30000);
        });
        Assert.Empty(RawInputParser.Parse(raw, Ptr, 0));
    }
}

public class InputMsgTests
{
    [Fact]
    public void Encodes_the_packed_layout_read_by_the_cpp_dll()
    {
        var b = InputMsg.Encode(new InputEvent(1234.5, InputKind.MouseMove, "", 3, -1));
        Assert.Equal(49, b.Length);
        Assert.Equal(4, b[0]);  // MsgKind::MouseMove
        Assert.Equal(1234.5, BinaryPrimitives.ReadDoubleLittleEndian(b.AsSpan(1)));
        Assert.Equal(3f, BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(9)));
        Assert.Equal(-1f, BinaryPrimitives.ReadSingleLittleEndian(b.AsSpan(13)));
    }

    [Fact]
    public void Value_is_truncated_and_nul_terminated()
    {
        var b = InputMsg.Encode(new InputEvent(0, InputKind.KeyDown, new string('A', 40)));
        Assert.Equal(new string('A', 31), Encoding.UTF8.GetString(b, 17, 31));
        Assert.Equal(0, b[48]);
    }
}
