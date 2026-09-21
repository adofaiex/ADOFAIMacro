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
        private static bool _autoLoadEnabled = true;

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

                if (_autoLoadEnabled)
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
                Main.Mod?.Logger.Log($"[LevelTechnique] CheckAndLoadLevelConfig error: {ex.Message}");
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
                    Main.Mod?.Logger.Log($"[LevelTechnique] 关卡配置不存在: {configPath}");
                    CacheVersion++;
                    return;
                }

                string json = File.ReadAllText(configPath);
                var config = JsonConvert.DeserializeObject<Settings.TechniqueProfile>(json);

                if (config != null)
                {
                    _loadedConfigs[levelPath] = config;
                    Main.Mod?.Logger.Log($"[LevelTechnique] 已加载关卡配置: {config.name} ({config.techniqueSegments?.Count ?? 0} 个分段)");
                }
                CacheVersion++;
            }
            catch (Exception ex)
            {
                Main.Mod?.Logger.Log($"[LevelTechnique] 加载配置失败: {ex.Message}");
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
                // 更新当前选中的配置
                var current = settings.TechniqueProfiles[settings.SelectedTechniqueProfileIndex];
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
                Main.Mod?.Logger.Log("[LevelTechnique] 无法保存配置：没有有效的关卡路径");
                return false;
            }

            try
            {
                var settings = Main.Settings;
                var currentProfile = settings.TechniqueProfiles[settings.SelectedTechniqueProfileIndex];
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
                Main.Mod?.Logger.Log($"[LevelTechnique] 已保存关卡配置到: {configPath}");
                return true;
            }
            catch (Exception ex)
            {
                Main.Mod?.Logger.Log($"[LevelTechnique] 保存配置失败: {ex.Message}");
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
                    Main.Mod?.Logger.Log($"[LevelTechnique] 已删除关卡配置: {configPath}");
                    return true;
                }
                return false;
            }
            catch (Exception ex)
            {
                Main.Mod?.Logger.Log($"[LevelTechnique] 删除配置失败: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// 启用/禁用自动加载
        /// </summary>
        public static void SetAutoLoad(bool enabled)
        {
            _autoLoadEnabled = enabled;
        }

        /// <summary>
        /// 强制重新加载当前关卡配置
        /// </summary>
        public static void ReloadCurrentLevelConfig()
        {
            if (!string.IsNullOrEmpty(_lastCheckedLevelPath))
            {
                _loadedConfigs.Remove(_lastCheckedLevelPath!); // _lastCheckedLevelPath 已检查过非 null
                LoadConfigForLevel(_lastCheckedLevelPath!);

                // 手动加载也应用到 Settings，让用户能在 UI 中看到配置项
                if (_loadedConfigs.TryGetValue(_lastCheckedLevelPath!, out var config) && config != null)
                    ApplyConfigToSettings(config);
            }
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
