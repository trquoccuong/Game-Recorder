using Odx.Core;
using Xunit;

namespace Odx.Tests;

public class HotkeyTests
{
    [Fact]
    public void Default_is_ctrl_shift_r() => Assert.Equal("Ctrl+Shift+R", Hotkey.Default.ToString());

    [Theory]
    [InlineData(HotkeyModifiers.None, 0x78, "F9")]
    [InlineData(HotkeyModifiers.Alt | HotkeyModifiers.Win, 0x20, "Alt+Win+Space")]
    [InlineData(HotkeyModifiers.Shift | HotkeyModifiers.Ctrl, '5', "Ctrl+Shift+5")]
    public void Formats_modifiers_in_fixed_order(HotkeyModifiers mods, int vk, string expected) =>
        Assert.Equal(expected, new Hotkey(mods, vk).ToString());
}
