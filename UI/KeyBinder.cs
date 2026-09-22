#nullable enable
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;
using ADOFAIMacro.Macro;

namespace ADOFAIMacro.UI
{
    /// <summary>
    /// 按键绑定器：武装后捕获下一次物理按键，回调其虚拟键码（VK）。
    /// 读取 GetAsyncKeyState 全局键态，覆盖 Unity KeyCode 之外的多媒体/特殊键；
    /// 仅认“新按下”边沿，ESC 取消，鼠标键不参与绑定。
    /// </summary>
    internal static class KeyBinder
    {
        [DllImport("user32.dll")]
        private static extern short GetAsyncKeyState(int vKey);

        private static readonly bool[] PrevDown = new bool[256];
        private static Action<byte>? _onCaptured;
        private static Action? _onCancel;
        private static int _lastFrame = -1;
        private static bool _armed;

        public static bool IsArmed => _armed;

        /// <summary>当前武装的槽位标识（供 UI 显示“正在记录”/点击取消）。</summary>
        public static object? Token { get; private set; }

        public static void Begin(object token, Action<byte> onCaptured, Action? onCancel = null)
        {
            _onCaptured = onCaptured;
            _onCancel = onCancel;
            Token = token;
            _armed = true;
            _lastFrame = Time.frameCount; // 重置，避免“面板曾关闭”的陈旧帧号把本次武装立即取消
            Snapshot(); // 只认新按下边沿，避免瞬间绑到当前已按住的键
        }

        public static void Cancel()
        {
            _armed = false;
            _onCaptured = null;
            _onCancel = null;
            Token = null;
        }

        private static void Snapshot()
        {
            for (int vk = 1; vk < 256; vk++)
                PrevDown[vk] = IsDown(vk);
        }

        private static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        /// <summary>每帧调用一次即可（由设置 OnGUI 触发）。</summary>
        public static void Poll()
        {
            if (!_armed) return;
            // 面板关闭过（帧号出现断层）→ 立即解除武装，避免重开面板时吞掉下一次按键
            if (_lastFrame >= 0 && Time.frameCount - _lastFrame > 5) { Cancel(); return; }
            if (Time.frameCount == _lastFrame) return;
            _lastFrame = Time.frameCount;

            if (IsDown(0x1B) && !PrevDown[0x1B]) // ESC 取消
            {
                var cancel = _onCancel;
                Cancel();
                cancel?.Invoke();
                return;
            }

            for (int vk = 1; vk < 256; vk++)
            {
                if (vk <= 0x06) continue; // 鼠标键不绑定
                bool down = IsDown(vk);
                bool pressed = down && !PrevDown[vk];
                PrevDown[vk] = down;
                if (!pressed) continue;
                var captured = _onCaptured;
                Cancel();
                captured?.Invoke((byte)vk);
                return;
            }
        }

        /// <summary>逗号分隔按键名 → VK 列表（兼容十六进制 0xNN）。</summary>
        public static byte[] Parse(string? text)
        {
            if (string.IsNullOrWhiteSpace(text)) return Array.Empty<byte>();
            var list = new List<byte>();
            foreach (var part in text!.Split(','))
            {
                var name = part.Trim();
                if (name.Length == 0) continue;
                name = name.ToUpperInvariant();
                if (name.Length == 1 && name[0] >= 'A' && name[0] <= 'Z') { list.Add((byte)name[0]); continue; }
                if (name.Length == 1 && name[0] >= '0' && name[0] <= '9') { list.Add((byte)name[0]); continue; }
                if (KeyMap.TryGetKeyCode(name, out byte code)) list.Add(code);
            }
            return list.ToArray();
        }

        /// <summary>VK 列表 → 逗号分隔按键名。</summary>
        public static string Join(IEnumerable<byte> keys)
        {
            var sb = new StringBuilder();
            foreach (var k in keys)
            {
                if (sb.Length > 0) sb.Append(',');
                sb.Append(KeyMap.GetName(k));
            }
            return sb.ToString();
        }

        /// <summary>绑定槽位显示文本（短标签）。</summary>
        public static string Label(byte vk) => KeyMap.GetDisplayText(vk) ?? KeyMap.GetName(vk);
    }
}
