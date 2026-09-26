using System.Collections.Generic;

#nullable enable

namespace ADOFAIMacro.Macro
{
    /// <summary>
    /// 统一的按键名称到虚拟键码映射
    /// 避免在 Macro.cs 和 TechniqueSimulator.cs 中重复定义
    /// </summary>
    internal static class KeyMap
    {
        /// <summary>
        /// 解析单个按键书写项。支持：单字符字母/数字、键名表、十六进制虚拟键码（0x41）。
        ///
        /// ⚠️ README 的「按键书写格式（通用）」声称支持十六进制，但旧实现里只有
        /// 按键过滤列表与死亡按键走了十六进制解析，按键序列(MacroKeys)与手法模拟的
        /// 左右手按键（含分段覆盖）会把 "0x44" 静默丢弃 —— 列表若因此变空还会回退成 J。
        /// 现在三处共用这一个解析入口。
        /// </summary>
        public static bool TryParse(string? token, out byte code)
        {
            code = 0;
            if (string.IsNullOrWhiteSpace(token)) return false;

            string name = token!.Trim().ToUpperInvariant();
            if (name.Length == 0) return false;

            // 单字符：字母/数字本身就是虚拟键码（"A"→0x41，"1"→0x31）
            if (name.Length == 1)
            {
                char c = name[0];
                if (c is >= 'A' and <= 'Z') { code = (byte)c; return true; }
                if (c is >= '0' and <= '9') { code = (byte)c; return true; }
            }

            // 十六进制虚拟键码（与 Patches / Settings 中的写法保持一致）
            if (name.StartsWith("0X") && name.Length > 2 &&
                byte.TryParse(name.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out byte hex))
            {
                code = hex;
                return true;
            }

            if (KeyNameToCode.TryGetValue(name, out byte vk))
            {
                code = vk;
                return true;
            }

            return false;
        }

        public static readonly Dictionary<string, byte> KeyNameToCode = new()
        {
            // 字母
            ["A"] = 0x41, ["B"] = 0x42, ["C"] = 0x43, ["D"] = 0x44, ["E"] = 0x45,
            ["F"] = 0x46, ["G"] = 0x47, ["H"] = 0x48, ["I"] = 0x49, ["J"] = 0x4A,
            ["K"] = 0x4B, ["L"] = 0x4C, ["M"] = 0x4D, ["N"] = 0x4E, ["O"] = 0x4F,
            ["P"] = 0x50, ["Q"] = 0x51, ["R"] = 0x52, ["S"] = 0x53, ["T"] = 0x54,
            ["U"] = 0x55, ["V"] = 0x56, ["W"] = 0x57, ["X"] = 0x58, ["Y"] = 0x59,
            ["Z"] = 0x5A,

            // 数字
            ["0"] = 0x30, ["1"] = 0x31, ["2"] = 0x32, ["3"] = 0x33, ["4"] = 0x34,
            ["5"] = 0x35, ["6"] = 0x36, ["7"] = 0x37, ["8"] = 0x38, ["9"] = 0x39,

            // 基础符号键
            ["`"] = 0xC0, ["-"] = 0xBD, ["="] = 0xBB, ["["] = 0xDB, ["]"] = 0xDD,
            ["\\"] = 0xDC, [";"] = 0xBA, ["'"] = 0xDE, [","] = 0xBC, ["."] = 0xBE,
            ["/"] = 0xBF, [" "] = 0x20,

            // 功能键
            ["F1"] = 0x70, ["F2"] = 0x71, ["F3"] = 0x72, ["F4"] = 0x73, ["F5"] = 0x74,
            ["F6"] = 0x75, ["F7"] = 0x76, ["F8"] = 0x77, ["F9"] = 0x78, ["F10"] = 0x79,
            ["F11"] = 0x7A, ["F12"] = 0x7B,

            // 控制键
            ["CTRL"] = 0x11, ["LCTRL"] = 0xA2, ["RCTRL"] = 0xA3,
            ["SHIFT"] = 0x10, ["LSHIFT"] = 0xA0, ["RSHIFT"] = 0xA1,
            ["ALT"] = 0x12, ["LALT"] = 0xA4, ["RALT"] = 0xA5,
            ["WIN"] = 0x5B, ["LWIN"] = 0x5B, ["RWIN"] = 0x5C, ["MENU"] = 0x5D,

            // 导航键
            ["LEFT"] = 0x25, ["UP"] = 0x26, ["RIGHT"] = 0x27, ["DOWN"] = 0x28,
            ["HOME"] = 0x24, ["END"] = 0x23, ["PAGEUP"] = 0x21, ["PAGEDOWN"] = 0x22,
            ["INSERT"] = 0x2D, ["DELETE"] = 0x2E,

            // 编辑键
            ["BACKSPACE"] = 0x08, ["TAB"] = 0x09, ["ENTER"] = 0x0D, ["RETURN"] = 0x0D,
            ["ESC"] = 0x1B, ["ESCAPE"] = 0x1B, ["SPACE"] = 0x20, ["SPACEBAR"] = 0x20,

            // 小键盘
            ["NUMPAD0"] = 0x60, ["NUMPAD1"] = 0x61, ["NUMPAD2"] = 0x62, ["NUMPAD3"] = 0x63,
            ["NUMPAD4"] = 0x64, ["NUMPAD5"] = 0x65, ["NUMPAD6"] = 0x66, ["NUMPAD7"] = 0x67,
            ["NUMPAD8"] = 0x68, ["NUMPAD9"] = 0x69, ["NUMPADMULTIPLY"] = 0x6A,
            ["NUMPADADD"] = 0x6B, ["NUMPADSEPARATOR"] = 0x6C, ["NUMPADSUBTRACT"] = 0x6D,
            ["NUMPADDECIMAL"] = 0x6E, ["NUMPADDIVIDE"] = 0x6F, ["NUMPADENTER"] = 0x0D,
            ["NUMLOCK"] = 0x90,

            // 其他
            ["PRINTSCREEN"] = 0x2C, ["SCROLLLOCK"] = 0x91, ["PAUSE"] = 0x13, ["BREAK"] = 0x13,
            ["CAPSLOCK"] = 0x14, ["HELP"] = 0x2F,

            // 多媒体键
            ["VOLUME_MUTE"] = 0xAD, ["VOLUME_DOWN"] = 0xAE, ["VOLUME_UP"] = 0xAF,
            ["MEDIA_NEXT_TRACK"] = 0xB0, ["MEDIA_PREV_TRACK"] = 0xB1, ["MEDIA_STOP"] = 0xB2,
            ["MEDIA_PLAY_PAUSE"] = 0xB3, ["BROWSER_HOME"] = 0xAC, ["BROWSER_SEARCH"] = 0xAA,
            ["BROWSER_FAVORITES"] = 0xAB, ["BROWSER_REFRESH"] = 0xA8, ["BROWSER_STOP"] = 0xA9,
            ["BROWSER_FORWARD"] = 0xA7, ["BROWSER_BACK"] = 0xA6,
            ["LAUNCH_MAIL"] = 0xB4, ["LAUNCH_MEDIA_SELECT"] = 0xB5, ["LAUNCH_APP1"] = 0xB6,
            ["LAUNCH_APP2"] = 0xB7,
        };
    }
}
