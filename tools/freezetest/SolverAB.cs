using System;
using System.Runtime.InteropServices;

class SolverAB {
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr LoadLibrary(string p);
    [DllImport("kernel32.dll", SetLastError=true)] static extern IntPtr GetProcAddress(IntPtr h, string n);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void SetCfg(ref Cfg c);
    [StructLayout(LayoutKind.Sequential, Pack=8)] public struct Cfg {
        public IntPtr lk; public int lkc; public IntPtr rk; public int rkc;
        public IntPtr lko; public IntPtr lkl; public int lkc2;
        public IntPtr rko; public IntPtr rkl; public int rkc3;
        public IntPtr lpt; public IntPtr rpt;
        public double lim; public int hp; public IntPtr seg; public int segc;
    }
    [StructLayout(LayoutKind.Sequential, Pack=8)] public struct SOpt {
        public int beam, maxMult; public double fragP, runP, roughP, dropP;
    }
    [StructLayout(LayoutKind.Sequential, Pack=8)] public struct Stats {
        public int frag, longest, dropped, pieces; public double var, total, fragC, runC, roughC; public int nodes;
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr Solve(double[] et, int[] pt, int[] fi, double[] sm, int n, double bpm, double sp, ref SOpt o, out int oc);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate IntPtr GetStats();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void FreeEv(IntPtr p);

    static void Main(string[] args) {
        IntPtr h = LoadLibrary(args[0]);
        var set = Marshal.GetDelegateForFunctionPointer<SetCfg>(GetProcAddress(h, "SetTechniqueConfig"));
        var solve = Marshal.GetDelegateForFunctionPointer<Solve>(GetProcAddress(h, "SolveTechniqueTrace"));
        var gs = Marshal.GetDelegateForFunctionPointer<GetStats>(GetProcAddress(h, "GetLastTraceStats"));
        var free = Marshal.GetDelegateForFunctionPointer<FreeEv>(GetProcAddress(h, "FreeTraceEvents"));

        byte[] lk = {0x52,0x33,0x32,0x51,0x20,0xA2,0xA0,0xA1};
        byte[] rk = {0x50,0xBB,0x08,0x2E,0xA5,0xBE,0xB1,0xB3};
        var gl = GCHandle.Alloc(lk, GCHandleType.Pinned); var gr = GCHandle.Alloc(rk, GCHandleType.Pinned);
        var c = new Cfg(); c.lk = gl.AddrOfPinnedObject(); c.lkc = 8; c.rk = gr.AddrOfPinnedObject(); c.rkc = 8;
        c.lim = 200; c.hp = 1; c.seg = IntPtr.Zero; c.segc = 0;
        set(ref c);

        // Qyoh 形态：30000 事件、间隔 0.5-10ms 随机 + 5% 密集群、speed 0.25~305 伪随机
        var rng = new Random(4242);
        int n = 30000;
        var et = new double[n]; var pt = new int[n]; var fi = new int[n]; var sm = new double[n];
        double t = 0;
        for (int i = 0; i < n; i++) {
            t += 0.00002 + rng.NextDouble()*0.00001;
            et[i] = t; pt[i] = 1; fi[i] = i;
            sm[i] = 0.25 + rng.NextDouble() * 305.5;
        }
        var o = new SOpt(); o.beam = 4; o.maxMult = 7; o.fragP = 1.0; o.runP = 0.6; o.roughP = 0.25; o.dropP = 10000;
        int oc; var ptr = solve(et, pt, fi, sm, n, 242.0, 1.0, ref o, out oc);
        var s = Marshal.PtrToStructure<Stats>(gs());
        Console.WriteLine("frag=" + s.frag + " longest=" + s.longest + " dropped=" + s.dropped + " pieces=" + s.pieces + " events=" + oc + " nodes=" + s.nodes);
        if (ptr != IntPtr.Zero) free(ptr);
        gl.Free(); gr.Free();
    }
}