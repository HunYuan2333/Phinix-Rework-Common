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
        // Deterministic interruption points for the production-path regression harness.
        internal Action<string> InstallationFault { get; set; }

        public ManagedExtensionInstallResult Install(ManagedExtensionInstallRequest request,IEnumerable<string> disabledModules,CancellationToken token)
        {
            if(request==null) throw new ArgumentNullException(nameof(request));
            lock(sync)
            {
                string operation=Guid.NewGuid().ToString("N");
                var rows=request.Packages.Select(p=>new ManagedExtensionPackageSnapshot(ManagedExtensionPaths.PackageKey(p.SourceId,p.Manifest.PackageId),
                    p.SourceId,p.RepositoryIdentitySha256,p.Manifest.PackageId,p.Manifest.Version.ToString(),ManagedExtensionPaths.Hash(p.ManifestBytes),
                    p.CatalogSnapshotId,p.CatalogSha256,p.ArtifactSha256,operation,operation,p.Manifest,ManagedExtensionDesiredState.Enabled,ManagedExtensionContentState.ContentVerified,null)).ToList();
                bool writing=false;
                try
                {
                    token.ThrowIfCancellationRequested();
                    var inventory=ManagedExtensionInventoryReader.Read(paths,token);
                    string code=ManagementGate(inventory,token); if(code!=null) throw ManagedExtensionJson.Error(code);
                    if(inventory.Packages.Count+request.Packages.Count(p=>p.Replacement==null)>ManagedExtensionInventoryReader.MaxPackages) throw ManagedExtensionJson.Error("PackageLimit");
                    var disabled=new HashSet<string>(disabledModules??new string[0],StringComparer.OrdinalIgnoreCase);
                    foreach(var row in rows) Emit("install","ManagedInstallRequested",row);
                    foreach(var package in request.Packages.Where(p=>p.Replacement!=null)) Emit("install","ManagedReplacementOriginal",CopyState(package.Replacement,ManagedExtensionDesiredState.Enabled,operation));
                    ValidateInstallation(request,rows,inventory,disabled,token);
                    foreach(var row in rows) Emit("install","ManagedInstallPreflightPassed",row);
                    writing=true;
                    ManagedExtensionInstallationRecovery.Install(paths,request,rows,token,InstallationFault,(c,r)=>Emit("install",c,r),t=>
                    {
                        var fresh=ManagedExtensionInventoryReader.Read(paths,t);
                        if(fresh.Diagnostics.Count!=0 || !SameInventory(inventory,fresh)) throw ManagedExtensionJson.Error("ManagedStateChanged");
                        ValidateInstallation(request,rows,fresh,disabled,t);
                        foreach(var row in rows) Emit("install","ManagedInstallCommitRevalidated",row);
                    });
                    return new ManagedExtensionInstallResult(true,"ManagedInstallSaved",operation,rows);
                }
                catch(OperationCanceledException)
                {
                    // Preparation has never authorized visible package mutation. Only delete exact owned bytes.
                    var errors=writing?ManagedExtensionInstallationRecovery.Recover(paths,(c,r)=>Emit("install-recovery",c,r),CancellationToken.None,Detail):new string[0];
                    foreach(var row in rows) Emit("install",errors.Count==0?"ManagedInstallCanceled":"ManagedInstallRecoveryRequired",row);
                    if(errors.Count!=0) return new ManagedExtensionInstallResult(false,"ManagedInstallRecoveryRequired",operation,rows);
                    throw;
                }
                catch(Exception ex)
                {
                    string cause=ex is ManagedExtensionValidationException?((ManagedExtensionValidationException)ex).Code:"ManagedInstallStorageFailed";
                    var referenceFailure=(ex as ManagedExtensionValidationException)?.ReferenceFailure;
                    foreach(var row in rows) Emit("install",cause,row,reference:referenceFailure,resourcePath:(ex as ManagedExtensionValidationException)?.ResourcePath); Detail(ex);
                    bool pending=File.Exists(Path.Combine(paths.TransactionsDirectory,"in-"+operation+".json")) || Directory.Exists(Path.Combine(paths.TransactionsDirectory,"in-"+operation));
                    string code=pending?"ManagedInstallRecoveryRequired":writing?"ManagedInstallOutcomeUncertain":cause;
                    if(code!=cause) foreach(var row in rows) Emit("install",code,row);
                    return new ManagedExtensionInstallResult(false,code,operation,rows,referenceFailure);
                }
            }
        }

        private static bool SameInventory(ManagedExtensionInventorySnapshot a,ManagedExtensionInventorySnapshot b)
        {
            if(a.Packages.Count!=b.Packages.Count) return false;
            return a.Packages.All(x=>b.Packages.Any(y=>x.RecordKey==y.RecordKey && x.SourceId==y.SourceId && x.PackageId==y.PackageId &&
                x.ManifestSha256==y.ManifestSha256 && x.Version==y.Version && x.RepositoryIdentitySha256==y.RepositoryIdentitySha256 &&
                x.CatalogSnapshotId==y.CatalogSnapshotId && x.CatalogSha256==y.CatalogSha256 && x.ArtifactSha256==y.ArtifactSha256 &&
                x.InstallationTransactionId==y.InstallationTransactionId && x.StateOperationId==y.StateOperationId && x.DesiredState==y.DesiredState &&
                x.ContentState==y.ContentState && x.DiagnosticCode==y.DiagnosticCode));
        }

        private void ValidateInstallation(ManagedExtensionInstallRequest request,List<ManagedExtensionPackageSnapshot> incoming,
            ManagedExtensionInventorySnapshot inventory,HashSet<string> disabled,CancellationToken token)
        {
            if(inventory.Diagnostics.Count!=0 || inventory.Packages.Any(p=>OwnershipGate(p)!=null)) throw ManagedExtensionJson.Error("ManagedInventoryUncertain");
            var replacements=new HashSet<string>(request.Packages.Where(p=>p.Replacement!=null).Select(p=>p.Replacement.RecordKey),StringComparer.Ordinal);
            foreach(var package in request.Packages.Where(p=>p.Replacement!=null))
            {
                var existing=inventory.Packages.Where(p=>p.RecordKey==package.Replacement.RecordKey).ToList();
                if(existing.Count!=1 || !SameInventory(new ManagedExtensionInventorySnapshot(existing,new string[0]),
                    new ManagedExtensionInventorySnapshot(new[]{package.Replacement},new string[0]))) throw ManagedExtensionJson.Error("ManagedStateChanged");
            }
            foreach(var existing in inventory.Packages.Where(p=>!replacements.Contains(p.RecordKey) && p.DesiredState!=ManagedExtensionDesiredState.PendingRemoval))
                foreach(var candidate in incoming)
                {
                    if(existing.Manifest.Dependencies.Any(d=>d.PackageId==candidate.PackageId && !d.VersionRange.Contains(candidate.Manifest.Version)))
                        throw ManagedExtensionJson.Error("CandidatePackageDependencyUnavailable");
                    var old=request.Packages.First(p=>p.Manifest.PackageId==candidate.PackageId).Replacement;
                    if(old!=null)
                    {
                        var removed=new HashSet<string>(old.Manifest.Modules.Select(m=>m.Id).Except(candidate.Manifest.Modules.Select(m=>m.Id)),StringComparer.Ordinal);
                        if(existing.Manifest.Modules.Any(m=>m.DependsOn.Any(removed.Contains))) throw ManagedExtensionJson.Error("CandidateModuleDependencyUnavailable");
                    }
                }
            var assemblyNames=new HashSet<string>(startupHost.Assemblies.Select(a=>a.Name),StringComparer.OrdinalIgnoreCase);
            foreach(var a in AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic))
            { ManagedExtensionPackageSnapshot owner; if(!TryGetOwner(a,out owner) || !replacements.Contains(owner.RecordKey)) assemblyNames.Add(a.GetName().Name); }
            var moduleIds=new HashSet<string>(startupHost.ModuleIds,StringComparer.OrdinalIgnoreCase);
            foreach(var existing in inventory.Packages.Where(p=>!replacements.Contains(p.RecordKey)))
            {
                assemblyNames.UnionWith(existing.Manifest.Assemblies.SelectMany(ManagedExtensionManifestReader.AssemblyNames));
                foreach(var m in existing.Manifest.Modules) moduleIds.Add(m.Id);
            }
            var payloads=new Dictionary<string,ManagedExtensionInspectedPayload>();
            for(int i=0;i<incoming.Count;i++)
            {
                token.ThrowIfCancellationRequested(); var row=incoming[i]; var package=request.Packages[i];
                if(package.Replacement==null)
                {
                    if(inventory.Packages.Any(p=>p.PackageId==row.PackageId)) throw ManagedExtensionJson.Error("ManagedInstallPackageConflict");
                    ManagedExtensionInstallationRecovery.EnsureNew(paths,row);
                }
                var incomingNames=row.Manifest.Assemblies.SelectMany(ManagedExtensionManifestReader.AssemblyNames).ToArray();
                if(incomingNames.Any(assemblyNames.Contains)) throw ManagedExtensionJson.Error("CandidateAssemblyConflict");
                assemblyNames.UnionWith(incomingNames);
                if(row.Manifest.Modules.Any(m=>!moduleIds.Add(m.Id))) throw ManagedExtensionJson.Error("CandidateModuleConflict");
                if(row.Manifest.Modules.All(m=>disabled.Contains(m.Id))) throw ManagedExtensionJson.Error("ManagedAllModulesDisabled");
                foreach(var file in row.Manifest.Assemblies.Select(a=>a.File).Concat(row.Manifest.Resources))
                    if(ManagedExtensionPaths.Hash(package.Content[file.Path])!=file.Sha256) throw ManagedExtensionJson.Error("FileDigestMismatch");
                ExtensionLocalizationCatalog.Load(row.Manifest.Localization,file=>package.Content[file.Path],token);
                var assemblies=row.Manifest.Assemblies.ToDictionary(a=>a.File.Path,a=>package.Content[a.File.Path],StringComparer.Ordinal);
                payloads.Add(row.RecordKey,ManagedExtensionPayloadInspector.Inspect(row.Manifest,assemblies,token));
            }
            long frozen=request.Packages.Sum(p=>p.ExpandedBytes);
            foreach(var row in inventory.Packages.Where(p=>p.DesiredState==ManagedExtensionDesiredState.Enabled && !replacements.Contains(p.RecordKey)))
            {
                token.ThrowIfCancellationRequested(); long size=row.Manifest.Assemblies.Sum(a=>a.File.Length);
                if(frozen+size>ManagedExtensionManifestReader.MaxExpandedBytes) throw ManagedExtensionJson.Error("ManagedInstallMemoryLimit");
                var content=new Dictionary<string,byte[]>();
                foreach(var a in row.Manifest.Assemblies)
                {
                    string file=Path.Combine(paths.GetPackageDirectory(row.SourceId,row.PackageId),a.File.Path);
                    ManagedExtensionInventoryReader.NoLinks(file); content.Add(a.File.Path,ManagedExtensionInventoryReader.Bytes(file,(int)ManagedExtensionManifestReader.MaxFileBytes,token));
                }
                payloads.Add(row.RecordKey,ManagedExtensionPayloadInspector.Inspect(row.Manifest,content,token)); frozen+=size;
            }
            var combined=inventory.Packages.Where(p=>!replacements.Contains(p.RecordKey)).Concat(incoming).ToList();
            // Upgrades must not break existing consumers, including their exact CLR references.
            ValidateCandidates(combined,payloads,disabled,combined.Where(p=>p.DesiredState==ManagedExtensionDesiredState.Enabled).Select(p=>p.RecordKey),token);
            // Startup eagerly loads dependency-first; reject CLR cycles before accepting new packages.
            var remaining=payloads.Values.SelectMany(p=>p.Assemblies).ToDictionary(a=>a.Metadata.Identity.FullName,StringComparer.Ordinal);
            while(remaining.Count!=0)
            {
                token.ThrowIfCancellationRequested();
                var ready=remaining.Where(a=>!a.Value.Metadata.References.Any(r=>remaining.ContainsKey(r.FullName))).Select(a=>a.Key).ToList();
                if(ready.Count==0) throw ManagedExtensionJson.Error("ManagedAssemblyReferenceCycle");
                foreach(string name in ready) remaining.Remove(name);
            }
        }
    }
}
