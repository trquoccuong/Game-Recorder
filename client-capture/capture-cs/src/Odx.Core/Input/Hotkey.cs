namespace Odx.Core;

/// <summary>Values match the Win32 RegisterHotKey MOD_* flags and WPF ModifierKeys.</summary>
[Flags]
public enum HotkeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }

public readonly record struct Hotkey(HotkeyModifiers Modifiers, int VirtualKey)
{
    // A lone F10: no gameplay binding uses it, so odx-build drops it from input_keys instead of adding actions.
    public static readonly Hotkey Default = new(HotkeyModifiers.None, 0x79);

    public override string ToString()
    {
        var parts = new List<string>();
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        parts.Add(KeyNames.FromVirtualKey(VirtualKey));
        return string.Join("+", parts);
    }
}
