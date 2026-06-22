using System;
using System.Collections.Generic;

namespace Utils.Framework
{
    /// <summary>
    /// 扩展展示状态的"待重启变更"类型。
    /// v1 重启生效：用户勾选后运行时状态不变，但 UI 需要提示重启后的结果。
    /// v2 运行时切换时此计算可替换为实时状态源。
    /// </summary>
    public enum ExtensionPendingChange
    {
        None = 0,
        WillDisableAfterRestart = 1,
        WillEnableAfterRestart = 2
    }

    /// <summary>
    /// 影响扩展激活状态的原因分类，UI 据此选择提示文案。
    /// </summary>
    public enum ExtensionDisplayReason
    {
        None = 0,
        UserDisabled = 1,
        DependencyDisabled = 2
    }

    /// <summary>
    /// 扩展展示状态的纯静态计算。输入 = 运行时发现结果 + 用户设置快照 + 依赖图；
    /// 输出 = 当前运行状态、重启后的有效状态、待重启变更、原因。
    ///
    /// 设计哲学 §2.1 松耦合：不依赖 RimWorld 或 host 具体类型，仅依赖共享契约层，
    /// 禁用集合以 IReadOnlyCollection&lt;string&gt; 传入（ExtensionId 字符串匹配，§1.2）。
    /// ExtensionManagerTab 与 Mod Settings 面板共用此计算，保证两处 UI 始终一致；
    /// 无需"刷新"动作——UI 只是（运行时结果 + 当前设置）的纯函数。
    /// </summary>
    public sealed class ExtensionDisplayState
    {
        /// <summary>本次启动时发现流程得到的运行时状态（Active / Disabled / DependencyDisabled / Failed…）。</summary>
        public ExtensionModuleState RuntimeState { get; }

        /// <summary>按当前设置重算后，重启 RimWorld 将呈现的有效状态。</summary>
        public ExtensionModuleState EffectiveState { get; }

        /// <summary>相对运行时的待重启变更；None 表示无变更。</summary>
        public ExtensionPendingChange PendingChange { get; }

        /// <summary>导致非激活状态的原因分类。</summary>
        public ExtensionDisplayReason Reason { get; }

        /// <summary>被禁用的依赖 ID 列表（Reason == DependencyDisabled 时有值）。</summary>
        public IReadOnlyList<string> DisabledDependencies { get; }

        private ExtensionDisplayState(
            ExtensionModuleState runtimeState,
            ExtensionModuleState effectiveState,
            ExtensionPendingChange pendingChange,
            ExtensionDisplayReason reason,
            IReadOnlyList<string> disabledDependencies)
        {
            RuntimeState = runtimeState;
            EffectiveState = effectiveState;
            PendingChange = pendingChange;
            Reason = reason;
            DisabledDependencies = disabledDependencies ?? Array.Empty<string>();
        }

        /// <summary>
        /// 计算指定扩展的展示状态。纯函数，无副作用，可在任意层调用。
        /// </summary>
        /// <param name="result">发现结果（运行时状态 + ExtensionId）。</param>
        /// <param name="userDisabledIds">用户当前禁用的扩展 ID 集合（Settings.DisabledExtensions）。</param>
        /// <param name="dependencyGraph">依赖图；可为 null（此时不做依赖连锁判断）。</param>
        public static ExtensionDisplayState Compute(
            ExtensionDiscoveryResult result,
            IReadOnlyCollection<string> userDisabledIds,
            ExtensionDependencyGraph dependencyGraph)
        {
            ExtensionModuleState runtime = result?.State ?? ExtensionModuleState.Unknown;

            // 失败状态保持原样展示，不参与启用/禁用计算
            if (runtime == ExtensionModuleState.Failed)
            {
                return new ExtensionDisplayState(
                    runtime, runtime, ExtensionPendingChange.None,
                    ExtensionDisplayReason.None, Array.Empty<string>());
            }

            string extensionId = result?.ExtensionId;
            // 手写大小写不敏感匹配：IReadOnlyCollection<T> 没有 Contains，
            // 且与 StaticActivationPolicy 的 OrdinalIgnoreCase 语义保持一致
            bool userDisabled = false;
            if (userDisabledIds != null && extensionId != null)
            {
                foreach (string disabledId in userDisabledIds)
                {
                    if (string.Equals(disabledId, extensionId, StringComparison.OrdinalIgnoreCase))
                    {
                        userDisabled = true;
                        break;
                    }
                }
            }

            IReadOnlyList<string> disabledDeps = dependencyGraph == null
                ? Array.Empty<string>()
                : dependencyGraph.GetDisabledDependencies(
                    extensionId, userDisabledIds ?? (IReadOnlyCollection<string>)Array.Empty<string>());
            bool depsDisabled = disabledDeps.Count > 0;

            ExtensionModuleState effective;
            ExtensionPendingChange pending;
            ExtensionDisplayReason reason;

            if (userDisabled)
            {
                // 用户显式禁用：重启后为 Disabled；当前若已在运行则提示"重启后禁用"
                effective = ExtensionModuleState.Disabled;
                reason = ExtensionDisplayReason.UserDisabled;
                pending = runtime == ExtensionModuleState.Disabled
                    ? ExtensionPendingChange.None
                    : ExtensionPendingChange.WillDisableAfterRestart;
            }
            else if (depsDisabled)
            {
                // 依赖被禁用：重启后为 DependencyDisabled；当前若在运行则提示"重启后失效"
                effective = ExtensionModuleState.DependencyDisabled;
                reason = ExtensionDisplayReason.DependencyDisabled;
                pending = (runtime == ExtensionModuleState.DependencyDisabled ||
                           runtime == ExtensionModuleState.Disabled)
                    ? ExtensionPendingChange.None
                    : ExtensionPendingChange.WillDisableAfterRestart;
            }
            else
            {
                effective = runtime;
                reason = ExtensionDisplayReason.None;
                pending = (runtime == ExtensionModuleState.Disabled ||
                           runtime == ExtensionModuleState.DependencyDisabled)
                    ? ExtensionPendingChange.WillEnableAfterRestart
                    : ExtensionPendingChange.None;
            }

            return new ExtensionDisplayState(
                runtime, effective, pending, reason, disabledDeps);
        }
    }
}
