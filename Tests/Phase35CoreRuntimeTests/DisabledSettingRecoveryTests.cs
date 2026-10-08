using System;
using System.Linq;
using Utils.Framework;

internal static partial class Program
{
    private static void AssertDisabledSettingRecoveryEntries()
    {
        var real = new ExtensionDiscoveryResult { ExtensionId="known", AssemblyName="Real", State=ExtensionModuleState.Active };
        var discovered = new[] { real };
        var disabled = new[] { "KNOWN", "unloaded.example", "UNLOADED.EXAMPLE", "uninstalled.example", "" };
        var rows = ExtensionDisplayState.IncludeDisabledSettings(discovered, disabled);
        if(rows.Count!=3 || !ReferenceEquals(rows[0],real) || discovered.Length!=1 || real.State!=ExtensionModuleState.Active)
            throw new Exception("Recovery rows must deduplicate IDs without changing actual discovery/runtime results.");
        var recovery=rows.Single(r=>r.ExtensionId=="unloaded.example");
        if(recovery.AssemblyName!=null || recovery.State!=ExtensionModuleState.Disabled || recovery.RegisteredApis.Count!=0)
            throw new Exception("Unloaded recovery settings must never claim an assembly or active service.");
        if(ExtensionDisplayState.Compute(recovery,disabled,null).EffectiveState!=ExtensionModuleState.Disabled)
            throw new Exception("Saved disabled intent must remain visible.");
        if(ExtensionDisplayState.Compute(recovery,new string[0],null).PendingChange!=ExtensionPendingChange.WillEnableAfterRestart)
            throw new Exception("Explicit recovery must require restart, never report activation.");
        if(ExtensionDisplayState.IncludeDisabledSettings(null,disabled).Count!=3 || ExtensionDisplayState.IncludeDisabledSettings(null,null).Count!=0)
            throw new Exception("Settings remain recoverable with no discovered plugins.");
    }
}
