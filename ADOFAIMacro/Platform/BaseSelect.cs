using System;

namespace ADOFAIMacro.Platform
{
    public static class BaseSelect
    {
        static unsafe BaseSelect()
        {
            if (UnityEngine.Application.platform is UnityEngine.RuntimePlatform.WindowsPlayer or UnityEngine.RuntimePlatform.WindowsServer or UnityEngine.RuntimePlatform.WindowsEditor)
                GetFileTime = &Windows.GetFileTime;
            else if (UnityEngine.Application.platform is UnityEngine.RuntimePlatform.LinuxPlayer or UnityEngine.RuntimePlatform.LinuxServer or UnityEngine.RuntimePlatform.LinuxEditor)
                GetFileTime = &Linux.GetFileTime;
            else
                GetFileTime = &Base;
        }
        public static long Base() => DateTime.Now.Ticks;
        public static unsafe delegate* managed<long> GetFileTime;
    }
}
