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
    public class Settings : UnityModManager.ModSettings
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
        // 注：这里原先还有一个 public List<TechniqueSegment> techniqueSegments 字段。
        // 它从未被任何代码读写（面板与算法用的都是 TechniqueProfile 上那个同名字段），
        // 却会被 XmlSerializer 持久化成一个孤立的 <techniqueSegments /> 元素 —— 已删除。

        private class SegmentEditState
        {
            public string startInput = "";
            public bool startFocused;
            public string endInput = "";
            public bool endFocused;
            public string bpmInput = "";
            public bool bpmFocused;
        }
        private List<SegmentEditState> _segmentEditStates = [];
        private List<bool> _segmentExpanded = [];

        // ─────────────────────────────────────────────
        //  OnGUI 缓存（避免每个 GUI 事件重复分配）
        // ─────────────────────────────────────────────
        private string[] _tabNames = [];
        private Action[] _tabDraws = [];
        private int _tabCount;
        private readonly string[] _langPairCache = new string[2];
        private readonly string[] _modePairCache = new string[2];
        private readonly string[] _handPairCache = new string[2];
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

                // XmlSerializer 反序列化时会经过这里，而那时 LocalizationManager 尚未
                // Initialize（_modPath 仍为空）→ LoadLanguage 会白读一次文件并打出
                // "语言文件不存在" 告警。真正的语言加载统一由 Main.Load 在
                // Initialize 之后完成，这里只在已初始化时响应运行时切换。
                if (!ADOFAIMacro.Localization.LocalizationManager.IsInitialized) return;

                Main.Mod?.Logger.Log($"[Settings] 切换语言 → UseChinese={value}");
                bool success = value
                    ? ADOFAIMacro.Localization.LocalizationManager.LoadLanguage("zh-CN")
                    : ADOFAIMacro.Localization.LocalizationManager.LoadLanguage("en-US");
                Main.Mod?.Logger.Log($"[Settings] LoadLanguage 返回 {success}，当前语言 " +
                         $"{ADOFAIMacro.Localization.LocalizationManager.CurrentLanguage}");
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

        private (string input, bool focused) _adjustStepState = (string.Empty, false);
        private (string input, bool focused) _timeOffsetState = (string.Empty, false);

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
            catch { return 0; }
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

        private (string input, bool focused) _techBpmState = (string.Empty, false);
        private (string input, bool focused) _deathKeyDelayState = (string.Empty, false);

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

        private (string input, bool focused) _filteredKeysState = (string.Empty, false);
        private (string input, bool focused) _filteredAsyncKeysState = (string.Empty, false);

        public bool HighPrecisionTime;

        // ── 配置选项卡索引 ─────────────────────────────
        private int selectedCardIndex = 0;

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

        private (string input, bool focused) _speedChangeToleranceState = (string.Empty, false);

        private (string input, bool focused) _techLeftKeysState = (string.Empty, false);
        private (string input, bool focused) _techRightKeysState = (string.Empty, false);
        private (string input, bool focused) _techLeftPressTimesState = (string.Empty, false);
        private (string input, bool focused) _techRightPressTimesState = (string.Empty, false);
        private (string input, bool focused) _techLeftOrdersState = (string.Empty, false);
        private (string input, bool focused) _techRightOrdersState = (string.Empty, false);

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

        /// <summary>
        /// 当前选中的手法配置（安全访问器）。索引越界或列表为空时返回 null，
        /// 调用方必须判空。持久化值与列表可能不同步，直接索引会抛异常。
        /// </summary>
        public TechniqueProfile CurrentTechniqueProfile
        {
            get
            {
                if (_techniqueProfiles == null || _techniqueProfiles.Count == 0) return null;
                int i = _selectedTechniqueProfileIndex;
                if (i < 0 || i >= _techniqueProfiles.Count) i = 0;
                return _techniqueProfiles[i];
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
        //  按键代码映射
        // ─────────────────────────────────────────────
        private static readonly Dictionary<string, int> KeyCodeMap = new(StringComparer.OrdinalIgnoreCase)
        {
            {"A",0x41},{"B",0x42},{"C",0x43},{"D",0x44},{"E",0x45},{"F",0x46},
            {"G",0x47},{"H",0x48},{"I",0x49},{"J",0x4A},{"K",0x4B},{"L",0x4C},
            {"M",0x4D},{"N",0x4E},{"O",0x4F},{"P",0x50},{"Q",0x51},{"R",0x52},
            {"S",0x53},{"T",0x54},{"U",0x55},{"V",0x56},{"W",0x57},{"X",0x58},
            {"Y",0x59},{"Z",0x5A},
            {"0",0x30},{"1",0x31},{"2",0x32},{"3",0x33},{"4",0x34},
            {"5",0x35},{"6",0x36},{"7",0x37},{"8",0x38},{"9",0x39},
            {"F1",0x70},{"F2",0x71},{"F3",0x72},{"F4",0x73},{"F5",0x74},
            {"F6",0x75},{"F7",0x76},{"F8",0x77},{"F9",0x78},{"F10",0x79},
            {"F11",0x7A},{"F12",0x7B},
            {"SPACE",0x20},{"ENTER",0x0D},{"RETURN",0x0D},{"ESC",0x1B},
            {"TAB",0x09},{"SHIFT",0x10},{"CTRL",0x11},{"ALT",0x12},
            {"BACKSPACE",0x08},{"DELETE",0x2E},{"INSERT",0x2D},
            {"HOME",0x24},{"END",0x23},{"PAGEUP",0x21},{"PAGEDOWN",0x22},
            {"UP",0x26},{"DOWN",0x28},{"LEFT",0x25},{"RIGHT",0x27}
        };

        private int? GetKeyCodeFromString(string keyString)
        {
            if (string.IsNullOrEmpty(keyString)) return null;
            if (keyString.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                if (int.TryParse(keyString.Substring(2), System.Globalization.NumberStyles.HexNumber, null, out int hex))
                    return hex;
            if (KeyCodeMap.TryGetValue(keyString, out int code)) return code;
            return null;
        }

        // ─────────────────────────────────────────────
        //  OnGUI 主入口
        // ─────────────────────────────────────────────
        // 页签集合每帧重建，但写入缓存数组（委托由编译器缓存），
        // 替代原先每次事件的 List + Select + ToArray 分配链
        private void AddTab(string name, Action draw)
        {
            if (_tabCount == _tabNames.Length)
            {
                int newSize = _tabNames.Length == 0 ? 8 : _tabNames.Length * 2;
                Array.Resize(ref _tabNames, newSize);
                Array.Resize(ref _tabDraws, newSize);
            }
            _tabNames[_tabCount] = name;
            _tabDraws[_tabCount] = draw;
            _tabCount++;
        }

        public void OnGUI(UnityModManager.ModEntry modEntry)
        {
            UIUtils.InitializeStyles();

            _tabCount = 0;
            AddTab(Localization.LocalizationManager.Get("tab.language"), DrawLanguageCard);
            AddTab(Localization.LocalizationManager.Get("tab.macro"), DrawMainSwitchCard);

            if (Macro)
            {
                AddTab(Localization.LocalizationManager.Get("tab.key_settings"), DrawKeySettingsCard);
                AddTab(Localization.LocalizationManager.Get("tab.key_filter"), DrawKeyFilterCard);
                AddTab(Localization.LocalizationManager.Get("tab.offset_settings"), DrawOffsetSettingsCard);
                AddTab(Localization.LocalizationManager.Get("tab.other_settings"), DrawOtherSettingsCard);

                if (SimulateKeyPress)
                    AddTab(Localization.LocalizationManager.Get("tab.technique_simulation"), DrawTechniqueSimCard);
            }

            AddTab(Localization.LocalizationManager.Get("tab.update_log"), DrawUpdateLogCard);
            AddTab(Localization.LocalizationManager.Get("tab.author"), DrawAuthorCard);

            if (IsBeta)
                AddTab(Localization.LocalizationManager.Get("tab.beta"), DrawBetaCard);

            if (selectedCardIndex >= _tabCount) selectedCardIndex = 0;
            if (_tabNames.Length != _tabCount)
            {
                // 页签数量变化时才收缩（仅在开关切换瞬间发生一次）
                Array.Resize(ref _tabNames, _tabCount);
                Array.Resize(ref _tabDraws, _tabCount);
            }

            selectedCardIndex = UIUtils.M3SelectionGrid(selectedCardIndex, _tabNames, _tabCount, GUILayout.Height(30));
            GUILayout.Space(10);

            // 单张卡抛异常不应让整个 UMM 窗口失效（UMM 只有这一层 OnGUI）。
            // 注意：若异常发生在 Begin/End 布局对之间，GUILayout 栈可能已失衡；
            // 这里至少保证异常不逃逸到 UMM 绘制循环，并给出可见的错误信息。
            try
            {
                _tabDraws[selectedCardIndex]();
            }
            catch (Exception ex)
            {
                UnityEngine.Debug.LogError($"[ADOFAIMacro/Settings] 绘制选项卡失败: {ex}");
                try
                {
                    GUILayout.BeginVertical(UIUtils.CardStyle);
                    GUILayout.Label("UI Error", UIUtils.HeaderStyle);
                    UIUtils.DrawInfoBox($"{ex.GetType().Name}: {ex.Message}", isError: true);
                    GUILayout.EndVertical();
                }
                catch { /* 布局已失衡时忽略绘制错误 */ }
            }
        }

        // ─────────────────────────────────────────────
        //  语言卡
        // ─────────────────────────────────────────────
        private void DrawLanguageCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(Localization.LocalizationManager.Get("tab.language"), UIUtils.HeaderStyle);
            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            GUILayout.Label(Localization.LocalizationManager.Get("language.display_language"), UIUtils.LabelStyle, GUILayout.Width(150));
            _langPairCache[0] = Localization.LocalizationManager.Get("language.chinese");
            _langPairCache[1] = Localization.LocalizationManager.Get("language.english");
            int sel = Localization.LocalizationManager.IsChinese ? 0 : 1;
            int newSel = UIUtils.M3SelectionGrid(sel, _langPairCache, 2, GUILayout.Width(200));
            if (newSel != sel)
            {
                UnityEngine.Debug.Log($"[DrawLanguageCard] Switching: UseChinese = {newSel == 0}");
                UseChinese = newSel == 0;
            }
            GUILayout.EndHorizontal();
            GUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────
        //  宏主开关卡
        // ─────────────────────────────────────────────
        /// <summary>
        /// 安全重启控制器（让宏重建事件表）。
        /// UMM 面板在选歌/编辑器等场景也能打开，那里 ADOBase.controller 可能为 null，
        /// 直接 .Restart() 会在 OnGUI 里抛 NRE 打断整个面板绘制。
        /// 用 Unity 重载的 != null 判断（?. 对"已销毁但引用非空"的对象无效）。
        /// </summary>
        private static void RestartControllerIfAny()
        {
            var c = ADOBase.controller;
            if (c != null) c.Restart();
        }

        private void DrawMainSwitchCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(Localization.LocalizationManager.Get("tab.macro"), UIUtils.HeaderStyle);
            bool newMacro = UIUtils.M3Switch(Macro, Localization.LocalizationManager.Get("macro.enable_macro"));
            if (newMacro != Macro) { Macro = newMacro; RestartControllerIfAny(); }
            GUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────
        //  延迟设置卡
        // ─────────────────────────────────────────────
        private void DrawOffsetSettingsCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(Localization.LocalizationManager.Get("tab.offset_settings"), UIUtils.HeaderStyle);
            GUILayout.Space(2);

            EnableKeyAdjust = UIUtils.M3Switch(EnableKeyAdjust,
                Localization.LocalizationManager.Get("offset.allow_ctrl_adjust"));
            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            AdjustStep = UIUtils.M3HorizontalSliderWithLabelAndInput(
                Localization.LocalizationManager.Get("offset.adjust_step"), AdjustStep, 0.1f, 10f,
                ref _adjustStepState.input, ref _adjustStepState.focused, "F2", 120, 240, 60);
            GUILayout.EndHorizontal();

            GUILayout.Space(2);
            GUILayout.BeginHorizontal();
            TimeOffset = UIUtils.M3HorizontalSliderWithLabelAndInput(
                Localization.LocalizationManager.Get("offset.offset_ms"), TimeOffset, -100f, 100f,
                ref _timeOffsetState.input, ref _timeOffsetState.focused, "F2", 120, 240, 60);
            GUILayout.EndHorizontal();

            GUILayout.Space(2);
            EnableArrowTimeAdjust = UIUtils.M3Switch(EnableArrowTimeAdjust,
                Localization.LocalizationManager.Get("offset.allow_arrow_adjust"));
            GUILayout.Space(2);
            HighPrecisionTime = UIUtils.M3Switch(HighPrecisionTime,
                Localization.LocalizationManager.Get("offset.enable_high_precision"));
            GUILayout.Space(2);
            HighPrecisionAsync = UIUtils.M3Switch(HighPrecisionAsync,
                Localization.LocalizationManager.Get("offset.enable_high_precision_async"));
            GUILayout.Space(2);
            AutoCalibrateJudgement = UIUtils.M3Switch(AutoCalibrateJudgement,
                Localization.LocalizationManager.Get("offset.auto_calibrate"));
            GUILayout.Space(2);
            SuppressGcPauses = UIUtils.M3Switch(SuppressGcPauses,
                Localization.LocalizationManager.Get("other.suppress_gc"));
            GUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────
        //  按键设置卡
        // ─────────────────────────────────────────────
        private void DrawKeySettingsCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(Localization.LocalizationManager.Get("tab.key_settings"), UIUtils.HeaderStyle);
            GUILayout.Space(2);

            if (!Main.Settings.EnableTechniqueSimulation)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label(Localization.LocalizationManager.Get("key_settings.keys_comma_separated"),
                    UIUtils.LabelStyle, GUILayout.Width(180));
                MacroKeys = GUILayout.TextField(MacroKeys, UIUtils.TextFieldStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(2);
            bool newSim = UIUtils.M3Switch(SimulateKeyPress, Localization.LocalizationManager.Get("key_settings.key_simulation"));
            if (newSim != SimulateKeyPress) { SimulateKeyPress = newSim; RestartControllerIfAny(); }

            if (SimulateKeyPress)
            {
                GUILayout.Space(2);
                SkyHookMode = UIUtils.M3Switch(SkyHookMode,
                    Localization.LocalizationManager.Get("key_settings.use_advanced_input"));

                if (SkyHookMode)
                {
                    GUILayout.Space(2);
                    bool newVirtual = UIUtils.M3Switch(UseVirtualAsyncInput,
                        Localization.LocalizationManager.Get("key_settings.virtual_async_input"));
                    if (newVirtual != UseVirtualAsyncInput) UseVirtualAsyncInput = newVirtual;

                    if (UseVirtualAsyncInput)
                    {
                        GUILayout.Space(2);
                        bool newMirror = UIUtils.M3Switch(MirrorVirtualKeys,
                            Localization.LocalizationManager.Get("key_settings.mirror_virtual_keys"));
                        if (newMirror != MirrorVirtualKeys) MirrorVirtualKeys = newMirror;
                    }
                    GUILayout.Space(2);

                    GUILayout.Space(6);
                    Color oc = GUI.color;
                    GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.3f);
                    GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
                    GUI.color = oc;
                    GUILayout.Space(4);

                    GUILayout.BeginHorizontal();
                    GUILayout.Label(Localization.LocalizationManager.Get("key_settings.win_api_input_mode"),
                        UIUtils.LabelStyle, GUILayout.Width(150));
                    if (InputSystem.IsInitialized)
                    {
                        var actual = InputSystem.GetInputMode();
                        GUIStyle hintStyle = UIUtils.LabelStyleVariant(0.5f, 0.9f, 0.5f, 0.8f, 10);
                        string actualLabel = GetModeLabel(actual);
                        GUILayout.Label(string.Format(
                            Localization.LocalizationManager.Get("key_settings.mode_indicator"),
                            actualLabel), hintStyle);
                    }
                    GUILayout.EndHorizontal();
                    GUILayout.Space(4);

                    bool hasInject = !ADOFAIMacro.Macro.InputSystem.IsInitialized ||
                                     ADOFAIMacro.Macro.InputSystem.IsModeAvailable(ADOFAIMacro.Macro.InputMode.NtUserInjectKeyboard);
                    bool hasNtSend = !ADOFAIMacro.Macro.InputSystem.IsInitialized ||
                                     ADOFAIMacro.Macro.InputSystem.IsModeAvailable(ADOFAIMacro.Macro.InputMode.NtUserSendInput);

                    GUILayout.BeginHorizontal();
                    for (int i = 0; i < 4; i++)
                    {
                        bool available = i switch { 1 => hasInject, 2 => hasNtSend, _ => true };
                        string modeKey = i switch
                        {
                            0 => "key_mode.auto",
                            1 => "key_mode.ntinject",
                            2 => "key_mode.ntsendinput",
                            3 => "key_mode.sendinput",
                            _ => ""
                        };
                        string lbl = Localization.LocalizationManager.Get(modeKey);
                        if (!available)
                            lbl += Localization.LocalizationManager.Get("key_mode_not_supported");
                        if (GUILayout.Button(lbl, UIUtils.ButtonStyle, GUILayout.Height(24)) && available && InputMode != i)
                            InputMode = i;
                    }
                    GUILayout.EndHorizontal();

                    GUILayout.Space(4);
                    GUIStyle descStyle = UIUtils.LabelStyleVariant(0.75f, 0.75f, 0.75f, 0.8f, 10, wordWrap: true);
                    string descKey = InputMode switch
                    {
                        0 => "key_mode_desc.auto",
                        1 => "key_mode_desc.ntinject",
                        2 => "key_mode_desc.ntsendinput",
                        3 => "key_mode_desc.sendinput",
                        _ => ""
                    };
                    GUILayout.Label(Localization.LocalizationManager.Get(descKey), descStyle);
                }
            }
            GUILayout.EndVertical();
        }

        private string GetModeLabel(Macro.InputMode mode) => mode switch
        {
            ADOFAIMacro.Macro.InputMode.Auto => LocalizationManager.Get("key_mode.auto"),
            ADOFAIMacro.Macro.InputMode.NtUserInjectKeyboard => LocalizationManager.Get("key_mode.ntinject"),
            ADOFAIMacro.Macro.InputMode.NtUserSendInput => LocalizationManager.Get("key_mode.ntsendinput"),
            ADOFAIMacro.Macro.InputMode.SendInput => LocalizationManager.Get("key_mode.sendinput"),
            _ => mode.ToString()
        };

        // ─────────────────────────────────────────────
        //  按键过滤卡
        // ─────────────────────────────────────────────
        private void DrawKeyFilterCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(Localization.LocalizationManager.Get("tab.key_filter"), UIUtils.HeaderStyle);
            GUILayout.Space(2);

            bool newEnable = UIUtils.M3Switch(EnableKeyFilter, Localization.LocalizationManager.Get("filter.enable_filter"));
            if (newEnable != EnableKeyFilter) EnableKeyFilter = newEnable;

            if (EnableKeyFilter)
            {
                GUILayout.Space(6);
                Color oc = GUI.color;
                GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.3f);
                GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
                GUI.color = oc;
                GUILayout.Space(4);

                GUILayout.BeginHorizontal();
                GUILayout.Label(Localization.LocalizationManager.Get("filter.filter_mode"), UIUtils.LabelStyle, GUILayout.Width(100));
                _modePairCache[0] = Localization.LocalizationManager.Get("filter.blacklist_mode");
                _modePairCache[1] = Localization.LocalizationManager.Get("filter.whitelist_mode");
                int newMode = UIUtils.M3SelectionGrid(FilterMode, _modePairCache, 2, GUILayout.Width(200));
                if (newMode != FilterMode) FilterMode = newMode;
                GUILayout.EndHorizontal();

                GUILayout.Space(8);
                GUIStyle descStyle = UIUtils.LabelStyleVariant(0.7f, 0.7f, 0.7f, 0.8f, 11, wordWrap: true);
                string desc = FilterMode == 0
                    ? Localization.LocalizationManager.Get("filter.blacklist_desc")
                    : Localization.LocalizationManager.Get("filter.whitelist_desc");
                GUILayout.Label(desc, descStyle);
                GUILayout.Space(8);

                GUILayout.BeginHorizontal();
                GUILayout.Label(Localization.LocalizationManager.Get("filter.keys_comma_separated"),
                    UIUtils.LabelStyle, GUILayout.Width(140));
                string newFK = UIUtils.M3TextField(FilteredKeys,
                    ref _filteredKeysState.input, ref _filteredKeysState.focused,
                    UIUtils.TextFieldStyle, "TechnicalkeysNormal");
                if (newFK != FilteredKeys) FilteredKeys = newFK;
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label(Localization.LocalizationManager.Get("filter.async_keys_comma_separated"),
                    UIUtils.LabelStyle, GUILayout.Width(140));
                if (SkyHookMode)
                {
                    string newAK = UIUtils.M3TextField(FilteredAsyncKeys,
                        ref _filteredAsyncKeysState.input, ref _filteredAsyncKeysState.focused,
                        UIUtils.TextFieldStyle, "TechnicalkeysAsync");
                    if (newAK != FilteredAsyncKeys) FilteredAsyncKeys = newAK;
                }
                else
                {
                    GUIStyle dis = UIUtils.LabelStyleVariant(0.5f, 0.5f, 0.5f, 0.5f);
                    GUILayout.Label(Localization.LocalizationManager.Get("filter.requires_skyhook"), dis);
                }
                GUILayout.EndHorizontal();

                GUILayout.Space(8);
                GUILayout.Label(Localization.LocalizationManager.Get("filter.common_keys"), UIUtils.LabelStyle);
                GUILayout.Space(2);

                void QuickSet(string k) { FilteredKeys = k; if (SkyHookMode) FilteredAsyncKeys = k; }

                GUILayout.BeginHorizontal();
                if (GUILayout.Button(Localization.LocalizationManager.Get("common.f1") + "," + Localization.LocalizationManager.Get("common.f2") + ",F3,F4", UIUtils.ButtonStyle, GUILayout.ExpandWidth(true))) QuickSet("F1,F2,F3,F4");
                if (GUILayout.Button("F5,F6,F7,F8", UIUtils.ButtonStyle, GUILayout.ExpandWidth(true))) QuickSet("F5,F6,F7,F8");
                if (GUILayout.Button("F9,F10,F11,F12", UIUtils.ButtonStyle, GUILayout.ExpandWidth(true))) QuickSet("F9,F10,F11,F12");
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("A,S,D,F", UIUtils.ButtonStyle, GUILayout.ExpandWidth(true))) QuickSet("A,S,D,F");
                if (GUILayout.Button("J,K,L", UIUtils.ButtonStyle, GUILayout.ExpandWidth(true))) QuickSet("J,K,L");
                if (GUILayout.Button("1,2,3,4", UIUtils.ButtonStyle, GUILayout.ExpandWidth(true))) QuickSet("1,2,3,4");
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                if (GUILayout.Button("SPACE,ENTER,ESC", UIUtils.ButtonStyle, GUILayout.ExpandWidth(true))) QuickSet("SPACE,ENTER,ESC");
                if (GUILayout.Button("UP,DOWN,LEFT,RIGHT", UIUtils.ButtonStyle, GUILayout.ExpandWidth(true))) QuickSet("UP,DOWN,LEFT,RIGHT");
                if (GUILayout.Button("CTRL,ALT,SHIFT", UIUtils.ButtonStyle, GUILayout.ExpandWidth(true))) QuickSet("CTRL,ALT,SHIFT");
                GUILayout.EndHorizontal();

                GUILayout.Space(8);
                GUIStyle tipStyle = UIUtils.LabelStyleVariant(0.7f, 0.7f, 0.7f, 0.7f, 10, wordWrap: true);
                GUILayout.Label(Localization.LocalizationManager.Get("filter.tip"), tipStyle);
            }
            GUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────
        //  其他选项卡
        // ─────────────────────────────────────────────
        private void DrawOtherSettingsCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(Localization.LocalizationManager.Get("tab.other_settings"), UIUtils.HeaderStyle);
            GUILayout.Space(2);

            bool newDK = UIUtils.M3Switch(EnableDeathKey,
                Localization.LocalizationManager.Get("other.enable_death_key"));
            if (newDK != EnableDeathKey) EnableDeathKey = newDK;

            if (EnableDeathKey)
            {
                GUILayout.Space(6);
                Color oc = GUI.color;
                GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.3f);
                GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
                GUI.color = oc;
                GUILayout.Space(4);

                GUILayout.BeginHorizontal();
                DeathKeyDelay = UIUtils.M3HorizontalSliderWithLabelAndInput(
                    Localization.LocalizationManager.Get("other.delay_seconds"),
                    DeathKeyDelay, 0.1f, 30f,
                    ref _deathKeyDelayState.input, ref _deathKeyDelayState.focused, "F1", 140, 200, 60);
                GUILayout.EndHorizontal();
                GUILayout.Space(4);

                GUILayout.BeginHorizontal();
                GUILayout.Label(Localization.LocalizationManager.Get("other.key"), UIUtils.LabelStyle, GUILayout.Width(80));
                string newDKI = GUILayout.TextField(DeathKeyInput, UIUtils.TextFieldStyle, GUILayout.Width(100));
                if (newDKI != DeathKeyInput) DeathKeyInput = newDKI;
                GUILayout.Space(10);
                GUIStyle codeStyle = UIUtils.LabelStyleVariant(0.6f, 0.6f, 0.6f, 0.8f, 10);
                GUILayout.Label($"0x{DeathKeyCode:X2}", codeStyle);
                GUILayout.FlexibleSpace();
                GUILayout.EndHorizontal();

                GUILayout.Space(4);
                GUILayout.BeginHorizontal();
                foreach (string k in _deathKeyQuickSet)
                    if (GUILayout.Button(k, UIUtils.ButtonStyle, GUILayout.ExpandWidth(true)))
                        DeathKeyInput = k;
                GUILayout.EndHorizontal();

                GUILayout.Space(2);
                GUIStyle tipStyle = UIUtils.LabelStyleVariant(0.7f, 0.7f, 0.7f, 0.7f, 10, wordWrap: true);
                GUILayout.Label(Localization.LocalizationManager.Get("other.tip_enter_key"), tipStyle);
                GUILayout.Space(4);
            }

            ChangeNoFaillInPlay = UIUtils.M3Switch(ChangeNoFaillInPlay,
                Localization.LocalizationManager.Get("other.switch_nofaill"));
            ChangeJudementInPlay = UIUtils.M3Switch(ChangeJudementInPlay,
                Localization.LocalizationManager.Get("other.switch_judgement"));

            bool newLock = UIUtils.M3Switch(LockLevelEditor,
                Localization.LocalizationManager.Get("other.lock_level_editor"));
            if (LockLevelEditor != newLock)
            {
                LockLevelEditor = newLock;
                if (ADOBase.sceneName == GCNS.sceneEditor) RestartControllerIfAny();
            }

            BlockInputWhenUnfocused = UIUtils.M3Switch(BlockInputWhenUnfocused,
                Localization.LocalizationManager.Get("other.block_input_unfocused"));
            GUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────
        //  作者卡
        // ─────────────────────────────────────────────
        private static readonly string[] _deathKeyQuickSet = ["R", "SPACE", "ENTER", "F2", "ESC"];

        /// <summary>
        /// UI 展示用版本号（含 Beta 后缀）。
        /// 绝不改写 Main.Mod.Info.Version —— 那是防篡改校验的比对基准，
        /// 追加后缀会导致"关闭再启用"被误判为修改 Info.json 而退出游戏。
        /// </summary>
        private string UiVersionText()
        {
            string v = Main.Mod?.Info.Version ?? "1.3.0";
            if (IsBeta) v += $"\nBeta{BetaVersion}";
            return v;
        }

        private void DrawAuthorCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUIStyle authorStyle = UIUtils.LabelStyleVariant(0.8f, 0.8f, 0.8f, 0.8f, richText: true);

            GUILayout.BeginHorizontal();
            GUILayout.Label($"👤 {Main.Mod.Info.Author}", authorStyle);
            GUILayout.FlexibleSpace();
            string ver = UiVersionText().Replace('\n', ' ').Replace('\r', ' ');
            GUILayout.Label($"📦 {ver}", authorStyle);
            GUILayout.FlexibleSpace();
            string emailKey = Localization.LocalizationManager.IsChinese ? "author.email_chinese" : "author.email_english";
            GUILayout.Label($"📧 {Localization.LocalizationManager.Get(emailKey)}", authorStyle);
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            Color oc = GUI.color;
            GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.3f);
            GUILayout.Box("", GUILayout.Height(10), GUILayout.ExpandWidth(true));
            GUI.color = oc;
            GUILayout.Space(4);

            GUIStyle thanksStyle = UIUtils.LabelStyleVariant(0.7f, 0.7f, 0.7f, 0.4f, 9, alignment: TextAnchor.MiddleCenter);
            GUILayout.Label(string.Format(Localization.LocalizationManager.Get("author.thanks"), Main.Mod.Info.Id), thanksStyle);
            GUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────
        //  测试版卡
        // ─────────────────────────────────────────────
        private void DrawBetaCard()
        {
            GUILayout.BeginHorizontal();
            GUIStyle betaStyle = UIUtils.LabelStyleVariant(1f, 0.5f, 0f, 0.9f, 12,
                richText: true, fontStyle: FontStyle.Bold);
            GUILayout.Label(string.Format(Localization.LocalizationManager.Get("beta.warning_format"), BetaVersion), betaStyle);

            GUIStyle feedbackStyle = UIUtils.LabelStyleVariant(0.5f, 0.5f, 0.5f, 0.7f, 10, alignment: TextAnchor.MiddleRight);
            GUILayout.Space(4);
            GUILayout.Label(Localization.LocalizationManager.Get("beta.feedback_message"), feedbackStyle);
            GUILayout.EndHorizontal();
        }

        // ─────────────────────────────────────────────
        //  更新日志卡
        // ─────────────────────────────────────────────
        private Vector2 _updateLogScrollPos;
        private void DrawUpdateLogCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(Localization.LocalizationManager.Get("update_log.title"), UIUtils.HeaderStyle);
            GUILayout.Space(4);
            _updateLogScrollPos = GUILayout.BeginScrollView(_updateLogScrollPos, GUILayout.Height(150));
            GUIStyle logStyle = UIUtils.LabelStyleVariant(0.88f, 0.88f, 0.9f, 1f, wordWrap: true, richText: true);
            string ver = UiVersionText().Replace('\n', ' ').Replace('\r', ' ');
            GUILayout.Label(string.Format(Localization.LocalizationManager.Get("update_log.content"), ver), logStyle);
            GUILayout.EndScrollView();
            GUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────
        //  手法模拟主卡
        // ─────────────────────────────────────────────
        private void DrawTechniqueSimCard()
        {
            GUILayout.BeginVertical(UIUtils.CardStyle);
            GUILayout.Label(LocalizationManager.Get("tab.technique_simulation"), UIUtils.HeaderStyle);
            GUILayout.Space(2);
            GUILayout.Label(
                LocalizationManager.Get("tech.note_first_death"),
                UIUtils.HeaderStyle);
            GUILayout.Space(2);

            bool dllLoaded = TechniqueSimulator.IsDllLoaded();

            GUILayout.BeginHorizontal();
#if DEBUG
            GUILayout.BeginVertical();
            GUILayout.BeginHorizontal();
            GUIStyle verStyle = UIUtils.LabelStyleVariant(0.3f, 0.6f, 1f, 0.8f);
            string debugStatus = dllLoaded
                ? LocalizationManager.Get("tech.dll_available")
                : LocalizationManager.Get("tech.dll_unavailable");
            GUILayout.Label(string.Format(LocalizationManager.Get("tech.debug_mode"), debugStatus), verStyle);
            GUILayout.EndHorizontal();

            if (dllLoaded)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                bool newUseCpp = UIUtils.M3Switch(UseCppTechniqueInDebug, LocalizationManager.Get("tech.use_cpp_version"));
                if (newUseCpp != UseCppTechniqueInDebug)
                {
                    UseCppTechniqueInDebug = newUseCpp;
                    ADOFAIMacro.Macro.Macro.Log($"[Macro] 手法模拟切换到{(newUseCpp ? "C++" : "C#")}版本");
                }
                GUILayout.EndHorizontal();
            }
            else
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(20);
                GUIStyle warnStyle = UIUtils.LabelStyleVariant(0.8f, 0.5f, 0.5f, 0.8f, 10);
                GUILayout.Label(LocalizationManager.Get("tech.dll_unavailable_notice"), warnStyle);
                GUILayout.EndHorizontal();
            }
            GUILayout.EndVertical();
#else
            string dllStatus = dllLoaded
                ? LocalizationManager.Get("tech.dll_available")
                : LocalizationManager.Get("tech.dll_unavailable");
            if (!dllLoaded) Main.Settings.EnableTechniqueSimulation = false;
            GUIStyle statusStyle = dllLoaded
                ? UIUtils.LabelStyleVariant(0.3f, 0.8f, 0.3f, 1f)
                : UIUtils.LabelStyleVariant(0.8f, 0.3f, 0.3f, 1f);
            GUILayout.Label(dllStatus, statusStyle);
#endif
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            // ── 关卡特定配置管理 ─────────────────────────────
            GUILayout.BeginVertical();
            GUILayout.Label(LocalizationManager.Get("tech.level_config"), UIUtils.LabelStyle);
            GUILayout.Space(2);

            // 显示当前关卡状态（结果缓存，见 GetLevelConfigStatusText）
            string levelStatus = GetLevelConfigStatusText();
            GUIStyle statusStyle1 = UIUtils.LabelStyleVariant(0.7f, 0.7f, 0.7f, 0.8f, 10, richText: true);
            GUILayout.Label(levelStatus, statusStyle1);
            GUILayout.Space(4);

            // 自动加载开关和操作按钮
            GUILayout.BeginHorizontal();
            GUILayout.Space(10);
            LevelConfigAutoLoad = UIUtils.M3Switch(LevelConfigAutoLoad,
                LocalizationManager.Get("tech.level_config_auto_load"));
            GUILayout.FlexibleSpace();

            if (GUILayout.Button(LocalizationManager.Get("tech.level_config_load"), UIUtils.ButtonStyle, GUILayout.Width(80)))
            {
                LevelTechniqueManager.ReloadCurrentLevelConfig();
            }
            if (GUILayout.Button(LocalizationManager.Get("tech.level_config_save"), UIUtils.ButtonStyle, GUILayout.Width(80)))
            {
                SaveLevelConfigToFile();
            }
            if (GUILayout.Button(LocalizationManager.Get("tech.level_config_delete"), UIUtils.ButtonStyle, GUILayout.Width(80)))
            {
                DeleteLevelConfigFile();
            }
            GUILayout.EndHorizontal();

            // 自定义配置名称输入（可选）
            GUILayout.BeginHorizontal();
            GUILayout.Space(10);
            GUILayout.Label(LocalizationManager.Get("tech.config_name_optional") + ":", UIUtils.LabelStyle, GUILayout.Width(100));
            string newName = UIUtils.M3TextField(_levelConfigNameState.input,
                ref _levelConfigNameState.input, ref _levelConfigNameState.focused,
                UIUtils.TextFieldStyle, "LevelConfigName");
            if (newName != _levelConfigNameState.input)
            {
                _levelConfigNameState.input = newName;
            }
            GUILayout.EndHorizontal();

            // 状态消息
            if (!string.IsNullOrEmpty(_levelConfigStatus))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Space(10);
                GUIStyle statusMsgStyle = UIUtils.LabelStyleVariant(0.6f, 0.6f, 0.6f, 0.9f, 9, richText: true);
                GUILayout.Label(_levelConfigStatus, statusMsgStyle);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(4);
            GUILayout.EndVertical();
            // ────────────────────────────────────────────────────────

            bool oldEnabled = GUI.enabled;
#if !DEBUG
            if (!dllLoaded) GUI.enabled = false;
#endif
            bool newEnable = UIUtils.M3Switch(EnableTechniqueSimulation,
                LocalizationManager.Get("tech.enable_technique"));
            GUI.enabled = oldEnabled;

#if !DEBUG
            if (dllLoaded && newEnable != EnableTechniqueSimulation)
                EnableTechniqueSimulation = newEnable;
#else
            if (newEnable != EnableTechniqueSimulation)
                EnableTechniqueSimulation = newEnable;
#endif

#if !DEBUG
            if (!dllLoaded)
            {
                GUILayout.Space(4);
                GUIStyle warnStyle = UIUtils.LabelStyleVariant(0.8f, 0.3f, 0.3f, 0.8f, 11, wordWrap: true);
                GUILayout.Label(LocalizationManager.Get("tech.dll_missing_notice"), warnStyle);
                GUILayout.EndVertical();
                return;
            }
#endif
            if (!EnableTechniqueSimulation) { GUILayout.EndVertical(); return; }

            if (_techniqueProfiles.Count == 0)
            {
                _techniqueProfiles.Add(new TechniqueProfile());
                LoadTechniqueProfileToFields(0);
            }

            GUILayout.Space(6);
            Color orig = GUI.color;
            GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.3f);
            GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
            GUI.color = orig;
            GUILayout.Space(4);

            // ── 配置管理 ────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.Get("tech.profile_name"), UIUtils.LabelStyle, GUILayout.Width(100));
            string profileName = GUILayout.TextField(_techniqueProfiles[SelectedTechniqueProfileIndex].name,
                UIUtils.TextFieldStyle, GUILayout.ExpandWidth(true));
            if (profileName != _techniqueProfiles[SelectedTechniqueProfileIndex].name)
                _techniqueProfiles[SelectedTechniqueProfileIndex].name = profileName;

            if (GUILayout.Button(LocalizationManager.Get("tech.new"), UIUtils.ButtonStyle, GUILayout.Width(60)))
            {
                _techniqueProfiles.Add(_techniqueProfiles[SelectedTechniqueProfileIndex].Clone());
                SelectedTechniqueProfileIndex = _techniqueProfiles.Count - 1;
            }
            if (GUILayout.Button(LocalizationManager.Get("tech.delete"), UIUtils.ButtonStyle, GUILayout.Width(60)))
            {
                if (_techniqueProfiles.Count > 1)
                {
                    _techniqueProfiles.RemoveAt(SelectedTechniqueProfileIndex);
                    SelectedTechniqueProfileIndex = Mathf.Clamp(SelectedTechniqueProfileIndex - 1, 0, _techniqueProfiles.Count - 1);
                }
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(4);

            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.Get("tech.select_profile"), UIUtils.LabelStyle, GUILayout.Width(100));
            if (_profileNameCache.Length != _techniqueProfiles.Count)
                _profileNameCache = new string[_techniqueProfiles.Count];
            for (int i = 0; i < _profileNameCache.Length; i++)
                _profileNameCache[i] = _techniqueProfiles[i].name;
            int newIdx = UIUtils.M3SelectionGrid(SelectedTechniqueProfileIndex, _profileNameCache,
                Mathf.Min(_profileNameCache.Length, 4), GUILayout.ExpandWidth(true));
            if (newIdx != SelectedTechniqueProfileIndex) SelectedTechniqueProfileIndex = newIdx;
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            // ── 起始手 ─────────────────────────────────────
            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.Get("tech.starting_hand"), UIUtils.LabelStyle, GUILayout.Width(140));
            _handPairCache[0] = LocalizationManager.Get("tech.left_hand");
            _handPairCache[1] = LocalizationManager.Get("tech.right_hand");
            int newHand = UIUtils.M3SelectionGrid(TechniqueHandPreference, _handPairCache, 2, GUILayout.Width(200));
            if (newHand != TechniqueHandPreference)
            {
                TechniqueHandPreference = newHand;
                _techniqueProfiles[SelectedTechniqueProfileIndex].handPreference = newHand;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(8);

            // ── 全局 BPM 阈值 ────────────────────────────────
            GUILayout.BeginHorizontal();
            TechniqueBpmLimit = UIUtils.M3HorizontalSliderWithLabelAndInput(
                LocalizationManager.Get("tech.global_bpm_limit"),
                TechniqueBpmLimit, 50f, 2000f,
                ref _techBpmState.input, ref _techBpmState.focused,
                "F0", 140, 220, 70);
            GUILayout.EndHorizontal();

            GUIStyle tipStyle = UIUtils.LabelStyleVariant(0.7f, 0.7f, 0.7f, 0.8f, 10, wordWrap: true);
            GUILayout.Label(LocalizationManager.Get("tech.bpm_explanation"), tipStyle);

            GUILayout.Space(4);
            GUILayout.BeginHorizontal();
            float newSct = UIUtils.M3HorizontalSliderWithLabelAndInput(
                LocalizationManager.Get("tech.speed_change_tolerance"),
                SpeedChangeTolerance, 0f, 0.5f,
                ref _speedChangeToleranceState.input, ref _speedChangeToleranceState.focused,
                "F2", 200, 240, 60);
            if (newSct != SpeedChangeTolerance)
            {
                // 与"起始手"同理：滑条改动必须同步写回当前配置。旧实现只写全局字段，
                // 切换配置时 LoadTechniqueProfileToFields 会把旧值灌回来（改动被静默还原），
                // 而 SaveCurrentToProfile 又会用新值覆盖，两处状态不一致。
                SpeedChangeTolerance = newSct;
                _techniqueProfiles[SelectedTechniqueProfileIndex].speedChangeTolerance = newSct;
            }
            GUILayout.EndHorizontal();
            GUILayout.Space(2);
            GUILayout.Label(LocalizationManager.Get("tech.speed_change_tolerance_desc"), tipStyle);

            // ── 变速分段 ─────────────────────────────────────
            DrawTechniqueSegments();

            GUILayout.Space(8);

            // ── 全局左右手按键 ────────────────────────────────
            GUILayout.Label($"── {_handPairCache[0]} ──", UIUtils.LabelStyle);
            GUILayout.Space(2);

            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.Get("tech.left_keys"), UIUtils.LabelStyle, GUILayout.Width(80));
            string newLK = UIUtils.M3TextField(TechLeftHandKeys, ref _techLeftKeysState.input, ref _techLeftKeysState.focused, UIUtils.TextFieldStyle, "TechLeftKeys");
            if (newLK != TechLeftHandKeys) { TechLeftHandKeys = newLK; _techniqueProfiles[SelectedTechniqueProfileIndex].leftHandKeys = newLK; }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.Get("tech.left_press_ratio"), UIUtils.LabelStyle, GUILayout.Width(80));
            string newLP = UIUtils.M3TextField(TechLeftHandPressTimes, ref _techLeftPressTimesState.input, ref _techLeftPressTimesState.focused, UIUtils.TextFieldStyle, "TechLeftPressTimes");
            if (newLP != TechLeftHandPressTimes) { TechLeftHandPressTimes = newLP; _techniqueProfiles[SelectedTechniqueProfileIndex].leftHandPressTimes = newLP; }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.Get("tech.left_orders"), UIUtils.LabelStyle, GUILayout.Width(80));
            string newLO = UIUtils.M3TextField(TechLeftHandOrders, ref _techLeftOrdersState.input, ref _techLeftOrdersState.focused, UIUtils.TextFieldStyle, "TechLeftOrders");
            if (newLO != TechLeftHandOrders) { TechLeftHandOrders = newLO; _techniqueProfiles[SelectedTechniqueProfileIndex].leftHandOrders = newLO; }
            GUILayout.EndHorizontal();

            GUILayout.Space(6);

            GUILayout.Label($"── {_handPairCache[1]} ──", UIUtils.LabelStyle);
            GUILayout.Space(2);

            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.Get("tech.right_keys"), UIUtils.LabelStyle, GUILayout.Width(80));
            string newRK = UIUtils.M3TextField(TechRightHandKeys, ref _techRightKeysState.input, ref _techRightKeysState.focused, UIUtils.TextFieldStyle, "TechRightKeys");
            if (newRK != TechRightHandKeys) { TechRightHandKeys = newRK; _techniqueProfiles[SelectedTechniqueProfileIndex].rightHandKeys = newRK; }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.Get("tech.right_press_ratio"), UIUtils.LabelStyle, GUILayout.Width(80));
            string newRP = UIUtils.M3TextField(TechRightHandPressTimes, ref _techRightPressTimesState.input, ref _techRightPressTimesState.focused, UIUtils.TextFieldStyle, "TechRightPressTimes");
            if (newRP != TechRightHandPressTimes) { TechRightHandPressTimes = newRP; _techniqueProfiles[SelectedTechniqueProfileIndex].rightHandPressTimes = newRP; }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            GUILayout.Label(LocalizationManager.Get("tech.right_orders"), UIUtils.LabelStyle, GUILayout.Width(80));
            string newRO = UIUtils.M3TextField(TechRightHandOrders, ref _techRightOrdersState.input, ref _techRightOrdersState.focused, UIUtils.TextFieldStyle, "TechRightOrders");
            if (newRO != TechRightHandOrders) { TechRightHandOrders = newRO; _techniqueProfiles[SelectedTechniqueProfileIndex].rightHandOrders = newRO; }
            GUILayout.EndHorizontal();

            // ── 预设 ──────────────────────────────────────────
            GUILayout.Space(6);
            GUILayout.Label(LocalizationManager.Get("tech.presets"), UIUtils.LabelStyle);
            GUILayout.BeginHorizontal();
            if (GUILayout.Button(LocalizationManager.Get("tech.preset_dfjk"), UIUtils.ButtonStyle, GUILayout.ExpandWidth(true)))
            {
                TechLeftHandKeys = "D,F"; TechRightHandKeys = "J,K";
                _techniqueProfiles[SelectedTechniqueProfileIndex].leftHandKeys = "D,F";
                _techniqueProfiles[SelectedTechniqueProfileIndex].rightHandKeys = "J,K";
            }
            if (GUILayout.Button(LocalizationManager.Get("tech.preset_dsjk"), UIUtils.ButtonStyle, GUILayout.ExpandWidth(true)))
            {
                TechLeftHandKeys = "D,S"; TechRightHandKeys = "J,K";
                _techniqueProfiles[SelectedTechniqueProfileIndex].leftHandKeys = "D,S";
                _techniqueProfiles[SelectedTechniqueProfileIndex].rightHandKeys = "J,K";
            }
            if (GUILayout.Button(LocalizationManager.Get("tech.preset_asdfjkl"), UIUtils.ButtonStyle, GUILayout.ExpandWidth(true)))
            {
                TechLeftHandKeys = "A,S,D,F"; TechRightHandKeys = "J,K,L";
                _techniqueProfiles[SelectedTechniqueProfileIndex].leftHandKeys = "A,S,D,F";
                _techniqueProfiles[SelectedTechniqueProfileIndex].rightHandKeys = "J,K,L";
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(4);
            GUILayout.Label(LocalizationManager.Get("tech.order_format"), tipStyle);

            GUILayout.EndVertical();
        }

        // ─────────────────────────────────────────────
        //  变速分段 UI（可折叠 + 按键覆盖）
        // ─────────────────────────────────────────────
        private void DrawTechniqueSegments()
        {
            var currentProfile = _techniqueProfiles[SelectedTechniqueProfileIndex];
            var segments = currentProfile.techniqueSegments;

            // 同步辅助列表长度
            while (_segmentEditStates.Count < segments.Count) _segmentEditStates.Add(new SegmentEditState());
            while (_segmentEditStates.Count > segments.Count) _segmentEditStates.RemoveAt(_segmentEditStates.Count - 1);
            while (_segmentExpanded.Count < segments.Count) _segmentExpanded.Add(false);
            while (_segmentExpanded.Count > segments.Count) _segmentExpanded.RemoveAt(_segmentExpanded.Count - 1);

            GUILayout.Space(6);
            GUILayout.Label(LocalizationManager.Get("tech.speed_segments"), UIUtils.HeaderStyle);

            GUIStyle tipStyle = UIUtils.LabelStyleVariant(0.65f, 0.65f, 0.65f, 0.8f, 10, wordWrap: true);
            GUILayout.Label(LocalizationManager.Get("tech.segment_inherit"), tipStyle);
            GUILayout.Space(4);

            for (int i = 0; i < segments.Count; i++)
            {
                var seg = segments[i];
                var state = _segmentEditStates[i];

                // ── 标题行（折叠/展开 + 删除）───────────────
                GUILayout.BeginHorizontal();

                string arrow = _segmentExpanded[i] ? "▼" : "▶";
                string overrideMk = seg.HasKeyOverride ? " ✎" : "";
                string segLabel = string.Format(LocalizationManager.Get("tech.segment_label"),
                    arrow, i + 1, seg.startFloor, seg.endFloor, seg.bpmLimit, overrideMk);

                if (GUILayout.Button(segLabel, UIUtils.ButtonStyle, GUILayout.ExpandWidth(true)))
                    _segmentExpanded[i] = !_segmentExpanded[i];

                if (GUILayout.Button("✕", UIUtils.ButtonStyle, GUILayout.Width(36)))
                {
                    segments.RemoveAt(i);
                    _segmentEditStates.RemoveAt(i);
                    _segmentExpanded.RemoveAt(i);
                    i--;
                    continue;
                }
                GUILayout.EndHorizontal();

                if (!_segmentExpanded[i]) continue;

                // ── 展开内容 ─────────────────────────────────
                GUILayout.BeginVertical();
                GUILayout.Space(2);

                // 地板范围
                GUILayout.BeginHorizontal();
                GUILayout.Space(16);
                GUILayout.Label(LocalizationManager.Get("tech.segment_start_floor"), UIUtils.LabelStyle, GUILayout.Width(80));
                string newStart = UIUtils.M3TextField(seg.startFloor.ToString(),
                    ref state.startInput, ref state.startFocused,
                    UIUtils.TextFieldStyle, $"SegStart_{i}", GUILayout.Width(60));
                if (int.TryParse(newStart, out int sv)) seg.startFloor = sv;

                GUILayout.Label(" ~ ", UIUtils.LabelStyle, GUILayout.Width(20));
                GUILayout.Label(LocalizationManager.Get("tech.segment_end_floor"), UIUtils.LabelStyle, GUILayout.Width(80));
                string newEnd = UIUtils.M3TextField(seg.endFloor.ToString(),
                    ref state.endInput, ref state.endFocused,
                    UIUtils.TextFieldStyle, $"SegEnd_{i}", GUILayout.Width(60));
                if (int.TryParse(newEnd, out int ev)) seg.endFloor = ev;
                GUILayout.EndHorizontal();

                // BPM 阈值
                GUILayout.BeginHorizontal();
                GUILayout.Space(16);
                seg.bpmLimit = UIUtils.M3HorizontalSliderWithLabelAndInput(
                    LocalizationManager.Get("tech.bpm_limit"),
                    seg.bpmLimit, 50f, 2000f,
                    ref state.bpmInput, ref state.bpmFocused,
                    "F0", 80, 160, 60,
                    controlId: $"SegBpm_{i}");
                GUILayout.EndHorizontal();

                // 分隔线
                GUILayout.Space(4);
                Color oc = GUI.color;
                GUI.color = new Color(0.5f, 0.5f, 0.5f, 0.2f);
                GUILayout.Box("", GUILayout.Height(1), GUILayout.ExpandWidth(true));
                GUI.color = oc;
                GUILayout.Space(4);

                // ── 按键覆盖（可选）──────────────────────────
                GUILayout.BeginHorizontal();
                GUILayout.Space(16);
                GUILayout.Label(LocalizationManager.Get("tech.left_keys"), UIUtils.LabelStyle, GUILayout.Width(80));
                seg.leftHandKeys = GUILayout.TextField(seg.leftHandKeys, UIUtils.TextFieldStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Space(16);
                GUILayout.Label(LocalizationManager.Get("tech.right_keys"), UIUtils.LabelStyle, GUILayout.Width(80));
                seg.rightHandKeys = GUILayout.TextField(seg.rightHandKeys, UIUtils.TextFieldStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Space(16);
                GUILayout.Label(LocalizationManager.Get("tech.left_press_ratio"), UIUtils.LabelStyle, GUILayout.Width(80));
                seg.leftHandPressTimes = GUILayout.TextField(seg.leftHandPressTimes, UIUtils.TextFieldStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Space(16);
                GUILayout.Label(LocalizationManager.Get("tech.right_press_ratio"), UIUtils.LabelStyle, GUILayout.Width(80));
                seg.rightHandPressTimes = GUILayout.TextField(seg.rightHandPressTimes, UIUtils.TextFieldStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Space(16);
                GUILayout.Label(LocalizationManager.Get("tech.left_orders"), UIUtils.LabelStyle, GUILayout.Width(80));
                seg.leftHandOrders = GUILayout.TextField(seg.leftHandOrders, UIUtils.TextFieldStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Space(16);
                GUILayout.Label(LocalizationManager.Get("tech.right_orders"), UIUtils.LabelStyle, GUILayout.Width(80));
                seg.rightHandOrders = GUILayout.TextField(seg.rightHandOrders, UIUtils.TextFieldStyle, GUILayout.ExpandWidth(true));
                GUILayout.EndHorizontal();

                GUILayout.Space(4);
                GUILayout.EndVertical();
            }

            GUILayout.Space(2);
            if (GUILayout.Button(LocalizationManager.Get("tech.add_segment"), UIUtils.ButtonStyle))
                segments.Add(new TechniqueSegment { bpmLimit = Main.Settings.TechniqueBpmLimit });
        }

        // ─────────────────────────────────────────────
        //  持久化
        // ─────────────────────────────────────────────
        public void OnSaveGUI(UnityModManager.ModEntry modEntry) => Save(modEntry);
        public override void Save(UnityModManager.ModEntry modEntry) => Save(this, modEntry);

        public static Settings Load(UnityModManager.ModEntry modEntry)
        {
            Settings s = Load<Settings>(modEntry);
            s.NormalizeAfterLoad();
            return s;
        }

        /// <summary>
        /// 反序列化后的自愈处理。
        /// SelectedTechniqueProfileIndex 与 TechniqueProfiles 在 XML 里是两个独立元素，
        /// 可能不同步（手工编辑 XML、删除配置后未保存、元素顺序差异、旧版本遗留值）。
        /// 而面板里有十余处直接 `_techniqueProfiles[SelectedTechniqueProfileIndex]`，
        /// 以及 Macro / LevelTechniqueManager 的同类索引 —— 越界会抛
        /// ArgumentOutOfRangeException（在 OnGUI 里会把整个面板打挂，在
        /// BuildHitEvents 里会被 catch 吞掉导致手法模拟静默无输出）。
        /// </summary>
        private void NormalizeAfterLoad()
        {
            if (_techniqueProfiles == null) _techniqueProfiles = [];
            if (_techniqueProfiles.Count == 0)
            {
                _techniqueProfiles.Add(new TechniqueProfile());
                _selectedTechniqueProfileIndex = 0;
            }
            else if (_selectedTechniqueProfileIndex < 0 || _selectedTechniqueProfileIndex >= _techniqueProfiles.Count)
            {
                _selectedTechniqueProfileIndex = 0;
            }
        }

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