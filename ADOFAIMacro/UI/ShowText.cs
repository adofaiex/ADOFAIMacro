using UnityEngine;
using System;
using System.Collections.Generic;
using ADOFAIMacro.Macro;
using ADOFAIMacro.Localization;

namespace ADOFAIMacro
{
    public class ShowText : MonoBehaviour
    {
        private bool _showMacroText;
        private Rect _rect;
        private GUIStyle _textStyle;
        private GUIStyle _shadowStyle;

        public void Awake()
        {
            _rect = new Rect(0, 0, 200, 20);
            _showMacroText = Main.Settings.Macro;

            // 初始化文本样式
            _textStyle = new GUIStyle();
            _textStyle.richText = false; // 不使用 richText，因为我们在样式里设置颜色
            _textStyle.fontSize = 12;
            _textStyle.normal.textColor = Color.green; // 直接在这里设置绿色
            _textStyle.alignment = TextAnchor.MiddleLeft;

            // 初始化阴影样式
            _shadowStyle = new GUIStyle(_textStyle);
            _shadowStyle.normal.textColor = new Color(0, 0, 0, 0.5f);
        }

        public void OnEnable()
        {
            if (Main.Settings != null)
                Main.Settings.OnMacroChanged += OnMacroChanged;
        }

        public void OnDisable()
        {
            if (Main.Settings != null)
                Main.Settings.OnMacroChanged -= OnMacroChanged;
        }

        private void OnMacroChanged(bool newValue)
        {
            _showMacroText = newValue;
        }

        public void Update()
        {
            // 每帧更新 AudioDSPManager
            DSPTimeSimulater.Update();
        }
        public void OnGUI()
        {
            if (_showMacroText)
            {
                // 当前驱动路径角标：HIT=直接判定 / VIRT=虚拟异步键盘 / NT=NT注入 / SI=SendInput
                string tag = !Main.Settings.SimulateKeyPress ? "HIT"
                    : Macro.VirtualAsyncInput.Active ? "VIRT"
                    : Main.Settings.SkyHookMode ? "NT/SI"
                    : "SI";
                string text = $"{LocalizationManager.Get("macro.enabled_text")} [{tag}]";

                // 绘制阴影
                GUI.Label(new Rect(_rect.x + 2, _rect.y + 2, _rect.width, _rect.height),
                    text, _shadowStyle);

                // 绘制原文本
                GUI.Label(_rect, text, _textStyle);

                // 虚拟异步键盘的内置按键显示（OS 层看不见，由模组自己渲染）
                if (Main.Settings.SimulateKeyPress && Main.Settings.UseVirtualAsyncInput)
                    DrawVirtualKeys();
            }
        }

        private static readonly Dictionary<byte, int> _downSnapshot = new(8);
        private static readonly List<(byte vk, int time)> _upSnapshot = new(8);
        private static GUIStyle _keyDisplayStyle;

        private void DrawVirtualKeys()
        {
            _downSnapshot.Clear();
            _upSnapshot.Clear();
            int nowMs = Environment.TickCount;
            lock (Macro.VirtualAsyncInput.DisplayLock)
            {
                foreach (var kv in Macro.VirtualAsyncInput.DisplayDown)
                    _downSnapshot[kv.Key] = kv.Value;
                for (int i = Macro.VirtualAsyncInput.DisplayUps.Count - 1; i >= 0; i--)
                {
                    var up = Macro.VirtualAsyncInput.DisplayUps[i];
                    if (nowMs - up.time <= 300) _upSnapshot.Add(up);
                    else Macro.VirtualAsyncInput.DisplayUps.RemoveAt(i);
                }
            }

            if (_keyDisplayStyle == null)
            {
                _keyDisplayStyle = new GUIStyle
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontSize = 14,
                    fontStyle = FontStyle.Bold,
                    normal = { textColor = Color.black }
                };
            }
            var whiteTex = UIUtils.GetCachedRoundedTex(64, 64, 6, Color.white);

            float x = 4;
            const float y = 26;
            const float size = 26;

            foreach (var kv in _downSnapshot)
            {
                byte vk = kv.Key;
                char c = vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? (char)vk : '?';
                // 按住：实心亮框
                GUI.color = new Color(0.2f, 0.9f, 0.4f, 0.9f);
                GUI.DrawTexture(new Rect(x, y, size, size), whiteTex);
                GUI.color = Color.white;
                GUI.Label(new Rect(x, y, size, size), c.ToString(), _keyDisplayStyle);
                x += size + 4;
            }

            // 淡出的松开键
            foreach (var up in _upSnapshot)
            {
                byte vk = up.vk;
                char c = vk is >= 0x30 and <= 0x39 or >= 0x41 and <= 0x5A ? (char)vk : '?';
                float fade = 1f - (nowMs - up.time) / 300f;
                GUI.color = new Color(0.2f, 0.9f, 0.4f, 0.45f * fade);
                GUI.DrawTexture(new Rect(x, y, size, size), whiteTex);
                GUI.color = new Color(0f, 0f, 0f, fade);
                GUI.Label(new Rect(x, y, size, size), c.ToString(), _keyDisplayStyle);
                GUI.color = Color.white;
                x += size + 4;
            }
        }

        public void OnDestroy()
        {
            // 清理样式
            if (_textStyle != null)
            {
                _textStyle = null;
            }
            if (_shadowStyle != null)
            {
                _shadowStyle = null;
            }
        }
    }
}