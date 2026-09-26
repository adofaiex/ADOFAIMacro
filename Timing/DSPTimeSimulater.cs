/*
* Copy in Cover Mod
* Assembly: Cover.dll
* NameSpace: Cover._Core
* Categoty: StaticClass
* Name: DSPTimeSimulater
* Flag: public auto ansi abstract sealed beforefieldinit flag(200000)
* Extends: [mscorlib]System.Object
*/
/*
* 本文件基于 [AsyncInputOptimize] 的代码修改
* 原始项目: [https://github.com/adofaiex/AsyncInputOptimize]
* 原始许可证: GPL-3.0
*/
using ADOFAIMacro.Platform;
using UnityEngine;


namespace ADOFAIMacro.Macro
{
    public static unsafe class DSPTimeSimulater
    {
        private readonly struct DSPLimit(double e, double m, double n)
        {
            /// <summary>
            /// Error Limit
            /// </summary>
            public readonly double E = e;
            /// <summary>
            /// Max Limit
            /// </summary>
            public readonly double M = m;
            /// <summary>
            /// Normal Limit
            /// </summary>
            public readonly double N = n;
        }
        public static class Config
        {
            public static double ErrorHitDeltaMultiply = 4;
            public static double MaxHitDeltaMultiply = 2;
        }

        private const int MAX_ERROR_COUNT = 8;
        private const int SINGLE_ROUND = 120;
        private const double SECOND_2_TICK = 10000000.0;

        // private static readonly ModsTagLib.Unity.VirtualModLog m_log = new("Cover.DSPTimeSimulater");
        private static double m_lastTime;
        private static double m_dspTimeDynamic;
        private static double m_dspTime;
        private static double m_dspDeltaTime;
        private static int m_dspDeltaTimeCounter;
        private static int m_dspErrorCounter;
        private static bool m_safe;

        private static bool _dspMainCached;
        private static double _dspMainValue;

        // Unity Time 状态主线程缓存：工作线程（WorkerLoop → GetDSPTimeAsFileTime）禁止
        // 直接调 Time.*——Unity 原生 API 非主线程调用会崩（实测崩溃栈：
        // WorkerLoop → GetPreciseDeltaTime → Time.get_captureFramerate/get_captureDeltaTime）
        private static volatile int _cachedCaptureFramerate;
        private static double _cachedTimeScale = 1.0;

        private static double GetTimeScaleAsDouble() => _cachedTimeScale;
        private static void CacheUnityTimeState()
        {
            // 仅主线程调用（Update 首行）
            _cachedCaptureFramerate = Time.captureFramerate;
            _cachedTimeScale = ((long)(Time.timeScale * 1E6 + 0.1)) * 1E-6;
        }
        private static double UpdatePreciseDeltaTime()
        {
            long l = BaseSelect.GetFileTime();
            double res = _cachedCaptureFramerate != 0
                ? 1.0 / _cachedCaptureFramerate
                : (l - m_lastTime) / SECOND_2_TICK * GetTimeScaleAsDouble();
            m_lastTime = l;
            return res;
        }
        private static double GetPreciseDeltaTime()
        {
            return _cachedCaptureFramerate > 0
                ? 1.0 / _cachedCaptureFramerate
                : (BaseSelect.GetFileTime() - m_lastTime) / SECOND_2_TICK * GetTimeScaleAsDouble();
        }
        private static DSPLimit GetDSPTimeDeltasLimit()
        {
            if (!_dspMainCached)
            {
                AudioConfiguration ac = AudioSettings.GetConfiguration();
                _dspMainValue = ac.dspBufferSize / (double)ac.sampleRate;
                _dspMainCached = true;
            }
            return new(_dspMainValue * Config.ErrorHitDeltaMultiply, _dspMainValue * Config.MaxHitDeltaMultiply, _dspMainValue);
        }

        internal static void Update()
        {
            // 主线程每帧刷新 Unity Time 缓存（工作线程只读缓存，见 CacheUnityTimeState 注释）
            CacheUnityTimeState();
            // 更新
            m_dspTime += UpdatePreciseDeltaTime();
            m_dspDeltaTime += AudioSettings.dspTime - m_dspTime;
            m_dspDeltaTimeCounter++;
            double avg = m_dspDeltaTime / m_dspDeltaTimeCounter;
            m_dspTimeDynamic = m_dspTime + avg;
            DSPLimit limit = GetDSPTimeDeltasLimit();

            if (avg > limit.E || avg < (-limit.E))
            {
                // m_log.ERROR("dspTime: " + AudioSettings.dspTime + ", m_dspTime: " + m_dspTime + "(" + (AudioSettings.dspTime - m_dspTime) + " | " + avg + ")");
                m_safe = false;
                m_dspTime = AudioSettings.dspTime - limit.N * GetTimeScaleAsDouble(); ;
                m_dspDeltaTime = 0;
                m_dspDeltaTimeCounter = 0;
                m_dspErrorCounter = 0;
            }
            else if (avg > limit.M || avg < (-limit.M))
            {
                // m_log.WARN("dspTime: " + AudioSettings.dspTime + ", m_dspTime: " + m_dspTime + "(" + (AudioSettings.dspTime - m_dspTime) + " | " + avg + ")");
                m_safe = false;
                m_dspErrorCounter++;
                if (m_dspErrorCounter >= MAX_ERROR_COUNT)
                {
                    m_dspTime = AudioSettings.dspTime - limit.N * GetTimeScaleAsDouble();
                    m_dspDeltaTime = 0;
                    m_dspDeltaTimeCounter = 0;
                    m_dspErrorCounter = 0;
                }
            }
            else if (m_dspDeltaTimeCounter >= SINGLE_ROUND)
            {
                if (!m_safe && (avg > limit.N || avg < (-limit.N)))
                {
                    // m_log.INFO("offset fix (" + avg + ")");
                    m_dspTime += avg;
                }
                else
                {
                    m_safe = true;
                }
                m_dspErrorCounter = 0;
                m_dspDeltaTime /= 10;
                m_dspDeltaTimeCounter /= 10;
            }
        }
        public static double GetDynamicDSPTime()
        {
            return m_dspTimeDynamic;
        }
        public static double GetDSPTime()
        {
            return m_dspTime + GetPreciseDeltaTime();
        }
        public static long GetDSPTimeAsFileTime()
        {
            double curr_dsp = m_dspTime + GetPreciseDeltaTime();
            return (long)(curr_dsp * SECOND_2_TICK);
        }
        public static (double, int) GetDebugContent()
        {
            return (m_dspDeltaTime, m_dspDeltaTimeCounter);
        }
    }
}