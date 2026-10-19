using System;

namespace ADOFAIMacro.Core
{
    /// <summary>补丁注入方式（Harmony 原生语义）。</summary>
    internal enum PatchType
    {
        /// <summary>前置：先于原方法执行；返回 bool 可跳过原方法（false = 跳过）。</summary>
        Prefix,
        /// <summary>后置：原方法之后执行，可改写 <c>__result</c>。</summary>
        Postfix,
        /// <summary>IL 改写：直接改写原方法体；异常发生在补丁应用期，不走异常兜底。</summary>
        Transpiler,
        /// <summary>收尾：原方法+全部补丁之后执行，吞/改异常。</summary>
        Finalizer,
    }

    // 方法级补丁声明，应用器见 MacroPatchHub。
    //     [MacroPatch(typeof(scrPlayer), "CountValidKeysPressed", PatchType.Prefix)]
    //     internal static void Prefix() { ... }
    // 注入规则与 Harmony 原生一致（__instance / ___field / __result / ref 参数按名绑定）。
    // 目标类型：稳定核心用 typeof 直引（编译期检查）；易变边缘用 ClassName 字符串，
    // 游戏改名时只改字符串不动签名。
    //
    // ── 多版本剧本（移植自 SpectrePatch 的设计）────────────────
    // ① 游戏改名（最常见）：候选链，新名在前、旧名兜底：
    //     [MacroPatch(typeof(scrController), "NewName", PatchType.Prefix,
    //         MethodNames = new[] { "PlayerControl_Update" })]
    //    解析顺序 = MethodName → MethodNames 依次尝试，命中即用，失配进探针。
    //
    // ② 核心补丁防未验证版本：pin 到已验证 build——游戏更新后自动停用
    //    （fail-closed），适配完成把 VerifiedBuild 推进到新 build 即全部解锁。
    //
    // ③ 真分叉（方法还在但语义随版本变了）：两个补丁方法各占一段版本区间。
    //
    // ④ 全局最低版本：MinSupportedBuild 是所有补丁的隐式 MinVersion 地板；
    //    MinVersion 字段只在某补丁需要比全局更高的下限时才设。
    //
    // ── 异常兜底 ───────────────────────────────────────────────
    //   - TryingCatch 默认 true：补丁体异常由 try/catch 包装吞掉并记录
    //     （无异常零开销），同一异常按窗口限流合并，持续异常窗口后打一行计数摘要；
    //     DisableAfterExceptions > 0 时窗口内累计达到阈值直接自动卸载该补丁。
    //     ⚠ Prefix 中途抛出被吞后原方法不会执行（Harmony 语义），该功能可能退化，
    //     属"有日志的可控降级"，仍优于旧二进制在新游戏上反复打断游戏流程。
#pragma warning disable CS0649
    /// <summary>
    /// 方法级补丁声明；由 <see cref="MacroPatchHub"/> 扫描应用（候选链/版本门控/异常兜底/探针）。
    /// 用法与多版本剧本详见上方注释块，注入规则与 Harmony 原生一致。
    /// </summary>
    [AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
    internal sealed class MacroPatchAttribute : Attribute
    {
        /// <summary>目标类型（typeof 直引，编译期检查）。与 <see cref="ClassName"/> 二选一，优先用本字段。</summary>
        public Type ClassType;

        /// <summary>目标类型全名字符串（类型不便直引时用，如游戏私有/改名类型）。</summary>
        public string ClassName;

        /// <summary>类型候选链：ClassName 未命中时依次尝试（类型改名/搬命名空间的多版本兜底，新名在前）。</summary>
        public string[] ClassNames;

        /// <summary>目标方法/属性名（首选候选）。</summary>
        public string MethodName;

        /// <summary>候选链：MethodName 之后依次尝试（游戏改名的多版本兜底，新名在前）。</summary>
        public string[] MethodNames;

        /// <summary>打在全部同名重载上。默认 false：重载不唯一且消歧失败时进探针不挂。</summary>
        public bool AllOverloads;

        /// <summary>注入方式，默认 Postfix。</summary>
        public PatchType PatchType = PatchType.Postfix;

        /// <summary>最低游戏 build（含）；通常不设——全局地板已是 MinSupportedBuild。</summary>
        public int MinVersion;

        /// <summary>最高游戏 build（含）；核心补丁 pin 到 VerifiedBuild，游戏更新自动停用（fail-closed）。</summary>
        public int MaxVersion;

        /// <summary>目标参数类型精确匹配（编译期检查），用于重载消歧。</summary>
        public Type[] ParameterTypes;

        /// <summary>补丁体异常被吞掉并记录（默认 true，无异常零开销）。</summary>
        public bool TryingCatch = true;

        /// <summary>异常兜底生效时，30s 窗口内异常累计达到该次数即自动卸载此补丁
        ///（0 = 从不，默认）。持续故障自我了断好过无限刷日志；
        /// 卸载后重新启用模组前不会自动恢复。</summary>
        public int DisableAfterExceptions;

        // 应用器扫描时回填，attribute 用法里不写
        internal System.Reflection.MethodInfo PatchMethod;

        public MacroPatchAttribute() { }

        public MacroPatchAttribute(Type classType, string methodName, PatchType patchType)
        {
            ClassType = classType;
            MethodName = methodName;
            PatchType = patchType;
        }

        public MacroPatchAttribute(string className, string methodName, PatchType patchType)
        {
            ClassName = className;
            MethodName = methodName;
            PatchType = patchType;
        }
    }
#pragma warning restore CS0649
}
