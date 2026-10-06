using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace Utils.Framework.ManagedExtensions
{
    /// <summary>Runs under the host lease before loading. Never removes extension data or unknown files.</summary>
    public static class ManagedExtensionRemovalRecovery
    {
        private sealed class Journal
        {
            internal string OperationId, SourceId, PackageId;
            internal byte[] ReceiptBytes, StateBytes, ManifestBytes;
            internal ManagedExtensionManifest Manifest;
            internal ManagedExtensionInventoryReader.Receipt Receipt;
        }
        public static IReadOnlyList<string> Recover(ManagedExtensionPaths paths, IEnumerable<string> hostDependencies,
            IEnumerable<string> loadedAssemblyNames, Action<string, ManagedExtensionPackageSnapshot> audit, CancellationToken token)
        { return Recover(paths, hostDependencies, loadedAssemblyNames, audit, token, null); }

        internal static IReadOnlyList<string> Recover(ManagedExtensionPaths paths, IEnumerable<string> hostDependencies,
            IEnumerable<string> loadedAssemblyNames, Action<string, ManagedExtensionPackageSnapshot> audit, CancellationToken token, Action<string> fault, Action<Exception> internalError = null)
        {
            var errors = new List<string>();
            var host = new HashSet<string>(hostDependencies ?? new string[0], StringComparer.OrdinalIgnoreCase);
            var loaded = new HashSet<string>(loadedAssemblyNames ?? new string[0], StringComparer.OrdinalIgnoreCase);
            try
            {
                token.ThrowIfCancellationRequested(); ManagedExtensionInventoryReader.NoLinks(paths.TransactionsDirectory);
                if (File.Exists(paths.TransactionsDirectory)) throw ManagedExtensionJson.Error("TransactionRootInvalid");
                var journals = new List<string>(); var workDirectories = new List<string>();
                if (Directory.Exists(paths.TransactionsDirectory))
                    foreach (string entry in Directory.EnumerateFileSystemEntries(paths.TransactionsDirectory))
                    {
                        token.ThrowIfCancellationRequested();
                        if (journals.Count + workDirectories.Count >= 2048) throw ManagedExtensionJson.Error("TransactionLimit");
                        ManagedExtensionInventoryReader.NoLinks(entry); string name = Path.GetFileName(entry);
                        bool directory = (File.GetAttributes(entry) & FileAttributes.Directory) != 0;
                        if (!System.Text.RegularExpressions.Regex.IsMatch(name, directory ? @"\Arm-[0-9a-f]{32}\z" : @"\Arm-[0-9a-f]{32}\.json\z"))
                            throw ManagedExtensionJson.Error("UnknownManagedTransaction");
                        if (directory) workDirectories.Add(entry); else journals.Add(entry);
                    }
                if (workDirectories.Any(dir => !journals.Contains(dir + ".json"))) throw ManagedExtensionJson.Error("OrphanRemovalTransaction");
                foreach (string path in journals.OrderBy(p => p, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested(); var journal = Parse(ManagedExtensionInventoryReader.Bytes(path, 4 * 1024 * 1024, token));
                    if (Path.GetFileName(path) != "rm-" + journal.OperationId + ".json") throw ManagedExtensionJson.Error("RemovalJournalIdentityMismatch");
                    // A pending journal is authoritative intent, but reverse ownership is rechecked each startup.
                    var row=new ManagedExtensionPackageSnapshot(ManagedExtensionPaths.PackageKey(journal.SourceId,journal.PackageId),journal.SourceId,journal.Receipt.IdentityHash,journal.PackageId,
                        journal.Receipt.Version,journal.Receipt.ManifestHash,journal.Receipt.SnapshotId,journal.Receipt.CatalogHash,journal.Receipt.ArtifactHash,journal.Receipt.TransactionId,journal.OperationId,
                        journal.Manifest,ManagedExtensionDesiredState.PendingRemoval,ManagedExtensionContentState.ContentVerified,null);
                    Emit(audit,"RemovalReplayStarted",row);
                    try { CheckDependents(paths, journal, host, loaded, token); Finish(paths, journal, path, token, fault); }
                    catch(ManagedExtensionValidationException ex) { Emit(audit,ex.Code,row); throw; }
                    Emit(audit, "RemovalRecovered", row);
                }
                // Remove dependents before providers. Re-read after each completed transaction.
                bool progress;
                do
                {
                    progress = false;
                    var inventory = ManagedExtensionInventoryReader.Read(paths, token);
                    if (inventory.Diagnostics.Count != 0) throw ManagedExtensionJson.Error("RemovalInventoryUncertain");
                    foreach (var row in inventory.Packages.Where(p => p.DesiredState == ManagedExtensionDesiredState.PendingRemoval))
                    {
                        token.ThrowIfCancellationRequested();
                        if (row.ContentState != ManagedExtensionContentState.ContentVerified || row.DiagnosticCode != null) continue;
                        bool prepared = false;
                        try
                        {
                            var moduleIds = row.Manifest.Modules.Select(m => m.Id).ToArray();
                            if (row.Manifest.Assemblies.Any(a => loaded.Contains(a.Name))) throw ManagedExtensionJson.Error("RemovalAssemblyAlreadyLoaded");
                            if (host.Overlaps(moduleIds) || inventory.Packages.Any(other => !ReferenceEquals(other, row) &&
                                (other.Manifest == null || other.Manifest.Dependencies.Any(d => d.PackageId == row.PackageId) ||
                                other.Manifest.Modules.Any(m => m.DependsOn.Any(moduleIds.Contains))))) throw ManagedExtensionJson.Error("RemovalHasDependents");
                            var journal = Create(paths, row, token);
                            string path = Path.Combine(paths.TransactionsDirectory, "rm-" + row.StateOperationId + ".json");
                            ManagedExtensionInventoryReader.NoLinks(path); Directory.CreateDirectory(paths.TransactionsDirectory);
                            byte[] bytes = Serialize(journal);
                            using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                            { output.Write(bytes, 0, bytes.Length); output.Flush(true); }
                            prepared = true; Emit(audit, "RemovalPrepared", row); fault?.Invoke("journal-written");
                            Finish(paths, journal, path, token, fault); Emit(audit, "RemovalCompleted", row);
                            progress = true; break;
                        }
                        catch (ManagedExtensionValidationException ex) { Emit(audit, ex.Code, row); if (prepared) throw; Detail(internalError, ex); }
                    }
                } while (progress);
            }
            catch (ManagedExtensionValidationException ex) { errors.Add(ex.Code); Emit(audit, ex.Code, null); Detail(internalError, ex); }
            catch (IOException ex) { errors.Add("RemovalStorageFailed"); Emit(audit, "RemovalStorageFailed", null); Detail(internalError, ex); }
            catch (UnauthorizedAccessException ex) { errors.Add("RemovalStorageFailed"); Emit(audit, "RemovalStorageFailed", null); Detail(internalError, ex); }
            return errors.AsReadOnly();
        }
        private static void Emit(Action<string, ManagedExtensionPackageSnapshot> audit, string code, ManagedExtensionPackageSnapshot row)
        { try { audit?.Invoke(code, row); } catch { } }
        private static void Detail(Action<Exception> internalError, Exception error)
        { try { internalError?.Invoke(error); } catch { } }
        private static Journal Create(ManagedExtensionPaths paths, ManagedExtensionPackageSnapshot row, CancellationToken token)
        {
            var journal = new Journal { OperationId = row.StateOperationId, SourceId = row.SourceId, PackageId = row.PackageId,
                ReceiptBytes = ManagedExtensionInventoryReader.Bytes(paths.GetInstalledRecordPath(row.SourceId, row.PackageId), ManagedExtensionInventoryReader.MaxRecordBytes, token),
                StateBytes = ManagedExtensionInventoryReader.Bytes(paths.GetDesiredStatePath(row.SourceId, row.PackageId), 8192, token),
                ManifestBytes = ManagedExtensionInventoryReader.Bytes(Path.Combine(paths.GetPackageDirectory(row.SourceId,row.PackageId), "manifest.json"), ManagedExtensionManifestReader.MaxManifestBytes, token) };
            if (ManagedExtensionPaths.Hash(journal.ManifestBytes) != row.ManifestSha256) throw ManagedExtensionJson.Error("RemovalOwnershipChanged");
            // Reparse the same serialized journal used for replay before authorizing a move.
            return Parse(Serialize(journal));
        }
        private static byte[] Serialize(Journal journal)
        {
            return Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"kind\":\"removal\",\"operationId\":\"" + journal.OperationId +
                "\",\"sourceId\":\"" + journal.SourceId + "\",\"packageId\":\"" + journal.PackageId +
                "\",\"receiptBase64\":\"" + Convert.ToBase64String(journal.ReceiptBytes) + "\",\"stateBase64\":\"" + Convert.ToBase64String(journal.StateBytes) + "\",\"manifestBase64\":\"" + Convert.ToBase64String(journal.ManifestBytes) + "\"}");
        }
        private static XElement Get(Dictionary<string, XElement> fields, string name) { return ManagedExtensionJson.Required(fields, name); }
        private static Journal Parse(byte[] bytes)
        {
            var f = ManagedExtensionJson.Object(ManagedExtensionJson.Read(bytes, 4 * 1024 * 1024), "schemaVersion", "kind", "operationId", "sourceId", "packageId", "receiptBase64", "stateBase64", "manifestBase64");
            if (ManagedExtensionJson.Integer(Get(f, "schemaVersion"), 1, 1) != 1 || ManagedExtensionJson.Text(Get(f, "kind"), 32) != "removal") throw ManagedExtensionJson.Error("RemovalJournalSchemaInvalid");
            var journal = new Journal { OperationId = ManagedExtensionJson.Hex(Get(f, "operationId"), 32), SourceId = ManagedExtensionJson.Id(Get(f, "sourceId")), PackageId = ManagedExtensionJson.Id(Get(f, "packageId")) };
            try { journal.ReceiptBytes = Convert.FromBase64String(ManagedExtensionJson.Text(Get(f, "receiptBase64"), 2800000)); journal.StateBytes = Convert.FromBase64String(ManagedExtensionJson.Text(Get(f, "stateBase64"), 12000)); journal.ManifestBytes = Convert.FromBase64String(ManagedExtensionJson.Text(Get(f,"manifestBase64"), 700000)); }
            catch (FormatException) { throw ManagedExtensionJson.Error("RemovalJournalInvalid"); }
            journal.Receipt = ManagedExtensionInventoryReader.ReadReceipt(journal.ReceiptBytes);
            var state = ManagedExtensionJson.Object(ManagedExtensionJson.Read(journal.StateBytes, 8192), "schemaVersion", "sourceId", "packageId", "manifestSha256", "operationId", "desiredState");
            if (ManagedExtensionJson.Integer(Get(state, "schemaVersion"), 1, 1) != 1 || journal.SourceId != journal.Receipt.SourceId || journal.PackageId != journal.Receipt.PackageId ||
                ManagedExtensionJson.Id(Get(state, "sourceId")) != journal.SourceId || ManagedExtensionJson.Id(Get(state, "packageId")) != journal.PackageId ||
                ManagedExtensionJson.Hex(Get(state, "manifestSha256"), 64) != journal.Receipt.ManifestHash || ManagedExtensionJson.Hex(Get(state, "operationId"), 32) != journal.OperationId ||
                ManagedExtensionJson.Text(Get(state, "desiredState"), 32) != "pending-removal") throw ManagedExtensionJson.Error("RemovalJournalIdentityMismatch");
            if (!journal.Receipt.Files.Any(file => file.Path == "manifest.json" && file.Sha256 == journal.Receipt.ManifestHash)) throw ManagedExtensionJson.Error("RemovalJournalInvalid");
            if (ManagedExtensionPaths.Hash(journal.ManifestBytes) != journal.Receipt.ManifestHash) throw ManagedExtensionJson.Error("RemovalJournalManifestMismatch");
            journal.Manifest = ManagedExtensionManifestReader.Read(journal.ManifestBytes);
            if (journal.Manifest.PackageId != journal.PackageId || journal.Manifest.Version.ToString() != journal.Receipt.Version) throw ManagedExtensionJson.Error("RemovalJournalIdentityMismatch");
            var expected = journal.Manifest.Assemblies.Select(a=>a.File).Concat(journal.Manifest.Resources).Concat(new[]{new ManagedExtensionFile("manifest.json",journal.ManifestBytes.Length,journal.Receipt.ManifestHash)}).ToList();
            if (expected.Count!=journal.Receipt.Files.Count || expected.Any(file=>!journal.Receipt.Files.Any(receiptFile=>receiptFile.Path==file.Path && receiptFile.Length==file.Length && receiptFile.Sha256==file.Sha256))) throw ManagedExtensionJson.Error("RemovalJournalFilesMismatch");
            if (!Serialize(journal).SequenceEqual(bytes)) throw ManagedExtensionJson.Error("RemovalJournalNotCanonical");
            return journal;
        }
        private static void CheckDependents(ManagedExtensionPaths paths, Journal journal,
            HashSet<string> host, HashSet<string> loaded, CancellationToken token)
        {
            var inventory = ManagedExtensionInventoryReader.Read(paths, token);
            if (inventory.Diagnostics.Count != 0) throw ManagedExtensionJson.Error("RemovalInventoryUncertain");
            string key=ManagedExtensionPaths.PackageKey(journal.SourceId,journal.PackageId);
            string[] ids=journal.Manifest.Modules.Select(m=>m.Id).ToArray();
            if (host.Overlaps(ids)) throw ManagedExtensionJson.Error("RemovalHasDependents");
            foreach (var other in inventory.Packages)
                if (other.RecordKey != key && (other.Manifest == null || other.Manifest.Dependencies.Any(d => d.PackageId == journal.PackageId) ||
                    other.Manifest.Modules.Any(m=>m.DependsOn.Any(ids.Contains)))) throw ManagedExtensionJson.Error("RemovalHasDependents");
            if (journal.Manifest.Assemblies.Any(a=>loaded.Contains(a.Name))) throw ManagedExtensionJson.Error("RemovalAssemblyAlreadyLoaded");
        }
        private static void ExactOrMissing(string path, byte[] expected, bool allowMissing, CancellationToken token)
        {
            ManagedExtensionInventoryReader.NoLinks(path);
            if (Directory.Exists(path)) throw ManagedExtensionJson.Error("RemovalOwnershipChanged");
            if (!File.Exists(path)) { if (!allowMissing) throw ManagedExtensionJson.Error("RemovalOwnershipRecordMissing"); return; }
            if (!ManagedExtensionInventoryReader.Bytes(path, ManagedExtensionInventoryReader.MaxRecordBytes, token).SequenceEqual(expected)) throw ManagedExtensionJson.Error("RemovalOwnershipChanged");
        }
        private static void Finish(ManagedExtensionPaths paths, Journal journal, string journalPath, CancellationToken token, Action<string> fault)
        {
            string original = paths.GetPackageDirectory(journal.SourceId, journal.PackageId);
            string work = Path.Combine(paths.TransactionsDirectory, "rm-" + journal.OperationId), quarantine = Path.Combine(work, "package");
            string receipt = paths.GetInstalledRecordPath(journal.SourceId, journal.PackageId), state = paths.GetDesiredStatePath(journal.SourceId, journal.PackageId);
            ManagedExtensionInventoryReader.NoLinks(original); ManagedExtensionInventoryReader.NoLinks(quarantine);
            bool hasOriginal = Directory.Exists(original), hasQuarantine = Directory.Exists(quarantine);
            if (hasOriginal && hasQuarantine) throw ManagedExtensionJson.Error("RemovalDirectoryConflict");
            if (File.Exists(original) || File.Exists(quarantine) || File.Exists(work)) throw ManagedExtensionJson.Error("RemovalDirectoryInvalid");
            ExactOrMissing(receipt, journal.ReceiptBytes, !hasOriginal && !hasQuarantine, token);
            ExactOrMissing(state, journal.StateBytes, !hasOriginal && !hasQuarantine, token);
            if (hasOriginal)
            {
                // Fresh/replayed move is permitted only for the complete original inventory, never a subset.
                ManagedExtensionInventoryReader.VerifyTree(original, journal.Receipt.Files, token);
                Directory.CreateDirectory(work); ManagedExtensionInventoryReader.NoLinks(work);
                if (Directory.EnumerateFileSystemEntries(work).Any()) throw ManagedExtensionJson.Error("RemovalWorkDirectoryChanged");
                Directory.Move(original, quarantine); fault?.Invoke("package-moved"); hasQuarantine = true;
            }
            if (hasQuarantine)
            {
                VerifySubset(quarantine, journal.Receipt.Files, token);
                foreach (var file in journal.Receipt.Files.OrderBy(f => f.Path, StringComparer.Ordinal))
                {
                    token.ThrowIfCancellationRequested(); string path = Path.Combine(quarantine, file.Path);
                    ManagedExtensionInventoryReader.NoLinks(path);
                    if (!File.Exists(path)) continue;
                    VerifyFile(path, file, token); File.Delete(path); fault?.Invoke("file-deleted");
                }
                // Non-recursive deletion preserves any newly inserted unknown files/directories.
                var directories = new HashSet<string>(StringComparer.Ordinal);
                foreach (var file in journal.Receipt.Files)
                { string relative=Path.GetDirectoryName(file.Path); while(!string.IsNullOrEmpty(relative)) { directories.Add(Path.Combine(quarantine,relative)); relative=Path.GetDirectoryName(relative); } }
                foreach (var dir in directories.OrderByDescending(p=>p.Length))
                { ManagedExtensionInventoryReader.NoLinks(dir); if(Directory.Exists(dir)) Directory.Delete(dir, false); }
                Directory.Delete(quarantine, false); fault?.Invoke("package-deleted");
            }
            if (Directory.Exists(work)) { ManagedExtensionInventoryReader.NoLinks(work); Directory.Delete(work, false); }
            ExactOrMissing(receipt, journal.ReceiptBytes, true, token); if (File.Exists(receipt)) File.Delete(receipt); fault?.Invoke("receipt-deleted");
            ExactOrMissing(state, journal.StateBytes, true, token); if (File.Exists(state)) File.Delete(state); fault?.Invoke("state-deleted");
            // A changed journal is evidence, not permission to remove it.
            ManagedExtensionInventoryReader.NoLinks(journalPath);
            if (!ManagedExtensionInventoryReader.Bytes(journalPath, 4 * 1024 * 1024, token).SequenceEqual(Serialize(journal))) throw ManagedExtensionJson.Error("RemovalJournalChanged");
            File.Delete(journalPath);
        }
        internal static void VerifySubset(string root, List<ManagedExtensionFile> files, CancellationToken token)
        {
            var expected = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var dirs = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in files)
            { int slash = file.Path.LastIndexOf('/'); while (slash >= 0) { string parent = file.Path.Substring(0, slash); dirs.Add(parent); slash = parent.LastIndexOf('/'); } }
            var pending = new Stack<Tuple<string, string>>(); pending.Push(Tuple.Create(root, ""));
            int count = 0; var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (pending.Count != 0)
            {
                var dir = pending.Pop();
                foreach (string entry in Directory.EnumerateFileSystemEntries(dir.Item1))
                {
                    token.ThrowIfCancellationRequested(); if (++count > 4096) throw ManagedExtensionJson.Error("PackageTreeLimit");
                    ManagedExtensionInventoryReader.NoLinks(entry); string relative = dir.Item2 + Path.GetFileName(entry);
                    if (!seen.Add(relative)) throw ManagedExtensionJson.Error("PathAlias");
                    if ((File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    { if (!dirs.Contains(relative)) throw ManagedExtensionJson.Error("UnexpectedDirectory"); pending.Push(Tuple.Create(entry, relative + "/")); }
                    else { ManagedExtensionFile file; if (!expected.TryGetValue(relative, out file)) throw ManagedExtensionJson.Error("UnexpectedFile"); VerifyFile(entry, file, token); }
                }
            }
        }
        internal static void VerifyFile(string path, ManagedExtensionFile file, CancellationToken token)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var sha = System.Security.Cryptography.SHA256.Create())
            {
                if (input.Length != file.Length) throw ManagedExtensionJson.Error("FileLengthMismatch");
                var buffer = new byte[65536]; long length = 0; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) != 0)
                { token.ThrowIfCancellationRequested(); length += count; sha.TransformBlock(buffer, 0, count, buffer, 0); }
                sha.TransformFinalBlock(buffer, 0, 0);
                if (length != file.Length || ManagedExtensionPaths.Hex(sha.Hash) != file.Sha256) throw ManagedExtensionJson.Error("FileDigestMismatch");
            }
        }
    }
}
