using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;

namespace Utils.Framework.ManagedExtensions
{
    public sealed class ManagedExtensionManagementPackage
    {
        internal ManagedExtensionManagementPackage(ManagedExtensionPackageSnapshot package, ManagedExtensionRuntimePackage current,
            string enableCode, string disableCode, string removalCode, string moduleSettingsCode, bool modulesRestartPending)
        { Package=package; Current=current; EnableBlockCode=enableCode; DisableBlockCode=disableCode; RemovalBlockCode=removalCode; ModuleSettingsBlockCode=moduleSettingsCode; ModulesRestartPending=modulesRestartPending; }
        public ManagedExtensionPackageSnapshot Package { get; }
        public ManagedExtensionRuntimePackage Current { get; }
        public string EnableBlockCode { get; }
        public string DisableBlockCode { get; }
        public string RemovalBlockCode { get; }
        public string ModuleSettingsBlockCode { get; }
        public bool ModulesRestartPending { get; }
        public bool RestartPending => Current==null || Current.Package.DesiredState!=Package.DesiredState ||
            Current.Package.Version!=Package.Version || Current.Package.ManifestSha256!=Package.ManifestSha256;
    }

    public sealed class ManagedExtensionManagementSnapshot
    {
        internal ManagedExtensionManagementSnapshot(IEnumerable<ManagedExtensionManagementPackage> packages, IEnumerable<string> diagnostics)
        { Packages=ManagedExtensionCompatibility.Freeze(packages); Diagnostics=ManagedExtensionCompatibility.Freeze(diagnostics); }
        public ReadOnlyCollection<ManagedExtensionManagementPackage> Packages { get; }
        public ReadOnlyCollection<string> Diagnostics { get; }
    }

    public sealed class ManagedExtensionStateChangeResult
    {
        internal ManagedExtensionStateChangeResult(bool succeeded, string code, ManagedExtensionPackageSnapshot package, string operationId)
        { Succeeded=succeeded; Code=code; Package=package; OperationId=operationId; }
        public bool Succeeded { get; }
        public string Code { get; }
        public ManagedExtensionPackageSnapshot Package { get; }
        public string OperationId { get; }
    }

    public interface IManagedExtensionManagementService : IManagedExtensionInventoryService
    {
        // Host UI captures module settings before background work. No game API is read here.
        ManagedExtensionManagementSnapshot Refresh(IEnumerable<string> disabledModules, CancellationToken token);
        ManagedExtensionStateChangeResult ChangeDesiredState(ManagedExtensionPackageSnapshot expected,
            ManagedExtensionDesiredState desired, IEnumerable<string> disabledModules, CancellationToken token);
    }
}
