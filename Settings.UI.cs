#nullable disable
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using UnityModManagerNet;
using ADOFAIMacro.Localization;
using ADOFAIMacro.Macro;
using ADOFAIMacro.UI;
using static ADOFAIMacro.UI.IridiumLayout;

namespace ADOFAIMacro
{
    /// <summary>
    /// 设置界面（基于移植的 Iridium 声明式 IMGUI 布局重写）。
    /// 数据字段/持久化仍在 Settings.cs，本文件只负责绘制与交互。
    /// </summary>
    public partial class Settings
    {
        // ── UI 状态 ──────────────────────────────────
        private int _uiTab;
        private Vector2 _updateLogScroll;
        private readonly HashSet<string> _rawKeyExpanded = new();
        private bool _ordersLeftExpanded, _ordersRightExpanded;

        private static readonly string[] _modeNameKeys =
            ["key_mode.auto", "key_mode.ntinject", "key_mode.ntsendinput", "key_mode.sendinput"];
        private static readonly string[] _modeDescKeys =
            ["key_mode_desc.auto", "key_mode_desc.ntinject", "key_mode_desc.ntsendinput", "key_mode_desc.sendinput"];
        private static readonly string[] _filterQuickSets =
        [
            "F1,F2,F3,F4", "F5,F6,F7,F8", "F9,F10,F11,F12",
            "A,S,D,F", "J,K,L", "1,2,3,4",
            "SPACE,ENTER,ESC", "UP,DOWN,LEFT,RIGHT", "CTRL,ALT,SHIFT",
        ];
        private static readonly string[] _deathKeyQuickSet = ["R", "SPACE", "ENTER", "F2", "ESC"];

        // ─────────────────────────────────────────────
        //  主入口
        // ─────────────────────────────────────────────
        public void OnGUI(UnityModManager.ModEntry modEntry)
        {
            KeyBinder.Poll();
            EnsureTexturesAlive(); // 场景切换后 Unity 可能销毁贴图，需重建

            var tabs = BuildTabKeys();
            if (_uiTab >= tabs.Count) _uiTab = 0;
            var names = new string[tabs.Count];
            for (int i = 0; i < tabs.Count; i++) names[i] = LocalizationManager.Get(tabs[i]);

            Render(
                VBox(ContainerStyle.Padding, null, WidthMax,
                    HBox(ContainerStyle.None, null, WidthMax,
                        Selector(_uiTab, names, i => _uiTab = i, ButtonStyle.Element, ButtonStyle.Primary, WidthMax)),
                    Space(6),
                    VBox(ContainerStyle.Background, null, Bag(BuildTab(tabs[_uiTab])))
                )
            );
        }

        private List<string> BuildTabKeys()
        {
            var list = new List<string> { "tab.language", "tab.macro" };
            if (Macro)
            {
                list.Add("tab.key_settings");
                list.Add("tab.key_filter");
                list.Add("tab.offset_settings");
                list.Add("tab.other_settings");
                if (SimulateKeyPress) list.Add("tab.technique_simulation");
            }
            list.Add("tab.update_log");
            list.Add("tab.author");
            if (IsBeta) list.Add("tab.beta");
            return list;
        }

        private Element[] BuildTab(string key)
        {
            switch (key)
            {
                case "tab.language": return BuildLanguageTab();
                case "tab.macro": return BuildMacroTab();
                case "tab.key_settings": return BuildKeySettingsTab();
                case "tab.key_filter": return BuildKeyFilterTab();
                case "tab.offset_settings": return BuildOffsetTab();
                case "tab.other_settings": return BuildOtherTab();
                case "tab.technique_simulation": return BuildTechniqueTab();
                case "tab.update_log": return BuildUpdateLogTab();
                case "tab.author": return BuildAuthorTab();
                case "tab.beta": return BuildBetaTab();
                default: return Array.Empty<Element>();
            }
        }

        // ─────────────────────────────────────────────
        //  通用行构件
        // ─────────────────────────────────────────────
        // 标签块：标题（撑满一行）+ 可选描述（另起一行）。描述另起一行可避免窄面板下与控件重叠。
        private static Element Labeled(string labelKey, string descKey = null)
        {
            var items = new List<object>
            {
                Text(LocalizationManager.Get(labelKey), TextStyle.Normal, WidthMax)
            };
            if (!string.IsNullOrEmpty(descKey))
                items.Add(Text(LocalizationManager.Get(descKey), TextStyle.Secondary, WidthMax));
            return VBox(ContainerStyle.None, null, Bag(items));
        }

        // 开关行：标题/描述占上方，开关单独成行靠右。比「标签+Fill+开关」更耐窄窗口。
        private Element SwitchRow(string labelKey, bool value, Action<bool> onChange, string descKey = null)
        {
            var items = new List<object>
            {
                Text(LocalizationManager.Get(labelKey), TextStyle.Normal, WidthMax)
            };
            if (!string.IsNullOrEmpty(descKey))
                items.Add(Text(LocalizationManager.Get(descKey), TextStyle.Secondary, WidthMax));
            items.Add(HBox(ContainerStyle.None, null, WidthMax, Fill(), Switch(value, onChange, WidthMin)));
            return VBox(ContainerStyle.None, null, Bag(items));
        }

        private Element SliderRow(string labelKey, float value, float min, float max, int precision,
            Action<float> onChange, string descKey = null)
        {
            var items = new List<object>
            {
                Text(LocalizationManager.Get(labelKey), TextStyle.Normal, WidthMax)
            };
            if (!string.IsNullOrEmpty(descKey))
                items.Add(Text(LocalizationManager.Get(descKey), TextStyle.Secondary, WidthMax));
            items.Add(HBox(ContainerStyle.None, null, WidthMax,
                Slider(value, min, max, onChange, WidthMax, Height(20)), // 滑块不约束高度会撑满竖向空间
                Space(6),
                StructField(value, FloatFormat(precision, min, max), v => onChange(v), Width(60), Height(24))));
            return VBox(ContainerStyle.None, null, Bag(items));
        }

        private static Element Subtitle(string key) => Text(LocalizationManager.Get(key), TextStyle.Subtitle, WidthMax);

        private static Element Divider() => VBox(ContainerStyle.None, null, Space(4), Separator(WidthMax), Space(4));

        // 把「元素 + 布局选项」合成单个 params 数组：否则 Element[] 与 GUILayoutOption 混传时，
        // 编译器走展开形式会把整个数组当成一个 object 丢掉（曾导致卡片/内容全空）。
        private static object[] Bag(object[] items)
        {
            var list = new List<object>(items.Length + 1) { WidthMax };
            list.AddRange(items);
            return list.ToArray();
        }

        private static object[] Bag(List<object> items) => Bag(items.ToArray());

        private Element Card(params object[] children)
            => VBox(ContainerStyle.Background, null, Bag(children));

        /// <summary>逗号分隔按压时长 → 长度 keyCount 的数组（缺省 0.8）。</summary>
        private static double[] ParseTechPressTimes(string input, int keyCount)
        {
            keyCount = Math.Max(1, keyCount);
            var result = new double[keyCount];
            for (int i = 0; i < keyCount; i++) result[i] = 0.8;
            if (string.IsNullOrWhiteSpace(input)) return result;
            var parts = input.Split(',');
            for (int i = 0; i < Math.Min(parts.Length, keyCount); i++)
                if (double.TryParse(parts[i].Trim(), NumberStyles.Any, CultureInfo.InvariantCulture, out double v))
                    result[i] = v;
            return result;
        }

        /// <summary>
        /// 按键列表行（方案A）：点击任意按键槽即可快速重绑该位置；武装时显示“正在记录…”，
        /// 再点一次取消。× 删除，+ 追加，“编辑”切换文本编辑。
        /// </summary>
        private Element KeyListRow(string controlId, string labelKey, string current, Action<string> onChange,
            string descKey = null, int maxSlots = 0)
        {
            const int perRow = 7;       // 每行最多槽位（留出尾按钮的空间），超出自动换行
            const float labelW = 84f;   // 标签缩进（缩小左侧空白）
            const float h = 26f;

            var keys = KeyBinder.Parse(current).ToList();

            // 每个按键槽 = 键按钮 + 删除按钮（同高，避免 × 上下错位）
            var slots = new List<object[]>();
            for (int i = 0; i < keys.Count; i++)
            {
                int idx = i;
                object token = controlId + "#" + idx;
                bool armed = KeyBinder.IsArmed && Equals(KeyBinder.Token, token);
                string label = armed ? LocalizationManager.Get("key_settings.recording") : KeyBinder.Label(keys[i]);

                var chip = Button(label, armed ? ButtonStyle.Primary : ButtonStyle.Element, () =>
                {
                    if (armed) { KeyBinder.Cancel(); return; }
                    KeyBinder.Begin(token, vk => { if (idx < keys.Count) { keys[idx] = vk; onChange(KeyBinder.Join(keys)); } });
                }, Height(h), Width(armed ? 104 : 42));

                var del = Button("×", ButtonStyle.Element, () =>
                {
                    if (idx < keys.Count) { keys.RemoveAt(idx); onChange(KeyBinder.Join(keys)); }
                }, Height(h), Width(24));

                slots.Add([chip, del]);
            }

            var tail = new List<object>();
            if (maxSlots == 0 || keys.Count < maxSlots)
            {
                object token = controlId + "#add";
                bool armed = KeyBinder.IsArmed && Equals(KeyBinder.Token, token);
                tail.Add(Button(armed ? LocalizationManager.Get("key_settings.recording") : "+",
                    armed ? ButtonStyle.Primary : ButtonStyle.Element, () =>
                    {
                        if (armed) { KeyBinder.Cancel(); return; }
                        KeyBinder.Begin(token, vk => { keys.Add(vk); onChange(KeyBinder.Join(keys)); });
                    }, Height(h), Width(armed ? 104 : 26)));
            }
            bool rawOpen = _rawKeyExpanded.Contains(controlId);
            tail.Add(Button(LocalizationManager.Get("key_settings.text_edit"),
                rawOpen ? ButtonStyle.Primary : ButtonStyle.Element,
                () => { if (rawOpen) _rawKeyExpanded.Remove(controlId); else _rawKeyExpanded.Add(controlId); },
                Height(h), Width(44)));

            // 分行：首行带标签，其余行按标签宽度缩进；尾按钮放最后一行，行满则另起一行
            var grid = new List<object>();
            int start = 0;
            while (true)
            {
                var row = new List<object>
                {
                    start == 0
                        ? Text(LocalizationManager.Get(labelKey), TextStyle.Normal, Width(labelW), Height(h))
                        : Space(labelW)
                };
                int end = Math.Min(start + perRow, slots.Count);
                for (int k = start; k < end; k++)
                {
                    row.Add(slots[k][0]);
                    row.Add(slots[k][1]);
                    row.Add(Space(4));
                }
                int count = end - start;

                if (end >= slots.Count && count < perRow)
                {
                    if (slots.Count == 0) row.Add(Text("—", TextStyle.Secondary, WidthMin));
                    row.AddRange(tail);
                    row.Add(Fill());
                    grid.Add(HBox(ContainerStyle.None, null, row.ToArray()));
                    break;
                }

                row.Add(Fill());
                grid.Add(HBox(ContainerStyle.None, null, row.ToArray()));

                if (end >= slots.Count) // 末行已满：尾按钮另起一行
                {
                    var tailRow = new List<object> { Space(labelW) };
                    tailRow.AddRange(tail);
                    tailRow.Add(Fill());
                    grid.Add(HBox(ContainerStyle.None, null, tailRow.ToArray()));
                    break;
                }
                start = end;
            }

            var top = VBox(ContainerStyle.None, null, Bag(grid));
            if (!rawOpen) return top;

            return VBox(ContainerStyle.None, null, Bag(new List<object>
            {
                top,
                TextField(current ?? string.Empty, onChange, null, WidthMax)
            }));
        }

        // ─────────────────────────────────────────────
        //  语言
        // ─────────────────────────────────────────────
        private Element[] BuildLanguageTab()
        {
            int sel = LocalizationManager.IsChinese ? 0 : 1;
            string[] langs = { LocalizationManager.Get("language.chinese"), LocalizationManager.Get("language.english") };
            return
            [
                Card(
                    HBox(ContainerStyle.None, null, WidthMax,
                        Text(LocalizationManager.Get("language.display_language"), TextStyle.Normal, WidthMax),
                        Selector(sel, langs, i => { UseChinese = i == 0; }, ButtonStyle.Element, ButtonStyle.Primary, Width(200))))
            ];
        }

        // ─────────────────────────────────────────────
        //  宏
        // ─────────────────────────────────────────────
        private Element[] BuildMacroTab()
        {
            return
            [
                Card(SwitchRow("macro.enable_macro", Macro, v =>
                {
                    Macro = v;
                    ADOFAIMacro.Macro.Macro.RequestRestart();
                }))
            ];
        }

        // ─────────────────────────────────────────────
        //  延迟设置
        // ─────────────────────────────────────────────
        private Element[] BuildOffsetTab()
        {
            return
            [
                Card(
                    SwitchRow("offset.allow_ctrl_adjust", EnableKeyAdjust, v => EnableKeyAdjust = v),
                    SliderRow("offset.adjust_step", AdjustStep, 0.1f, 10f, 2, v => AdjustStep = v),
                    SliderRow("offset.offset_ms", TimeOffset, -100f, 100f, 2, v => TimeOffset = v),
                    SwitchRow("offset.allow_arrow_adjust", EnableArrowTimeAdjust, v => EnableArrowTimeAdjust = v),
                    SwitchRow("offset.enable_high_precision", HighPrecisionTime, v => HighPrecisionTime = v),
                    SwitchRow("offset.enable_high_precision_async", HighPrecisionAsync, v => HighPrecisionAsync = v),
                    SwitchRow("offset.auto_calibrate", AutoCalibrateJudgement, v => AutoCalibrateJudgement = v),
                    SwitchRow("other.suppress_gc", SuppressGcPauses, v => SuppressGcPauses = v))
            ];
        }

        // ─────────────────────────────────────────────
        //  按键设置
        // ─────────────────────────────────────────────
        private Element[] BuildKeySettingsTab()
        {
            var c = new List<object>();

            if (!EnableTechniqueSimulation)
                c.Add(KeyListRow("MacroKeys", "key_settings.keys_comma_separated", MacroKeys, v => MacroKeys = v));

            c.Add(SwitchRow("key_settings.key_simulation", SimulateKeyPress, v =>
            {
                SimulateKeyPress = v;
                ADOFAIMacro.Macro.Macro.RequestRestart();
            }));

            if (SimulateKeyPress)
            {
                c.Add(SwitchRow("key_settings.use_advanced_input", SkyHookMode, v => SkyHookMode = v));

                if (SkyHookMode)
                {
                    c.Add(SwitchRow("key_settings.virtual_async_input", UseVirtualAsyncInput, v => UseVirtualAsyncInput = v));
                    if (UseVirtualAsyncInput)
                        c.Add(SwitchRow("key_settings.mirror_virtual_keys", MirrorVirtualKeys, v => MirrorVirtualKeys = v));

                    c.Add(Divider());

                    var actualRow = new List<object>
                    {
                        Text(LocalizationManager.Get("key_settings.win_api_input_mode"), TextStyle.Normal, WidthMax)
                    };
                    if (ADOFAIMacro.Macro.InputSystem.IsInitialized)
                    {
                        var actual = ADOFAIMacro.Macro.InputSystem.GetInputMode();
                        int ai = (int)actual;
                        actualRow.Add(Text(LocalizationManager.Get("key_settings.mode_indicator",
                            (ai >= 0 && ai < _modeNameKeys.Length) ? LocalizationManager.Get(_modeNameKeys[ai]) : actual.ToString()),
                            TextStyle.Secondary, WidthMin));
                    }
                    c.Add(HBox(ContainerStyle.None, null, Bag(actualRow)));

                    bool hasInject = !ADOFAIMacro.Macro.InputSystem.IsInitialized || ADOFAIMacro.Macro.InputSystem.IsModeAvailable(ADOFAIMacro.Macro.InputMode.NtUserInjectKeyboard);
                    bool hasNtSend = !ADOFAIMacro.Macro.InputSystem.IsInitialized || ADOFAIMacro.Macro.InputSystem.IsModeAvailable(ADOFAIMacro.Macro.InputMode.NtUserSendInput);
                    var modeRow = new List<object>();
                    for (int i = 0; i < 4; i++)
                    {
                        int m = i;
                        bool available = m switch { 1 => hasInject, 2 => hasNtSend, _ => true };
                        string lbl = LocalizationManager.Get(_modeNameKeys[m]) + (available ? "" : LocalizationManager.Get("key_mode_not_supported"));
                        modeRow.Add(Enabled(() => available,
                            Button(lbl, m == InputMode ? ButtonStyle.Primary : ButtonStyle.Element,
                                () => { if (available && InputMode != m) InputMode = m; }, WidthMax)));
                    }
                    c.Add(HBox(ContainerStyle.None, null, Bag(modeRow)));

                    string descKey = (InputMode >= 0 && InputMode < _modeDescKeys.Length) ? _modeDescKeys[InputMode] : "";
                    c.Add(Text(LocalizationManager.Get(descKey), TextStyle.Secondary, WidthMax));
                }
            }

            c.Add(Text(LocalizationManager.Get("key_settings.bind_hint"), TextStyle.Secondary, WidthMax));
            return [Card(c.ToArray())];
        }

        // ─────────────────────────────────────────────
        //  按键过滤
        // ─────────────────────────────────────────────
        private Element[] BuildKeyFilterTab()
        {
            var c = new List<object> { SwitchRow("filter.enable_filter", EnableKeyFilter, v => EnableKeyFilter = v) };

            if (EnableKeyFilter)
            {
                c.Add(Divider());

                string[] modes = { LocalizationManager.Get("filter.blacklist_mode"), LocalizationManager.Get("filter.whitelist_mode") };
                c.Add(HBox(ContainerStyle.None, null, WidthMax,
                    Text(LocalizationManager.Get("filter.filter_mode"), TextStyle.Normal, WidthMax),
                    Selector(FilterMode, modes, i => FilterMode = i, ButtonStyle.Element, ButtonStyle.Primary, Width(200))));

                c.Add(Text(LocalizationManager.Get(FilterMode == 0 ? "filter.blacklist_desc" : "filter.whitelist_desc"),
                    TextStyle.Secondary, WidthMax));

                c.Add(KeyListRow("FilterKeys", "filter.keys_comma_separated", FilteredKeys, v => FilteredKeys = v));
                if (SkyHookMode)
                    c.Add(KeyListRow("FilterAsyncKeys", "filter.async_keys_comma_separated", FilteredAsyncKeys, v => FilteredAsyncKeys = v));
                else
                    c.Add(Text(LocalizationManager.Get("filter.requires_skyhook"), TextStyle.Secondary, WidthMax));

                c.Add(Text(LocalizationManager.Get("filter.common_keys"), TextStyle.Normal, WidthMax));
                var quickRow = new List<object>();
                for (int i = 0; i < _filterQuickSets.Length; i++)
                {
                    string k = _filterQuickSets[i];
                    quickRow.Add(Button(k, ButtonStyle.Element, () =>
                    {
                        FilteredKeys = k;
                        if (SkyHookMode) FilteredAsyncKeys = k;
                    }, WidthMax));
                    if (i % 3 == 2 && i != _filterQuickSets.Length - 1) { c.Add(HBox(ContainerStyle.None, null, Bag(quickRow))); quickRow.Clear(); }
                }
                if (quickRow.Count > 0) c.Add(HBox(ContainerStyle.None, null, Bag(quickRow)));

                c.Add(Text(LocalizationManager.Get("filter.tip"), TextStyle.Secondary, WidthMax));
                c.Add(Text(LocalizationManager.Get("key_settings.bind_hint"), TextStyle.Secondary, WidthMax));
            }

            return [Card(c.ToArray())];
        }

        // ─────────────────────────────────────────────
        //  其他选项
        // ─────────────────────────────────────────────
        private Element[] BuildOtherTab()
        {
            var c = new List<object> { SwitchRow("other.enable_death_key", EnableDeathKey, v => EnableDeathKey = v) };

            if (EnableDeathKey)
            {
                c.Add(Divider());
                c.Add(SliderRow("other.delay_seconds", DeathKeyDelay, 0.1f, 30f, 1, v => DeathKeyDelay = v));
                c.Add(KeyListRow("DeathKey", "other.key", DeathKeyInput, v => DeathKeyInput = v, maxSlots: 1));
                var quick = new List<object>();
                foreach (var k in _deathKeyQuickSet)
                {
                    string kk = k;
                    quick.Add(Button(kk, ButtonStyle.Element, () => DeathKeyInput = kk, WidthMax));
                }
                c.Add(HBox(ContainerStyle.None, null, Bag(quick)));
                c.Add(Text(LocalizationManager.Get("other.tip_enter_key"), TextStyle.Secondary, WidthMax));
            }

            c.Add(SwitchRow("other.switch_nofaill", ChangeNoFaillInPlay, v => ChangeNoFaillInPlay = v));
            c.Add(SwitchRow("other.switch_judgement", ChangeJudementInPlay, v => ChangeJudementInPlay = v));
            c.Add(SwitchRow("other.lock_level_editor", LockLevelEditor, v =>
            {
                LockLevelEditor = v;
                if (ADOBase.sceneName == GCNS.sceneEditor) ADOFAIMacro.Macro.Macro.RequestRestart();
            }));
            c.Add(SwitchRow("other.block_input_unfocused", BlockInputWhenUnfocused, v => BlockInputWhenUnfocused = v));

            return [Card(c.ToArray())];
        }

        // ─────────────────────────────────────────────
        //  更新日志 / 作者 / 测试版
        // ─────────────────────────────────────────────
        private Element[] BuildUpdateLogTab()
        {
            string ver = Main.Mod.Info.Version.Replace('\n', ' ').Replace('\r', ' ');
            return
            [
                Card(
                    Text(LocalizationManager.Get("update_log.title"), TextStyle.Subtitle, WidthMax),
                    ScrollView(_updateLogScroll, p => _updateLogScroll = p, Height(150),
                        Text(LocalizationManager.Get("update_log.content", ver), TextStyle.Normal, WidthMax)))
            ];
        }

        private Element[] BuildAuthorTab()
        {
            string ver = Main.Mod.Info.Version.Replace('\n', ' ').Replace('\r', ' ');
            string emailKey = LocalizationManager.IsChinese ? "author.email_chinese" : "author.email_english";
            return
            [
                Card(
                    HBox(ContainerStyle.None, null, WidthMax,
                        Text($"👤 {Main.Mod.Info.Author}", TextStyle.Normal, WidthMin),
                        Fill(),
                        Text($"📦 {ver}", TextStyle.Normal, WidthMin),
                        Fill(),
                        Text($"📧 {LocalizationManager.Get(emailKey)}", TextStyle.Normal, WidthMin)),
                    Divider(),
                    Text(string.Format(LocalizationManager.Get("author.thanks"), Main.Mod.Info.Id), TextStyle.Secondary, WidthMax))
            ];
        }

        private Element[] BuildBetaTab()
        {
            return
            [
                Card(Text(string.Format(LocalizationManager.Get("beta.warning_format"), BetaVersion), TextStyle.Subtitle, WidthMax),
                     Text(LocalizationManager.Get("beta.feedback_message"), TextStyle.Secondary, WidthMax))
            ];
        }

        // ─────────────────────────────────────────────
        //  手法模拟
        // ─────────────────────────────────────────────
        private Element[] BuildTechniqueTab()
        {
            var c = new List<object>
            {
                Text(LocalizationManager.Get("tech.note_first_death"), TextStyle.Secondary, WidthMax),
                Text(LocalizationManager.Get("key_settings.bind_hint"), TextStyle.Secondary, WidthMax)
            };

            bool dllLoaded = TechniqueSimulator.IsDllLoaded();

#if DEBUG
            c.Add(Text(LocalizationManager.Get("tech.debug_mode",
                LocalizationManager.Get(dllLoaded ? "tech.dll_available" : "tech.dll_unavailable")), TextStyle.Secondary, WidthMax));
            if (dllLoaded)
                c.Add(SwitchRow("tech.use_cpp_version", UseCppTechniqueInDebug, v => UseCppTechniqueInDebug = v));
            else
                c.Add(Text(LocalizationManager.Get("tech.dll_unavailable_notice"), TextStyle.Secondary, WidthMax));
#else
            if (!dllLoaded) EnableTechniqueSimulation = false;
            c.Add(Text(LocalizationManager.Get(dllLoaded ? "tech.dll_available" : "tech.dll_unavailable"),
                TextStyle.Secondary, WidthMax));
#endif

            c.Add(Divider());
            c.Add(BuildLevelConfigSection());

#if !DEBUG
            c.Add(Enabled(() => dllLoaded,
                SwitchRow("tech.enable_technique", EnableTechniqueSimulation, v => EnableTechniqueSimulation = v)));
#else
            c.Add(SwitchRow("tech.enable_technique", EnableTechniqueSimulation, v => EnableTechniqueSimulation = v));
#endif

#if !DEBUG
            if (!dllLoaded)
            {
                c.Add(Text(LocalizationManager.Get("tech.dll_missing_notice"), TextStyle.Secondary, WidthMax));
                return [Card(c.ToArray())];
            }
#endif
            if (!EnableTechniqueSimulation) return [Card(c.ToArray())];

            if (_techniqueProfiles.Count == 0)
            {
                _techniqueProfiles.Add(new TechniqueProfile());
                LoadTechniqueProfileToFields(0);
            }

            c.Add(Divider());
            c.Add(BuildProfileSection());
            c.Add(Divider());
            c.Add(SwitchRow("tech.legacy_press_duration", TechniqueLegacyPressDuration,
                v => TechniqueLegacyPressDuration = v, "tech.legacy_press_duration_desc"));
            c.Add(SwitchRow("tech.multi_chord_balance", TechniqueMultiChordBalance,
                v => TechniqueMultiChordBalance = v, "tech.multi_chord_balance_desc"));

            c.Add(BuildHandSection(true));
            c.Add(BuildHandSection(false));
            c.Add(BuildSegmentsSection());

            return [Card(c.ToArray())];
        }

        private Element BuildLevelConfigSection()
        {
            var c = new List<object>
            {
                Text(LocalizationManager.Get("tech.level_config"), TextStyle.Normal, WidthMax),
                Text(GetLevelConfigStatusText(), TextStyle.Secondary, WidthMax)
            };

            var btns = new List<object>
            {
                Switch(LevelConfigAutoLoad, v => LevelConfigAutoLoad = v, WidthMin),
                Space(6),
                Text(LocalizationManager.Get("tech.level_config_auto_load"), TextStyle.Normal, WidthMin),
                Fill(),
                Button(LocalizationManager.Get("tech.level_config_load"), ButtonStyle.Element, () => LevelTechniqueManager.ReloadCurrentLevelConfig(), WidthMin),
                Space(4),
                Button(LocalizationManager.Get("tech.level_config_save"), ButtonStyle.Element, () => SaveLevelConfigToFile(), WidthMin),
                Space(4),
                Button(LocalizationManager.Get("tech.level_config_delete"), ButtonStyle.Element, () => DeleteLevelConfigFile(), WidthMin),
            };
            c.Add(HBox(ContainerStyle.None, null, Bag(btns)));

            c.Add(HBox(ContainerStyle.None, null, WidthMax,
                Text(LocalizationManager.Get("tech.config_name_optional") + ":", TextStyle.Normal, WidthMin),
                Fill(),
                TextField(_levelConfigNameState.input, v => _levelConfigNameState.input = v, null, Width(200))));

            if (!string.IsNullOrEmpty(_levelConfigStatus))
                c.Add(Text(_levelConfigStatus, TextStyle.Secondary, WidthMax));

            return VBox(ContainerStyle.None, null, Bag(c));
        }

        private Element BuildProfileSection()
        {
            var p = _techniqueProfiles[Mathf.Clamp(SelectedTechniqueProfileIndex, 0, _techniqueProfiles.Count - 1)];
            var c = new List<object>();

            c.Add(HBox(ContainerStyle.None, null, WidthMax,
                Text(LocalizationManager.Get("tech.profile_name"), TextStyle.Normal, WidthMin),
                Fill(),
                TextField(p.name, v => p.name = v, null, Width(160)),
                Button(LocalizationManager.Get("tech.new"), ButtonStyle.Element, () =>
                {
                    _techniqueProfiles.Add(_techniqueProfiles[SelectedTechniqueProfileIndex].Clone());
                    SelectedTechniqueProfileIndex = _techniqueProfiles.Count - 1;
                }, Width(60)),
                Button(LocalizationManager.Get("tech.delete"), ButtonStyle.Element, () =>
                {
                    if (_techniqueProfiles.Count > 1)
                    {
                        _techniqueProfiles.RemoveAt(SelectedTechniqueProfileIndex);
                        SelectedTechniqueProfileIndex = Mathf.Clamp(SelectedTechniqueProfileIndex - 1, 0, _techniqueProfiles.Count - 1);
                    }
                }, Width(60))));

            if (_profileNameCache.Length != _techniqueProfiles.Count)
                _profileNameCache = new string[_techniqueProfiles.Count];
            for (int i = 0; i < _profileNameCache.Length; i++) _profileNameCache[i] = _techniqueProfiles[i].name;

            c.Add(HBox(ContainerStyle.None, null, WidthMax,
                Text(LocalizationManager.Get("tech.select_profile"), TextStyle.Normal, WidthMin),
                Fill(),
                Selector(SelectedTechniqueProfileIndex, _profileNameCache, i => SelectedTechniqueProfileIndex = i,
                    ButtonStyle.Element, ButtonStyle.Primary, WidthMax)));

            string[] hands = { LocalizationManager.Get("tech.left_hand"), LocalizationManager.Get("tech.right_hand") };
            c.Add(HBox(ContainerStyle.None, null, WidthMax,
                Text(LocalizationManager.Get("tech.starting_hand"), TextStyle.Normal, WidthMax),
                Selector(TechniqueHandPreference, hands, i =>
                {
                    TechniqueHandPreference = i;
                    _techniqueProfiles[SelectedTechniqueProfileIndex].handPreference = i;
                }, ButtonStyle.Element, ButtonStyle.Primary, Width(160))));

            c.Add(SliderRow("tech.global_bpm_limit", TechniqueBpmLimit, 50f, 2000f, 0, v => TechniqueBpmLimit = v, "tech.bpm_explanation"));
            c.Add(SliderRow("tech.speed_change_tolerance", SpeedChangeTolerance, 0f, 0.5f, 2, v =>
            {
                SpeedChangeTolerance = v;
                if (_techniqueProfiles.Count > 0)
                    _techniqueProfiles[Mathf.Clamp(SelectedTechniqueProfileIndex, 0, _techniqueProfiles.Count - 1)].speedChangeTolerance = v;
            }, "tech.speed_change_tolerance_desc"));

            return VBox(ContainerStyle.None, null, Bag(c));
        }

        // ── 单只手的按键 / 时长 / 顺序（方案B：结构化每键行）─────────
        private Element BuildHandSection(bool left)
        {
            string handName = LocalizationManager.Get(left ? "tech.left_hand" : "tech.right_hand");
            string keys = left ? TechLeftHandKeys : TechRightHandKeys;
            string times = left ? TechLeftHandPressTimes : TechRightHandPressTimes;
            string orders = left ? TechLeftHandOrders : TechRightHandOrders;

            var vks = KeyBinder.Parse(keys);

            var rows = new List<object> { Text($"── {handName} ──", TextStyle.Subtitle, WidthMax) };

            rows.Add(KeyListRow(left ? "TechLeftKeys" : "TechRightKeys",
                left ? "tech.left_keys" : "tech.right_keys", keys,
                v => { if (left) { TechLeftHandKeys = v; SaveProfileKey(v, true, null); } else { TechRightHandKeys = v; SaveProfileKey(v, false, null); } }));

            // 每键时长（紧凑网格：每行最多 6 个「键名 + 输入框」，对齐按键显示器的排列）
            rows.Add(Text(LocalizationManager.Get(left ? "tech.left_press_ratio" : "tech.right_press_ratio"),
                TextStyle.Normal, WidthMax));
            rows.Add(BuildDurationGrid(left, vks, times));

            // 顺序：按“使用键数”分组编辑
            bool expanded = left ? _ordersLeftExpanded : _ordersRightExpanded;
            rows.Add(HBox(ContainerStyle.None, null, WidthMax,
                ArrowButton(expanded ? ArrowStyle.Down : ArrowStyle.Right,
                    () => { if (left) _ordersLeftExpanded = !_ordersLeftExpanded; else _ordersRightExpanded = !_ordersRightExpanded; }, WidthMin),
                Text(LocalizationManager.Get(left ? "tech.left_orders" : "tech.right_orders"), TextStyle.Normal, WidthMax)));

            if (expanded)
            {
                rows.Add(Text(LocalizationManager.Get("tech.order_format"), TextStyle.Secondary, WidthMax));
                string[] groups = (orders ?? "").Split('|');
                for (int m = 1; m <= vks.Length; m++)
                {
                    int mm = m;
                    string def = string.Join(",", Enumerable.Range(1, mm));
                    string cur = (mm - 1 < groups.Length && !string.IsNullOrWhiteSpace(groups[mm - 1])) ? groups[mm - 1] : def;
                    rows.Add(HBox(ContainerStyle.None, null, WidthMax,
                        Text(string.Format(LocalizationManager.Get("tech.orders_group"), mm), TextStyle.Normal, Width(120)),
                        Fill(),
                        TextField(cur, v =>
                        {
                            var g = (orders ?? "").Split('|').ToList();
                            while (g.Count < mm) g.Add("");
                            g[mm - 1] = v;
                            string s = string.Join("|", g);
                            if (left) { TechLeftHandOrders = s; SaveProfileKey(s, true, "order"); }
                            else { TechRightHandOrders = s; SaveProfileKey(s, false, "order"); }
                        }, null, Width(200))));
                }
            }

            return VBox(ContainerStyle.None, null, Bag(rows));
        }

        /// <summary>
        /// 每键按压时长的网格：每个单元格「键名 + 输入框」均横向撑满（随窗口自适应，不留尾巴），
        /// 每行数量按按键数选择（≤4 一行；5~8 两行；更多则每行 6）。
        /// </summary>
        private Element BuildDurationGrid(bool left, byte[] vks, string times)
        {
            if (vks.Length == 0) return Space(0);
            int perRow = vks.Length <= 4 ? vks.Length : (vks.Length <= 8 ? Mathf.CeilToInt(vks.Length / 2f) : 6);
            var grid = new List<object>();
            var row = new List<object>();
            for (int i = 0; i < vks.Length; i++)
            {
                int idx = i;
                var timesList = ParseTechPressTimes(times, vks.Length);
                row.Add(HBox(ContainerStyle.None, null, WidthMax,
                    Text(KeyBinder.Label(vks[i]), TextStyle.Normal, Width(30), Height(28)),
                    StructField((float)timesList[idx], FloatFormat(2, 0f, 2f), v =>
                    {
                        var arr = ParseTechPressTimes(times, vks.Length);
                        arr[idx] = v;
                        string s = string.Join(",", arr.Select(x => x.ToString("0.##", CultureInfo.InvariantCulture)));
                        if (left) { TechLeftHandPressTimes = s; SaveProfileKey(s, true, "time"); }
                        else { TechRightHandPressTimes = s; SaveProfileKey(s, false, "time"); }
                    }, WidthMax, Height(28))));
                row.Add(Space(8));
                if ((i + 1) % perRow == 0) { grid.Add(HBox(ContainerStyle.None, null, row.ToArray())); row = new List<object>(); }
            }
            if (row.Count > 0) grid.Add(HBox(ContainerStyle.None, null, row.ToArray()));
            return VBox(ContainerStyle.None, null, Bag(grid));
        }

        /// <summary>把按键/时长/顺序改动立即写回当前配置档（对应字段 type：null=keys, "time", "order"）。</summary>
        private void SaveProfileKey(string value, bool left, string type)
        {
            if (_techniqueProfiles == null || _techniqueProfiles.Count == 0) return;
            var p = _techniqueProfiles[Mathf.Clamp(SelectedTechniqueProfileIndex, 0, _techniqueProfiles.Count - 1)];
            switch (type)
            {
                case "time": if (left) p.leftHandPressTimes = value; else p.rightHandPressTimes = value; break;
                case "order": if (left) p.leftHandOrders = value; else p.rightHandOrders = value; break;
                default: if (left) p.leftHandKeys = value; else p.rightHandKeys = value; break;
            }
        }

        private Element BuildSegmentsSection()
        {
            var p = _techniqueProfiles[Mathf.Clamp(SelectedTechniqueProfileIndex, 0, _techniqueProfiles.Count - 1)];
            var segments = p.techniqueSegments ?? (p.techniqueSegments = new List<TechniqueSegment>());

            while (_segmentExpanded.Count < segments.Count) _segmentExpanded.Add(false);
            while (_segmentExpanded.Count > segments.Count) _segmentExpanded.RemoveAt(_segmentExpanded.Count - 1);

            var rows = new List<object>
            {
                Text(LocalizationManager.Get("tech.speed_segments"), TextStyle.Subtitle, WidthMax),
                Text(LocalizationManager.Get("tech.segment_inherit"), TextStyle.Secondary, WidthMax)
            };

            for (int i = 0; i < segments.Count; i++)
            {
                int si = i; // 闭包需按迭代捕获，避免所有按钮共享循环变量
                var seg = segments[si];
                string segLabel = string.Format(LocalizationManager.Get("tech.segment_label"),
                    _segmentExpanded[si] ? "▼" : "▶", si + 1, seg.startFloor, seg.endFloor, seg.bpmLimit, seg.HasKeyOverride ? " ✎" : "");

                var head = new List<object>
                {
                    Button(segLabel, ButtonStyle.Element, () => _segmentExpanded[si] = !_segmentExpanded[si], WidthMax),
                    Button("✕", ButtonStyle.Element, () =>
                    {
                        if (si < segments.Count) segments.RemoveAt(si);
                        if (si < _segmentExpanded.Count) _segmentExpanded.RemoveAt(si);
                    }, Width(36))
                };
                rows.Add(HBox(ContainerStyle.None, null, Bag(head)));

                if (!_segmentExpanded[si]) continue;

                var body = new List<object>
                {
                    HBox(ContainerStyle.None, null, WidthMax,
                        Text(LocalizationManager.Get("tech.segment_start_floor"), TextStyle.Normal, WidthMin),
                        Fill(),
                        StructField(seg.startFloor, IntFormat(0, int.MaxValue), v => seg.startFloor = v, Width(70)),
                        Space(6),
                        Text(LocalizationManager.Get("tech.segment_end_floor"), TextStyle.Normal, WidthMin),
                        Fill(),
                        StructField(seg.endFloor, IntFormat(0, int.MaxValue), v => seg.endFloor = v, Width(70))),
                    SliderRow("tech.bpm_limit", seg.bpmLimit, 50f, 2000f, 0, v => seg.bpmLimit = v),
                    KeyListRow($"SegL{i}", "tech.left_keys", seg.leftHandKeys, v => seg.leftHandKeys = v),
                    KeyListRow($"SegR{i}", "tech.right_keys", seg.rightHandKeys, v => seg.rightHandKeys = v),
                };
                rows.Add(VBox(ContainerStyle.None, null, Bag(body)));
            }

            rows.Add(Button(LocalizationManager.Get("tech.add_segment"), ButtonStyle.Element,
                () => segments.Add(new TechniqueSegment { bpmLimit = TechniqueBpmLimit }), WidthMax));

            return VBox(ContainerStyle.None, null, Bag(rows));
        }
    }
}
