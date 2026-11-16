using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Utils.Framework.ManagedExtensions;

namespace Utils.Framework.Themes
{
    public sealed class InstalledThemePackage
    {
        internal InstalledThemePackage(ManagedPackageStorageRecord record,ThemePackageManifest manifest,IDictionary<string,byte[]> documents)
        { Record=record; Manifest=manifest; this.documents=new Dictionary<string,byte[]>(documents,StringComparer.Ordinal); }
        internal ManagedPackageStorageRecord Record { get; }
        private readonly Dictionary<string,byte[]> documents;
        public ThemePackageManifest Manifest { get; }
        public string ArtifactSha256 => Record.ArtifactSha256;
        public string SourceId => Record.Origin?.SourceId??Record.SourceId;
        public ThemePackageOrigin Origin => Record.Origin;
        public byte[] CopyDocument(string path) => (byte[])documents[path].Clone();
    }
    public sealed class ThemePackageInventory
    {
        internal ThemePackageInventory(IEnumerable<InstalledThemePackage> packages,IEnumerable<string> diagnostics)
        { Packages=new List<InstalledThemePackage>(packages).AsReadOnly(); Diagnostics=new List<string>(diagnostics).AsReadOnly(); }
        public ReadOnlyCollection<InstalledThemePackage> Packages { get; }
        public ReadOnlyCollection<string> Diagnostics { get; }
    }

    // Host-owned pure data storage. Installation and deletion use the same replay engines as DLL packages.
    public sealed class ManagedThemePackageStore : IDisposable
    {
        private readonly ManagedExtensionPaths paths;
        private readonly ManagedExtensionLease lease;
        private readonly Action<string> completeRemoval;
        private bool disposed;
        private const int MaxPackages=256;
        public ManagedThemePackageStore(string saveRoot,Action<string> completeRemoval)
        {
            paths=new ManagedExtensionPaths(saveRoot,"ManagedThemes"); this.completeRemoval=completeRemoval??throw new ArgumentNullException(nameof(completeRemoval));
            lease=ManagedExtensionLease.Acquire(paths);
        }
        public ThemePackageInventory Refresh(CancellationToken token)
        {
            using(ManagedPackageMutation.Enter())
            {
            RequireLive();
            var errors=ManagedExtensionInstallationRecovery.Recover(paths,null,token).ToList();
            if(errors.Count==0)
            {
                try { RecoverRemovals(token); }
                catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is ManagedExtensionValidationException)
                { errors.Add(Code(ex)); }
            }
            return ReadInventory(paths,token,errors);
                    }
}
        private static ThemePackageInventory ReadInventory(ManagedExtensionPaths paths,CancellationToken token,List<string> errors,bool hidePending=true)
        {
            var packages=new List<InstalledThemePackage>();
            long retainedBytes=0;
            var recordKeys=new HashSet<string>(StringComparer.Ordinal);
            try
            {
                ManagedExtensionInventoryReader.NoLinks(paths.InstalledRecordsDirectory);
                if(File.Exists(paths.InstalledRecordsDirectory)) throw Error("DataRecordsRootInvalid");
                if(Directory.Exists(paths.InstalledRecordsDirectory))
                {
                    int count=0;
                    foreach(string file in Directory.EnumerateFileSystemEntries(paths.InstalledRecordsDirectory))
                    {
                        token.ThrowIfCancellationRequested(); if(++count>MaxPackages) throw Error("DataPackageLimit");
                        try
                        {
                            ManagedExtensionInventoryReader.NoLinks(file);
                            var receipt=ManagedExtensionInventoryReader.Bytes(file,65536,token);
                            // Read only enough identity to resolve bounded owned locations, then validate canonical facts.
                            var f=ManagedExtensionJson.Object(ManagedExtensionJson.Read(receipt,65536),"schemaVersion","kind","sourceKind","sourceId","packageId","version","manifestSha256","artifactSha256","installationTransactionId","files","origin");
                            string id=ManagedExtensionJson.Id(ManagedExtensionJson.Required(f,"packageId"));
                            if(Path.GetFileName(file)!=ManagedExtensionPaths.PackageKey(ManagedPackageStorageRecord.LocalSource,id)+".json") throw Error("DataRecordPathMismatch");
                            recordKeys.Add(Path.GetFileNameWithoutExtension(file));
                            string root=paths.GetPackageDirectory(ManagedPackageStorageRecord.LocalSource,id);
                            ManagedExtensionInventoryReader.NoLinks(root);
                            string manifestPath=Path.Combine(root,"manifest.json"),statePath=paths.GetDesiredStatePath(ManagedPackageStorageRecord.LocalSource,id);
                            ManagedExtensionInventoryReader.NoLinks(manifestPath); ManagedExtensionInventoryReader.NoLinks(statePath);
                            var manifestBytes=ManagedExtensionInventoryReader.Bytes(manifestPath,65536,token);
                            var row=ManagedPackageStorageRecord.Read(receipt,ManagedExtensionInventoryReader.Bytes(statePath,8192,token),manifestBytes);
                            ManagedExtensionInventoryReader.VerifyTree(root,row.Files,token);
                            var manifest=ThemePackageManifest.Read(manifestBytes);
                            retainedBytes+=manifest.Themes.Sum(t=>(long)t.Length);
                            if(retainedBytes>64L*1024*1024) throw Error("ThemeInventoryMemoryLimit");
                            var documents=new Dictionary<string,byte[]>(StringComparer.Ordinal);
                            foreach(var theme in manifest.Themes)
                            {
                                string path=Path.Combine(root,theme.Path); ManagedExtensionInventoryReader.NoLinks(path);
                                var bytes=ManagedExtensionInventoryReader.Bytes(path,1048576,token);
                                if(bytes.Length!=theme.Length || ThemePackageJson.Hash(bytes)!=theme.Sha256) throw Error("ThemeDigestMismatch");
                                ThemePackageDocument.Validate(bytes,theme.SchemaVersion); documents.Add(theme.Path,bytes);
                            }
                            packages.Add(new InstalledThemePackage(row,manifest,documents));
                        }
                        catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is ManagedExtensionValidationException)
                        { errors.Add(Path.GetFileName(file)+": "+Code(ex)); }
                    }
                }
                foreach(string directory in new[]{paths.PackagesDirectory,paths.DesiredStateDirectory})
                {
                    ManagedExtensionInventoryReader.NoLinks(directory);
                    if(File.Exists(directory)) throw Error("DataOwnedRootInvalid");
                    if(!Directory.Exists(directory)) continue;
                    int entries=0;
                    foreach(string entry in Directory.EnumerateFileSystemEntries(directory))
                    {
                        token.ThrowIfCancellationRequested();if(++entries>MaxPackages) throw Error("DataPackageLimit");
                        ManagedExtensionInventoryReader.NoLinks(entry);
                        string key=directory==paths.PackagesDirectory?Path.GetFileName(entry):Path.GetFileNameWithoutExtension(entry);
                        if(!recordKeys.Contains(key) || directory==paths.DesiredStateDirectory && (Directory.Exists(entry) || Path.GetFileName(entry)!=key+".json") || directory==paths.PackagesDirectory && !Directory.Exists(entry)) throw Error("DataUnrecordedOwnership");
                    }
                }
                // An in-progress commit must not expose mixed package versions.
                if(hidePending && Directory.Exists(paths.TransactionsDirectory) && Directory.EnumerateFileSystemEntries(paths.TransactionsDirectory).Any()) { packages.Clear(); errors.Add("ThemeRecoveryRequired"); }
            }
            catch(Exception ex) when(ex is IOException || ex is UnauthorizedAccessException || ex is ManagedExtensionValidationException) { errors.Add(Code(ex)); }
            return new ThemePackageInventory(packages,errors);
        }
        public void Install(ThemePackage package,string expectedArtifact,string hostVersion,Action<string> checkOtherPackage,CancellationToken token)
        {
            using(ManagedPackageMutation.Enter())
            {
            RequireLive(); if(package==null) throw new ArgumentNullException(nameof(package));
            var inventory=Refresh(token); RequireCertain(inventory);
            if(!package.Manifest.PhinixRange.Contains(ManagedExtensionVersion.Parse(hostVersion))) throw Error("CandidateHostIncompatible");
            checkOtherPackage?.Invoke(package.Manifest.PackageId);
            var old=inventory.Packages.SingleOrDefault(p=>p.Manifest.PackageId==package.Manifest.PackageId);
            if((old?.ArtifactSha256)!=expectedArtifact) throw Error("ManagedStateChanged");
            if(old!=null && (old.ArtifactSha256==package.Sha256 || package.Manifest.Version.CompareTo(old.Manifest.Version)<0)) throw Error("LocalThemeVersionConflict");
            if(old==null && inventory.Packages.Count>=MaxPackages) throw Error("DataPackageLimit");
            var files=package.Manifest.Themes.ToDictionary(t=>t.Path,t=>package.CopyFile(t.Path),StringComparer.Ordinal);
            byte[] manifest=package.CopyFile("manifest.json");
            var row=ManagedPackageStorageRecord.Create(package.Manifest.PackageId,package.Manifest.Version.ToString(),package.Sha256,manifest,
                package.Manifest.Themes.Select(t=>new ManagedExtensionFile(t.Path,t.Length,t.Sha256)).Concat(new[]{new ManagedExtensionFile("manifest.json",manifest.Length,package.ManifestSha256)}),Guid.NewGuid().ToString("N"),package.Origin);
            {
                ManagedExtensionInstallationRecovery.InstallData(paths,row,manifest,files,old?.Record,token,t=>
                {
                    var fresh=ReadInventory(paths,t,new List<string>(),false);
                    // Compare the pre-decision inventory, including unchanged packages.
                    if(fresh.Diagnostics.Count!=0 || fresh.Packages.Count!=inventory.Packages.Count || inventory.Packages.Any(p=>!fresh.Packages.Any(v=>v.Record.ReceiptBytes.SequenceEqual(p.Record.ReceiptBytes)))) throw Error("DataInventoryUncertain");
                    checkOtherPackage?.Invoke(package.Manifest.PackageId);
                });
            }
                    }
}
        public void Remove(string packageId,string expectedArtifact,string selectionIntent,CancellationToken token)
        {
            using(ManagedPackageMutation.Enter())
            {
            RequireLive(); var inventory=Refresh(token); RequireCertain(inventory);
            var old=inventory.Packages.SingleOrDefault(p=>p.Manifest.PackageId==packageId);
            if(old==null || old.ArtifactSha256!=expectedArtifact) throw Error("ManagedStateChanged");
            if(selectionIntent==null || selectionIntent.Length>512 || selectionIntent.Any(c=>char.IsControl(c) && c!='\n')) throw Error("RemovalIntentInvalid");
            string operation=Guid.NewGuid().ToString("N");
            byte[] manifest=ManagedExtensionInventoryReader.Bytes(Path.Combine(paths.GetPackageDirectory(old.Record.SourceId,packageId),"manifest.json"),65536,token);
            byte[] journal=RemovalBytes(operation,old.Record,manifest,selectionIntent);
            string path=Path.Combine(paths.TransactionsDirectory,"rm-"+operation+".json");
            ManagedExtensionInstallationRecovery.ValidateFilePath(path); ManagedExtensionInventoryReader.NoLinks(path);
            Directory.CreateDirectory(paths.TransactionsDirectory); token.ThrowIfCancellationRequested();
            using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {file.Write(journal,0,journal.Length);file.Flush(true);}
            // The journal is durable removal intent: finish or preserve for replay, never cancel halfway and report rollback.
            FinishRemoval(path,journal,CancellationToken.None);
                    }
}
        private static byte[] RemovalBytes(string operation,ManagedPackageStorageRecord row,byte[] manifest,string intent)
            => Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"kind\":\"data-removal\",\"operationId\":\""+operation+"\",\"receiptBase64\":\""+Convert.ToBase64String(row.ReceiptBytes)+
                "\",\"stateBase64\":\""+Convert.ToBase64String(row.StateBytes)+"\",\"manifestBase64\":\""+Convert.ToBase64String(manifest)+"\",\"intentBase64\":\""+Convert.ToBase64String(Encoding.UTF8.GetBytes(intent))+"\"}");
        private void RecoverRemovals(CancellationToken token)
        {
            if(!Directory.Exists(paths.TransactionsDirectory)) return;
            var entries=Directory.EnumerateFileSystemEntries(paths.TransactionsDirectory).Take(2049).ToArray();
            if(entries.Length>2048) throw Error("TransactionLimit");
            foreach(string entry in entries)
            {
                ManagedExtensionInventoryReader.NoLinks(entry);
                if(Directory.Exists(entry) && !File.Exists(entry+".json")) throw Error("OrphanRemovalTransaction");
            }
            foreach(string path in entries.Where(p=>File.Exists(p) && Path.GetFileName(p).StartsWith("rm-",StringComparison.Ordinal)).OrderBy(p=>p,StringComparer.Ordinal))
            { token.ThrowIfCancellationRequested(); ManagedExtensionInventoryReader.NoLinks(path); FinishRemoval(path,ManagedExtensionInventoryReader.Bytes(path,4*1024*1024,token),token); }
        }
        private void FinishRemoval(string path,byte[] bytes,CancellationToken token)
        {
            var f=ManagedExtensionJson.Object(ManagedExtensionJson.Read(bytes,4*1024*1024),"schemaVersion","kind","operationId","receiptBase64","stateBase64","manifestBase64","intentBase64");
            if(ManagedExtensionJson.Integer(ManagedExtensionJson.Required(f,"schemaVersion"),3,3)!=3 || ManagedExtensionJson.Text(ManagedExtensionJson.Required(f,"kind"),32)!="data-removal") throw Error("RemovalJournalSchemaInvalid");
            string operation=ManagedExtensionJson.Hex(ManagedExtensionJson.Required(f,"operationId"),32);
            if(Path.GetFileName(path)!="rm-"+operation+".json") throw Error("RemovalJournalIdentityMismatch");
            byte[] receipt=Decode(f,"receiptBase64",65536),state=Decode(f,"stateBase64",8192),manifest=Decode(f,"manifestBase64",65536);
            string intent;
            try {intent=new UTF8Encoding(false,true).GetString(Decode(f,"intentBase64",2048,allowEmpty:true));}
            catch(DecoderFallbackException ex) {throw new ManagedExtensionValidationException("RemovalIntentInvalid",ex);}
            if(intent.Length>512 || intent.Any(c=>char.IsControl(c) && c!='\n')) throw Error("RemovalIntentInvalid");
            var row=ManagedPackageStorageRecord.Read(receipt,state,manifest);
            if(!bytes.SequenceEqual(RemovalBytes(operation,row,manifest,intent))) throw Error("RemovalJournalNotCanonical");
            ManagedExtensionRemovalRecovery.FinishOwned(paths,row.SourceId,row.PackageId,operation,receipt,state,row.Files,path,bytes,token,null,()=>completeRemoval(intent));
        }
        private static byte[] Decode(Dictionary<string,System.Xml.Linq.XElement> f,string key,int maximum,bool allowEmpty=false)
        {
            var node=ManagedExtensionJson.Required(f,key); ManagedExtensionJson.Type(node,"string");
            // Removing an inactive package has no selected-theme recovery to perform.
            if(allowEmpty && node.Value.Length==0) return new byte[0];
            try { var bytes=Convert.FromBase64String(ManagedExtensionJson.Text(node,(maximum+2)/3*4)); if(bytes.Length>maximum) throw Error("DataRecordLimit"); return bytes; }
            catch(FormatException ex) { throw new ManagedExtensionValidationException("DataRecordInvalid",ex); }
        }
        internal static void CheckIdentity(string saveRoot,string packageId)
        {
            var inventory=ReadInventory(new ManagedExtensionPaths(saveRoot,"ManagedThemes"),CancellationToken.None,new List<string>());
            RequireCertain(inventory); if(inventory.Packages.Any(p=>p.Manifest.PackageId==packageId)) throw Error("ManagedInstallPackageConflict");
        }
        private static void RequireCertain(ThemePackageInventory inventory) { if(inventory.Diagnostics.Count!=0) throw Error("DataInventoryUncertain"); }
        private void RequireLive() { if(disposed) throw new ObjectDisposedException(nameof(ManagedThemePackageStore)); }
        private static string Code(Exception ex) => (ex as ManagedExtensionValidationException)?.Code??"ThemeStorageFailed";
        private static ManagedExtensionValidationException Error(string code) => new ManagedExtensionValidationException(code);
        public void Dispose() { if(disposed) return; disposed=true; lease.Dispose(); }
    }
}
