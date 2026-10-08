using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace Utils.Framework.ManagedExtensions
{
    /// <summary>Lease-owned installation journal: rollback preparation, roll forward a durable commit decision.</summary>
    internal static class ManagedExtensionInstallationRecovery
    {
        internal const int MaxJournalBytes=32*1024*1024;
        private sealed class Item
        {
            internal byte[] ReceiptBytes,StateBytes,ManifestBytes,OldReceiptBytes,OldStateBytes,OldManifestBytes;
            internal ManagedExtensionInventoryReader.Receipt Receipt,OldReceipt;
            internal ManagedExtensionPackageSnapshot Row,OldRow;
        }
        private sealed class Journal
        { internal int Schema; internal string Operation,Phase; internal List<Item> Items; }

        internal static IReadOnlyList<string> Recover(ManagedExtensionPaths paths,Action<string,ManagedExtensionPackageSnapshot> audit,
            CancellationToken token,Action<Exception> detail=null)
        {
            var errors=new List<string>();
            try
            {
                var journals=new List<string>(); var directories=new List<string>(); int count=0;
                ManagedExtensionInventoryReader.NoLinks(paths.TransactionsDirectory);
                if(File.Exists(paths.TransactionsDirectory)) throw Error("TransactionRootInvalid");
                if(Directory.Exists(paths.TransactionsDirectory)) foreach(string entry in Directory.EnumerateFileSystemEntries(paths.TransactionsDirectory))
                {
                    token.ThrowIfCancellationRequested(); if(++count>2048) throw Error("TransactionLimit");
                    ManagedExtensionInventoryReader.NoLinks(entry); string name=Path.GetFileName(entry);
                    bool directory=Directory.Exists(entry);
                    if(System.Text.RegularExpressions.Regex.IsMatch(name,directory?@"\Arm-[0-9a-f]{32}\z":@"\Arm-[0-9a-f]{32}\.json\z")) continue;
                    if(!System.Text.RegularExpressions.Regex.IsMatch(name,directory?@"\Ain-[0-9a-f]{32}\z":@"\Ain-[0-9a-f]{32}\.json\z")) throw Error("UnknownManagedTransaction");
                    if(directory) directories.Add(entry); else journals.Add(entry);
                }
                if(directories.Any(d=>!journals.Contains(d+".json"))) throw Error("OrphanInstallTransaction");
                foreach(string path in journals.OrderBy(p=>p,StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested(); var journal=Parse(ManagedExtensionInventoryReader.Bytes(path,MaxJournalBytes,token));
                    if(Path.GetFileName(path)!="in-"+journal.Operation+".json") throw Error("InstallJournalIdentityMismatch");
                    foreach(var item in journal.Items) Emit(audit,"InstallReplayStarted",item.Row);
                    if(journal.Phase=="preparing") Rollback(paths,journal,path,token);
                    else Finish(paths,journal,path,token,null,audit);
                    foreach(var item in journal.Items) Emit(audit,journal.Phase=="preparing"?"InstallPreparationRolledBack":"InstallRecovered",item.Row);
                }
            }
            catch(ManagedExtensionValidationException ex) { errors.Add(ex.Code); Emit(audit,ex.Code,null); Detail(detail,ex); }
            catch(IOException ex) { errors.Add("InstallRecoveryStorageFailed"); Emit(audit,"InstallRecoveryStorageFailed",null); Detail(detail,ex); }
            catch(UnauthorizedAccessException ex) { errors.Add("InstallRecoveryStorageFailed"); Emit(audit,"InstallRecoveryStorageFailed",null); Detail(detail,ex); }
            return errors.AsReadOnly();
        }

        internal static void Install(ManagedExtensionPaths paths,ManagedExtensionInstallRequest request,
            IList<ManagedExtensionPackageSnapshot> rows,CancellationToken token,Action<string> fault,Action<string,ManagedExtensionPackageSnapshot> audit,Action<CancellationToken> revalidate=null,Action beginWrite=null)
        {
            var journal=new Journal {Schema=2,Operation=rows[0].InstallationTransactionId,Phase="preparing",Items=new List<Item>()};
            for(int i=0;i<rows.Count;i++)
            {
                var row=rows[i]; var package=request.Packages[i];
                var item=new Item {Row=row,ManifestBytes=package.ManifestBytes,ReceiptBytes=ReceiptBytes(row,package.ManifestBytes.Length),StateBytes=StateBytes(row)};
                if(package.Replacement!=null)
                {
                    item.OldRow=package.Replacement;
                    item.OldReceiptBytes=ManagedExtensionInventoryReader.Bytes(paths.GetInstalledRecordPath(row.SourceId,row.PackageId),ManagedExtensionInventoryReader.MaxRecordBytes,token);
                    item.OldStateBytes=ManagedExtensionInventoryReader.Bytes(paths.GetDesiredStatePath(row.SourceId,row.PackageId),8192,token);
                    item.OldManifestBytes=ManagedExtensionInventoryReader.Bytes(Path.Combine(paths.GetPackageDirectory(row.SourceId,row.PackageId),"manifest.json"),ManagedExtensionManifestReader.MaxManifestBytes,token);
                }
                journal.Items.Add(item);
            }
            // Validate the exact durable format and byte limits before creating any work tree.
            journal=Parse(Serialize(journal));
            string path=JournalPath(paths,journal),work=Work(paths,journal),stage=Path.Combine(work,"stage");
            ValidateStoragePaths(paths,journal);
            token.ThrowIfCancellationRequested(); foreach(var item in journal.Items) EnsureBefore(paths,journal,item,token);
            beginWrite?.Invoke();
            MakeDirectory(paths.TransactionsDirectory); WriteNew(path,Serialize(journal));
            foreach(var item in journal.Items) Emit(audit,"InstallJournalPrepared",item.Row); fault?.Invoke("journal-written");
            MakeDirectory(stage);
            for(int i=0;i<journal.Items.Count;i++)
            {
                var item=journal.Items[i]; var package=request.Packages[i];
                string root=Staged(paths,journal,item); MakeDirectory(root);
                foreach(var file in item.Receipt.Files)
                {
                    token.ThrowIfCancellationRequested(); string destination=Path.Combine(root,file.Path); MakeDirectory(Path.GetDirectoryName(destination));
                    WriteNew(destination,file.Path=="manifest.json"?package.ManifestBytes:package.Content[file.Path]); fault?.Invoke("file-written");
                }
                ManagedExtensionInventoryReader.VerifyTree(root,item.Receipt.Files,token); Emit(audit,"InstallPackageStaged",item.Row); fault?.Invoke("package-staged");
            }
            VerifyWork(paths,journal,token);
            foreach(var item in journal.Items) { EnsureBefore(paths,journal,item,token); ManagedExtensionInventoryReader.VerifyTree(Staged(paths,journal,item),item.Receipt.Files,token); }
            token.ThrowIfCancellationRequested();
            var committed=new Journal {Schema=journal.Schema,Operation=journal.Operation,Phase="committing",Items=journal.Items};
            string transition=Path.Combine(work,"transition.json"); WriteNew(transition,Serialize(committed)); fault?.Invoke("transition-written");
            Exact(path,Serialize(journal),MaxJournalBytes,token); Exact(transition,Serialize(committed),MaxJournalBytes,token);
            revalidate?.Invoke(token);
            foreach(var item in journal.Items) { EnsureBefore(paths,journal,item,token); ManagedExtensionInventoryReader.VerifyTree(Staged(paths,journal,item),item.Receipt.Files,token); }
            token.ThrowIfCancellationRequested();
            File.Replace(transition,path,null); // Durable decision: subsequent cancellation cannot roll this batch back.
            foreach(var item in journal.Items) Emit(audit,"InstallCommitDecided",item.Row); fault?.Invoke("commit-decided");
            Finish(paths,committed,path,CancellationToken.None,fault,audit);
        }

        internal static void EnsureNew(ManagedExtensionPaths paths,ManagedExtensionPackageSnapshot row)
        {
            RequireAbsent(paths.GetPackageDirectory(row.SourceId,row.PackageId));
            RequireAbsent(paths.GetInstalledRecordPath(row.SourceId,row.PackageId));
            RequireAbsent(paths.GetDesiredStatePath(row.SourceId,row.PackageId));
        }
        private static void EnsureBefore(ManagedExtensionPaths paths,Journal journal,Item item,CancellationToken token)
        {
            if(item.OldRow==null) { EnsureNew(paths,item.Row); return; }
            RequireAbsent(Backup(paths,journal,item));
            Exact(paths.GetInstalledRecordPath(item.Row.SourceId,item.Row.PackageId),item.OldReceiptBytes,ManagedExtensionInventoryReader.MaxRecordBytes,token);
            Exact(paths.GetDesiredStatePath(item.Row.SourceId,item.Row.PackageId),item.OldStateBytes,8192,token);
            ManagedExtensionInventoryReader.VerifyTree(paths.GetPackageDirectory(item.Row.SourceId,item.Row.PackageId),item.OldReceipt.Files,token);
        }
        private static void RequireAbsent(string path)
        {
            ManagedExtensionInventoryReader.NoLinks(path);
            if(File.Exists(path) || Directory.Exists(path)) throw Error("ManagedInstallTargetExists");
            string parent=Path.GetDirectoryName(path); if(!Directory.Exists(parent)) return;
            int count=0;
            foreach(string entry in Directory.EnumerateFileSystemEntries(parent))
            {
                if(++count>4096) throw Error("ManagedInstallDirectoryLimit");
                if(string.Equals(Path.GetFileName(entry),Path.GetFileName(path),StringComparison.OrdinalIgnoreCase)) throw Error("ManagedInstallTargetExists");
            }
        }
        private static void Finish(ManagedExtensionPaths paths,Journal journal,string journalPath,CancellationToken token,
            Action<string> fault,Action<string,ManagedExtensionPackageSnapshot> audit)
        {
            VerifyWork(paths,journal,token); Exact(journalPath,Serialize(journal),MaxJournalBytes,token);
            // Recheck the entire batch before making the next replay mutation.
            foreach(var item in journal.Items)
            {
                string staged=Staged(paths,journal,item),target=paths.GetPackageDirectory(item.Row.SourceId,item.Row.PackageId);
                if(item.OldRow!=null) { VerifyReplacement(paths,journal,item,token); continue; }
                ManagedExtensionInventoryReader.NoLinks(staged); ManagedExtensionInventoryReader.NoLinks(target);
                if(File.Exists(staged) || File.Exists(target) || Directory.Exists(staged)==Directory.Exists(target)) throw Error("InstallDirectoryConflict");
                ManagedExtensionInventoryReader.VerifyTree(Directory.Exists(staged)?staged:target,item.Receipt.Files,token);
                OptionalExact(paths.GetInstalledRecordPath(item.Row.SourceId,item.Row.PackageId),item.ReceiptBytes,token);
                OptionalExact(paths.GetDesiredStatePath(item.Row.SourceId,item.Row.PackageId),item.StateBytes,token);
            }
            MakeDirectory(paths.PackagesDirectory); MakeDirectory(paths.InstalledRecordsDirectory); MakeDirectory(paths.DesiredStateDirectory);
            foreach(var item in journal.Items)
            {
                token.ThrowIfCancellationRequested(); string staged=Staged(paths,journal,item),target=paths.GetPackageDirectory(item.Row.SourceId,item.Row.PackageId);
                if(item.OldRow!=null && Directory.Exists(staged) && !Directory.Exists(Backup(paths,journal,item)))
                {
                    EnsureBefore(paths,journal,item,token); MakeDirectory(Path.GetDirectoryName(Backup(paths,journal,item)));
                    Directory.Move(target,Backup(paths,journal,item)); Emit(audit,"ReplacementOriginalMoved",item.Row); fault?.Invoke("replacement-original-moved");
                }
                if(Directory.Exists(staged))
                { RequireAbsent(target); ManagedExtensionInventoryReader.VerifyTree(staged,item.Receipt.Files,token); Directory.Move(staged,target); Emit(audit,"InstallPackageMoved",item.Row); fault?.Invoke("package-moved"); }
                if(item.OldRow==null) CommitRecord(Work(paths,journal),MetadataKey(journal,item,"receipt"),paths.GetInstalledRecordPath(item.Row.SourceId,item.Row.PackageId),item.ReceiptBytes,token,fault);
                else ReplaceRecord(Work(paths,journal),MetadataKey(journal,item,"receipt"),paths.GetInstalledRecordPath(item.Row.SourceId,item.Row.PackageId),item.OldReceiptBytes,item.ReceiptBytes,token,fault);
                Emit(audit,"InstallReceiptCommitted",item.Row); fault?.Invoke("receipt-committed");
                if(item.OldRow==null) CommitRecord(Work(paths,journal),MetadataKey(journal,item,"state"),paths.GetDesiredStatePath(item.Row.SourceId,item.Row.PackageId),item.StateBytes,token,fault);
                else ReplaceRecord(Work(paths,journal),MetadataKey(journal,item,"state"),paths.GetDesiredStatePath(item.Row.SourceId,item.Row.PackageId),item.OldStateBytes,item.StateBytes,token,fault);
                Emit(audit,"InstallStateCommitted",item.Row); fault?.Invoke("state-committed");
            }
            foreach(var item in journal.Items)
            {
                ManagedExtensionInventoryReader.VerifyTree(paths.GetPackageDirectory(item.Row.SourceId,item.Row.PackageId),item.Receipt.Files,token);
                Exact(paths.GetInstalledRecordPath(item.Row.SourceId,item.Row.PackageId),item.ReceiptBytes,ManagedExtensionInventoryReader.MaxRecordBytes,token);
                Exact(paths.GetDesiredStatePath(item.Row.SourceId,item.Row.PackageId),item.StateBytes,8192,token);
            }
            foreach(var item in journal.Items.Where(i=>i.OldRow!=null))
            {
                DeleteOwnedTree(Backup(paths,journal,item),item.OldReceipt.Files,token,()=>fault?.Invoke("replacement-backup-file-deleted"));
                Emit(audit,"ReplacementOriginalCleaned",item.Row);
            }
            string backups=Path.Combine(Work(paths,journal),"backup");
            if(Directory.Exists(backups)) { ManagedExtensionInventoryReader.NoLinks(backups); Directory.Delete(backups,false); }
            string stage=Path.Combine(Work(paths,journal),"stage");
            if(Directory.Exists(stage)) { ManagedExtensionInventoryReader.NoLinks(stage); Directory.Delete(stage,false); }
            if(Directory.Exists(Work(paths,journal))) { ManagedExtensionInventoryReader.NoLinks(Work(paths,journal)); Directory.Delete(Work(paths,journal),false); }
            fault?.Invoke("work-deleted"); Exact(journalPath,Serialize(journal),MaxJournalBytes,token); File.Delete(journalPath);
            foreach(var item in journal.Items) Emit(audit,"InstallCommitted",item.Row);
        }
        private static void VerifyReplacement(ManagedExtensionPaths paths,Journal journal,Item item,CancellationToken token)
        {
            string staged=Staged(paths,journal,item),target=paths.GetPackageDirectory(item.Row.SourceId,item.Row.PackageId),backup=Backup(paths,journal,item);
            foreach(string path in new[]{staged,target,backup}) { ManagedExtensionInventoryReader.NoLinks(path); if(File.Exists(path)) throw Error("InstallDirectoryConflict"); }
            bool s=Directory.Exists(staged),t=Directory.Exists(target),b=Directory.Exists(backup);
            if(s)
            {
                ManagedExtensionInventoryReader.VerifyTree(staged,item.Receipt.Files,token);
                if(t==b) throw Error("ReplacementDirectoryConflict");
                ManagedExtensionInventoryReader.VerifyTree(b?backup:target,item.OldReceipt.Files,token);
                Exact(paths.GetInstalledRecordPath(item.Row.SourceId,item.Row.PackageId),item.OldReceiptBytes,ManagedExtensionInventoryReader.MaxRecordBytes,token);
                Exact(paths.GetDesiredStatePath(item.Row.SourceId,item.Row.PackageId),item.OldStateBytes,8192,token);
            }
            else
            {
                if(!t) throw Error("ReplacementDirectoryConflict");
                ManagedExtensionInventoryReader.VerifyTree(target,item.Receipt.Files,token);
                bool done=RecordIs(paths.GetInstalledRecordPath(item.Row.SourceId,item.Row.PackageId),item.ReceiptBytes,token) && RecordIs(paths.GetDesiredStatePath(item.Row.SourceId,item.Row.PackageId),item.StateBytes,token);
                if(b)
                {
                    if(done) ManagedExtensionRemovalRecovery.VerifySubset(backup,item.OldReceipt.Files,token);
                    else ManagedExtensionInventoryReader.VerifyTree(backup,item.OldReceipt.Files,token);
                }
                else if(!done) throw Error("ReplacementBackupMissing");
                EitherRecord(paths.GetInstalledRecordPath(item.Row.SourceId,item.Row.PackageId),item.OldReceiptBytes,item.ReceiptBytes,token);
                EitherRecord(paths.GetDesiredStatePath(item.Row.SourceId,item.Row.PackageId),item.OldStateBytes,item.StateBytes,token);
            }
        }
        private static bool RecordIs(string path,byte[] bytes,CancellationToken token)
        { ManagedExtensionInventoryReader.NoLinks(path); return ManagedExtensionInventoryReader.Bytes(path,ManagedExtensionInventoryReader.MaxRecordBytes,token).SequenceEqual(bytes); }
        private static void EitherRecord(string path,byte[] oldBytes,byte[] newBytes,CancellationToken token)
        { if(!RecordIs(path,newBytes,token) && !RecordIs(path,oldBytes,token)) throw Error("ReplacementMetadataConflict"); }
        private static void ReplaceRecord(string work,string name,string target,byte[] oldBytes,byte[] newBytes,CancellationToken token,Action<string> fault)
        {
            string temporary=Path.Combine(work,name);
            if(RecordIs(target,newBytes,token)) { RequireAbsent(temporary); return; }
            Exact(target,oldBytes,ManagedExtensionInventoryReader.MaxRecordBytes,token);
            if(File.Exists(temporary)) Exact(temporary,newBytes,ManagedExtensionInventoryReader.MaxRecordBytes,token); else WriteNew(temporary,newBytes);
            fault?.Invoke("metadata-flushed"); token.ThrowIfCancellationRequested(); Exact(target,oldBytes,ManagedExtensionInventoryReader.MaxRecordBytes,token);
            File.Replace(temporary,target,null);
        }
        private static void DeleteOwnedTree(string root,IEnumerable<ManagedExtensionFile> files,CancellationToken token,Action deleted=null)
        {
            ManagedExtensionInventoryReader.NoLinks(root); if(!Directory.Exists(root)) return;
            var all=files.ToList(); ManagedExtensionRemovalRecovery.VerifySubset(root,all,token);
            foreach(var file in all)
            {
                token.ThrowIfCancellationRequested(); string path=Path.Combine(root,file.Path); ManagedExtensionInventoryReader.NoLinks(path);
                if(File.Exists(path)) { ManagedExtensionRemovalRecovery.VerifyFile(path,file,token); File.Delete(path); deleted?.Invoke(); }
            }
            var directories=new HashSet<string>(StringComparer.Ordinal);
            foreach(var file in all) for(string parent=Path.GetDirectoryName(file.Path);!string.IsNullOrEmpty(parent);parent=Path.GetDirectoryName(parent)) directories.Add(Path.Combine(root,parent));
            foreach(string dir in directories.OrderByDescending(d=>d.Length)) { ManagedExtensionInventoryReader.NoLinks(dir); if(Directory.Exists(dir)) Directory.Delete(dir,false); }
            Directory.Delete(root,false);
        }
        private static void CommitRecord(string work,string name,string destination,byte[] bytes,CancellationToken token,Action<string> fault)
        {
            string temporary=Path.Combine(work,name); ManagedExtensionInventoryReader.NoLinks(temporary);
            if(File.Exists(destination)) { Exact(destination,bytes,ManagedExtensionInventoryReader.MaxRecordBytes,token); if(File.Exists(temporary)) throw Error("InstallMetadataConflict"); return; }
            RequireAbsent(destination); MakeDirectory(work);
            if(File.Exists(temporary)) Exact(temporary,bytes,ManagedExtensionInventoryReader.MaxRecordBytes,token);
            else WriteNew(temporary,bytes);
            fault?.Invoke("metadata-flushed"); token.ThrowIfCancellationRequested(); RequireAbsent(destination);
            Exact(temporary,bytes,ManagedExtensionInventoryReader.MaxRecordBytes,token); File.Move(temporary,destination);
        }
        private static void Rollback(ManagedExtensionPaths paths,Journal journal,string journalPath,CancellationToken token)
        {
            VerifyWork(paths,journal,token); Exact(journalPath,Serialize(journal),MaxJournalBytes,token);
            foreach(var item in journal.Items) { EnsureBefore(paths,journal,item,token); string root=Staged(paths,journal,item); if(Directory.Exists(root)) ManagedExtensionRemovalRecovery.VerifySubset(root,item.Receipt.Files,token); }
            foreach(var item in journal.Items)
            {
                string root=Staged(paths,journal,item); if(!Directory.Exists(root)) continue;
                foreach(var file in item.Receipt.Files)
                {
                    token.ThrowIfCancellationRequested(); string path=Path.Combine(root,file.Path); ManagedExtensionInventoryReader.NoLinks(path);
                    if(File.Exists(path)) { ManagedExtensionRemovalRecovery.VerifyFile(path,file,token); File.Delete(path); }
                }
                var directories=new HashSet<string>(StringComparer.Ordinal);
                foreach(var file in item.Receipt.Files)
                    for(string parent=Path.GetDirectoryName(file.Path);!string.IsNullOrEmpty(parent);parent=Path.GetDirectoryName(parent)) directories.Add(Path.Combine(root,parent));
                foreach(string dir in directories.OrderByDescending(d=>d.Length)) { ManagedExtensionInventoryReader.NoLinks(dir); if(Directory.Exists(dir)) Directory.Delete(dir,false); }
                Directory.Delete(root,false);
            }
            string work=Work(paths,journal),transition=Path.Combine(work,"transition.json"),stage=Path.Combine(work,"stage");
            if(File.Exists(transition)) { Exact(transition,Serialize(new Journal {Schema=journal.Schema,Operation=journal.Operation,Phase="committing",Items=journal.Items}),MaxJournalBytes,token); File.Delete(transition); }
            if(Directory.Exists(stage)) { ManagedExtensionInventoryReader.NoLinks(stage); Directory.Delete(stage,false); }
            if(Directory.Exists(work)) { ManagedExtensionInventoryReader.NoLinks(work); Directory.Delete(work,false); }
            Exact(journalPath,Serialize(journal),MaxJournalBytes,token); File.Delete(journalPath);
        }
        private static void VerifyWork(ManagedExtensionPaths paths,Journal journal,CancellationToken token)
        {
            string work=Work(paths,journal); ManagedExtensionInventoryReader.NoLinks(work);
            if(File.Exists(work)) throw Error("InstallWorkChanged"); if(!Directory.Exists(work)) return;
            var metadata=new Dictionary<string,byte[]>(StringComparer.Ordinal);
            if(journal.Phase=="preparing") metadata.Add("transition.json",Serialize(new Journal {Schema=journal.Schema,Operation=journal.Operation,Phase="committing",Items=journal.Items}));
            else foreach(var item in journal.Items) { metadata.Add(MetadataKey(journal,item,"receipt"),item.ReceiptBytes); metadata.Add(MetadataKey(journal,item,"state"),item.StateBytes); }
            int count=0;
            foreach(string entry in Directory.EnumerateFileSystemEntries(work))
            {
                token.ThrowIfCancellationRequested(); if(++count>ManagedExtensionInstallRequest.MaxPackages*2+2) throw Error("InstallWorkChanged");
                ManagedExtensionInventoryReader.NoLinks(entry); string name=Path.GetFileName(entry);
                if(name=="stage" && Directory.Exists(entry))
                {
                    int staged=0;
                    foreach(string root in Directory.EnumerateFileSystemEntries(entry))
                    {
                        if(++staged>journal.Items.Count) throw Error("InstallWorkChanged");
                        ManagedExtensionInventoryReader.NoLinks(root); var item=journal.Items.SingleOrDefault(i=>WorkKey(journal,i)==Path.GetFileName(root));
                        if(item==null || !Directory.Exists(root)) throw Error("InstallWorkChanged");
                        ManagedExtensionRemovalRecovery.VerifySubset(root,item.Receipt.Files,token);
                    }
                }
                else if(name=="backup" && Directory.Exists(entry) && journal.Phase=="committing")
                {
                    int countBackups=0;
                    foreach(string root in Directory.EnumerateFileSystemEntries(entry))
                    {
                        token.ThrowIfCancellationRequested(); if(++countBackups>journal.Items.Count) throw Error("InstallWorkChanged");
                        ManagedExtensionInventoryReader.NoLinks(root);
                        var item=journal.Items.SingleOrDefault(i=>i.OldRow!=null && WorkKey(journal,i)==Path.GetFileName(root));
                        if(item==null || !Directory.Exists(root)) throw Error("InstallWorkChanged");
                        ManagedExtensionRemovalRecovery.VerifySubset(root,item.OldReceipt.Files,token);
                    }
                }
                else { byte[] expected; if(!metadata.TryGetValue(name,out expected)) throw Error("InstallWorkChanged"); Exact(entry,expected,MaxJournalBytes,token); }
            }
        }
        private static void OptionalExact(string path,byte[] bytes,CancellationToken token)
        { ManagedExtensionInventoryReader.NoLinks(path); if(Directory.Exists(path)) throw Error("InstallOwnershipChanged"); if(File.Exists(path)) Exact(path,bytes,ManagedExtensionInventoryReader.MaxRecordBytes,token); }
        private static void Exact(string path,byte[] bytes,int limit,CancellationToken token)
        { ManagedExtensionInventoryReader.NoLinks(path); if(!ManagedExtensionInventoryReader.Bytes(path,limit,token).SequenceEqual(bytes)) throw Error("InstallOwnershipChanged"); }
        private static void MakeDirectory(string path)
        { ManagedExtensionInventoryReader.NoLinks(path); Directory.CreateDirectory(path); ManagedExtensionInventoryReader.NoLinks(path); }
        private static void WriteNew(string path,byte[] bytes)
        { RequireAbsent(path); using(var file=new FileStream(path,FileMode.CreateNew,FileAccess.Write,FileShare.None)) { file.Write(bytes,0,bytes.Length); file.Flush(true); } }
        private static string Work(ManagedExtensionPaths paths,Journal j) { return Path.Combine(paths.TransactionsDirectory,"in-"+j.Operation); }
        private static string JournalPath(ManagedExtensionPaths paths,Journal j) { return Work(paths,j)+".json"; }
        // v1 journals use full record keys. v2 journals use a canonical batch index,
        // retaining the full identity in the journal and final package path.
        private static string WorkKey(Journal j,Item i)
        { return j.Schema==1?i.Row.RecordKey:"p"+j.Items.IndexOf(i).ToString(System.Globalization.CultureInfo.InvariantCulture); }
        private static string MetadataKey(Journal j,Item i,string kind)
        { return kind+"-"+WorkKey(j,i)+".json"; }
        private static void ValidateStoragePaths(ManagedExtensionPaths paths,Journal journal)
        {
            ValidateFilePath(JournalPath(paths,journal));
            ValidateFilePath(Path.Combine(Work(paths,journal),"transition.json"));
            foreach(var item in journal.Items)
            {
                ValidateFilePath(paths.GetInstalledRecordPath(item.Row.SourceId,item.Row.PackageId));
                ValidateFilePath(paths.GetDesiredStatePath(item.Row.SourceId,item.Row.PackageId));
                ValidateFilePath(Path.Combine(Work(paths,journal),MetadataKey(journal,item,"receipt")));
                ValidateFilePath(Path.Combine(Work(paths,journal),MetadataKey(journal,item,"state")));
                foreach(var file in item.Receipt.Files)
                {
                    ValidateFilePath(Path.Combine(Staged(paths,journal,item),file.Path));
                    ValidateFilePath(Path.Combine(paths.GetPackageDirectory(item.Row.SourceId,item.Row.PackageId),file.Path));
                }
                if(item.OldReceipt!=null) foreach(var file in item.OldReceipt.Files)
                    ValidateFilePath(Path.Combine(Backup(paths,journal,item),file.Path));
            }
        }
        // Preflight every path before writing the journal. The Unity/Framework host
        // cannot assume Windows long-path support from the OS setting alone.
        internal static void ValidateFilePath(string path)
            => ValidateFilePath(path,Path.DirectorySeparatorChar=='\\');
        internal static void ValidateFilePath(string path,bool windows)
        {
            if(windows && (path.Length>=260 || Path.GetDirectoryName(path).Length>=248)) throw Error("ManagedInstallPathTooLong");
            foreach(string component in path.Split(Path.DirectorySeparatorChar,Path.AltDirectorySeparatorChar))
                if((windows?component.Length:Encoding.UTF8.GetByteCount(component))>255) throw Error("ManagedInstallPathTooLong");
        }
        private static string Staged(ManagedExtensionPaths paths,Journal j,Item i) { return Path.Combine(Work(paths,j),"stage",WorkKey(j,i)); }
        private static string Backup(ManagedExtensionPaths paths,Journal j,Item i) { return Path.Combine(Work(paths,j),"backup",WorkKey(j,i)); }
        private static byte[] ReceiptBytes(ManagedExtensionPackageSnapshot row,int manifestLength)
        {
            var files=row.Manifest.Assemblies.Select(a=>a.File).Concat(row.Manifest.Resources).Concat(new[]{new ManagedExtensionFile("manifest.json",manifestLength,row.ManifestSha256)});
            return SerializeReceipt(row,files);
        }
        private static byte[] SerializeReceipt(ManagedExtensionPackageSnapshot row,IEnumerable<ManagedExtensionFile> files)
        {
            return Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"sourceId\":\""+row.SourceId+"\",\"repositoryIdentitySha256\":\""+row.RepositoryIdentitySha256+
                "\",\"packageId\":\""+row.PackageId+"\",\"version\":\""+row.Version+"\",\"manifestSha256\":\""+row.ManifestSha256+"\",\"catalogSnapshotId\":\""+row.CatalogSnapshotId+
                "\",\"catalogSha256\":\""+row.CatalogSha256+"\",\"artifactSha256\":\""+row.ArtifactSha256+"\",\"installationTransactionId\":\""+row.InstallationTransactionId+"\",\"files\":["+
                string.Join(",",files.OrderBy(f=>f.Path,StringComparer.Ordinal).Select(f=>"{\"path\":\""+f.Path+"\",\"length\":"+f.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"sha256\":\""+f.Sha256+"\"}"))+"]}");
        }
        private static byte[] StateBytes(ManagedExtensionPackageSnapshot row)
        { return Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"sourceId\":\""+row.SourceId+"\",\"packageId\":\""+row.PackageId+"\",\"manifestSha256\":\""+row.ManifestSha256+"\",\"operationId\":\""+row.StateOperationId+"\",\"desiredState\":\"enabled\"}"); }
        private static byte[] Serialize(Journal j)
        { return Encoding.UTF8.GetBytes("{\"schemaVersion\":"+j.Schema.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"kind\":\"installation\",\"operationId\":\""+j.Operation+"\",\"phase\":\""+j.Phase+"\",\"packages\":["+string.Join(",",j.Items.Select(i=>"{\"receiptBase64\":\""+Convert.ToBase64String(i.ReceiptBytes)+"\",\"stateBase64\":\""+Convert.ToBase64String(i.StateBytes)+"\",\"manifestBase64\":\""+Convert.ToBase64String(i.ManifestBytes)+"\""+(i.OldRow==null?"":",\"oldReceiptBase64\":\""+Convert.ToBase64String(i.OldReceiptBytes)+"\",\"oldStateBase64\":\""+Convert.ToBase64String(i.OldStateBytes)+"\",\"oldManifestBase64\":\""+Convert.ToBase64String(i.OldManifestBytes)+"\"")+"}"))+"]}"); }
        private static Journal Parse(byte[] bytes)
        {
            var f=ManagedExtensionJson.Object(ManagedExtensionJson.Read(bytes,MaxJournalBytes),"schemaVersion","kind","operationId","phase","packages");
            int schema=(int)ManagedExtensionJson.Integer(ManagedExtensionJson.Required(f,"schemaVersion"),1,2);
            if(ManagedExtensionJson.Text(ManagedExtensionJson.Required(f,"kind"),32)!="installation") throw Error("InstallJournalSchemaInvalid");
            var j=new Journal {Schema=schema,Operation=ManagedExtensionJson.Hex(ManagedExtensionJson.Required(f,"operationId"),32),Phase=ManagedExtensionJson.Text(ManagedExtensionJson.Required(f,"phase"),32),Items=new List<Item>()};
            if(j.Phase!="preparing" && j.Phase!="committing") throw Error("InstallJournalSchemaInvalid");
            foreach(var node in ManagedExtensionJson.Array(ManagedExtensionJson.Required(f,"packages"),ManagedExtensionInstallRequest.MaxPackages))
            {
                var itemFields=ManagedExtensionJson.Object(node,"receiptBase64","stateBase64","manifestBase64","oldReceiptBase64","oldStateBase64","oldManifestBase64");
                var item=new Item {ReceiptBytes=Decode(itemFields,"receiptBase64",ManagedExtensionInventoryReader.MaxRecordBytes),StateBytes=Decode(itemFields,"stateBase64",8192),ManifestBytes=Decode(itemFields,"manifestBase64",ManagedExtensionManifestReader.MaxManifestBytes)};
                var r=ManagedExtensionInventoryReader.ReadReceipt(item.ReceiptBytes); item.Receipt=r;
                var manifest=ManagedExtensionManifestReader.Read(item.ManifestBytes);
                if(r.TransactionId!=j.Operation || r.ManifestHash!=ManagedExtensionPaths.Hash(item.ManifestBytes) || r.PackageId!=manifest.PackageId || r.Version!=manifest.Version.ToString()) throw Error("InstallJournalIdentityMismatch");
                item.Row=new ManagedExtensionPackageSnapshot(ManagedExtensionPaths.PackageKey(r.SourceId,r.PackageId),r.SourceId,r.IdentityHash,r.PackageId,r.Version,r.ManifestHash,r.SnapshotId,r.CatalogHash,r.ArtifactHash,r.TransactionId,j.Operation,manifest,ManagedExtensionDesiredState.Enabled,ManagedExtensionContentState.ContentVerified,null);
                var expected=manifest.Assemblies.Select(a=>a.File).Concat(manifest.Resources).Concat(new[]{new ManagedExtensionFile("manifest.json",item.ManifestBytes.Length,r.ManifestHash)}).ToList();
                if(r.Files.Count!=expected.Count || expected.Any(e=>!r.Files.Any(v=>v.Path==e.Path && v.Length==e.Length && v.Sha256==e.Sha256)) || !StateBytes(item.Row).SequenceEqual(item.StateBytes)) throw Error("InstallJournalFilesMismatch");
                if(itemFields.ContainsKey("oldReceiptBase64")) ReadOld(item,itemFields);
                j.Items.Add(item);
            }
            if(j.Items.Count==0 || j.Items.Select(i=>i.Row.RecordKey).Distinct().Count()!=j.Items.Count || j.Items.Sum(i=>i.Receipt.Files.Sum(v=>v.Length))>ManagedExtensionManifestReader.MaxExpandedBytes) throw Error("InstallJournalLimit");
            var first=j.Items[0].Row;
            if(j.Items.Any(i=>i.Row.SourceId!=first.SourceId || i.Row.RepositoryIdentitySha256!=first.RepositoryIdentitySha256 || i.Row.CatalogSnapshotId!=first.CatalogSnapshotId || i.Row.CatalogSha256!=first.CatalogSha256)) throw Error("InstallJournalIdentityMismatch");
            if(!Serialize(j).SequenceEqual(bytes)) throw Error("InstallJournalNotCanonical"); return j;
        }
        private static void ReadOld(Item item,Dictionary<string,XElement> fields)
        {
            item.OldReceiptBytes=Decode(fields,"oldReceiptBase64",ManagedExtensionInventoryReader.MaxRecordBytes);
            item.OldStateBytes=Decode(fields,"oldStateBase64",8192);
            item.OldManifestBytes=Decode(fields,"oldManifestBase64",ManagedExtensionManifestReader.MaxManifestBytes);
            var old=ManagedExtensionInventoryReader.ReadReceipt(item.OldReceiptBytes); item.OldReceipt=old;
            var manifest=ManagedExtensionManifestReader.Read(item.OldManifestBytes);
            if(old.SourceId!=item.Row.SourceId || old.IdentityHash!=item.Row.RepositoryIdentitySha256 || old.PackageId!=item.Row.PackageId ||
                old.ManifestHash!=ManagedExtensionPaths.Hash(item.OldManifestBytes) || manifest.PackageId!=old.PackageId || manifest.Version.ToString()!=old.Version ||
                manifest.Version.CompareTo(item.Row.Manifest.Version)>=0) throw Error("ManagedReplacementIdentityInvalid");
            var state=ManagedExtensionJson.Object(ManagedExtensionJson.Read(item.OldStateBytes,8192),"schemaVersion","sourceId","packageId","manifestSha256","operationId","desiredState");
            string operation=ManagedExtensionJson.Hex(ManagedExtensionJson.Required(state,"operationId"),32);
            item.OldRow=new ManagedExtensionPackageSnapshot(item.Row.RecordKey,old.SourceId,old.IdentityHash,old.PackageId,old.Version,old.ManifestHash,old.SnapshotId,old.CatalogHash,
                old.ArtifactHash,old.TransactionId,operation,manifest,ManagedExtensionDesiredState.Enabled,ManagedExtensionContentState.ContentVerified,null);
            var expected=manifest.Assemblies.Select(a=>a.File).Concat(manifest.Resources).Concat(new[]{new ManagedExtensionFile("manifest.json",item.OldManifestBytes.Length,old.ManifestHash)}).ToList();
            if(!StateBytes(item.OldRow).SequenceEqual(item.OldStateBytes) || old.Files.Count!=expected.Count || expected.Any(e=>!old.Files.Any(v=>v.Path==e.Path && v.Length==e.Length && v.Sha256==e.Sha256))) throw Error("ReplacementJournalFilesMismatch");
        }
        private static byte[] Decode(Dictionary<string,XElement> f,string key,int maximum)
        {
            try { byte[] bytes=Convert.FromBase64String(ManagedExtensionJson.Text(ManagedExtensionJson.Required(f,key),(maximum+2)/3*4)); if(bytes.Length>maximum) throw Error("InstallJournalLimit"); return bytes; }
            catch(FormatException ex) { throw new ManagedExtensionValidationException("InstallJournalInvalid",ex); }
        }
        private static ManagedExtensionValidationException Error(string code) { return ManagedExtensionJson.Error(code); }
        private static void Emit(Action<string,ManagedExtensionPackageSnapshot> audit,string code,ManagedExtensionPackageSnapshot row) { try { audit?.Invoke(code,row); } catch { } }
        private static void Detail(Action<Exception> detail,Exception ex) { try { detail?.Invoke(ex); } catch { } }
    }
}
