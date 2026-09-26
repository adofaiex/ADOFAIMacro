using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

#nullable enable

namespace ADOFAIMacro.Localization
{
    /// <summary>
    /// 本地化管理器 - 负责加载、切换和提供翻译文本
    /// </summary>
    public static class LocalizationManager
    {
        private static Dictionary<string, string>? _currentTranslations;
        private static string _currentLanguage = "zh-CN";
        private static bool _initialized = false;
        private static string _modPath = "";

        // 默认翻译（fallback，防止语言文件缺失）
        // 默认翻译（fallback，防止语言文件缺失）
        // 受保护的键及其硬编码翻译（根据语言返回固定值，不可被 JSON 覆盖）
        private static readonly Dictionary<string, Dictionary<string, string>> _protectedTranslations = new()
        {
            ["macro.enabled_text"] = new Dictionary<string, string>
            {
                ["zh-CN"] = "宏已开启！",
                ["en-US"] = "Macro is enabled!"
            }
        };

        // 默认翻译（fallback，防止语言文件缺失）
        private static readonly Dictionary<string, string> _fallbackTranslations = new()
        {
            // Chinese fallback (original hardcoded strings)
            ["tab.language"] = "语言",
            ["tab.macro"] = "宏",
            ["tab.key_settings"] = "按键设置",
            ["tab.key_filter"] = "按键过滤",
            ["tab.offset_settings"] = "延迟设置",
            ["tab.other_settings"] = "其他选项",
            ["tab.update_log"] = "更新日志",
            ["tab.author"] = "作者",
            ["tab.beta"] = "测试版",
            ["tab.technique_simulation"] = "手法模拟",
            ["language.display_language"] = "显示语言",
            ["language.chinese"] = "中文",
            ["language.english"] = "英文",
            ["macro.enable_macro"] = "启用宏",
            ["offset.allow_ctrl_adjust"] = "允许Ctrl+左右键调整步长偏移(游戏中)",
            ["offset.adjust_step"] = "调整步长",
            ["offset.offset_ms"] = "延迟 (ms)",
            ["offset.allow_arrow_adjust"] = "允许左右键调整延迟(游戏中)",
            ["offset.enable_high_precision"] = "启用高精度时间（提高同步精度）",
            ["offset.enable_high_precision_async"] = "[实验性]启用高精度异步（需重新启用模组生效）",
            ["offset.auto_calibrate"] = "判定误差自动校准(闭环消除变速段偏移)",
            ["other.suppress_gc"] = "游玩期抑制GC停顿(消除高密度图误差尖峰)",
            ["key_settings.keys_comma_separated"] = "按键序列 (逗号分隔)",
            ["key_settings.key_simulation"] = "按键模拟",
            ["key_settings.use_advanced_input"] = "使用高级输入(否则使用SendInput API)",
            ["key_settings.virtual_async_input"] = "虚拟异步键盘直喂(跳过系统注入,不可用时自动回退)",
            ["key_settings.mirror_virtual_keys"] = "虚拟按键同步系统输入(按键显示器可见,回声自动丢弃)",
            ["key_settings.win_api_input_mode"] = "Win API 输入模式",
            ["key_settings.actual"] = "实际",
            ["key_settings.active"] = "Active",
            ["key_settings.input_mode"] = "输入模式",
            ["key_mode.auto"] = "自动",
            ["key_mode.ntinject"] = "NtInject",
            ["key_mode.ntsendinput"] = "NtSendInput ★",
            ["key_mode.sendinput"] = "SendInput",
            ["key_mode_desc.auto"] = "自动：优先使用最底层可用方式",
            ["key_mode_desc.ntinject"] = "NtInject（最底层）：直接注入原始输入流",
            ["key_mode_desc.ntsendinput"] = "NtSendInput ★：内核边界注入",
            ["key_mode_desc.sendinput"] = "SendInput：标准 Win32 API，兼容性最佳",
            ["key_mode_not_supported"] = "(不支持)",
            ["key_mode_na"] = "(N/A)",
            ["key_settings.mode_indicator"] = "实际: {0}",
            ["filter.enable_filter"] = "启用按键过滤",
            ["filter.filter_mode"] = "过滤模式",
            ["filter.blacklist_mode"] = "黑名单模式",
            ["filter.whitelist_mode"] = "白名单模式",
            ["filter.blacklist_desc"] = "⛔ 黑名单模式：列表中的按键将被阻止",
            ["filter.whitelist_desc"] = "✅ 白名单模式：只有列表中的按键允许通过",
            ["filter.keys_comma_separated"] = "按键列表 (逗号分隔)",
            ["filter.async_keys_comma_separated"] = "异步按键列表 (逗号分隔)",
            ["filter.requires_skyhook"] = "（需开启 SkyHook 模式）",
            ["filter.common_keys"] = "常用按键:",
            ["filter.tip"] = "提示：支持按键名称（A、B、SPACE、ENTER等）和虚拟键码（0x41格式）。多个按键用逗号分隔。",
            ["other.enable_death_key"] = "死亡后自动按键(仅SkyHook模式)",
            ["other.delay_seconds"] = "延迟秒数",
            ["other.key"] = "按键",
            ["other.switch_nofaill"] = "游戏允许切换到失败模式",
            ["other.switch_judgement"] = "游戏中允许切换判定",
            ["other.lock_level_editor"] = "锁定关卡编辑器（防止误操作）",
            ["other.tip_enter_key"] = "提示：可直接输入字母、数字、F1-F12或特殊键名（如SPACE、ENTER），也可输入十六进制代码（如0x52）",
            ["author.email_chinese"] = "hitmargin@qq.com",
            ["author.email_english"] = "hitmargin@Outlook.com",
            ["author.thanks"] = "❤️ 感谢使用 {0}",
            ["update_log.title"] = "更新日志",
            ["update_log.whats_new"] = "What's New",
            ["update_log.content"] = "<b>版本 {0}</b>\n• 手法模拟优化和修复\n• 分段支持按键覆盖",
            ["tech.note_first_death"] = "注：最开始进入游戏需要死亡一次来校准时间",
            ["tech.debug_mode"] = "🔧 调试模式 - {0}",
            ["tech.dll_available"] = "DLL可用",
            ["tech.dll_unavailable"] = "DLL不可用",
            ["tech.use_cpp_version"] = "使用C++版本",
            ["tech.dll_unavailable_notice"] = "⚠️ DLL不可用，将使用C#版本",
            ["tech.enable_technique"] = "启用手法模拟（左右手交替）",
            ["tech.dll_missing_notice"] = "请将 TechniqueSimulator.dll 放在 Mods/ADOFAIMacro/ 目录下",
            ["tech.profile_name"] = "配置名称",
            ["tech.new"] = "新建",
            ["tech.delete"] = "删除",
            ["tech.select_profile"] = "选择配置",
            ["tech.starting_hand"] = "起始手",
            ["tech.left_hand"] = "左手",
            ["tech.right_hand"] = "右手",
            ["tech.global_bpm_limit"] = "全局·速度阈值 (BPM)",
            ["tech.bpm_explanation"] = "超过此BPM时自动细分时间片，允许同一只手连续承担多个事件",
            ["tech.speed_segments"] = "变速分段设置",
            ["tech.segment_inherit"] = "留空的按键字段将继承全局配置。",
            ["tech.add_segment"] = "+ 添加分段",
            ["tech.segment_start_floor"] = "起始地板",
            ["tech.segment_end_floor"] = "结束地板",
            ["tech.bpm_limit"] = "BPM 阈值",
            ["tech.left_keys"] = "左手按键:",
            ["tech.right_keys"] = "右手按键:",
            ["tech.left_press_ratio"] = "左手时长:",
            ["tech.right_press_ratio"] = "右手时长:",
            ["tech.left_orders"] = "左手顺序:",
            ["tech.right_orders"] = "右手顺序:",
            ["tech.presets"] = "预设:",
            ["tech.preset_dfjk"] = "DF / JK",
            ["tech.preset_dsjk"] = "DS / JK",
            ["tech.preset_asdfjkl"] = "ASDF / JKL",
            ["tech.order_format"] = "按键顺序格式：用 | 分隔不同按键数，逗号分隔键序号(1-based)。留空=默认顺序。",
            ["common.f2"] = "F2",
            ["common.f1"] = "F1",
            ["common.f0"] = "F0",
            ["common.expand"] = "展开",
            ["common.collapse"] = "折叠",
            // Beta card
            ["beta.warning_format"] = "⚠️ 测试版本 {0} - 功能可能不稳定，请谨慎使用 ⚠️",
            ["beta.feedback_message"] = "如遇问题请通过邮箱反馈，感谢您的测试！",
            // Technique simulation segment label
            ["tech.segment_label"] = "{0} 段 {1}  [{2}~{3}]  BPM≤{4:F0}{5}",

            // ── 以下 19 个 key 原先只在 zh-CN/en-US.json 里有，硬编码 fallback 缺失 ──
            // 安装时漏拷 Localization/ 目录（README 有提醒）或 JSON 损坏时，
            // 「关卡特定配置」整块 UI 会退化成显示 key 原文（如 tech.level_config_load）。
            ["other.block_input_unfocused"] = "窗口未激活时阻止按键输入",
            ["tech.speed_change_tolerance"] = "变速容差",
            ["tech.speed_change_tolerance_desc"] = "自动调整BPM使时间片对齐事件时序（0=关闭, 0.2=适中, 0.5=激进）。应对连续微变速谱面。",
            ["tech.level_config"] = "关卡配置",
            ["tech.level_config_auto_load"] = "自动从关卡目录加载",
            ["tech.config_name_optional"] = "配置名称（可选）",
            ["tech.level_config_load"] = "加载",
            ["tech.level_config_save"] = "保存到关卡目录",
            ["tech.level_config_delete"] = "删除",
            ["tech.level_config_no_level"] = "未加载关卡",
            ["tech.level_config_missing"] = "'{0}' 没有保存的配置",
            ["tech.level_config_has"] = "'{0}' 已有保存的配置",
            ["tech.level_config_has_with_name"] = "'{0}' 已有保存的配置 (配置名: {1})",
            ["tech.level_config_error"] = "检查关卡配置时出错",
            ["tech.level_config_no_level_warn"] = "未加载关卡！",
            ["tech.level_config_saved"] = "配置已保存！",
            ["tech.level_config_save_failed"] = "保存失败！",
            ["tech.level_config_deleted"] = "配置已删除。",
            ["tech.level_config_delete_failed"] = "删除失败！"
        };

        /// <summary>
        /// 初始化本地化系统
        /// </summary>
        public static void Initialize(string modPath)
        {
            if (_initialized) return;

            _modPath = modPath;
            LoadLanguage("zh-CN"); // 默认中文
            _initialized = true;
        }

        /// <summary>
        /// 加载指定语言
        /// </summary>
        public static bool LoadLanguage(string languageCode)
        {
            try
            {
                string langDir = Path.Combine(_modPath, "Localization");
                string filePath = Path.Combine(langDir, $"{languageCode}.json");

                if (!File.Exists(filePath))
                {
                    if (Main.LoggingEnabled)
                        UnityEngine.Debug.LogWarning($"[Localization] 语言文件不存在: {filePath}, 使用默认翻译");
                    _currentTranslations = new Dictionary<string, string>(_fallbackTranslations);
                    _currentLanguage = "zh-CN";
                    return false;
                }

                string json = File.ReadAllText(filePath);
                var langData = JsonConvert.DeserializeObject<LanguageData>(json);

                if (langData == null || langData.translations == null)
                {
                    if (Main.LoggingEnabled)
                        UnityEngine.Debug.LogError($"[Localization] 语言文件格式错误或translations为空: {filePath}");
                    _currentTranslations = new Dictionary<string, string>(_fallbackTranslations);
                    _currentLanguage = "zh-CN";
                    return false;
                }

                _currentTranslations = new Dictionary<string, string>(langData.translations);
                _currentLanguage = languageCode;

                if (Main.LoggingEnabled)
                    UnityEngine.Debug.Log($"[Localization] 已加载语言: {langData.name} ({languageCode})");
                return true;
            }
            catch (Exception ex)
            {
                if (Main.LoggingEnabled)
                    UnityEngine.Debug.LogError($"[Localization] 加载语言失败: {ex.Message}");
                _currentTranslations = new Dictionary<string, string>(_fallbackTranslations);
                _currentLanguage = "zh-CN";
                return false;
            }
        }

        /// <summary>
        /// 获取翻译文本
        /// </summary>
        public static string Get(string key, params object[] args)
        {
            // 检查是否为受保护键 - 强制返回硬编码翻译，忽略 JSON
            if (_protectedTranslations.TryGetValue(key, out Dictionary<string, string> protectedTrans))
            {
                if (protectedTrans.TryGetValue(_currentLanguage, out string protectedValue))
                {
                    //UnityEngine.Debug.Log($"[Localization] Key '{key}' is protected, returning hardcoded translation");
                    if (args.Length > 0)
                        return string.Format(protectedValue, args);
                    return protectedValue;
                }
                // 受保护键但没有对应语言，fall through 到正常逻辑
            }

            if (_currentTranslations == null)
            {
                // Get 在 OnGUI 里每帧被调用数十次，未初始化期间会刷屏 —— 每个 key 只报一次
                WarnOnce(key, $"[Localization] _currentTranslations is null, returning key: {key}");
                return args.Length > 0 ? FormatSafe(key, args) : key;
            }

            if (_currentTranslations.TryGetValue(key, out string value))
            {
                if (args.Length > 0)
                    return string.Format(value, args);
                return value;
            }

            // Fallback 到默认翻译
            if (_fallbackTranslations.TryGetValue(key, out string fallback))
            {
                WarnOnce(key, $"[Localization] Key '{key}' not found in current language, using fallback");
                if (args.Length > 0)
                    return string.Format(fallback, args);
                return fallback;
            }

            // 返回 key 本身作为最后的 fallback
            WarnOnce(key, $"[Localization] Key '{key}' not found in any dictionary, returning key itself");
            return args.Length > 0 ? FormatSafe(key, args) : key;
        }

        // 已告警过的 key：语言文件缺失时若某个 key 两个字典都没有，旧实现会在
        // 每一次 Get 调用上打一条 LogWarning（OnGUI 每帧数十次）→ 日志刷屏。
        private static readonly HashSet<string> _warnedKeys = [];

        private static void WarnOnce(string key, string message)
        {
            if (!Main.LoggingEnabled) return;
            if (_warnedKeys.Add(key))
                UnityEngine.Debug.LogWarning(message);
        }

        /// <summary>key 本身不含占位符时 string.Format 会抛 FormatException，这里兜住。</summary>
        private static string FormatSafe(string key, object[] args)
        {
            try { return string.Format(key, args); }
            catch (FormatException) { return key; }
        }

        /// <summary>
        /// 当前语言代码
        /// </summary>
        public static string CurrentLanguage => _currentLanguage;

        /// <summary>
        /// 是否已初始化
        /// </summary>
        public static bool IsInitialized => _initialized;

        /// <summary>
        /// 检查是否为中文
        /// </summary>
        public static bool IsChinese => _currentLanguage == "zh-CN";

        /// <summary>
        /// 语言数据容器（用于 JSON 反序列化）
        /// </summary>
        [Serializable]
        private class LanguageData
        {
            public string? language = null;
            public string? name = string.Empty;
            public Dictionary<string, string>? translations = [];
        }
    }
}
