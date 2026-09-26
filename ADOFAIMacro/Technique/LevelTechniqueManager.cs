using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;
using Newtonsoft.Json;

#nullable enable

namespace ADOFAIMacro.Macro
{
    /// <summary>
    /// 关卡特定手法配置管理器
    /// 负责保存/加载每个关卡的手手法模拟配置
    /// </summary>
    internal static class LevelTechniqueManager
    {
        private const string CONFIG_EXTENSION = ".adofaimacro.json";
        private static string? _lastCheckedLevelPath = null;
        private static readonly Dictionary<string, Settings.TechniqueProfile?> _loadedConfigs = new();

        /// <summary>
        /// 配置状态版本号：加载/保存/删除/关卡切换时递增。
        /// Settings UI 用它做 GetLevelConfigStatusText 的缓存失效，避免每帧 File.Exists。
        /// </summary>
        internal static int CacheVersion { get; private set; }

        /// <summary>
        /// 检测关卡变化并自动加载配置（应在关卡切换时调用，例如 Patches 或 scnGame Load 事件）
        /// </summary>
        public static void CheckAndLoadLevelConfig()
        {
            try
            {
                string? levelPath = ADOBase.levelPath;

                // 如果没有关卡路径或文件不存在，不处理
                if (string.IsNullOrEmpty(levelPath) || !File.Exists(levelPath))
                {
                    if (_lastCheckedLevelPath != null) CacheVersion++;
                    _lastCheckedLevelPath = null;
                    return;
                }

                // 关卡未变化，跳过
                if (levelPath == _lastCheckedLevelPath)
                {
                    return;
                }

                _lastCheckedLevelPath = levelPath;
                CacheVersion++;

                // 单一事实来源：直接读 Settings 的开关。旧实现在这里维护了一份独立的
                // _autoLoadEnabled + 从未被任何地方调用的 SetAutoLoad()，两处状态可能不一致。
                if (Main.Settings.LevelConfigAutoLoad)
                {
                    LoadConfigForLevel(levelPath);
                }
            }
            catch (NullReferenceException)
            {
                // ADOBase 尚未初始化，忽略
            }
            catch (Exception ex)
            {
                Macro.Log($"[LevelTechnique] CheckAndLoadLevelConfig error: {ex.Message}");
            }
        }

        /// <summary>
        /// 立即重置检查状态（用于关卡切换时强制重新检测）
        /// </summary>
        public static void ResetCheckState()
        {
            _lastCheckedLevelPath = null;
        }

        /// <summary>
        /// 为当前关卡加载配置（如果存在）
        /// </summary>
        private static void LoadConfigForLevel(string levelPath)
        {
            try
            {
                string configPath = GetConfigPath(levelPath);
                if (!File.Exists(configPath))
                {
                    Macro.Log($"[LevelTechnique] 关卡配置不存在: {configPath}");
                    CacheVersion++;
                    return;
                }

                string json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<Settings.TechniqueProfile>(json);

                if (config != null)
                {
                    SanitizeConfig(config);
                    _loadedConfigs[levelPath] = config;
                    Macro.Log($"[LevelTechnique] 已加载关卡配置: {config.name} ({config.techniqueSegments?.Count ?? 0} 个分段)");
                }
                CacheVersion++;
            }
            catch (Exception ex)
            {
                Macro.Log($"[LevelTechnique] 加载配置失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 将配置应用到 Settings
        /// </summary>
        public static void ApplyConfigToSettings(Settings.TechniqueProfile config)
        {
            if (config == null) return;

            var settings = Main.Settings;

            // 应用到全局字段（这些会被用作默认值）
            settings.TechLeftHandKeys = config.leftHandKeys;
            settings.TechRightHandKeys = config.rightHandKeys;
            settings.TechLeftHandOrders = config.leftHandOrders;
            settings.TechRightHandOrders = config.rightHandOrders;
            settings.TechLeftHandPressTimes = config.leftHandPressTimes;
            settings.TechRightHandPressTimes = config.rightHandPressTimes;
            settings.TechniqueHandPreference = config.handPreference;
            settings.SpeedChangeTolerance = config.speedChangeTolerance;

            // 应用到当前配置列表
            if (settings.TechniqueProfiles.Count == 0)
            {
                settings.TechniqueProfiles.Add(new Settings.TechniqueProfile
                {
                    name = config.name,
                    leftHandKeys = config.leftHandKeys,
                    rightHandKeys = config.rightHandKeys,
                    leftHandOrders = config.leftHandOrders,
                    rightHandOrders = config.rightHandOrders,
                    leftHandPressTimes = config.leftHandPressTimes,
                    rightHandPressTimes = config.rightHandPressTimes,
                    handPreference = config.handPreference,
                    speedChangeTolerance = config.speedChangeTolerance,
                    techniqueSegments = CloneTechniqueSegments(config.techniqueSegments)
                });
                settings.SelectedTechniqueProfileIndex = 0;
            }
            else
            {
                // 更新当前选中的配置（用安全访问器：持久化索引可能越界）
                var current = settings.CurrentTechniqueProfile;
                if (current == null) return;
                current.leftHandKeys = config.leftHandKeys;
                current.rightHandKeys = config.rightHandKeys;
                current.leftHandOrders = config.leftHandOrders;
                current.rightHandOrders = config.rightHandOrders;
                current.leftHandPressTimes = config.leftHandPressTimes;
                current.rightHandPressTimes = config.rightHandPressTimes;
                current.handPreference = config.handPreference;
                current.speedChangeTolerance = config.speedChangeTolerance;

                // 如果配置文件有分段，则覆盖当前分段（包括空列表表示清除）
                if (config.techniqueSegments != null)
                {
                    current.techniqueSegments = CloneTechniqueSegments(config.techniqueSegments);
                }
                // 如果配置文件没有分段（null），保持当前分段不变
            }
        }

        /// <summary>
        /// 夹紧来自磁盘的配置值。`*.adofaimacro.json` 允许被手工编辑，
        /// 而 bpmLimit ≤ 0 会让时间片折算陷入死循环（见 Macro.GetAdviceBpm 与
        /// C++ GetAdviceBpm 的折叠循环），必须在进入算法前拦掉。
        /// 面板滑条本身的量程就是 [50, 2000]，这里保持一致。
        /// </summary>
        private static void SanitizeConfig(Settings.TechniqueProfile config)
        {
            if (config.techniqueSegments == null) return;
            foreach (var seg in config.techniqueSegments)
            {
                if (seg.bpmLimit < 50f) seg.bpmLimit = 50f;
                else if (seg.bpmLimit > 2000f) seg.bpmLimit = 2000f;
            }
        }

        /// <summary>
        /// 深拷贝手法分段列表
        /// </summary>
        private static List<Settings.TechniqueSegment> CloneTechniqueSegments(List<Settings.TechniqueSegment>? segments)
        {
            if (segments == null) return new List<Settings.TechniqueSegment>();

            return segments.Select(s => new Settings.TechniqueSegment
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

        /// <summary>
        /// 保存当前 Settings 中的手法配置到关卡目录
        /// </summary>
        public static bool SaveConfigForCurrentLevel(string? customName = null)
        {
            string? levelPath = ADOBase.levelPath;
            if (string.IsNullOrEmpty(levelPath) || !File.Exists(levelPath))
            {
                Macro.Log("[LevelTechnique] 无法保存配置：没有有效的关卡路径");
                return false;
            }

            try
            {
                var settings = Main.Settings;
                var currentProfile = settings.CurrentTechniqueProfile;
                if (currentProfile == null)
                {
                    Macro.Log("[LevelTechnique] 没有可用的手法配置，无法保存关卡配置");
                    return false;
                }
                var profile = new Settings.TechniqueProfile
                {
                    name = customName ?? $"关卡配置 - {Path.GetFileNameWithoutExtension(levelPath)}",
                    leftHandKeys = currentProfile.leftHandKeys,
                    rightHandKeys = currentProfile.rightHandKeys,
                    leftHandOrders = currentProfile.leftHandOrders,
                    rightHandOrders = currentProfile.rightHandOrders,
                    leftHandPressTimes = currentProfile.leftHandPressTimes,
                    rightHandPressTimes = currentProfile.rightHandPressTimes,
                    handPreference = currentProfile.handPreference,
                    speedChangeTolerance = currentProfile.speedChangeTolerance,
                    techniqueSegments = CloneTechniqueSegments(currentProfile.techniqueSegments)
                };

                string json = JsonConvert.SerializeObject(profile, Formatting.Indented);
                string configPath = GetConfigPath(levelPath);
                File.WriteAllText(configPath, json);

                _loadedConfigs[levelPath] = profile;
                CacheVersion++;
                Macro.Log($"[LevelTechnique] 已保存关卡配置到: {configPath}");
                return true;
            }
            catch (Exception ex)
            {
                Macro.Log($"[LevelTechnique] 保存配置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 获取关卡配置文件路径
        /// </summary>
        private static string GetConfigPath(string levelPath)
        {
            string levelDir = Path.GetDirectoryName(levelPath) ?? "";
            string levelName = Path.GetFileNameWithoutExtension(levelPath);
            return Path.Combine(levelDir, levelName + CONFIG_EXTENSION);
        }

        /// <summary>
        /// 检查当前关卡是否有保存的配置
        /// </summary>
        public static bool HasConfigForCurrentLevel()
        {
            string? levelPath = ADOBase.levelPath;
            if (string.IsNullOrEmpty(levelPath)) return false;

            string configPath = GetConfigPath(levelPath);
            return File.Exists(configPath);
        }

        /// <summary>
        /// 删除当前关卡的配置
        /// </summary>
        public static bool DeleteConfigForCurrentLevel()
        {
            string? levelPath = ADOBase.levelPath;
            if (string.IsNullOrEmpty(levelPath)) return false;

            try
            {
                string configPath = GetConfigPath(levelPath);
                if (File.Exists(configPath))
                {
                    File.Delete(configPath);
                    _loadedConfigs.Remove(levelPath!); // levelPath 已检查过非 null
                    CacheVersion++;
                    Macro.Log($"[LevelTechnique] 已删除关卡配置: {configPath}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Macro.Log($"[LevelTechnique] 删除配置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 强制重新加载当前关卡配置（面板「加载」按钮）。
        /// 注意：不能只依赖 _lastCheckedLevelPath —— 关闭"自动从关卡目录加载"后
        /// CheckAndLoadLevelConfig 不会被调用，该字段恒为 null，旧实现在这种情况下
        /// 整个方法直接空转（按钮点了没反应），且即便加载成功 GetCurrentLevelConfig()
        /// 也因路径未记录而返回 null，配置根本不会生效。
        /// </summary>
        public static void ReloadCurrentLevelConfig()
        {
            string? levelPath = _lastCheckedLevelPath;
            if (string.IsNullOrEmpty(levelPath))
            {
                // ADOBase.levelPath = scnGame.instance.levelPath，scnGame 实例不存在时
                // （编辑器试玩等）会抛 NullReferenceException；本方法由面板按钮调用，
                // 不能让它把异常抛进 GUI 绘制。
                try { levelPath = ADOBase.levelPath; }
                catch (NullReferenceException) { levelPath = null; }
            }

            if (string.IsNullOrEmpty(levelPath) || !File.Exists(levelPath))
            {
                Macro.Log("[LevelTechnique] 无法加载配置：没有有效的关卡路径");
                return;
            }

            _lastCheckedLevelPath = levelPath;
            CacheVersion++;
            _loadedConfigs.Remove(levelPath!);
            LoadConfigForLevel(levelPath!);

            // 手动加载也应用到 Settings，让用户能在 UI 中看到配置项
            if (_loadedConfigs.TryGetValue(levelPath!, out var config) && config != null)
                ApplyConfigToSettings(config);
        }

        /// <summary>
        /// 获取当前关卡配置（如果已加载）
        /// </summary>
        public static Settings.TechniqueProfile? GetCurrentLevelConfig()
        {
            if (string.IsNullOrEmpty(_lastCheckedLevelPath)) return null;
            return _loadedConfigs.TryGetValue(_lastCheckedLevelPath!, out var config) ? config : null;
        }
    }
}
