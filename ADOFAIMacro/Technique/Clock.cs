using System;
using System.Runtime.CompilerServices;

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 高精度时钟：按设置在 QPC 与 DateTime.UtcNow 之间切换时间源，
    /// 并把时间源包装成委托，以消除热路径上的分支判断。
    /// </summary>
    internal static partial class MacroEngine
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetRawTicks() => _getTicksImpl();

        // 两个时间戳之间的真实流逝秒（按当前时间源的刻度换算）
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static double ElapsedSec(long from, long to)
            => (double)(to - from) * (_cachedHighPrecision ? 1e-7 : perfFreqInv);

        // 切换时间源委托（根据 HighPrecision 设置）
        private static void UpdateTicksDelegate()
        {
            _getTicksImpl = _cachedHighPrecision ? _getTicksHigh : _getTicksNormal;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static long GetTicks()
        {
            if (usePerfCounter && QueryPerformanceCounter(out long c)) return c;
            return DateTime.UtcNow.Ticks;
        }
    }
}
