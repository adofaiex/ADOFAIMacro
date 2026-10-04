using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace ADOFAIMacro.Core
{
    /// <summary>
    /// 游戏内通知栏助手：借用游戏原生 Notification 通知栏（完整资源、原生动画），
    /// 通过反射调用其私有 SetupNotification。用法照搬 Spectre（SpectreState.TriggerMessage）：
    ///   1. 设置文本与图标（Info=绿色对勾 / Warning=红色感叹号）；
    ///   2. 清掉通知按钮的点击监听（避免玩家点通知触发游戏逻辑）；
    ///   3. 临时关闭通知栏所有 Graphic 的 raycastTarget（通知不再挡点击），动画播完恢复；
    ///   4. 反射调 SetupNotification(delay, scale, reset) 播放滑入滑出。
    /// 调用点全部在主线程（UI 按钮回调 / Harmony postfix），不做后台线程泵。
    /// </summary>
    internal static class GameNotifier
    {
        internal enum NotifType { Info, Warning }

        private static MethodInfo _setupMethod;

        internal static void Info(string message, float dur = 3f) => Trigger(message, dur, NotifType.Info);
        internal static void Warning(string message, float dur = 5f) => Trigger(message, dur, NotifType.Warning);

        private static void Trigger(string message, float dur, NotifType type)
        {
            if (string.IsNullOrEmpty(message)) return;
            try
            {
                var n = Notification.instance;
                if (n == null || n.text == null || n.bar == null) return;

                n.text.text = message.TrimEnd('\n');
                if (n.icon != null)
                {
                    n.icon.enabled = true;
                    switch (type)
                    {
                        case NotifType.Warning:
                            if (n.warningIcon != null) n.icon.sprite = n.warningIcon;
                            n.icon.color = new Color(1f, 0.2f, 0.2f);
                            break;
                        default:
                            if (n.completeIcon != null) n.icon.sprite = n.completeIcon;
                            n.icon.color = new Color(0.2f, 0.7215686f, 0.3921569f);
                            break;
                    }
                }

                if (n.button != null) n.button.onClick.RemoveAllListeners();

                // 通知栏显示期间不挡点击：关 raycastTarget，动画结束后恢复
                var graphics = n.bar.GetComponentsInChildren<Graphic>(true);
                var states = graphics.Select(g => g.raycastTarget).ToArray();
                foreach (var g in graphics) g.raycastTarget = false;
                n.StartCoroutine(RestoreGraphics(graphics, states, dur));

                if (_setupMethod == null)
                {
                    _setupMethod = typeof(Notification).GetMethod(
                        "SetupNotification",
                        BindingFlags.Instance | BindingFlags.NonPublic,
                        null, new[] { typeof(float), typeof(float), typeof(bool) }, null);
                }
                _setupMethod?.Invoke(n, new object[] { dur, 1f, true });
            }
            catch (Exception ex)
            {
                Main.Log($"[Macro-Notify] 通知失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static IEnumerator RestoreGraphics(Graphic[] graphics, bool[] states, float dur)
        {
            // 0.5 滑入 + 0.5 前 delay + dur 停留 + 0.3 滑出（与 SetupNotification 内部时间轴对齐）
            yield return new WaitForSecondsRealtime(0.5f + 0.5f + dur + 0.3f);
            for (int i = 0; i < graphics.Length; i++)
                if (graphics[i] != null) graphics[i].raycastTarget = states[i];
        }
    }
}
