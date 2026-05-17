// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

// Ported from Em68030_WinUI3Cpp/Em68030/IO/KeyMapping.h. Kept as a host-side
// utility because Windows VK_* is host-specific and does not belong in the
// OS-neutral plugin C ABI in external/emfe_plugins/api/.

using System;

namespace emfe;

internal static class KeyMapping
{
    // Linux input-event mouse button codes (input-event-codes.h).
    public const int LINUX_BTN_LEFT   = 0x110;
    public const int LINUX_BTN_RIGHT  = 0x111;
    public const int LINUX_BTN_MIDDLE = 0x112;

    /// <summary>
    /// Maps a Windows virtual-key code (VK_*) to a Linux input event code (KEY_*).
    /// Returns 0 if the key is not mapped.
    /// </summary>
    public static ushort WindowsVkToLinuxKey(int vk) => vk switch
    {
        // Row 0: Escape + Function keys
        0x1B => 1,   // VK_ESCAPE -> KEY_ESC
        0x70 => 59,  // VK_F1     -> KEY_F1
        0x71 => 60,  // VK_F2
        0x72 => 61,  // VK_F3
        0x73 => 62,  // VK_F4
        0x74 => 63,  // VK_F5
        0x75 => 64,  // VK_F6
        0x76 => 65,  // VK_F7
        0x77 => 66,  // VK_F8
        0x78 => 67,  // VK_F9
        0x79 => 68,  // VK_F10
        0x7A => 87,  // VK_F11
        0x7B => 88,  // VK_F12

        // Row 1: Number row ('0'-'9' = 0x30-0x39)
        0x31 => 2,   // '1' -> KEY_1
        0x32 => 3,
        0x33 => 4,
        0x34 => 5,
        0x35 => 6,
        0x36 => 7,
        0x37 => 8,
        0x38 => 9,
        0x39 => 10,
        0x30 => 11,  // '0' -> KEY_0

        // Letter keys ('A'-'Z' = 0x41-0x5A)
        0x41 => 30,  // A -> KEY_A
        0x42 => 48,  // B -> KEY_B
        0x43 => 46,  // C -> KEY_C
        0x44 => 32,  // D -> KEY_D
        0x45 => 18,  // E -> KEY_E
        0x46 => 33,  // F -> KEY_F
        0x47 => 34,  // G -> KEY_G
        0x48 => 35,  // H -> KEY_H
        0x49 => 23,  // I -> KEY_I
        0x4A => 36,  // J -> KEY_J
        0x4B => 37,  // K -> KEY_K
        0x4C => 38,  // L -> KEY_L
        0x4D => 50,  // M -> KEY_M
        0x4E => 49,  // N -> KEY_N
        0x4F => 24,  // O -> KEY_O
        0x50 => 25,  // P -> KEY_P
        0x51 => 16,  // Q -> KEY_Q
        0x52 => 19,  // R -> KEY_R
        0x53 => 31,  // S -> KEY_S
        0x54 => 20,  // T -> KEY_T
        0x55 => 22,  // U -> KEY_U
        0x56 => 47,  // V -> KEY_V
        0x57 => 17,  // W -> KEY_W
        0x58 => 45,  // X -> KEY_X
        0x59 => 21,  // Y -> KEY_Y
        0x5A => 44,  // Z -> KEY_Z

        // Special keys
        0x08 => 14,  // VK_BACK      -> KEY_BACKSPACE
        0x09 => 15,  // VK_TAB       -> KEY_TAB
        0x0D => 28,  // VK_RETURN    -> KEY_ENTER
        0x20 => 57,  // VK_SPACE     -> KEY_SPACE

        // Modifier keys
        0x10 => 42,  // VK_SHIFT     -> KEY_LEFTSHIFT
        0xA0 => 42,  // VK_LSHIFT    -> KEY_LEFTSHIFT
        0xA1 => 54,  // VK_RSHIFT    -> KEY_RIGHTSHIFT
        0x11 => 29,  // VK_CONTROL   -> KEY_LEFTCTRL
        0xA2 => 29,  // VK_LCONTROL  -> KEY_LEFTCTRL
        0xA3 => 97,  // VK_RCONTROL  -> KEY_RIGHTCTRL
        0x12 => 56,  // VK_MENU      -> KEY_LEFTALT
        0xA4 => 56,  // VK_LMENU     -> KEY_LEFTALT
        0xA5 => 100, // VK_RMENU     -> KEY_RIGHTALT
        0x14 => 58,  // VK_CAPITAL   -> KEY_CAPSLOCK

        // Punctuation (US layout)
        0xBD => 12,  // VK_OEM_MINUS  -> KEY_MINUS
        0xBB => 13,  // VK_OEM_PLUS   -> KEY_EQUAL
        0xDB => 26,  // VK_OEM_4 ([)  -> KEY_LEFTBRACE
        0xDD => 27,  // VK_OEM_6 (])  -> KEY_RIGHTBRACE
        0xDC => 43,  // VK_OEM_5 (\)  -> KEY_BACKSLASH
        0xBA => 39,  // VK_OEM_1 (;)  -> KEY_SEMICOLON
        0xDE => 40,  // VK_OEM_7 (')  -> KEY_APOSTROPHE
        0xC0 => 41,  // VK_OEM_3 (`)  -> KEY_GRAVE
        0xBC => 51,  // VK_OEM_COMMA  -> KEY_COMMA
        0xBE => 52,  // VK_OEM_PERIOD -> KEY_DOT
        0xBF => 53,  // VK_OEM_2 (/)  -> KEY_SLASH

        // Navigation
        0x2D => 110, // VK_INSERT   -> KEY_INSERT
        0x2E => 111, // VK_DELETE   -> KEY_DELETE
        0x24 => 102, // VK_HOME     -> KEY_HOME
        0x23 => 107, // VK_END      -> KEY_END
        0x21 => 104, // VK_PRIOR    -> KEY_PAGEUP
        0x22 => 109, // VK_NEXT     -> KEY_PAGEDOWN
        0x26 => 103, // VK_UP       -> KEY_UP
        0x28 => 108, // VK_DOWN     -> KEY_DOWN
        0x25 => 105, // VK_LEFT     -> KEY_LEFT
        0x27 => 106, // VK_RIGHT    -> KEY_RIGHT

        // Numpad
        0x60 => 82,  // VK_NUMPAD0  -> KEY_KP0
        0x61 => 79,  // VK_NUMPAD1
        0x62 => 80,  // VK_NUMPAD2
        0x63 => 81,  // VK_NUMPAD3
        0x64 => 75,  // VK_NUMPAD4
        0x65 => 76,  // VK_NUMPAD5
        0x66 => 77,  // VK_NUMPAD6
        0x67 => 71,  // VK_NUMPAD7
        0x68 => 72,  // VK_NUMPAD8
        0x69 => 73,  // VK_NUMPAD9  -> KEY_KP9
        0x6A => 55,  // VK_MULTIPLY -> KEY_KPASTERISK
        0x6B => 78,  // VK_ADD      -> KEY_KPPLUS
        0x6D => 74,  // VK_SUBTRACT -> KEY_KPMINUS
        0x6E => 83,  // VK_DECIMAL  -> KEY_KPDOT
        0x6F => 98,  // VK_DIVIDE   -> KEY_KPSLASH
        0x90 => 69,  // VK_NUMLOCK  -> KEY_NUMLOCK

        // Misc
        0x91 => 70,  // VK_SCROLL   -> KEY_SCROLLLOCK
        0x13 => 119, // VK_PAUSE    -> KEY_PAUSE
        0x2C => 99,  // VK_SNAPSHOT -> KEY_SYSRQ (PrintScreen)

        _ => 0,
    };

    /// <summary>
    /// Maps an ASCII character to a Linux KEY_* code and whether Shift is needed.
    /// Returns (0, false) if unmapped.  US layout assumed.
    /// </summary>
    public static (ushort keyCode, bool needShift) CharToLinuxKey(char ch)
    {
        // Letters
        if (ch >= 'a' && ch <= 'z')
        {
            ReadOnlySpan<ushort> map = stackalloc ushort[]
            {
                30,48,46,32,18,33,34,35,23,36,37,38,50,49,24,25,16,19,31,20,22,47,17,45,21,44
            };
            return (map[ch - 'a'], false);
        }
        if (ch >= 'A' && ch <= 'Z')
        {
            ReadOnlySpan<ushort> map = stackalloc ushort[]
            {
                30,48,46,32,18,33,34,35,23,36,37,38,50,49,24,25,16,19,31,20,22,47,17,45,21,44
            };
            return (map[ch - 'A'], true);
        }

        switch (ch)
        {
            case '0': return (11, false);
            case '1': return (2,  false);
            case '2': return (3,  false);
            case '3': return (4,  false);
            case '4': return (5,  false);
            case '5': return (6,  false);
            case '6': return (7,  false);
            case '7': return (8,  false);
            case '8': return (9,  false);
            case '9': return (10, false);

            // Unshifted punctuation (US layout)
            case '-':  return (12, false);
            case '=':  return (13, false);
            case '[':  return (26, false);
            case ']':  return (27, false);
            case '\\': return (43, false);
            case ';':  return (39, false);
            case '\'': return (40, false);
            case '`':  return (41, false);
            case ',':  return (51, false);
            case '.':  return (52, false);
            case '/':  return (53, false);

            // Shifted punctuation
            case '!': return (2,  true);
            case '@': return (3,  true);
            case '#': return (4,  true);
            case '$': return (5,  true);
            case '%': return (6,  true);
            case '^': return (7,  true);
            case '&': return (8,  true);
            case '*': return (9,  true);
            case '(': return (10, true);
            case ')': return (11, true);
            case '_': return (12, true);
            case '+': return (13, true);
            case '{': return (26, true);
            case '}': return (27, true);
            case '|': return (43, true);
            case ':': return (39, true);
            case '"': return (40, true);
            case '~': return (41, true);
            case '<': return (51, true);
            case '>': return (52, true);
            case '?': return (53, true);

            // Whitespace / control
            case ' ':  return (57, false);  // KEY_SPACE
            case '\n': return (28, false);  // KEY_ENTER
            case '\t': return (15, false);  // KEY_TAB

            default: return (0, false);
        }
    }
}
