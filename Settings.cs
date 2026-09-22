using ADOFAIMacro.Macro;
using ADOFAIMacro.Localization;
using HarmonyLib;
using Newgrounds;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityModManagerNet;

namespace ADOFAIMacro
{
    /// <summary>
    /// Mod settings class / Mod 设置类
    /// </summary>
    public partial class Settings : UnityModManager.ModSettings
    {
        // ─────────────────────────────────────────────
        //  手法配置文件
        // ─────────────────────────────────────────────
        [Serializable]
        public class TechniqueProfile
        {
            public string name = "默认配置";
            public string leftHandKeys = "D,F";
            public string rightHandKeys = "J,K";
            public string leftHandOrders = "";
            public string rightHandOrders = "";
            public string leftHandPressTimes = "0.8,0.8";
            public string rightHandPressTimes = "0.8,0.8";
            public int handPreference = 1; // 0=左手优先, 1=右手优先
            public float speedChangeTolerance = 0f;
            public List<TechniqueSegment> techniqueSegments = [];

            public TechniqueProfile() { }

            public TechniqueProfile Clone()
            {
                return new TechniqueProfile
                {
                    name = this.name + " (副本)",
                    leftHandKeys = this.leftHandKeys,
                    rightHandKeys = this.rightHandKeys,
                    leftHandOrders = this.leftHandOrders,
                    rightHandOrders = this.rightHandOrders,
                    leftHandPressTimes = this.leftHandPressTimes,
                    rightHandPressTimes = this.rightHandPressTimes,
                    handPreference = this.handPreference,
                    speedChangeTolerance = this.speedChangeTolerance,
                    techniqueSegments = [.. this.techniqueSegments.Select(s => new TechniqueSegment {
                        startFloor          = s.startFloor,
                        endFloor            = s.endFloor,
                        bpmLimit            = s.bpmLimit,
                        leftHandKeys        = s.leftHandKeys,
                        rightHandKeys       = s.rightHandKeys,
                        leftHandOrders      = s.leftHandOrders,
                        rightHandOrders     = s.rightHandOrders,
                        leftHandPressTimes  = s.leftHandPressTimes,
                        rightHandPressTimes = s.rightHandPressTimes,
                    })]
                };
            }
        }

        // ─────────────────────────────────────────────
        //  变速分段（含可选按键覆盖）
        // ─────────────────────────────────────────────
        [Serializable]
        public class TechniqueSegment
        {
            public int startFloor;
            public int endFloor;
            public float bpmLimit;

            // 可选按键覆盖（留空 = 继承全局配置）
            public string leftHandKeys = "";
            public string rightHandKeys = "";
            public string leftHandOrders = "";
            public string rightHandOrders = "";
            public string leftHandPressTimes = "";
            public string rightHandPressTimes = "";

            /// <summary>任一手的按键字段非空即视为有覆盖</summary>
            public bool HasKeyOverride =>
                !string.IsNullOrWhiteSpace(leftHandKeys) ||
                !string.IsNullOrWhiteSpace(rightHandKeys);
        }

        // ─────────────────────────────────────────────
        //  UI 内部状态
        // ─────────────────────────────────────────────
        public List<TechniqueSegment> techniqueSegments = [];

        private List<bool> _segmentExpanded = [];
        private string[] _profileNameCache = [];
        private string _levelStatusTextCache = "";
        private int _levelStatusVersion = -1;

        // ─────────────────────────────────────────────
        //  基础设置属性
        // ─────────────────────────────────────────────
        public event Action<bool> OnMacroChanged;

        // 语言设置 - 现在由 LocalizationManager 管理
        private bool _useChinese;
        public bool UseChinese
        {
            get => _useChinese;
            set
            {
                if (_useChinese == value) return;
                _useChinese = value;
                UnityEngine.Debug.Log($"[Settings] UseChinese changed to: {value}, loading language...");
                if (value)
                {
                    bool success = ADOFAIMacro.Localization.LocalizationManager.LoadLanguage("zh-CN");
                    UnityEngine.Debug.Log($"[Settings] LoadLanguage('zh-CN') returned: {success}");
                }
                else
                {
                    bool success = ADOFAIMacro.Localization.LocalizationManager.LoadLanguage("en-US");
                    UnityEngine.Debug.Log($"[Settings] LoadLanguage('en-US') returned: {success}");
                }
                UnityEngine.Debug.Log($"[Settings] Current language after switch: {ADOFAIMacro.Localization.LocalizationManager.CurrentLanguage}, IsChinese: {ADOFAIMacro.Localization.LocalizationManager.IsChinese}");
            }
        }

        private bool _macro;
        public bool Macro
        {
            get => _macro;
            set
            {
                if (_macro == value) return;
                _macro = value;
                OnMacroChanged?.Invoke(value);
            }
        }

        private string _macroKeys = "D,F,J,K";
        public string MacroKeys
        {
            get => _macroKeys;
            set { if (_macroKeys == value) return; _macroKeys = value; }
        }

        private bool _simulateKeyPress = false;
        public bool SimulateKeyPress
        {
            get => _simulateKeyPress;
            set { if (_simulateKeyPress == value) return; _simulateKeyPress = value; }
        }

        public bool EnableKeyAdjust = true;
        public float AdjustStep = 1f;

        private float _timeOffset;
        public float TimeOffset
        {
            get => _timeOffset;
            set => _timeOffset = Mathf.Clamp(value, -100f, 100f);
        }

        public bool EnableArrowTimeAdjust = true;

        private bool _skyHookMode = false;
        public bool SkyHookMode
        {
            get => _skyHookMode;
            set { if (_skyHookMode == value) return; _skyHookMode = value; }
        }

        // ── 虚拟异步键盘：合成事件直喂游戏 keyQueue（详见 VirtualAsyncInput）──
        // 需 SkyHookMode + 游戏异步输入开启；不可用时自动回退系统注入
        private bool _useVirtualAsyncInput = true;
        public bool UseVirtualAsyncInput
        {
            get => _useVirtualAsyncInput;
            set { if (_useVirtualAsyncInput == value) return; _useVirtualAsyncInput = value; }
        }

        // ── 虚拟按键镜像：直喂成功后同步注入一份真实按键（SendInput），
        //    让读 Unity Input / OS 键盘的按键显示器（JipperKeyViewer 等）看见
        //    虚拟按键；注入回声在 HookCallback 里按配额丢弃，不会双判定 ──
        private bool _mirrorVirtualKeys = true;
        public bool MirrorVirtualKeys
        {
            get => _mirrorVirtualKeys;
            set { if (_mirrorVirtualKeys == value) return; _mirrorVirtualKeys = value; }
        }

        // ── 游玩期 GC 停顿抑制（方案8）：高密度图消除 GC 尖峰 ──
        // （当前环境 TryStartNoGCRegion 从未成功过，默认关闭减少变量）
        private bool _suppressGcPauses = false;
        public bool SuppressGcPauses
        {
            get => _suppressGcPauses;
            set { if (_suppressGcPauses == value) return; _suppressGcPauses = value; }
        }

        // ── 判定误差闭环校准（方案7）：实测判定误差反馈自动补偿变速段偏移 ──
        private bool _autoCalibrateJudgement = true;
        public bool AutoCalibrateJudgement
        {
            get => _autoCalibrateJudgement;
            set { if (_autoCalibrateJudgement == value) return; _autoCalibrateJudgement = value; }
        }

        private bool _highPrecisionAsync = false;
        public bool HighPrecisionAsync
        {
            get => _highPrecisionAsync;
            set { if (_highPrecisionAsync == value) return; _highPrecisionAsync = value; }
        }

        // ── 版本信息 ──────────────────────────────────
        private int? _betaVersion = null;
        public int BetaVersion
        {
            get
            {
                if (_betaVersion == null) _betaVersion = GetBetaVersionFromAssembly();
                return _betaVersion.Value;
            }
        }
        public bool IsBeta => BetaVersion > 0;

        private int GetBetaVersionFromAssembly()
        {
            try { return Assembly.GetExecutingAssembly().GetName().Version.Revision; }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogWarning($"[Settings] 读取 Beta 版本号失败，按正式版处理: {ex.Message}");
                return 0;
            }
        }

        // ── 输入模式 ──────────────────────────────────
        private int _inputMode = 0;
        public int InputMode
        {
            get => _inputMode;
            set
            {
                if (_inputMode == value) return;
                _inputMode = value;
                if (ADOFAIMacro.Macro.InputSystem.IsInitialized)
                    ADOFAIMacro.Macro.InputSystem.SetInputMode((Macro.InputMode)value);
            }
        }

        // ── 死亡按键 ──────────────────────────────────
        private bool _enableDeathKey = false;
        public bool EnableDeathKey
        {
            get => _enableDeathKey;
            set { if (_enableDeathKey == value) return; _enableDeathKey = value; }
        }

        private float _deathKeyDelay = 5f;
        public float DeathKeyDelay
        {
            get => _deathKeyDelay;
            set => _deathKeyDelay = Mathf.Clamp(value, 0.1f, 30f);
        }

        private int _deathKeyCode = 0x52;
        public int DeathKeyCode
        {
            get => _deathKeyCode;
            set => _deathKeyCode = value;
        }

        private string _deathKeyInput = "R";
        public string DeathKeyInput
        {
            get => _deathKeyInput;
            set
            {
                if (_deathKeyInput == value) return;
                _deathKeyInput = value.ToUpper();
                int? code = GetKeyCodeFromString(_deathKeyInput);
                if (code.HasValue) _deathKeyCode = code.Value;
            }
        }

        public bool ChangeNoFaillInPlay = false;
        public bool ChangeJudementInPlay = false;
        public bool LockLevelEditor = false;
        public bool BlockInputWhenUnfocused = true;

        // ─────────────────────────────────────────────
        //  手法模拟全局设置
        // ─────────────────────────────────────────────
        private bool _enableTechSim = false;
        public bool EnableTechniqueSimulation
        {
            get => _enableTechSim;
            set { if (_enableTechSim == value) return; _enableTechSim = value; }
        }

        private float _techniqueBpmLimit = 500f;
        public float TechniqueBpmLimit
        {
            get => _techniqueBpmLimit;
            set => _techniqueBpmLimit = Mathf.Clamp(value, 50f, 2000f);
        }

        public string TechLeftHandKeys = "D,F";
        public string TechRightHandKeys = "J,K";
        public string TechLeftHandOrders = "";
        public string TechRightHandOrders = "";
        public string TechLeftHandPressTimes = "0.8,0.8";
        public string TechRightHandPressTimes = "0.8,0.8";



        // ── 按键过滤 ──────────────────────────────────
        private bool _enableKeyFilter = false;
        public bool EnableKeyFilter
        {
            get => _enableKeyFilter;
            set { if (_enableKeyFilter == value) return; _enableKeyFilter = value; }
        }

        private int _filterMode = 0;
        public int FilterMode
        {
            get => _filterMode;
            set => _filterMode = value;
        }

        private string _filteredKeys = "F1,F2,F3,F4";
        public string FilteredKeys
        {
            get => _filteredKeys;
            set => _filteredKeys = value;
        }

        private string _filteredAsyncKeys = "";
        public string FilteredAsyncKeys
        {
            get => _filteredAsyncKeys;
            set => _filteredAsyncKeys = value;
        }

        public bool HighPrecisionTime;


        // ── 手法起始手 ────────────────────────────────
        private int _techniqueHandPreference = 1;
        public int TechniqueHandPreference
        {
            get => _techniqueHandPreference;
            set { if (_techniqueHandPreference == value) return; _techniqueHandPreference = value; }
        }

        // ── 手法输入框状态 ────────────────────────────
        private float _speedChangeTolerance = 0f;
        public float SpeedChangeTolerance
        {
            get => _speedChangeTolerance;
            set => _speedChangeTolerance = Mathf.Clamp(value, 0f, 0.5f);
        }

        // ── 按压时长风格：false=跟随音符片长（新），true=旧版 1.3.0.30 折叠片长 ──
        private bool _techniqueLegacyPressDuration = false;
        public bool TechniqueLegacyPressDuration
        {
            get => _techniqueLegacyPressDuration;
            set { if (_techniqueLegacyPressDuration == value) return; _techniqueLegacyPressDuration = value; }
        }

        // ── 多押按键均分：false=主手取满按键数后余数给另一手；true=对半均分到两手 ──
        private bool _techniqueMultiChordBalance = false;
        public bool TechniqueMultiChordBalance
        {
            get => _techniqueMultiChordBalance;
            set { if (_techniqueMultiChordBalance == value) return; _techniqueMultiChordBalance = value; }
        }

        // ── 配置列表 ──────────────────────────────────
        private List<TechniqueProfile> _techniqueProfiles = [];
        public List<TechniqueProfile> TechniqueProfiles
        {
            get => _techniqueProfiles;
            set => _techniqueProfiles = value;
        }

        private int _selectedTechniqueProfileIndex = 0;
        public int SelectedTechniqueProfileIndex
        {
            get => _selectedTechniqueProfileIndex;
            set
            {
                if (_selectedTechniqueProfileIndex == value) return;
                _selectedTechniqueProfileIndex = value;
                LoadTechniqueProfileToFields(value);
            }
        }

        private void LoadTechniqueProfileToFields(int index)
        {
            if (index < 0 || index >= _techniqueProfiles.Count) return;
            var p = _techniqueProfiles[index];
            TechLeftHandKeys = p.leftHandKeys;
            TechRightHandKeys = p.rightHandKeys;
            TechLeftHandOrders = p.leftHandOrders;
            TechRightHandOrders = p.rightHandOrders;
            TechLeftHandPressTimes = p.leftHandPressTimes;
            TechRightHandPressTimes = p.rightHandPressTimes;
            TechniqueHandPreference = p.handPreference;
            SpeedChangeTolerance = p.speedChangeTolerance;
        }

        private void SaveCurrentToProfile(int index)
        {
            if (index < 0 || index >= _techniqueProfiles.Count) return;
            var p = _techniqueProfiles[index];
            p.leftHandKeys = TechLeftHandKeys;
            p.rightHandKeys = TechRightHandKeys;
            p.leftHandOrders = TechLeftHandOrders;
            p.rightHandOrders = TechRightHandOrders;
            p.leftHandPressTimes = TechLeftHandPressTimes;
            p.rightHandPressTimes = TechRightHandPressTimes;
            p.handPreference = TechniqueHandPreference;
            p.speedChangeTolerance = SpeedChangeTolerance;

            // 保存分段配置：深拷贝当前分段列表
            var currentSegments = p.techniqueSegments;
            if (currentSegments != null)
            {
                p.techniqueSegments = currentSegments.Select(s => new TechniqueSegment
                {
                    startFloor = s.startFloor,
                    endFloor = s.endFloor,
                    bpmLimit = s.bpmLimit,
                    leftHandKeys = s.leftHandKeys,
                    rightHandKeys = s.rightHandKeys,
                    leftHandOrders = s.leftHandOrders,
                    rightHandOrders = s.rightHandOrders,
                    leftHandPressTimes = s.leftHandPressTimes,
                    rightHandPressTimes = s.rightHandPressTimes
                }).ToList();
            }
            else
            {
                p.techniqueSegments = new List<TechniqueSegment>();
            }
        }

#if DEBUG
        private bool _useCppTechniqueInDebug = true;
        public bool UseCppTechniqueInDebug
        {
            get => _useCppTechniqueInDebug;
            set { if (_useCppTechniqueInDebug == value) return; _useCppTechniqueInDebug = value; }
        }
#endif

        // ── 关卡特定手法配置 ─────────────────────────────
        private static bool _levelConfigAutoLoad = true;
        public bool LevelConfigAutoLoad
        {
            get => _levelConfigAutoLoad;
            set { if (_levelConfigAutoLoad == value) return; _levelConfigAutoLoad = value; }
        }

        private (string input, bool focused) _levelConfigNameState = (string.Empty, false);
        private string _levelConfigStatus = "";

        // ─────────────────────────────────────────────
        //  按键代码映射（统一走 Macro.KeyMap 单一数据源）
        // ─────────────────────────────────────────────
        private int? GetKeyCodeFromString(string keyString)
        {
            if (string.IsNullOrEmpty(keyString)) return null;
            string key = keyString.Trim();
            if (key.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                if (int.TryParse(key.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out int hex))
                    return hex;
            if (ADOFAIMacro.Macro.KeyMap.TryGetKeyCode(key.ToUpperInvariant(), out byte code)) return code;
            return null;
        }

        // ─────────────────────────────────────────────
        //  持久化
        // ─────────────────────────────────────────────
        public void OnSaveGUI(UnityModManager.ModEntry modEntry) => Save(modEntry);
        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);
        public static Settings Load(UnityModManager.ModEntry modEntry) => Load<Settings>(modEntry);

        // ─────────────────────────────────────────────
        //  关卡特定配置辅助方法
        // ─────────────────────────────────────────────
        // 结果按 LevelTechniqueManager.CacheVersion 缓存：
        // 原先每个 GUI 事件都执行 File.Exists + Path 组合（含系统调用），每帧多次
        private string GetLevelConfigStatusText()
        {
            if (_levelStatusVersion == LevelTechniqueManager.CacheVersion)
                return _levelStatusTextCache;
            _levelStatusVersion = LevelTechniqueManager.CacheVersion;
            _levelStatusTextCache = BuildLevelConfigStatusText();
            return _levelStatusTextCache;
        }

        private string BuildLevelConfigStatusText()
        {
            try
            {
                if (string.IsNullOrEmpty(ADOBase.levelPath))
                {
                    return LocalizationManager.Get("tech.level_config_no_level");
                }

                bool hasConfig = LevelTechniqueManager.HasConfigForCurrentLevel();
                string levelName = Path.GetFileNameWithoutExtension(ADOBase.levelPath);
                string key = hasConfig ? "tech.level_config_has" : "tech.level_config_missing";

                var config = LevelTechniqueManager.GetCurrentLevelConfig();
                if (hasConfig && config != null)
                {
                    int segCount = config.techniqueSegments?.Count ?? 0;
                    return string.Format(LocalizationManager.Get("tech.level_config_has_with_name"), levelName, config.name) +
                           $" (segments: {segCount})";
                }

                return string.Format(LocalizationManager.Get(key), levelName);
            }
            catch
            {
                return LocalizationManager.Get("tech.level_config_error");
            }
        }

        private void SaveLevelConfigToFile()
        {
            if (string.IsNullOrEmpty(ADOBase.levelPath))
            {
                _levelConfigStatus = LocalizationManager.Get("tech.level_config_no_level_warn");
                return;
            }

            // 先将当前全局字段保存到选中的配置文件（确保包含最新的按键、顺序、时长等设置）
            SaveCurrentToProfile(SelectedTechniqueProfileIndex);

            // 提示用户输入配置名称
            string defaultName = $"关卡配置 - {Path.GetFileNameWithoutExtension(ADOBase.levelPath)}";
            var customName = _levelConfigNameState.input;

            bool success = LevelTechniqueManager.SaveConfigForCurrentLevel(
                string.IsNullOrWhiteSpace(customName) ? defaultName : customName);

            if (success)
            {
                _levelConfigStatus = "<color=green>" + LocalizationManager.Get("tech.level_config_saved") + "</color>";
                // 清空输入
                _levelConfigNameState.input = "";
                _levelConfigNameState.focused = false;
            }
            else
            {
                _levelConfigStatus = "<color=red>" + LocalizationManager.Get("tech.level_config_save_failed") + "</color>";
            }
        }

        private void DeleteLevelConfigFile()
        {
            if (LevelTechniqueManager.DeleteConfigForCurrentLevel())
            {
                _levelConfigStatus = "<color=yellow>" + LocalizationManager.Get("tech.level_config_deleted") + "</color>";
            }
            else
            {
                _levelConfigStatus = "<color=red>" + LocalizationManager.Get("tech.level_config_delete_failed") + "</color>";
            }
        }
    }
}
