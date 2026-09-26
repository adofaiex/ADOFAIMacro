using System.Runtime.InteropServices;

namespace ADOFAIMacro.Platform
{
    public static class Windows
    {
        [DllImport("Kernel32.dll")]
        public static extern void GetSystemTimePreciseAsFileTime(out long val);

        public static long GetFileTime()
        {
            GetSystemTimePreciseAsFileTime(out long res);
            return res + 50491123200_0000000;
        }
    }
}