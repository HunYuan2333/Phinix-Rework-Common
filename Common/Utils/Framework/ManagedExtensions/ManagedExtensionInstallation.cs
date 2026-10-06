using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace Utils.Framework.ManagedExtensions
{
    /// <summary>Frozen caller input; provenance is not a signature or remote freshness approval.</summary>
    public sealed class ManagedExtensionInstallPackage
    {
        internal readonly byte[] ManifestBytes;
        internal readonly Dictionary<string,byte[]> Content;
        public ManagedExtensionInstallPackage(string sourceId,string identityHash,string snapshotId,string catalogHash,
            string artifactHash,byte[] manifest,IReadOnlyDictionary<string,byte[]> files)
            : this(sourceId,identityHash,snapshotId,catalogHash,artifactHash,manifest,files,null) { }
        public ManagedExtensionInstallPackage(string sourceId,string identityHash,string snapshotId,string catalogHash,
            string artifactHash,byte[] manifest,IReadOnlyDictionary<string,byte[]> files,ManagedExtensionPackageSnapshot replacement)
        {
            SourceId=ManagedExtensionJson.Identifier(sourceId); RepositoryIdentitySha256=ManagedExtensionJson.Hex(identityHash,64);
            CatalogSnapshotId=ManagedExtensionJson.Hex(snapshotId,40); CatalogSha256=ManagedExtensionJson.Hex(catalogHash,64);
            ArtifactSha256=ManagedExtensionJson.Hex(artifactHash,64);
            if(manifest==null || manifest.Length==0 || manifest.Length>ManagedExtensionManifestReader.MaxManifestBytes) throw ManagedExtensionJson.Error("DocumentLimit");
            ManifestBytes=(byte[])manifest.Clone(); Manifest=ManagedExtensionManifestReader.Read(ManifestBytes);
            if(replacement!=null && (replacement.SourceId!=SourceId || replacement.RepositoryIdentitySha256!=RepositoryIdentitySha256 ||
                replacement.PackageId!=Manifest.PackageId || replacement.DesiredState!=ManagedExtensionDesiredState.Enabled ||
                replacement.ContentState!=ManagedExtensionContentState.ContentVerified || replacement.DiagnosticCode!=null ||
                Manifest.Version.CompareTo(ManagedExtensionVersion.Parse(replacement.Version))<=0)) throw ManagedExtensionJson.Error("ManagedReplacementIdentityInvalid");
            Replacement=replacement;
            if(files==null || files.Count!=Manifest.Assemblies.Count+Manifest.Resources.Count) throw ManagedExtensionJson.Error("ManagedInstallFilesMismatch");
            var expected=Manifest.Assemblies.Select(a=>a.File).Concat(Manifest.Resources).ToDictionary(f=>f.Path,StringComparer.Ordinal);
            Content=new Dictionary<string,byte[]>(StringComparer.Ordinal); long length=ManifestBytes.Length;
            foreach(var pair in files)
            {
                ManagedExtensionFile file;
                if(!expected.TryGetValue(pair.Key,out file) || pair.Value==null || pair.Value.LongLength!=file.Length || pair.Value.LongLength>ManagedExtensionManifestReader.MaxFileBytes)
                    throw ManagedExtensionJson.Error("ManagedInstallFilesMismatch");
                length=checked(length+pair.Value.LongLength);
                if(length>ManagedExtensionManifestReader.MaxExpandedBytes) throw ManagedExtensionJson.Error("ManagedInstallMemoryLimit");
                Content.Add(pair.Key,(byte[])pair.Value.Clone());
            }
            ExpandedBytes=length;
        }
        public string SourceId { get; }
        public string RepositoryIdentitySha256 { get; }
        public string CatalogSnapshotId { get; }
        public string CatalogSha256 { get; }
        public string ArtifactSha256 { get; }
        public ManagedExtensionManifest Manifest { get; }
        public long ExpandedBytes { get; }
        public ManagedExtensionPackageSnapshot Replacement { get; }
    }

    public sealed class ManagedExtensionInstallRequest
    {
        public const int MaxPackages=32;
        public ManagedExtensionInstallRequest(IEnumerable<ManagedExtensionInstallPackage> packages)
        {
            if(packages==null) throw new ArgumentNullException(nameof(packages));
            var items=new List<ManagedExtensionInstallPackage>(); long total=0;
            foreach(var package in packages)
            {
                if(package==null || items.Count==MaxPackages) throw ManagedExtensionJson.Error("ManagedInstallBatchLimit");
                total=checked(total+package.ExpandedBytes);
                if(total>ManagedExtensionManifestReader.MaxExpandedBytes) throw ManagedExtensionJson.Error("ManagedInstallMemoryLimit");
                items.Add(package);
            }
            if(items.Count==0) throw ManagedExtensionJson.Error("ManagedInstallBatchLimit");
            var first=items[0];
            if(items.Any(p=>p.SourceId!=first.SourceId || p.RepositoryIdentitySha256!=first.RepositoryIdentitySha256 || p.CatalogSnapshotId!=first.CatalogSnapshotId || p.CatalogSha256!=first.CatalogSha256))
                throw ManagedExtensionJson.Error("ManagedInstallProvenanceMismatch");
            if(items.Select(p=>p.Manifest.PackageId).Distinct(StringComparer.Ordinal).Count()!=items.Count) throw ManagedExtensionJson.Error("ManagedInstallPackageConflict");
            Packages=items.AsReadOnly();
        }
        public ReadOnlyCollection<ManagedExtensionInstallPackage> Packages { get; }
    }

    public sealed class ManagedExtensionInstallResult
    {
        internal ManagedExtensionInstallResult(bool success,string code,string transaction,IEnumerable<ManagedExtensionPackageSnapshot> packages,ManagedExtensionAssemblyReferenceFailure referenceFailure=null)
        { Succeeded=success; Code=code; TransactionId=transaction; Packages=ManagedExtensionCompatibility.Freeze(packages); ReferenceFailure=referenceFailure; }
        public bool Succeeded { get; }
        public string Code { get; }
        public string TransactionId { get; }
        public ManagedExtensionAssemblyReferenceFailure ReferenceFailure { get; }
        public ReadOnlyCollection<ManagedExtensionPackageSnapshot> Packages { get; }
    }

    public interface IManagedExtensionInstallationService
    {
        // Host captures module settings before background work. No download or game API is used here.
        ManagedExtensionInstallResult Install(ManagedExtensionInstallRequest request,IEnumerable<string> disabledModules,CancellationToken token);
    }
}
