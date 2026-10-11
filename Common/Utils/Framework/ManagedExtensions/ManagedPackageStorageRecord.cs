using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Utils.Framework.ManagedExtensions
{
    internal static class ManagedPackageMutation
    {
        internal static readonly object Gate=new object();
        internal static IDisposable Enter()
        {
            if(!System.Threading.Monitor.TryEnter(Gate)) throw new ManagedExtensionValidationException("ManagedStoreBusy");
            return new Held();
        }
        private sealed class Held:IDisposable
        {public void Dispose() {System.Threading.Monitor.Exit(Gate);}}
    }

    // Current storage facts. DLL schemas are decoded only by the existing input adapter.
    internal sealed class ManagedPackageStorageRecord
    {
        internal string RecordKey, SourceId, PackageId, Version, ManifestSha256, ArtifactSha256, InstallationTransactionId;
        internal List<ManagedExtensionFile> Files;
        internal ManagedExtensionPackageSnapshot Extension;
        internal byte[] ReceiptBytes, StateBytes;
        internal static ManagedPackageStorageRecord FromExtension(ManagedExtensionPackageSnapshot row, IEnumerable<ManagedExtensionFile> files)
            => new ManagedPackageStorageRecord { RecordKey=row.RecordKey,SourceId=row.SourceId,PackageId=row.PackageId,Version=row.Version,
                ManifestSha256=row.ManifestSha256,ArtifactSha256=row.ArtifactSha256,InstallationTransactionId=row.InstallationTransactionId,
                Files=files.ToList(),Extension=row };

        internal const string LocalSource="local-theme";
        internal static ManagedPackageStorageRecord Create(string id,string version,string artifact,byte[] manifest,
            IEnumerable<ManagedExtensionFile> files,string operation)
        {
            var r=new ManagedPackageStorageRecord {SourceId=LocalSource,PackageId=ManagedExtensionJson.Identifier(id),Version=ManagedExtensionVersion.Parse(version).ToString(),
                ArtifactSha256=ManagedExtensionJson.Hex(artifact,64),ManifestSha256=ManagedExtensionPaths.Hash(manifest),InstallationTransactionId=ManagedExtensionJson.Hex(operation,32),Files=files.ToList()};
            r.RecordKey=ManagedExtensionPaths.PackageKey(r.SourceId,r.PackageId);
            r.ReceiptBytes=Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"kind\":\"theme\",\"sourceKind\":\"local\",\"sourceId\":\""+r.SourceId+
                "\",\"packageId\":\""+r.PackageId+"\",\"version\":\""+r.Version+"\",\"manifestSha256\":\""+r.ManifestSha256+"\",\"artifactSha256\":\""+r.ArtifactSha256+
                "\",\"installationTransactionId\":\""+r.InstallationTransactionId+"\",\"files\":["+string.Join(",",r.Files.OrderBy(f=>f.Path,StringComparer.Ordinal).Select(f=>
                    "{\"path\":\""+f.Path+"\",\"length\":"+f.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)+",\"sha256\":\""+f.Sha256+"\"}"))+"]}");
            r.StateBytes=Encoding.UTF8.GetBytes("{\"schemaVersion\":3,\"kind\":\"theme\",\"sourceId\":\""+r.SourceId+"\",\"packageId\":\""+r.PackageId+
                "\",\"manifestSha256\":\""+r.ManifestSha256+"\",\"operationId\":\""+r.InstallationTransactionId+"\",\"state\":\"installed\"}");
            return r;
        }
        internal static ManagedPackageStorageRecord Read(byte[] receipt,byte[] state,byte[] manifest)
        {
            var f=ManagedExtensionJson.Object(ManagedExtensionJson.Read(receipt,65536),"schemaVersion","kind","sourceKind","sourceId","packageId","version","manifestSha256","artifactSha256","installationTransactionId","files");
            if(ManagedExtensionJson.Integer(ManagedExtensionJson.Required(f,"schemaVersion"),3,3)!=3 ||
                ManagedExtensionJson.Text(ManagedExtensionJson.Required(f,"kind"),32)!="theme" ||
                ManagedExtensionJson.Text(ManagedExtensionJson.Required(f,"sourceKind"),32)!="local" ||
                ManagedExtensionJson.Id(ManagedExtensionJson.Required(f,"sourceId"))!=LocalSource) throw ManagedExtensionJson.Error("DataReceiptInvalid");
            var parsed=Themes.ThemePackageManifest.Read(manifest);
            var files=parsed.Themes.Select(t=>new ManagedExtensionFile(t.Path,t.Length,t.Sha256))
                .Concat(new[]{new ManagedExtensionFile("manifest.json",manifest.Length,ManagedExtensionPaths.Hash(manifest))}).ToList();
            if(files.Sum(file=>file.Length)>8L*1024*1024) throw ManagedExtensionJson.Error("ThemeExpandedLimit");
            var row=Create(ManagedExtensionJson.Id(ManagedExtensionJson.Required(f,"packageId")),ManagedExtensionJson.Text(ManagedExtensionJson.Required(f,"version"),32),
                ManagedExtensionJson.Hex(ManagedExtensionJson.Required(f,"artifactSha256"),64),manifest,files,ManagedExtensionJson.Hex(ManagedExtensionJson.Required(f,"installationTransactionId"),32));
            if(parsed.PackageId!=row.PackageId || parsed.Version.ToString()!=row.Version || !receipt.SequenceEqual(row.ReceiptBytes) || !state.SequenceEqual(row.StateBytes))
                throw ManagedExtensionJson.Error("DataReceiptIdentityMismatch");
            return row;
        }
    }
}
