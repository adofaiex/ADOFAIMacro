using System;
using System.Runtime.InteropServices;
using System.Threading;

class FreezeTest {
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr LoadLibrary(string path);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern IntPtr GetProcAddress(IntPtr h, string name);
    [DllImport("kernel32.dll")]
    static extern bool FreeLibrary(IntPtr h);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void SetConfig(ref Config cfg);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate IntPtr BuildEvents(double[] et, int[] pt, int[] fi, double[] sm, int count, double bpm, double speed, out int outCount);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    delegate void FreeEvents(IntPtr p);

    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public struct Segment {
        public int startFloor, endFloor; public double bpmLimit;
        public IntPtr leftKeys; public int leftKeyCount;
        public IntPtr rightKeys; public int rightKeyCount;
        public IntPtr leftKeyOrders; public IntPtr leftOrderLengths; public int leftOrderCounts;
        public IntPtr rightKeyOrders; public IntPtr rightOrderLengths; public int rightOrderCounts;
        public IntPtr leftPressTimes; public IntPtr rightPressTimes;
        public int hasKeyOverride;
    }
    [StructLayout(LayoutKind.Sequential, Pack=8)]
    public struct Config {
        public IntPtr leftKeys; public int leftKeyCount;
        public IntPtr rightKeys; public int rightKeyCount;
        public IntPtr leftKeyOrders; public IntPtr leftOrderLengths; public int leftOrderCounts;
        public IntPtr rightKeyOrders; public IntPtr rightOrderLengths; public int rightOrderCounts;
        public IntPtr leftPressTimes; public IntPtr rightPressTimes;
        public double bpmLimit; public int handPreference;
        public IntPtr segments; public int segmentCount;
    }

    static void Main(string[] args) {
        // args: <dll> <mode>  mode: freeze | dumpA | dumpB
        string dll = args[0];
        string mode = args[1];

        IntPtr h = LoadLibrary(dll);
        if (h == IntPtr.Zero) { Console.WriteLine("LOAD_FAIL|" + Marshal.GetLastWin32Error()); Environment.Exit(3); }
        var setCfg = Marshal.GetDelegateForFunctionPointer<SetConfig>(GetProcAddress(h, "SetTechniqueConfig"));
        var build  = Marshal.GetDelegateForFunctionPointer<BuildEvents>(GetProcAddress(h, "BuildTechniqueHitEventsEx"));
        var free   = Marshal.GetDelegateForFunctionPointer<FreeEvents>(GetProcAddress(h, "FreeHitEvents"));

        byte[] lk, rk; double limit; int handPref; double bpm;
        double[] et; int[] pt; int[] fi; double[] sm;

        if (mode == "freeze") {
            // 触发模式：单键手 + 0.9ms 密集事件 → 副手片在 mult=7 时恒 cnt=2>1 → 振荡
            lk = new byte[]{0x41}; rk = new byte[]{0x4C}; limit = 500; handPref = 0; bpm = 120;
            int n = 2000;
            et = new double[n]; pt = new int[n]; fi = new int[n]; sm = new double[n];
            for (int i = 0; i < n; i++) { et[i] = i * 0.00015; pt[i] = 1; fi[i] = i; sm[i] = 1.0; }
        } else if (mode == "dumpC") {
            // 混合模式：前 500 音 0.15ms 密集群（触发回溯路径但不至于无限振荡），
            // 后 1500 音 33ms 常规——验证回溯路径走过后的输出与旧 DLL 一致
            lk = new byte[]{0x41}; rk = new byte[]{0x4C}; limit = 500; handPref = 0; bpm = 120;
            int n = 2000;
            et = new double[n]; pt = new int[n]; fi = new int[n]; sm = new double[n];
            for (int i = 0; i < 500; i++) { et[i] = i * 0.00015; pt[i] = 1; fi[i] = i; sm[i] = 1.0; }
            double tail = 500 * 0.00015 + 0.05;
            for (int i = 500; i < n; i++) { tail += 0.0333; et[i] = tail; pt[i] = 1; fi[i] = i; sm[i] = 1.0; }
        } else if (mode == "dumpA") {
            // 一致性模式 A：33ms 常规间隔（不触发回溯路径）
            lk = new byte[]{0x41}; rk = new byte[]{0x4C}; limit = 500; handPref = 0; bpm = 120;
            int n = 2000;
            et = new double[n]; pt = new int[n]; fi = new int[n]; sm = new double[n];
            for (int i = 0; i < n; i++) { et[i] = i * 0.0333; pt[i] = 1; fi[i] = i; sm[i] = 1.0; }
        } else {
            // 一致性模式 B：仿 fc80dab3 形态——38638 事件、2-10ms 随机间隔、8+8 键、
            // limit=200、handPreference=1、变速（speedMuls 伪随机 0.5~2.0）
            lk = new byte[]{0x52,0x33,0x32,0x51,0x20,0xA2,0xA0,0xA1};
            rk = new byte[]{0x50,0xBB,0x08,0x2E,0xA5,0xBE,0xB1,0xB3};
            limit = 200; handPref = 1; bpm = 130;
            var rng = new Random(20261004);
            int n = 38638;
            et = new double[n]; pt = new int[n]; fi = new int[n]; sm = new double[n];
            double t = 0;
            for (int i = 0; i < n; i++) {
                // 大部分 4-10ms，掺 5% 密集群（0.5-1.5ms）
                t += (rng.NextDouble() < 0.05) ? 0.0005 + rng.NextDouble() * 0.001 : 0.004 + rng.NextDouble() * 0.006;
                et[i] = t; pt[i] = 1; fi[i] = i;
                sm[i] = 0.5 + rng.NextDouble() * 1.5;
            }
        }

        var gchL = GCHandle.Alloc(lk, GCHandleType.Pinned);
        var gchR = GCHandle.Alloc(rk, GCHandleType.Pinned);
        var cfg = new Config();
        cfg.leftKeys = gchL.AddrOfPinnedObject(); cfg.leftKeyCount = lk.Length;
        cfg.rightKeys = gchR.AddrOfPinnedObject(); cfg.rightKeyCount = rk.Length;
        cfg.bpmLimit = limit; cfg.handPreference = handPref;
        cfg.segments = IntPtr.Zero; cfg.segmentCount = 0;
        setCfg(ref cfg);

        int outCount = 0; IntPtr result = IntPtr.Zero;
        Exception workerErr = null;
        var worker = new Thread(() => {
            try { result = build(et, pt, fi, sm, et.Length, bpm, 1.0, out outCount); }
            catch (Exception ex) { workerErr = ex; }
        });
        worker.IsBackground = true;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        worker.Start();
        bool frozen = !worker.Join(15000);
        sw.Stop();
        if (frozen) {
            Console.WriteLine("FREEZE_CONFIRMED|15s 未返回（挂死复现）");
            Environment.Exit(2);
        }
        if (workerErr != null) { Console.WriteLine("ERROR|" + workerErr.Message); Environment.Exit(3); }
        Console.WriteLine("OK|耗时=" + sw.ElapsedMilliseconds + "ms|输出事件=" + outCount);

        if (mode != "freeze" && result != IntPtr.Zero) {
            var bytes = new byte[outCount * 24];
            Marshal.Copy(result, bytes, 0, bytes.Length);
            System.IO.File.WriteAllBytes(dll + "." + mode + ".bin", bytes);
            Console.WriteLine("DUMP|" + dll + "." + mode + ".bin|" + bytes.Length + "B");
        }
        if (result != IntPtr.Zero) free(result);
        gchL.Free(); gchR.Free();
        FreeLibrary(h);
    }
}