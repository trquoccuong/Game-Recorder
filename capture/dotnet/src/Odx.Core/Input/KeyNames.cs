namespace Odx.Core;

/// <summary>Stable names for Windows virtual-key codes, used in input_keys. Never contain ',' or '|'.</summary>
public static class KeyNames
{
    // "Left"/"Right"/"Middle"/"X1"/"X2" are reserved for mouse buttons, so arrow keys are "Arrow*".
    private static readonly Dictionary<int, string> Named = new()
    {
        [0x20] = "Space", [0x10] = "Shift", [0x11] = "Ctrl", [0x12] = "Alt", [0x09] = "Tab",
        [0x0D] = "Enter", [0x1B] = "Escape", [0x08] = "Backspace", [0x14] = "CapsLock",
        [0x26] = "ArrowUp", [0x28] = "ArrowDown", [0x25] = "ArrowLeft", [0x27] = "ArrowRight",
        [0x2D] = "Insert", [0x2E] = "Delete", [0x24] = "Home", [0x23] = "End", [0x21] = "PageUp", [0x22] = "PageDown",
        [0xC0] = "Backquote", [0xBD] = "Minus", [0xBB] = "Equals", [0xDB] = "BracketLeft", [0xDD] = "BracketRight",
        [0xBA] = "Semicolon", [0xDE] = "Quote", [0xBC] = "Comma", [0xBE] = "Period", [0xBF] = "Slash", [0xDC] = "Backslash",
    };

    public const string NumpadEnter = "NumEnter";

    public static string FromVirtualKey(int vk)
    {
        if (vk is >= 'A' and <= 'Z' or >= '0' and <= '9') return ((char)vk).ToString();
        if (vk is >= 0x70 and <= 0x87) return "F" + (vk - 0x70 + 1);    // VK_F1..VK_F24
        if (vk is >= 0x60 and <= 0x69) return "Num" + (vk - 0x60);      // VK_NUMPAD0..9
        return Named.TryGetValue(vk, out var name) ? name : $"VK_{vk:X2}";
    }
}
