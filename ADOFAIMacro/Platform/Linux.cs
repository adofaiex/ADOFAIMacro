using System.Runtime.InteropServices;

namespace ADOFAIMacro.Platform
{
    public static class Linux
    {
        [DllImport("librt.so.1", SetLastError = true)]
        private static extern int clock_gettime(int clock_id, out Timespec tp);
        [StructLayout(LayoutKind.Sequential)]
        public struct Timespec
        {
            public long tv_sec;   // 秒
            public long tv_nsec;  // 纳秒
        }

        public static long GetFileTime()
        {
            clock_gettime(0, out Timespec res);
            return res.tv_sec * 10000000 + res.tv_nsec / 100 + 62135596800_0000000;
        }
    }
}
