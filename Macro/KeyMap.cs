using System.Collections.Generic;

namespace ADOFAIMacro.Macro
{
    /// <summary>
    /// 统一的按键名称到虚拟键码映射
    /// 避免在 Macro.cs 和 TechniqueSimulator.cs 中重复定义
    /// </summary>
    internal static class KeyMap
    {
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

        /// <summary>
        /// 虚拟键码 → 覆盖层按键显示的短标签（1~4 字符）。
        /// 覆盖主键盘、小键盘、功能键与常用控制键；未知键返回 null（调用方自行兜底）。
        /// </summary>
        public static string GetDisplayText(byte vk)
        {
            // 主键盘数字 / 字母
            if (vk >= 0x30 && vk <= 0x39) return ((char)vk).ToString();
            if (vk >= 0x41 && vk <= 0x5A) return ((char)vk).ToString();

            // 小键盘数字（VK_NUMPAD0..9）——加 N 前缀与主键盘数字区分
            if (vk >= 0x60 && vk <= 0x69) return "N" + (char)('0' + vk - 0x60);

            // 功能键 F1..F24
            if (vk >= 0x70 && vk <= 0x87) return "F" + (vk - 0x6F);

            switch (vk)
            {
                // 小键盘运算符（同样加 N 前缀）
                case 0x6A: return "N*";
                case 0x6B: return "N+";
                case 0x6C: return "N,";
                case 0x6D: return "N-";
                case 0x6E: return "N.";
                case 0x6F: return "N/";
                // 编辑 / 控制
                case 0x08: return "BS";
                case 0x09: return "TAB";
                case 0x0D: return "ENT";
                case 0x13: return "PAU";
                case 0x14: return "CAP";
                case 0x1B: return "ESC";
                case 0x20: return "SP";
                case 0x21: return "PGU";
                case 0x22: return "PGD";
                case 0x23: return "END";
                case 0x24: return "HOM";
                case 0x25: return "←";
                case 0x26: return "↑";
                case 0x27: return "→";
                case 0x28: return "↓";
                case 0x2C: return "PRT";
                case 0x2D: return "INS";
                case 0x2E: return "DEL";
                case 0x2F: return "HELP";
                case 0x5B: return "WIN";
                case 0x5C: return "WIN";
                case 0x5D: return "MENU";
                case 0x90: return "NUM";
                case 0x91: return "SCR";
                // 修饰键（通用 + 左右）
                case 0x10: case 0xA0: case 0xA1: return "SHF";
                case 0x11: case 0xA2: case 0xA3: return "CTL";
                case 0x12: case 0xA4: case 0xA5: return "ALT";
                // 标点（OEM 键）
                case 0xBA: return ";";
                case 0xBB: return "=";
                case 0xBC: return ",";
                case 0xBD: return "-";
                case 0xBE: return ".";
                case 0xBF: return "/";
                case 0xC0: return "`";
                case 0xDB: return "[";
                case 0xDC: return "\\";
                case 0xDD: return "]";
                case 0xDE: return "'";
            }
            return null;
        }
    }
}
