using ADOFAIMacro.Macro;
using HarmonyLib;
using SA.GoogleDoc;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;
using UnityModManagerNet;
using static UnityModManagerNet.UnityModManager;

#nullable enable

namespace ADOFAIMacro
{
#if DEBUG
    [EnableReloading]
#endif
    public static class Main
    {
        public static UnityModManager.ModEntry? Mod { get; private set; }
        public static Harmony? Harmony { get; private set; }
        public static Settings Settings { get; private set; } = null!;
        private static GameObject? _uiObject;
        public static bool Load(UnityModManager.ModEntry modEntry)
        {
            Mod = modEntry;
            modEntry.Logger.Log("[ADOFAIMacro] Build: 2026-08-17-2 (focus-cache-fix)");
            Settings = Settings.Load(modEntry);

            // 初始化本地化系统
            Localization.LocalizationManager.Initialize(modEntry.Path);
            // 根据 Settings.UseChinese 加载对应语言
            if (Settings.UseChinese)
                Localization.LocalizationManager.LoadLanguage("zh-CN");
            else
                Localization.LocalizationManager.LoadLanguage("en-US");

            // 手动初始化 InputSystem
            if (InputSystem.Initialize())
            {
                modEntry.Logger.Log("[InputSystem] 初始化成功");
            }
            else
            {
                modEntry.Logger.Log("[InputSystem] 初始化失败");
            }

            if (InputSystem.IsUsingNtFunctions())
            {
                modEntry.Logger.Log("[InputSystem] 当前使用 NT 内核函数");
            }
            else
            {
                modEntry.Logger.Log("[InputSystem] 当前使用传统输入模拟");
            }

            if (TechniqueSimulator.LoadTechniqueDll())
            {
                modEntry.Logger.Log("[TechniqueSimulator] 技巧模拟器 DLL 加载成功");

            }
            else
            {
                modEntry.Logger.Log("[TechniqueSimulator] 技巧模拟器 DLL 加载失败");
            }

            modEntry.OnToggle = OnToggle;
            modEntry.OnGUI = Settings.OnGUI;
            modEntry.OnSaveGUI = Settings.OnSaveGUI;
            modEntry.OnUnload = Unload;

            Harmony = new Harmony(modEntry.Info.Id);
            return true;
        }

        public static bool Unload(UnityModManager.ModEntry modEntry)
        {
            Harmony?.UnpatchAll(modEntry.Info.Id);
            Harmony = null;

            // 释放依赖对象：_uiObject 挂着 ShowText 且是 DontDestroyOnLoad，
            // 不随场景卸载，必须显式销毁（原注释就写了要做这件事，但一直没做）。
            DestroyOverlay();

            return true;
        }

        /// <summary>销毁游戏内覆盖层（幂等）。</summary>
        private static void DestroyOverlay()
        {
            if (_uiObject == null) return;
            UnityEngine.Object.Destroy(_uiObject);
            _uiObject = null;
        }

        public static bool IsDebugAssembly()
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            var attribute = assembly.GetCustomAttribute<DebuggableAttribute>();
            if (attribute == null)
                return false;
            return attribute.IsJITOptimizerDisabled;
        }

        private static bool OnToggle(UnityModManager.ModEntry modEntry, bool value)
        {
            if (value)
            {
#if DEBUG
                // None

#else
                if (modEntry.Info.Version != "1.3.0" || modEntry.Info.Id != "ADOFAIMacro" || modEntry.Info.DisplayName != "ADOFAI Macro" || modEntry.Info.Author != "HitMargin" || modEntry.Info.AssemblyName != "ADOFAIMacro.dll" || modEntry.Info.EntryMethod != "ADOFAIMacro.Main.Load")
                {
                    Mod?.Logger.Error("Modifying the Info.json file is NOT allowed!");
                    Application.Quit();
                }
                /*
                var creplayMod = UnityModManager.modEntries.FirstOrDefault(m => m.Info.Id.Equals("CreplayMod", StringComparison.OrdinalIgnoreCase));
                if (creplayMod != null && creplayMod.Enabled)  // 若使用旧版 UnityModManager，可能是 .Enabled
                {
                    Mod?.Logger.Error("Detected CreplayMod, which is incompatible. Exiting...");
                    Application.Quit();
                }
                */
#endif
                if (UnityModManager.modEntries.FirstOrDefault(m => m.Info.Id.Equals("BaseMacro", StringComparison.OrdinalIgnoreCase)) != null)
                {
                    Mod?.Logger.Error("Detected BaseMacro, which is incompatible. Exiting...");
                    Application.Quit();
                }
                // ⚠️ 绝不可改写 Mod.Info.Version：上面的防篡改校验拿它与硬编码的
                // "1.3.0" 比对。IsBeta 由程序集 Revision 决定（当前 AssemblyVersion
                // 1.3.0.30 → Revision=30 → IsBeta 恒为真），旧实现每次启用都追加
                // "\nBeta30"，导致【关闭再启用】时校验失败 → Application.Quit()，
                // 用户却看到"Info.json 被修改"的误报。
                // Beta / 调试后缀只在 UI 展示层拼接（见 Settings.UiVersionText）。
                if (IsDebugAssembly() && Mod?.Info.DisplayName?.Contains("(Debug)") == false)
                    Mod?.Info.DisplayName += " <color=grey>(Debug)</color>";

                IsEnabled = true;
                Harmony?.PatchAll(Assembly.GetExecutingAssembly());

                // 关闭模组时 OnToggle(false) 会 FreeLibrary 手法模拟 DLL；再次启用必须
                // 重载，否则手法模拟在本次游戏会话内永久失效（IsDllLoaded() 恒 false，
                // Release 面板还会据此强制关闭"启用手法模拟"）。
                if (!TechniqueSimulator.IsDllLoaded() && !TechniqueSimulator.LoadTechniqueDll())
                    Mod?.Logger.Log("[TechniqueSimulator] 技巧模拟器 DLL 重新加载失败");
                if (_uiObject == null)
                {
                    _uiObject = new GameObject("MacroText");
                    _uiObject.AddComponent<ShowText>();
                    UnityEngine.Object.DontDestroyOnLoad(_uiObject);
                    //TrySetWindowTitle($"{GetClean(modEntry.Info.DisplayName)}, {GetClean(modEntry.Info.Version)}, {modEntry.Info.Author}");
                }
            }
            else
            {
                IsEnabled = false;
                Harmony?.UnpatchAll(modEntry.Info.Id);
                TrySetWindowTitle(null);
                // 卸载补丁后 Macro.Update 不会再被调用，必须在这里把
                // requireHolding 交还游戏（否则禁用宏后长按地板判定一直是"不需要按住"）
                ADOFAIMacro.Macro.Macro.RestoreHoldBehavior();
                InputSystem.EmergencyStop();
                TechniqueSimulator.Unload();
                // 覆盖层必须销毁：ShowText 的 _showMacroText 跟的是"启用宏"开关而不是
                // 模组启用状态，禁用模组后它会继续在屏幕上画"宏已开启！"，并且
                // 每帧继续推 DSPTimeSimulater.Update()。销毁后再次启用会走
                // _uiObject == null 分支重建。
                DestroyOverlay();
            }
            return true;
        }

        public static string GetClean(string version)
        {
            if (version.Contains("<color="))
            {
                int startIndex = version.IndexOf('>') + 1;
                int endIndex = version.LastIndexOf('<');
                if (startIndex > 0 && endIndex > startIndex)
                {
                    return version.Substring(startIndex, endIndex - startIndex);
                }
            }
            return version;
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowText(IntPtr hWnd, string lpString);

        private static void TrySetWindowTitle(string? title)
        {
            try
            {
                // 获取当前进程
                Process currentProcess = Process.GetCurrentProcess();

                // 等待主窗口句柄可用
                for (int i = 0; i < 10 && currentProcess.MainWindowHandle == IntPtr.Zero; i++)
                {
                    currentProcess.Refresh();
                    System.Threading.Thread.Sleep(100);
                }

                IntPtr hwnd = currentProcess.MainWindowHandle;

                if (hwnd != IntPtr.Zero)
                {
                    string newTitle = $"A Dance of Fire and Ice";
                    if (title != null)
                    {
                        // 修复：将换行符替换为空格
                        string cleanTitle = title.Replace('\n', ' ').Replace('\r', ' ');
                        newTitle = $"A Dance of Fire and Ice - {cleanTitle}";
                    }

                    if (SetWindowText(hwnd, newTitle))
                    {
                        Mod?.Logger.Log($"成功设置窗口标题: {newTitle}");
                    }
                    else
                    {
                        Mod?.Logger.Log("设置窗口标题失败");
                    }
                }
                else
                {
                    Mod?.Logger.Log("未获取到主窗口句柄");
                }
            }
            catch (Exception ex)
            {
                Mod?.Logger.Log($"设置窗口标题异常: {ex.Message}");
            }
        }
        public static bool IsEnabled { get; internal set; }
    }
}
