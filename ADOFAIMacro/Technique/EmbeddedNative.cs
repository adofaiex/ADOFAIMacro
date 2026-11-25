using System;
using System.IO;
using System.Runtime.InteropServices;
using ADOFAIMacro.Core;

namespace ADOFAIMacro.Technique
{
    /// <summary>
    /// 嵌入式原生库加载器：把 TriggerRs（macro_trigger.dll）以资源形式内嵌在
    /// ADOFAIMacro.dll 里，首次需要时解压到 %LOCALAPPDATA%\ADOFAIMacro\native\
    /// 再 LoadLibrary —— 用户部署面保持「一个 ADOFAIMacro.dll」，不用单独拷 Rust DLL。
    ///
    /// 为什么不用内存直接加载（ManualMap）：
    ///   - 需要手工解析 PE 重定位/导入表，病毒误报率极高；
    ///   - 本项目早先已有「解压+绝对路径 LoadLibrary」的成熟先例（不再随模组发布的原生手法库即采用过同一做法）。
    ///
    /// 提取目标选 %LOCALAPPDATA% 而非 Mods 目录：UMM 更新是整目录覆盖，
    /// 写 Mods 会被下次更新/校验踩掉；临时目录只写一次、版本不匹配时重写。
    ///
    /// 版本治理：嵌入资源随 ADOFAIMacro.dll 走，解压前比对目标文件哈希，
    /// 一致则跳过提取（避免每次启动重写触发杀软扫描）。
    /// </summary>
    internal static class EmbeddedNative
    {
        /// <summary>嵌入资源名（csproj EmbeddedResource 定义）。</summary>
        private const string ResourceName = "ADOFAIMacro.Technique.macro_trigger.dll";

        /// <summary>解压目录（用户本地，跨 UMM 更新存续）。</summary>
        private static string NativeDir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "ADOFAIMacro", "native");

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern IntPtr LoadLibrary(string lpFileName);

        private static string ToHex(byte[] hash)
        {
            var sb = new System.Text.StringBuilder(hash.Length * 2);
            foreach (byte b in hash) sb.Append(b.ToString("x2"));
            return sb.ToString();
        }

        /// <summary>
        /// 从嵌入资源加载原生 DLL。成功返回模块句柄；失败返回 IntPtr.Zero（调用方走回退路径）。
        /// </summary>
        internal static IntPtr LoadEmbedded()
        {
            try
            {
                var asm = typeof(EmbeddedNative).Assembly;
                using var rs = asm.GetManifestResourceStream(ResourceName);
                if (rs == null)
                {
                    Main.Log($"[EmbeddedNative] 嵌入资源缺失: {ResourceName}");
                    return IntPtr.Zero;
                }

                string dir = NativeDir;
                Directory.CreateDirectory(dir);
                string target = Path.Combine(dir, "macro_trigger.dll");

                // 哈希一致则跳过提取（不重复触发杀软扫描）
                using (var md5 = System.Security.Cryptography.MD5.Create())
                {
                    byte[] resBytes = new byte[rs.Length];
                    int read = 0;
                    while (read < resBytes.Length)
                    {
                        int n = rs.Read(resBytes, read, resBytes.Length - read);
                        if (n <= 0) break;
                        read += n;
                    }
                    string resHash = ToHex(md5.ComputeHash(resBytes, 0, read));
                    if (File.Exists(target))
                    {
                        string curHash = ToHex(md5.ComputeHash(File.ReadAllBytes(target)));
                        if (curHash == resHash)
                            return LoadLibrary(target);
                    }
                    using var fs = File.Create(target);
                    fs.Write(resBytes, 0, read);
                }

                IntPtr h = LoadLibrary(target);
                if (h == IntPtr.Zero)
                    Main.Log($"[EmbeddedNative] LoadLibrary 失败，错误码: {Marshal.GetLastWin32Error()}");
                return h;
            }
            catch (Exception ex)
            {
                Main.Log($"[EmbeddedNative] 加载异常: {ex.GetType().Name}: {ex.Message}");
                return IntPtr.Zero;
            }
        }
    }
}
