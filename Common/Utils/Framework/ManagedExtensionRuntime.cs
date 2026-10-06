using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using Utils.Framework.ManagedExtensions;

namespace Utils.Framework
{
    /// <summary>Host-owned startup domain; independent of any store plugin.</summary>
    public sealed partial class ManagedExtensionRuntime : IManagedExtensionManagementService, IManagedExtensionInstallationService, IExtensionDiscoveryPolicy, IDisposable
    {
        private sealed class Package
        { internal ManagedExtensionPackageSnapshot Row; internal ManagedExtensionInspectedPayload Payload; internal string Code; internal bool Loaded; internal ExtensionLocalizationCatalog Localization; }
        private sealed class Entry
        { internal Package Package; internal ManagedExtensionInspectedAssembly Bytes; internal Assembly Assembly; internal bool Loading; }
        private readonly ManagedExtensionPaths paths;
        private readonly Action<ManagedExtensionRuntimeAudit> audit;
        private readonly Action<Exception> internalError;
        private readonly string startupId = Guid.NewGuid().ToString("N");
        private readonly Dictionary<Assembly, Package> owners = new Dictionary<Assembly, Package>();
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(StringComparer.Ordinal);
        private readonly Dictionary<string, Assembly> hostAssemblies = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        private readonly Dictionary<string, Assembly> hostReferenceBindings = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        private readonly HashSet<Assembly> baseline = new HashSet<Assembly>();
        private readonly List<Package> packages = new List<Package>();
        private readonly List<string> diagnostics = new List<string>();
        private readonly object sync = new object();
        private ManagedExtensionLease lease;
        private IDisposable legacyGuard;
        private bool started, disposed;
        private long auditSequence;
        public ManagedExtensionRuntime(ManagedExtensionPaths paths, Action<ManagedExtensionRuntimeAudit> audit = null, Action<Exception> internalError = null)
        { this.paths = paths ?? throw new ArgumentNullException(nameof(paths)); this.audit = audit; this.internalError = internalError; }
        public ManagedExtensionRuntimeSnapshot Snapshot
        { get { lock(sync) return new ManagedExtensionRuntimeSnapshot(startupId, packages.Select(p=>new ManagedExtensionRuntimePackage(p.Row,p.Code,p.Loaded)),diagnostics); } }
        public bool TryGetOwner(Assembly assembly, out ManagedExtensionPackageSnapshot package)
        { lock(sync) { Package owner; bool found=assembly!=null && owners.TryGetValue(assembly,out owner); package=found?owners[assembly].Row:null; return found; } }
        public ExtensionLocalizationCatalog GetLocalization(Assembly assembly)
        { lock(sync) { Package owner; if(disposed || assembly==null || !owners.TryGetValue(assembly,out owner) || !owner.Loaded || owner.Code!=null) throw new InvalidOperationException("LocalizationOwnerUnavailable"); return owner.Localization ?? ExtensionLocalizationCatalog.Empty; } }
        public void Start(ManagedExtensionHostFacts host, IEnumerable<string> disabledModules, IEnumerable<string> hostDependencies, CancellationToken token)
        {
            if(host==null) throw new ArgumentNullException(nameof(host));
            lock(sync)
            {
                if(started || disposed) throw new InvalidOperationException("Managed startup is single-use."); started=true; startupHost=host;
                startupDisabledModules=new HashSet<string>(disabledModules??new string[0],StringComparer.OrdinalIgnoreCase);
                foreach(var assembly in AppDomain.CurrentDomain.GetAssemblies().Where(a=>!a.IsDynamic))
                { baseline.Add(assembly); hostAssemblies[assembly.GetName().FullName]=assembly; }
                try
                {
                    token.ThrowIfCancellationRequested(); lease=ManagedExtensionLease.Acquire(paths); Emit("startup","ManagedLeaseAcquired",null);
                    var recoveryCodes=new Dictionary<string,string>(StringComparer.Ordinal);
                    diagnostics.AddRange(ManagedExtensionInstallationRecovery.Recover(paths,(code,row)=>Emit("install-recovery",code,row),token,Detail));
                    if(diagnostics.Count==0) diagnostics.AddRange(ManagedExtensionRemovalRecovery.Recover(paths,hostDependencies,baseline.Select(a=>a.GetName().Name).Concat(baseline.SelectMany(a=>a.GetReferencedAssemblies()).Select(a=>a.Name)),
                        (code,row)=>{ if(row?.RecordKey!=null) recoveryCodes[row.RecordKey]=code; Emit("recovery",code,row); },token,null,Detail));
                    var inventory=ManagedExtensionInventoryReader.Read(paths,token); diagnostics.AddRange(inventory.Diagnostics);
                    var inspected=new Dictionary<string,ManagedExtensionInspectedPayload>(); long frozen=0;
                    foreach(var row in inventory.Packages)
                    {
                        var package=new Package{Row=row}; packages.Add(package);
                        if(row.DesiredState!=ManagedExtensionDesiredState.Enabled || row.ContentState!=ManagedExtensionContentState.ContentVerified || row.DiagnosticCode!=null) continue;
                        if(diagnostics.Count!=0) continue;
                        try
                        {
                            token.ThrowIfCancellationRequested();
                            long size=row.Manifest.Assemblies.Sum(a=>a.File.Length)+(row.Manifest.Localization?.Files.Sum(f=>f.Length)??0);
                            if(frozen+size>ManagedExtensionManifestReader.MaxExpandedBytes) throw new ManagedExtensionValidationException("ManagedStartupMemoryLimit");
                            string root=paths.GetPackageDirectory(row.SourceId,row.PackageId);
                            var bytes=new Dictionary<string,byte[]>();
                            foreach(var declaration in row.Manifest.Assemblies)
                            {
                                string file=Path.Combine(root,declaration.File.Path); ManagedExtensionInventoryReader.NoLinks(file);
                                bytes.Add(declaration.File.Path,ManagedExtensionInventoryReader.Bytes(file,(int)ManagedExtensionManifestReader.MaxFileBytes,token));
                            }
                            package.Payload=ManagedExtensionPayloadInspector.Inspect(row.Manifest,bytes,token);
                            var all=row.Manifest.Assemblies.Select(a=>a.File).Concat(row.Manifest.Resources).ToList();
                            byte[] manifest=ManagedExtensionInventoryReader.Bytes(Path.Combine(root,"manifest.json"),ManagedExtensionManifestReader.MaxManifestBytes,token);
                            if(ManagedExtensionPaths.Hash(manifest)!=row.ManifestSha256) throw new ManagedExtensionValidationException("ManifestDigestMismatch");
                            all.Add(new ManagedExtensionFile("manifest.json",manifest.Length,row.ManifestSha256));
                            ManagedExtensionInventoryReader.VerifyTree(root,all,token);
                            package.Localization=ExtensionLocalizationCatalog.LoadDirectory(root,row.Manifest.Localization,token);
                            inspected.Add(row.RecordKey,package.Payload); frozen+=size; Emit("inspect","ManagedPayloadVerified",row);
                        }
                        catch(ManagedExtensionValidationException ex) { package.Code=ex.Code; Emit("inspect",ex.Code,row,resourcePath:ex.ResourcePath); Detail(ex); }
                        catch(IOException ex) { package.Code="ManagedPayloadReadFailed"; Emit("inspect",package.Code,row); Detail(ex); }
                        catch(UnauthorizedAccessException ex) { package.Code="ManagedPayloadReadFailed"; Emit("inspect",package.Code,row); Detail(ex); }
                    }
                    var results=ManagedExtensionCandidatePlanner.Plan(inventory,inspected,host,startupId,token);
                    for(int i=0;i<packages.Count;i++)
                    { var p=packages[i]; string recoveryCode;
                        p.Code=diagnostics.Count!=0?"ManagedRecoveryUncertain":p.Code??(p.Row.DesiredState==ManagedExtensionDesiredState.PendingRemoval && p.Row.RecordKey!=null && recoveryCodes.TryGetValue(p.Row.RecordKey,out recoveryCode)?recoveryCode:results[i].DiagnosticCode); Emit("preflight",p.Code??"CandidatePreflightPassed",p.Row,reference:results[i].ReferenceFailure); }
                    FinalModuleGate(host,startupDisabledModules,token);
                    foreach(var p in packages.Where(p=>p.Code==null))
                        foreach(var bytes in p.Payload.Assemblies) entries.Add(bytes.Metadata.Identity.FullName,new Entry{Package=p,Bytes=bytes});
                    // Eager dependency-first loading prevents adjacent directory probing. All external
                    // references must already name actual host assemblies, not merely claimed host facts.
                    var actualHost = hostAssemblies.Keys.Where(name=>host.Assemblies.Any(a=>a.FullName==name))
                        .Select(name=>ManagedAssemblyIdentity.FromAssemblyName(hostAssemblies[name].GetName())).ToList();
                    foreach(var entry in entries.Values)
                    foreach(var reference in entry.Bytes.Metadata.References.Where(r=>!entries.ContainsKey(r.FullName)))
                    {
                        var selected=ManagedAssemblyIdentity.SelectHostReference(reference,actualHost);
                        if(selected==null) { entry.Package.Code="ManagedHostAssemblyUnavailable"; break; }
                        // Freeze aliases to actual pre-start assemblies. Resolution never probes a
                        // directory, loads another version or adopts an assembly loaded later.
                        hostReferenceBindings[reference.FullName]=hostAssemblies[selected.FullName];
                        if(selected.FullName!=reference.FullName)
                            Emit("preload","ManagedHostReferenceUpgraded",entry.Package.Row,entry.Bytes.Metadata.Identity.Name,
                                reference:new ManagedExtensionAssemblyReferenceFailure(entry.Bytes.Metadata.Identity.Name,reference,new[]{selected.FullName}));
                    }
                    var order=AssemblyOrder(); PropagatePackageFailures();
                    legacyGuard=ExtensionAssemblyLoader.RegisterResolutionGuard(args=>!IsOwnedRequest(args));
                    AppDomain.CurrentDomain.AssemblyResolve+=Resolve;
                    foreach(var entry in order)
                    {
                        token.ThrowIfCancellationRequested(); if(entry.Package.Code!=null) continue;
                        try { Load(entry); }
                        catch(Exception ex) { entry.Package.Code="ManagedAssemblyLoadFailed"; Emit("load",entry.Package.Code,entry.Package.Row,entry.Bytes.Metadata.Identity.Name); Detail(ex); PropagatePackageFailures(); }
                    }
                    foreach(var p in packages.Where(p=>p.Code==null))
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            var actual=owners.Where(pair=>ReferenceEquals(pair.Value,p)).SelectMany(pair=>pair.Key.GetTypes()).ToList();
                            foreach(var declared in p.Row.Manifest.Modules)
                            {
                                Type type=actual.SingleOrDefault(t=>t.FullName==declared.EntryType && t.Assembly.GetName().Name==declared.AssemblyName);
                                if(type==null || !type.IsVisible || type.IsAbstract || type.ContainsGenericParameters || !typeof(IPhinixExtensionModule).IsAssignableFrom(type) || type.GetConstructor(Type.EmptyTypes)==null)
                                    throw new ManagedExtensionValidationException("ManagedEntryRuntimeMismatch");
                            }
                            Emit("discovery","ManagedEntriesReady",p.Row);
                        }
                        catch(Exception ex) { p.Code=ex is ManagedExtensionValidationException?((ManagedExtensionValidationException)ex).Code:"ManagedTypeLoadFailed"; Emit("discovery",p.Code,p.Row); Detail(ex); }
                    }
                    PropagatePackageFailures(); Emit("startup","ManagedStartupCompleted",null);
                }
                catch(OperationCanceledException) { FailDomain("ManagedStartupCanceled"); throw; }
                catch(Exception ex) { FailDomain(ex is ManagedExtensionValidationException?((ManagedExtensionValidationException)ex).Code:"ManagedStartupFailed"); Detail(ex); }
            }
        }
        private void FinalModuleGate(ManagedExtensionHostFacts host, HashSet<string> disabled, CancellationToken token)
        {
            var codes=ManagedExtensionModuleGate.Check(host,packages.Where(p=>p.Code==null).Select(p=>p.Row),disabled,token);
            foreach(var p in packages.Where(p=>p.Code==null))
            {
                string code;
                if(codes.TryGetValue(p.Row.RecordKey,out code)) { p.Code=code; Emit("preload",code,p.Row); }
            }
            PropagatePackageFailures();
        }
        private List<Entry> AssemblyOrder()
        {
            var remaining=new HashSet<Entry>(entries.Values.Where(e=>e.Package.Code==null)); var order=new List<Entry>();
            while(remaining.Count!=0)
            {
                var ready=remaining.Where(e=>!e.Bytes.Metadata.References.Any(r=>entries.ContainsKey(r.FullName) && remaining.Contains(entries[r.FullName])))
                    .OrderBy(e=>e.Bytes.Metadata.Identity.FullName,StringComparer.Ordinal).ToList();
                if(ready.Count==0) { foreach(var e in remaining) { e.Package.Code="ManagedAssemblyReferenceCycle"; Emit("preload",e.Package.Code,e.Package.Row); } break; }
                foreach(var e in ready) { remaining.Remove(e); order.Add(e); }
            }
            return order;
        }
        private void PropagatePackageFailures()
        {
            bool changed;
            do
            {
                changed=false;
                foreach(var p in packages.Where(p=>p.Code==null))
                    if(packages.Any(other=>other.Code!=null && other.Row.Manifest!=null &&
                        (p.Row.Manifest.Dependencies.Any(d=>d.PackageId==other.Row.PackageId) || p.Row.Manifest.Modules.Any(m=>m.DependsOn.Any(id=>other.Row.Manifest.Modules.Any(n=>n.Id==id))))))
                    { p.Code="ManagedDependencyRejected"; Emit("preload",p.Code,p.Row); changed=true; }
            }while(changed);
        }
        private bool IsOwnedRequest(ResolveEventArgs args)
        {
            lock(sync)
            {
                if(args.RequestingAssembly==null) return false;
                if(owners.ContainsKey(args.RequestingAssembly)) return true;
                Entry entry; return entries.TryGetValue(args.RequestingAssembly.GetName().FullName,out entry) && entry.Loading;
            }
        }
        private Assembly Resolve(object sender, ResolveEventArgs args)
        {
            lock(sync)
            {
                if(disposed || !IsOwnedRequest(args)) return null;
                Package requester; if(!owners.TryGetValue(args.RequestingAssembly,out requester)) requester=entries[args.RequestingAssembly.GetName().FullName].Package;
                string name; try { name=new AssemblyName(args.Name).FullName; } catch { return null; }
                var origin=entries.Values.FirstOrDefault(e=>e.Package==requester && e.Bytes.Metadata.Identity.FullName==args.RequestingAssembly.GetName().FullName);
                if(requester.Code!=null || origin==null || !origin.Bytes.Metadata.References.Any(r=>r.FullName==name))
                { Emit("resolve","ManagedResolutionUndeclared",requester.Row); return null; }
                Assembly host; if(hostReferenceBindings.TryGetValue(name,out host)) return host;
                Entry dependency;
                if(!entries.TryGetValue(name,out dependency) || dependency.Package.Code!=null ||
                    dependency.Package!=requester && !requester.Row.Manifest.Dependencies.Any(d=>d.PackageId==dependency.Package.Row.PackageId))
                { Emit("resolve","ManagedResolutionRejected",requester.Row); return null; }
                // All approved DLLs were loaded eagerly; a resolver never probes or loads an unknown file.
                Emit("resolve",dependency.Assembly!=null?"ManagedResolutionOwned":"ManagedResolutionNotReady",requester.Row,dependency.Bytes.Metadata.Identity.Name);
                return dependency.Assembly;
            }
        }
        private Assembly Load(Entry entry)
        {
            entry.Loading=true;
            try
            {
                Assembly assembly=Assembly.Load(entry.Bytes.CopyBytes());
                // Returning an existing host assembly would break ownership and discovery isolation.
                if(baseline.Contains(assembly) || assembly.GetName().FullName!=entry.Bytes.Metadata.Identity.FullName) throw new ManagedExtensionValidationException("ManagedLoadIdentityMismatch");
                entry.Assembly=assembly; owners.Add(assembly,entry.Package); entry.Package.Loaded=true;
                Emit("load","ManagedAssemblyLoaded",entry.Package.Row,entry.Bytes.Metadata.Identity.Name); return assembly;
            }
            finally { entry.Loading=false; }
        }
        public bool ShouldScanAssembly(Assembly assembly)
        {
            lock(sync)
            {
                Package p; if(owners.TryGetValue(assembly,out p)) return !disposed && p.Code==null;
                // Failed loads may have left an assembly in the AppDomain. It is never adopted.
                return baseline.Contains(assembly) || !entries.ContainsKey(assembly.GetName().FullName);
            }
        }
        public bool ShouldDiscoverType(Type type)
        { lock(sync) { Package p; return !owners.TryGetValue(type.Assembly,out p) || !disposed && p.Code==null && p.Row.Manifest.Modules.Any(m=>m.EntryType==type.FullName && m.AssemblyName==type.Assembly.GetName().Name); } }
        public void RecordLifecycle(IEnumerable<ExtensionDiscoveryResult> results)
        {
            lock(sync)
                foreach(var result in results??new ExtensionDiscoveryResult[0])
                {
                    var p=packages.FirstOrDefault(package=>package.Loaded && package.Code==null && package.Row.Manifest!=null && package.Row.Manifest.Modules.Any(m=>m.Id==result.ExtensionId && m.AssemblyName==result.AssemblyName));
                    if(p!=null) Emit("lifecycle","ManagedModule"+result.State,p.Row,result.AssemblyName,result.ExtensionId);
                }
        }
        private void FailDomain(string code)
        { diagnostics.Add(code); foreach(var p in packages) p.Code=code; Emit("startup",code,null); }
        private void Emit(string stage,string code,ManagedExtensionPackageSnapshot row,string assembly=null,string module=null,ManagedExtensionAssemblyReferenceFailure reference=null,string resourcePath=null)
        { try { audit?.Invoke(new ManagedExtensionRuntimeAudit(startupId,stage,code,row,assembly,module,++auditSequence,reference,resourcePath)); } catch { } }
        private void Detail(Exception ex) { try { internalError?.Invoke(ex); } catch { } }
        public void Dispose()
        {
            lock(sync)
            { if(disposed) return; disposed=true; AppDomain.CurrentDomain.AssemblyResolve-=Resolve; legacyGuard?.Dispose(); lease?.Dispose(); foreach(var package in packages) package.Localization=null; }
        }
    }
}
