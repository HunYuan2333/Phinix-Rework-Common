using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Utils.Framework.ManagedExtensions;

namespace Utils.Framework
{
    public sealed partial class ManagedExtensionRuntime
    {
        private ManagedExtensionHostFacts startupHost;
        private HashSet<string> startupDisabledModules=new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public ManagedExtensionManagementSnapshot Refresh(IEnumerable<string> disabledModules, CancellationToken token)
        {
            lock(sync)
            {
                token.ThrowIfCancellationRequested();
                var inventory=ManagedExtensionInventoryReader.Read(paths,token);
                string global=ManagementGate(inventory,token);
                var disabled=new HashSet<string>(disabledModules??new string[0],StringComparer.OrdinalIgnoreCase);
                var rows=inventory.Packages.Select(row=>
                {
                    var current=packages.FirstOrDefault(p=>p.Row.RecordKey!=null && p.Row.RecordKey==row.RecordKey);
                    string basic=global??OwnershipGate(row);
                    return new ManagedExtensionManagementPackage(row,current==null?null:new ManagedExtensionRuntimePackage(current.Row,current.Code,current.Loaded),
                        basic??EnableIntentGate(row,inventory,disabled),basic??ReverseGate(row,inventory,false),basic??ReverseGate(row,inventory,true),basic??ModuleSettingsGate(row,inventory),
                        row.Manifest?.Modules.Any(m=>disabled.Contains(m.Id)!=startupDisabledModules.Contains(m.Id))==true);
                }).ToList();
                var codes=inventory.Diagnostics.Concat(global==null?new string[0]:new[]{global}).Distinct().ToList();
                Emit("management","ManagedInventoryRefreshed",null);
                return new ManagedExtensionManagementSnapshot(rows,codes);
            }
        }

        public ManagedExtensionStateChangeResult ChangeDesiredState(ManagedExtensionPackageSnapshot expected,
            ManagedExtensionDesiredState desired, IEnumerable<string> disabledModules, CancellationToken token)
        {
            if(expected==null) throw new ArgumentNullException(nameof(expected));
            lock(sync)
            {
                ManagedExtensionPackageSnapshot row=null; string preparedOperation=null;
                try
                {
                    token.ThrowIfCancellationRequested();
                    if(desired==ManagedExtensionDesiredState.Unknown) throw ManagedExtensionJson.Error("InvalidDesiredState");
                    var inventory=ManagedExtensionInventoryReader.Read(paths,token);
                    string code=ManagementGate(inventory,token);
                    if(code!=null) throw ManagedExtensionJson.Error(code);
                    row=inventory.Packages.SingleOrDefault(p=>p.RecordKey!=null && p.RecordKey==expected.RecordKey);
                    if(row==null) throw ManagedExtensionJson.Error("ManagedPackageMissing");
                    if(row.SourceId!=expected.SourceId || row.PackageId!=expected.PackageId || row.ManifestSha256!=expected.ManifestSha256 ||
                        row.InstallationTransactionId!=expected.InstallationTransactionId || row.StateOperationId!=expected.StateOperationId || row.DesiredState!=expected.DesiredState ||
                        row.Version!=expected.Version || row.RepositoryIdentitySha256!=expected.RepositoryIdentitySha256 || row.CatalogSnapshotId!=expected.CatalogSnapshotId ||
                        row.CatalogSha256!=expected.CatalogSha256 || row.ArtifactSha256!=expected.ArtifactSha256)
                        throw ManagedExtensionJson.Error("ManagedStateChanged");
                    code=OwnershipGate(row); if(code!=null) throw ManagedExtensionJson.Error(code);
                    if(row.DesiredState==desired) return new ManagedExtensionStateChangeResult(true,"ManagedStateUnchanged",row,row.StateOperationId);
                    var disabled=new HashSet<string>(disabledModules??new string[0],StringComparer.OrdinalIgnoreCase);
                    code=desired==ManagedExtensionDesiredState.Enabled?EnableIntentGate(row,inventory,disabled):ReverseGate(row,inventory,desired==ManagedExtensionDesiredState.PendingRemoval);
                    if(code!=null) throw ManagedExtensionJson.Error(code);
                    Emit("state","ManagedStateRequested",row);
                    if(desired==ManagedExtensionDesiredState.Enabled) ValidateEnable(row,inventory,disabled,token);
                    string operation=ManagedExtensionDesiredStateWriter.Write(paths,row,desired,token,null,
                        op=>{ preparedOperation=op; Emit("state","ManagedStatePrepared",CopyState(row,desired,op)); });
                    var changed=CopyState(row,desired,operation);
                    Emit("state","ManagedStateSaved"+desired,changed);
                    return new ManagedExtensionStateChangeResult(true,"ManagedStateSaved",changed,operation);
                }
                catch(OperationCanceledException) { Emit("state","ManagedStateCanceled",row??expected); throw; }
                catch(Exception ex)
                {
                    string code=ex is ManagedExtensionValidationException?((ManagedExtensionValidationException)ex).Code:"ManagedStateWriteFailed";
                    Emit("state",code,preparedOperation==null?row??expected:CopyState(row,desired,preparedOperation)); Detail(ex);
                    return new ManagedExtensionStateChangeResult(false,code,row,preparedOperation);
                }
            }
        }

        private string ManagementGate(ManagedExtensionInventorySnapshot inventory, CancellationToken token)
        {
            if(!started || disposed || lease==null || startupHost==null) return "ManagedManagementUnavailable";
            if(diagnostics.Count!=0 || inventory.Diagnostics.Count!=0) return "ManagedInventoryUncertain";
            try
            {
                ManagedExtensionInventoryReader.NoLinks(paths.TransactionsDirectory);
                if(File.Exists(paths.TransactionsDirectory)) return "TransactionRootInvalid";
                if(Directory.Exists(paths.TransactionsDirectory))
                    foreach(string entry in Directory.EnumerateFileSystemEntries(paths.TransactionsDirectory))
                    { token.ThrowIfCancellationRequested(); return "ManagedTransactionPending"; }
            }
            catch(ManagedExtensionValidationException ex) { return ex.Code; }
            catch(IOException ex) { Detail(ex); return "ManagedInventoryUncertain"; }
            catch(UnauthorizedAccessException ex) { Detail(ex); return "ManagedInventoryUncertain"; }
            return null;
        }
        private static string OwnershipGate(ManagedExtensionPackageSnapshot row)
        { return row.DiagnosticCode??(row.ContentState!=ManagedExtensionContentState.ContentVerified || row.Manifest==null || row.StateOperationId==null?"ManagedOwnershipUncertain":null); }

        private string ModuleSettingsGate(ManagedExtensionPackageSnapshot row, ManagedExtensionInventorySnapshot inventory)
        {
            var ids=new HashSet<string>(row.Manifest.Modules.Select(m=>m.Id),StringComparer.OrdinalIgnoreCase);
            if(startupHost.ModuleIds.Any(ids.Contains) || inventory.Packages.Any(p=>p.RecordKey!=row.RecordKey && p.Manifest?.Modules.Any(m=>ids.Contains(m.Id))==true))
                return "CandidateModuleConflict";
            return null;
        }

        private string ReverseGate(ManagedExtensionPackageSnapshot row, ManagedExtensionInventorySnapshot inventory, bool removal)
        {
            var ids=new HashSet<string>(row.Manifest.Modules.Select(m=>m.Id),StringComparer.OrdinalIgnoreCase);
            // Removal checks even disabled packages; disabling allows explicitly disabled/removing consumers.
            foreach(var other in inventory.Packages.Where(p=>p.RecordKey!=row.RecordKey))
            {
                if(other.Manifest==null) return "ManagedDependencyUncertain";
                if(removal && other.DesiredState==ManagedExtensionDesiredState.PendingRemoval) continue;
                if(!removal && other.DesiredState!=ManagedExtensionDesiredState.Enabled) continue;
                if(other.Manifest.Dependencies.Any(d=>d.PackageId==row.PackageId) || other.Manifest.Modules.Any(m=>m.DependsOn.Any(ids.Contains)))
                    return "ManagedHasDependents";
            }
            if(startupHost.ModuleDeclarations.Any(m=>m.DependsOn.Any(ids.Contains))) return "ManagedHostHasDependents";
            // An unmanaged CLR reference can bind before managed removal; keep the original code available.
            if(baseline.Any(a=>a.GetReferencedAssemblies().Any(r=>row.Manifest.Assemblies.Any(own=>own.Name==r.Name)))) return "ManagedHostAssemblyDependent";
            return null;
        }
        private string EnableIntentGate(ManagedExtensionPackageSnapshot row, ManagedExtensionInventorySnapshot inventory, HashSet<string> disabled)
        {
            if(row.Manifest.Modules.All(m=>disabled.Contains(m.Id))) return "ManagedAllModulesDisabled";
            foreach(var dep in row.Manifest.Dependencies)
            {
                var providers=inventory.Packages.Where(p=>p.PackageId==dep.PackageId).ToList();
                if(dep.Optional && providers.Count==0) continue;
                if(providers.Count!=1 || OwnershipGate(providers[0])!=null || providers[0].DesiredState!=ManagedExtensionDesiredState.Enabled || !dep.VersionRange.Contains(providers[0].Manifest.Version))
                    return "CandidatePackageDependencyUnavailable";
            }
            if(row.Manifest.Modules.Any(m=>!disabled.Contains(m.Id) && m.DependsOn.Any(disabled.Contains))) return "ManagedModuleDependencyUnavailable";
            return null; // The complete metadata/compatibility/combined graph gate runs before the actual write.
        }
        private void ValidateEnable(ManagedExtensionPackageSnapshot target, ManagedExtensionInventorySnapshot inventory, HashSet<string> disabled, CancellationToken token)
        {
            var rows=inventory.Packages.Select(p=>p.RecordKey==target.RecordKey?CopyState(p,ManagedExtensionDesiredState.Enabled,p.StateOperationId):p).ToList();
            var payloads=new Dictionary<string,ManagedExtensionInspectedPayload>(); long frozen=0;
            foreach(var row in rows.Where(p=>p.DesiredState==ManagedExtensionDesiredState.Enabled && OwnershipGate(p)==null))
            {
                token.ThrowIfCancellationRequested();
                long size=row.Manifest.Assemblies.Sum(a=>a.File.Length);
                if(frozen+size>ManagedExtensionManifestReader.MaxExpandedBytes) throw ManagedExtensionJson.Error("ManagedStartupMemoryLimit");
                var bytes=new Dictionary<string,byte[]>();
                try
                {
                    foreach(var asm in row.Manifest.Assemblies)
                    {
                        string file=Path.Combine(paths.GetPackageDirectory(row.SourceId,row.PackageId),asm.File.Path);
                        ManagedExtensionInventoryReader.NoLinks(file); bytes.Add(asm.File.Path,ManagedExtensionInventoryReader.Bytes(file,(int)ManagedExtensionManifestReader.MaxFileBytes,token));
                    }
                    payloads[row.RecordKey]=ManagedExtensionPayloadInspector.Inspect(row.Manifest,bytes,token); frozen+=size;
                }
                catch(ManagedExtensionValidationException) { if(row.RecordKey==target.RecordKey) throw; }
                catch(IOException) { if(row.RecordKey==target.RecordKey) throw; }
                catch(UnauthorizedAccessException) { if(row.RecordKey==target.RecordKey) throw; }
            }
            ValidateCandidates(rows,payloads,disabled,new[]{target.RecordKey},token);
        }
        private void ValidateCandidates(List<ManagedExtensionPackageSnapshot> rows,
            Dictionary<string,ManagedExtensionInspectedPayload> payloads,HashSet<string> disabled,IEnumerable<string> targets,CancellationToken token)
        {
            var available=startupHost.ModuleIds.Where(id=>!disabled.Contains(id));
            var facts=new ManagedExtensionHostFacts(startupHost.GameVersion,startupHost.PhinixVersion.ToString(),startupHost.AbstractionsVersion.ToString(),
                startupHost.Assemblies,startupHost.ModuleIds,available,startupHost.ActiveModIds,startupHost.ModuleDeclarations,startupHost.ReferenceRules);
            var planned=ManagedExtensionCandidatePlanner.Plan(new ManagedExtensionInventorySnapshot(rows,new string[0]),payloads,facts,startupId,token);
            var targetKeys=new HashSet<string>(targets,StringComparer.Ordinal);
            foreach(var result in planned.Where(p=>targetKeys.Contains(p.Package.RecordKey)))
                if(result.DiagnosticCode!=null) throw new ManagedExtensionValidationException(result.DiagnosticCode) {ReferenceFailure=result.ReferenceFailure};
            var codes=ManagedExtensionModuleGate.Check(facts,planned.Where(p=>p.DiagnosticCode==null).Select(p=>p.Package),disabled,token);
            // Propagate a rejected module provider to packages depending on it.
            bool changed;
            do
            {
                changed=false;
                foreach(var p in planned.Where(p=>p.DiagnosticCode==null && !codes.ContainsKey(p.Package.RecordKey)))
                    if(planned.Any(other=>codes.ContainsKey(other.Package.RecordKey) && (p.Package.Manifest.Dependencies.Any(d=>d.PackageId==other.Package.PackageId) ||
                        p.Package.Manifest.Modules.Any(m=>m.DependsOn.Any(id=>other.Package.Manifest.Modules.Any(n=>n.Id==id))))))
                    { codes[p.Package.RecordKey]="ManagedDependencyRejected"; changed=true; }
            } while(changed);
            foreach(string key in targetKeys) { string code; if(codes.TryGetValue(key,out code)) throw ManagedExtensionJson.Error(code); }
        }
        private static ManagedExtensionPackageSnapshot CopyState(ManagedExtensionPackageSnapshot row, ManagedExtensionDesiredState desired, string operation)
        { return new ManagedExtensionPackageSnapshot(row.RecordKey,row.SourceId,row.RepositoryIdentitySha256,row.PackageId,row.Version,row.ManifestSha256,row.CatalogSnapshotId,
            row.CatalogSha256,row.ArtifactSha256,row.InstallationTransactionId,operation,row.Manifest,desired,row.ContentState,row.DiagnosticCode); }
    }
}
