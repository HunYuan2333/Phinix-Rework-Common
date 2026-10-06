using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace Utils.Framework.ManagedExtensions
{
    public enum ManagedExtensionDesiredState { Unknown, Enabled, Disabled, PendingRemoval }
    public enum ManagedExtensionContentState { Invalid, ContentVerified }

    /// <summary>Stable allocation only. No directory creation or filesystem certification.</summary>
    public sealed class ManagedExtensionPaths
    {
        public ManagedExtensionPaths(string saveDataRoot)
        {
            SaveDataRoot = Absolute(saveDataRoot);
            RootDirectory = Path.Combine(SaveDataRoot, "Phinix", "ManagedExtensions");
            PackagesDirectory = Path.Combine(RootDirectory, "packages");
            InstalledRecordsDirectory = Path.Combine(RootDirectory, "state", "installed");
            DesiredStateDirectory = Path.Combine(RootDirectory, "state", "desired");
            TransactionsDirectory = Path.Combine(RootDirectory, "transactions");
        }
        public string SaveDataRoot { get; }
        public string RootDirectory { get; }
        public string PackagesDirectory { get; }
        public string InstalledRecordsDirectory { get; }
        public string DesiredStateDirectory { get; }
        public string TransactionsDirectory { get; }
        public string GetPackageDirectory(string sourceId, string packageId) { return Path.Combine(PackagesDirectory, PackageKey(sourceId, packageId)); }
        public string GetInstalledRecordPath(string sourceId, string packageId) { return Path.Combine(InstalledRecordsDirectory, PackageKey(sourceId, packageId) + ".json"); }
        public string GetDesiredStatePath(string sourceId, string packageId) { return Path.Combine(DesiredStateDirectory, PackageKey(sourceId, packageId) + ".json"); }
        public static string PackageKey(string sourceId, string packageId)
        {
            ManagedExtensionJson.Identifier(sourceId); ManagedExtensionJson.Identifier(packageId);
            return "pkg-" + Hash(Encoding.UTF8.GetBytes(sourceId + "\n" + packageId));
        }
        public static string Hash(byte[] bytes)
        {
            return ManagedExtensionDigest.Hash(bytes);
        }
        internal static string Hex(byte[] bytes) { return ManagedExtensionDigest.Hex(bytes); }
        internal static string Absolute(string path)
        {
            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path)) throw new ArgumentException("An absolute save-data root is required.", nameof(path));
            string root = Path.GetPathRoot(path);
            if (Path.DirectorySeparatorChar == '\\' && (root == "\\" || root == "/" || root.Length == 2 && root[1] == ':'))
                throw new ArgumentException("A fully qualified save-data root is required.", nameof(path));
            string full = Path.GetFullPath(path);
            string trimmed = full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmed.Length < Path.GetPathRoot(full).Length ? Path.GetPathRoot(full) : trimmed;
        }
    }

    /// <summary>Local inspection, not proof of loadability, activation or remote approval.</summary>
    public sealed class ManagedExtensionPackageSnapshot
    {
        internal ManagedExtensionPackageSnapshot(string recordKey, string sourceId, string identityHash, string packageId,
            string version, string manifestHash, string snapshotId, string catalogHash, string artifactHash, string transactionId, string stateOperationId,
            ManagedExtensionManifest manifest, ManagedExtensionDesiredState desiredState,
            ManagedExtensionContentState contentState, string diagnosticCode)
        {
            RecordKey = recordKey; SourceId = sourceId; RepositoryIdentitySha256 = identityHash; PackageId = packageId;
            Version = version; ManifestSha256 = manifestHash; InstallationTransactionId = transactionId;
            CatalogSnapshotId = snapshotId; CatalogSha256 = catalogHash; ArtifactSha256 = artifactHash;
            StateOperationId = stateOperationId; Manifest = manifest; DesiredState = desiredState;
            ContentState = contentState; DiagnosticCode = diagnosticCode;
        }
        public string RecordKey { get; }
        public string SourceId { get; }
        public string RepositoryIdentitySha256 { get; }
        public string PackageId { get; }
        public string Version { get; }
        public string ManifestSha256 { get; }
        public string CatalogSnapshotId { get; }
        public string CatalogSha256 { get; }
        public string ArtifactSha256 { get; }
        public string InstallationTransactionId { get; }
        public string StateOperationId { get; }
        public ManagedExtensionManifest Manifest { get; }
        public ManagedExtensionDesiredState DesiredState { get; }
        public ManagedExtensionContentState ContentState { get; }
        public string DiagnosticCode { get; }
        // Deliberately no CanLoad flag: M2 must validate PE identity, references and compatibility.
    }

    public sealed class ManagedExtensionInventorySnapshot
    {
        internal ManagedExtensionInventorySnapshot(IEnumerable<ManagedExtensionPackageSnapshot> packages, IEnumerable<string> diagnostics)
        { Packages = ManagedExtensionCompatibility.Freeze(packages); Diagnostics = ManagedExtensionCompatibility.Freeze(diagnostics); }
        public ReadOnlyCollection<ManagedExtensionPackageSnapshot> Packages { get; }
        /// <summary>Root-level uncertainty. Loading must fail closed when this collection is not empty.</summary>
        public ReadOnlyCollection<string> Diagnostics { get; }
    }

    public static class ManagedExtensionInventoryReader
    {
        public const int MaxPackages = 1024;
        public const int MaxRecordBytes = 2 * 1024 * 1024;
        internal sealed class Receipt
        {
            internal string SourceId, IdentityHash, PackageId, Version, ManifestHash, SnapshotId, CatalogHash, ArtifactHash, TransactionId;
            internal List<ManagedExtensionFile> Files;
        }

        public static ManagedExtensionInventorySnapshot Read(ManagedExtensionPaths paths, CancellationToken cancellationToken)
        {
            if (paths == null) throw new ArgumentNullException(nameof(paths));
            cancellationToken.ThrowIfCancellationRequested();
            var packages = new List<ManagedExtensionPackageSnapshot>();
            var diagnostics = new List<string>();
            try
            {
                NoLinks(paths.InstalledRecordsDirectory);
                if (System.IO.File.Exists(paths.InstalledRecordsDirectory)) throw ManagedExtensionJson.Error("InventoryRootNotDirectory");
                if (!Directory.Exists(paths.InstalledRecordsDirectory)) return new ManagedExtensionInventorySnapshot(packages, diagnostics);
                // Bounded streaming enumeration prevents a directory flood becoming an unbounded list.
                var records = new List<string>();
                foreach (string path in Directory.EnumerateFileSystemEntries(paths.InstalledRecordsDirectory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (records.Count == MaxPackages) { diagnostics.Add("InventoryLimit"); break; }
                    records.Add(path);
                }
                foreach (string record in records.OrderBy(Path.GetFileName, StringComparer.Ordinal))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    packages.Add(ReadPackage(paths, record, cancellationToken));
                }
            }
            catch (ManagedExtensionValidationException ex) { diagnostics.Add(ex.Code); }
            catch (IOException) { diagnostics.Add("InventoryStorageFailed"); }
            catch (UnauthorizedAccessException) { diagnostics.Add("InventoryStorageFailed"); }
            return new ManagedExtensionInventorySnapshot(packages, diagnostics);
        }

        private static ManagedExtensionPackageSnapshot ReadPackage(ManagedExtensionPaths paths, string record, CancellationToken token)
        {
            Receipt receipt = null;
            ManagedExtensionManifest manifest = null;
            ManagedExtensionDesiredState desired = ManagedExtensionDesiredState.Unknown;
            string operationId = null, code = null;
            bool contentVerified = false;
            string key = Path.GetFileNameWithoutExtension(record);
            // Never echo an unvalidated directory entry through public diagnostic/identity fields.
            string safeKey = System.Text.RegularExpressions.Regex.IsMatch(key, @"\Apkg-[0-9a-f]{64}\z") ? key : null;
            try
            {
                if (safeKey == null || Path.GetFileName(record) != safeKey + ".json") throw ManagedExtensionJson.Error("InvalidRecordName");
                NoLinks(record);
                receipt = ReadReceipt(Bytes(record, MaxRecordBytes, token));
                if (ManagedExtensionPaths.PackageKey(receipt.SourceId, receipt.PackageId) != safeKey) throw ManagedExtensionJson.Error("ReceiptIdentityMismatch");
                string root = paths.GetPackageDirectory(receipt.SourceId, receipt.PackageId);
                NoLinks(root);
                string manifestPath = Path.Combine(root, ManagedExtensionManifest.FileName);
                NoLinks(manifestPath);
                byte[] manifestBytes = Bytes(manifestPath, ManagedExtensionManifestReader.MaxManifestBytes, token);
                if (ManagedExtensionPaths.Hash(manifestBytes) != receipt.ManifestHash) throw ManagedExtensionJson.Error("ManifestDigestMismatch");
                manifest = ManagedExtensionManifestReader.Read(manifestBytes);
                if (manifest.PackageId != receipt.PackageId || manifest.Version.ToString() != receipt.Version) throw ManagedExtensionJson.Error("ManifestIdentityMismatch");

                var expected = manifest.Assemblies.Select(a => a.File).Concat(manifest.Resources).ToList();
                expected.Add(new ManagedExtensionFile(ManagedExtensionManifest.FileName, manifestBytes.Length, receipt.ManifestHash));
                if (expected.Count != receipt.Files.Count || expected.Any(f => !receipt.Files.Any(r =>
                    r.Path == f.Path && r.Length == f.Length && r.Sha256 == f.Sha256))) throw ManagedExtensionJson.Error("ReceiptFilesMismatch");
                VerifyTree(root, expected, token);
                contentVerified = true;

                string statePath = paths.GetDesiredStatePath(receipt.SourceId, receipt.PackageId);
                NoLinks(statePath);
                var state = ManagedExtensionJson.Object(ManagedExtensionJson.Read(Bytes(statePath, 8192, token), 8192),
                    "schemaVersion", "sourceId", "packageId", "manifestSha256", "operationId", "desiredState");
                if (ManagedExtensionJson.Integer(Get(state, "schemaVersion"), 1, int.MaxValue) != 1) throw ManagedExtensionJson.Error("UnsupportedStateSchema");
                if (ManagedExtensionJson.Id(Get(state, "sourceId")) != receipt.SourceId ||
                    ManagedExtensionJson.Id(Get(state, "packageId")) != receipt.PackageId ||
                    ManagedExtensionJson.Hex(Get(state, "manifestSha256"), 64) != receipt.ManifestHash) throw ManagedExtensionJson.Error("DesiredStateIdentityMismatch");
                operationId = ManagedExtensionJson.Hex(Get(state, "operationId"), 32);
                switch (ManagedExtensionJson.Text(Get(state, "desiredState"), 32))
                {
                    case "enabled": desired = ManagedExtensionDesiredState.Enabled; break;
                    case "disabled": desired = ManagedExtensionDesiredState.Disabled; break;
                    case "pending-removal": desired = ManagedExtensionDesiredState.PendingRemoval; break;
                    default: throw ManagedExtensionJson.Error("InvalidDesiredState");
                }
            }
            catch (ManagedExtensionValidationException ex) { code = ex.Code; }
            catch (FileNotFoundException) { code = contentVerified ? "DesiredStateMissing" : "PackageFileMissing"; }
            catch (DirectoryNotFoundException) { code = contentVerified ? "DesiredStateMissing" : "PackageFileMissing"; }
            catch (IOException) { code = "PackageStorageFailed"; }
            catch (UnauthorizedAccessException) { code = "PackageStorageFailed"; }
            return new ManagedExtensionPackageSnapshot(safeKey, receipt?.SourceId, receipt?.IdentityHash, receipt?.PackageId, receipt?.Version,
                receipt?.ManifestHash, receipt?.SnapshotId, receipt?.CatalogHash, receipt?.ArtifactHash, receipt?.TransactionId, operationId, manifest, desired,
                contentVerified ? ManagedExtensionContentState.ContentVerified : ManagedExtensionContentState.Invalid, code);
        }

        internal static Receipt ReadReceipt(byte[] bytes)
        {
            var f = ManagedExtensionJson.Object(ManagedExtensionJson.Read(bytes, MaxRecordBytes),
                "schemaVersion", "sourceId", "repositoryIdentitySha256", "packageId", "version", "manifestSha256", "catalogSnapshotId",
                "catalogSha256", "artifactSha256", "installationTransactionId", "files");
            if (ManagedExtensionJson.Integer(Get(f, "schemaVersion"), 1, int.MaxValue) != 1) throw ManagedExtensionJson.Error("UnsupportedReceiptSchema");
            var result = new Receipt
            {
                SourceId = ManagedExtensionJson.Id(Get(f, "sourceId")), IdentityHash = ManagedExtensionJson.Hex(Get(f, "repositoryIdentitySha256"), 64),
                PackageId = ManagedExtensionJson.Id(Get(f, "packageId")), Version = ManagedExtensionVersion.Parse(ManagedExtensionJson.Text(Get(f, "version"), 32)).ToString(),
                ManifestHash = ManagedExtensionJson.Hex(Get(f, "manifestSha256"), 64), TransactionId = ManagedExtensionJson.Hex(Get(f, "installationTransactionId"), 32),
                SnapshotId = ManagedExtensionJson.Hex(Get(f, "catalogSnapshotId"), 40), CatalogHash = ManagedExtensionJson.Hex(Get(f, "catalogSha256"), 64),
                ArtifactHash = ManagedExtensionJson.Hex(Get(f, "artifactSha256"), 64),
                Files = new List<ManagedExtensionFile>()
            };
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var item in ManagedExtensionJson.Array(Get(f, "files"), 577))
            {
                var fields = ManagedExtensionJson.Object(item, "path", "length", "sha256");
                var file = ManagedExtensionManifestReader.File(fields);
                if (!paths.Add(file.Path)) throw ManagedExtensionJson.Error("DuplicateFile");
                total = checked(total + file.Length);
                result.Files.Add(file);
            }
            if (result.Files.Count < 2 || total > ManagedExtensionManifestReader.MaxExpandedBytes) throw ManagedExtensionJson.Error("ReceiptLimit");
            ManagedExtensionManifestReader.ValidatePathTree(paths);
            return result;
        }

        internal static void VerifyTree(string root, List<ManagedExtensionFile> files, CancellationToken token)
        {
            var expected = files.ToDictionary(f => f.Path, StringComparer.Ordinal);
            var directories = new HashSet<string>(StringComparer.Ordinal);
            foreach (string path in expected.Keys)
            {
                int slash = path.LastIndexOf('/');
                while (slash > 0) { directories.Add(path.Substring(0, slash)); slash = path.LastIndexOf('/', slash - 1); }
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Stack<Tuple<string, string>>();
            pending.Push(Tuple.Create(root, ""));
            int entries = 0;
            while (pending.Count != 0)
            {
                var directory = pending.Pop();
                NoLinks(directory.Item1);
                foreach (string entry in Directory.EnumerateFileSystemEntries(directory.Item1))
                {
                    token.ThrowIfCancellationRequested();
                    if (++entries > 4096) throw ManagedExtensionJson.Error("PackageTreeLimit");
                    string relative = directory.Item2 + Path.GetFileName(entry);
                    ManagedExtensionJson.Path(relative);
                    if (!seen.Add(relative)) throw ManagedExtensionJson.Error("PathAlias");
                    NoLinks(entry);
                    if ((System.IO.File.GetAttributes(entry) & FileAttributes.Directory) != 0)
                    {
                        if (!directories.Contains(relative)) throw ManagedExtensionJson.Error("UnexpectedDirectory");
                        pending.Push(Tuple.Create(entry, relative + "/"));
                    }
                    else
                    {
                        ManagedExtensionFile file;
                        if (!expected.TryGetValue(relative, out file)) throw ManagedExtensionJson.Error("UnexpectedFile");
                        using (var input = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read))
                        using (var sha = SHA256.Create())
                        {
                            if (input.Length != file.Length) throw ManagedExtensionJson.Error("FileLengthMismatch");
                            byte[] buffer = new byte[64 * 1024];
                            long length = 0;
                            int count;
                            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                            {
                                token.ThrowIfCancellationRequested();
                                length += count;
                                if (length > file.Length) throw ManagedExtensionJson.Error("FileLengthMismatch");
                                sha.TransformBlock(buffer, 0, count, buffer, 0);
                            }
                            sha.TransformFinalBlock(buffer, 0, 0);
                            if (length != file.Length || ManagedExtensionPaths.Hex(sha.Hash) != file.Sha256) throw ManagedExtensionJson.Error("FileDigestMismatch");
                        }
                    }
                }
            }
            if (expected.Keys.Any(path => !seen.Contains(path))) throw ManagedExtensionJson.Error("PackageFileMissing");
        }

        internal static byte[] Bytes(string path, int maximum, CancellationToken token)
        {
            using (var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (input.Length < 1 || input.Length > maximum) throw ManagedExtensionJson.Error("DocumentLimit");
                byte[] bytes = new byte[(int)input.Length];
                int offset = 0;
                while (offset != bytes.Length)
                {
                    token.ThrowIfCancellationRequested();
                    int read = input.Read(bytes, offset, bytes.Length - offset);
                    if (read == 0) throw ManagedExtensionJson.Error("DocumentChanged");
                    offset += read;
                }
                if (input.ReadByte() != -1) throw ManagedExtensionJson.Error("DocumentChanged");
                return bytes;
            }
        }

        // Inspect all ancestors, including dangling links. Lexical containment alone is insufficient.
        internal static void NoLinks(string path)
        {
            string current = ManagedExtensionPaths.Absolute(path);
            while (current != null)
            {
                try
                {
                    if ((System.IO.File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw ManagedExtensionJson.Error("UnsafeFilesystemLink");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
                var parent = Directory.GetParent(current);
                current = parent?.FullName;
            }
        }
        private static XElement Get(Dictionary<string, XElement> fields, string name) { return ManagedExtensionJson.Required(fields, name); }
    }
}
