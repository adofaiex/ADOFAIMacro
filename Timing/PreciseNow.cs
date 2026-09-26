using System;
using ADOFAIMacro.Platform;

namespace ADOFAIMacro.Macro
{
    /// <summary>
    /// 精确本地时间（.NET ticks，与 DateTime.Now.Ticks 同域）。
    ///
    /// 时间域依据（2026-08-16 对游戏本体的逆向结论）：
    ///  - skyhook.dll 导入 GetTimeZoneInformation + GetLocalTime + SystemTimeToFileTime，
    ///    原生事件时间戳为"本地域 Unix 时间"；
    ///  - SkyHookEvent.GetTimeInTicks() = 本地Unix秒*1e7 + ns/100 + Unix纪元ticks
    ///    ≈ DateTime.Now.Ticks（本地 .NET ticks）；
    ///  - scrConductor 的 currFrameTick = DateTime.Now.Ticks —— 与事件同域。
    /// 因此：conductor 高精度补丁（Patches.Update_1）与虚拟异步键盘
    /// （VirtualAsyncInput 合成事件时间戳）必须使用同一套"精确本地 ticks"，
    /// 即 GetSystemTimePreciseAsFileTime（UTC，精确）+ 本地时区偏移（缓存）。
    /// </summary>
    internal static unsafe class PreciseNow
    {
        private const long UnixEpochTicks = 621355968000000000L; // 1970-01-01 的 .NET ticks

        // 偏移缓存：DateTime 域查询较贵，偏移只在 DST 切换时变化，60s 重算一次。
        // 两个调用线程（conductor 主线程 / 宏工作线程）并发刷新只会重复计算同值，无害。
        private static long _localUtcOffsetTicks;
        private static int _localUtcOffsetStamp;
        private static volatile bool _localUtcOffsetValid;

        /// <summary>当前本地时间（.NET ticks），时钟源为 GetSystemTimePreciseAsFileTime。</summary>
        public static long LocalTicks()
        {
            int nowMs = Environment.TickCount;
            if (!_localUtcOffsetValid || unchecked(nowMs - _localUtcOffsetStamp) >= 60_000)
            {
                _localUtcOffsetValid = true;
                _localUtcOffsetStamp = nowMs;
                _localUtcOffsetTicks = TimeZoneInfo.Local.GetUtcOffset(DateTime.UtcNow).Ticks;
            }
            return BaseSelect.GetFileTime() + _localUtcOffsetTicks;
        }

        /// <summary>Unix 纪元 ticks 常量（供事件时间戳拆分使用）。</summary>
        public static long UnixEpoch => UnixEpochTicks;

        /// <summary>
        /// 拆分精确本地时间为 skyhook 事件格式（本地域 Unix 秒 + 纳秒）。
        /// GetTimeInTicks() 重建后与 LocalTicks() 严格一致（100ns 精度无损）。
        /// </summary>
        public static void SplitLocalUnix(long localTicks, out long timeSec, out uint timeSubsecNano)
        {
            long unixLocal = localTicks - UnixEpochTicks;
            long sec = Math.DivRem(unixLocal, 10_000_000, out long rem);
            timeSec = sec;
            timeSubsecNano = (uint)(rem * 100); // rem &lt; 1e7 ticks → *100 后 &lt; 1e9 ns，uint 无溢出
        }
    }
}
