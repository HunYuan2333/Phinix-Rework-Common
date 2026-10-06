using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Utils.Framework.ManagedExtensions
{
    public sealed class ManagedExtensionRuntimePackage
    {
        internal ManagedExtensionRuntimePackage(ManagedExtensionPackageSnapshot package, string code, bool loaded)
        { Package = package; DiagnosticCode = code; AssembliesLoaded = loaded; }
        public ManagedExtensionPackageSnapshot Package { get; }
        public string DiagnosticCode { get; }
        // Loaded bytes cannot be unloaded. Failure never authorizes discovery or activation.
        public bool AssembliesLoaded { get; }
    }

    public sealed class ManagedExtensionRuntimeSnapshot
    {
        internal ManagedExtensionRuntimeSnapshot(string startupId, IEnumerable<ManagedExtensionRuntimePackage> packages, IEnumerable<string> diagnostics)
        { StartupId = startupId; Packages = ManagedExtensionCompatibility.Freeze(packages); Diagnostics = ManagedExtensionCompatibility.Freeze(diagnostics); }
        public string StartupId { get; }
        public ReadOnlyCollection<ManagedExtensionRuntimePackage> Packages { get; }
        public ReadOnlyCollection<string> Diagnostics { get; }
    }

    public interface IManagedExtensionInventoryService
    {
        // Startup facts only; future desired-state changes do not change current-session loading.
        ManagedExtensionRuntimeSnapshot Snapshot { get; }
    }

    public sealed class ManagedExtensionRuntimeAudit
    {
        internal ManagedExtensionRuntimeAudit(string startupId, string stage, string code, ManagedExtensionPackageSnapshot package, string assemblyName = null, string moduleId = null, long sequence = 0,ManagedExtensionAssemblyReferenceFailure referenceFailure=null,string resourcePath=null)
        { StartupId = startupId; Stage = stage; Code = code; Package = package; AssemblyName = assemblyName; ModuleId = moduleId; Sequence=sequence; TimeUtc=DateTime.UtcNow; ReferenceFailure=referenceFailure; ResourcePath=resourcePath; }
        public string StartupId { get; }
        public long Sequence { get; }
        public DateTime TimeUtc { get; }
        public string Stage { get; }
        public string Code { get; }
        public ManagedExtensionPackageSnapshot Package { get; }
        public string AssemblyName { get; }
        public string ModuleId { get; }
        public string ResourcePath { get; }
        public ManagedExtensionAssemblyReferenceFailure ReferenceFailure { get; }
    }
}
