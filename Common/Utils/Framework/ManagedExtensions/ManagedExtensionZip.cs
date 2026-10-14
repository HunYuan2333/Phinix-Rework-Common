using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;

namespace Utils.Framework.ManagedExtensions
{
    public sealed partial class ManagedExtensionZip
    {
        public const int MaxPackageBytes=128*1024*1024;
        private readonly byte[] manifestBytes;
        private readonly Dictionary<string,byte[]> content;
        private ManagedExtensionZip(byte[] manifest,Dictionary<string,byte[]> files,string digest,ManagedExtensionInspectedPayload inspected,ExtensionLocalizationCatalog localization)
        {
            manifestBytes=manifest; content=files; Sha256=digest; Inspected=inspected; Localization=localization;
            Manifest=inspected.Manifest;
            Files=Manifest.AllFiles
                .Concat(new[]{new ManagedExtensionFile(ManagedExtensionManifest.FileName,manifest.Length,ManagedExtensionDigest.Hash(manifest))})
                .OrderBy(f=>f.Path,StringComparer.Ordinal).ToList().AsReadOnly();
        }
        public ManagedExtensionManifest Manifest { get; }
        public ManagedExtensionInspectedPayload Inspected { get; }
        public ExtensionLocalizationCatalog Localization { get; }
        public string Sha256 { get; }
        public ReadOnlyCollection<ManagedExtensionFile> Files { get; }
        public byte[] CopyManifestBytes() => (byte[])manifestBytes.Clone();
        public byte[] CopyFile(string path)
        {
            byte[] bytes;
            if(!content.TryGetValue(path,out bytes))throw ManagedExtensionJson.Error("ManagedFilesMismatch");
            return (byte[])bytes.Clone();
        }
        // Remote downloads, local imports and offline preflight share this bounded static inspector.
        public static ManagedExtensionZip Read(Stream input,CancellationToken token,long? expectedLength=null,string expectedDigest=null,Action<byte[]> verifyManifest=null,string gameVersion=null)
        {
            if(input==null || !input.CanRead) throw new ArgumentException("A readable ZIP stream is required.",nameof(input));
            byte[] bytes;
            using(var frozen=new MemoryStream())
            {
                byte[] buffer=new byte[65536]; int count;
                while((count=input.Read(buffer,0,buffer.Length))>0)
                {
                    token.ThrowIfCancellationRequested();
                    if(frozen.Length+count>Math.Min(MaxPackageBytes,expectedLength??MaxPackageBytes)) throw Error("PayloadLimit");
                    frozen.Write(buffer,0,count);
                }
                bytes=frozen.ToArray();
            }
            token.ThrowIfCancellationRequested();
            if(expectedLength.HasValue && expectedLength.Value!=bytes.LongLength) throw Error("PayloadSizeMismatch");
            string digest=ManagedExtensionDigest.Hash(bytes);
            if(expectedDigest!=null && digest!=expectedDigest) throw Error("PayloadDigestMismatch");
            try
            {
                using(var frozen=new MemoryStream(bytes,false))
                using(var zip=new ZipArchive(frozen,ZipArchiveMode.Read,false))
                {
                    if(zip.Entries.Count>4096) throw Error("PayloadLimit");
                    var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var tree=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
                    var entries=new Dictionary<string,ZipArchiveEntry>(StringComparer.Ordinal);
                    var directories=new List<string>(); long expanded=0;
                    foreach(var entry in zip.Entries)
                    {
                        token.ThrowIfCancellationRequested();
                        bool directory=entry.FullName.EndsWith("/",StringComparison.Ordinal);
                        string path=directory?entry.FullName.Substring(0,entry.FullName.Length-1):entry.FullName;
                        try { ManagedExtensionJson.Path(path); } catch(ManagedExtensionValidationException) { throw Error("UnsafeArchivePath"); }
                        if(!paths.Add(path)) throw Error("ArchivePathConflict");
                        RegisterPath(tree,path,directory);
                        int kind=(entry.ExternalAttributes>>16)&0xf000;
                        if((entry.ExternalAttributes&0x448)!=0 || (!directory && (entry.ExternalAttributes&0x10)!=0) ||
                            kind!=0 && kind!=(directory?0x4000:0x8000)) throw Error("UnsafeArchiveEntry");
                        if(entry.Length<0 || entry.Length>ManagedExtensionManifestReader.MaxFileBytes || entry.CompressedLength<0 ||
                            entry.CompressedLength>bytes.LongLength || directory && entry.Length!=0) throw Error("PayloadLimit");
                        expanded=checked(expanded+entry.Length);
                        if(expanded>ManagedExtensionManifestReader.MaxExpandedBytes || entry.Length>Math.Max(1024*1024L,entry.CompressedLength*200)) throw Error("PayloadLimit");
                        if(directory) directories.Add(path); else entries.Add(path,entry);
                    }
                    ZipArchiveEntry manifestEntry;
                    if(!entries.TryGetValue(ManagedExtensionManifest.FileName,out manifestEntry)) throw Error("ManagedFilesMismatch");
                    byte[] manifest=ReadEntry(manifestEntry,ManagedExtensionManifestReader.MaxManifestBytes,token);
                    var parsed=ManagedExtensionManifestReader.Read(manifest);
                    if(gameVersion!=null)parsed=parsed.ForGame(gameVersion);
                    verifyManifest?.Invoke((byte[])manifest.Clone());
                    var declarations=parsed.AllFiles.ToDictionary(f=>f.Path,StringComparer.Ordinal);
                    var allowed=new HashSet<string>(declarations.Keys,StringComparer.Ordinal) {ManagedExtensionManifest.FileName};
                    var allowedDirectories=new HashSet<string>(StringComparer.Ordinal);
                    foreach(string path in allowed)
                        for(int slash=path.IndexOf('/');slash>=0;slash=path.IndexOf('/',slash+1)) allowedDirectories.Add(path.Substring(0,slash));
                    if(directories.Any(d=>!allowedDirectories.Contains(d)) || entries.Keys.Any(p=>!allowed.Contains(p))) throw Error("UnsupportedManagedLayout");
                    if(!allowed.SetEquals(entries.Keys)) throw Error("ManagedFilesMismatch");
                    var content=new Dictionary<string,byte[]>(StringComparer.Ordinal);
                    foreach(var file in declarations.Values)
                    {
                        token.ThrowIfCancellationRequested(); var entry=entries[file.Path];
                        if(entry.Length!=file.Length) throw Error("ManagedFileLengthMismatch");
                        byte[] data=ReadEntry(entry,(int)ManagedExtensionManifestReader.MaxFileBytes,token);
                        if(ManagedExtensionDigest.Hash(data)!=file.Sha256) throw Error("ManagedFileDigestMismatch");
                        content.Add(file.Path,data);
                    }
                    foreach(var variant in parsed.Variants)ExtensionLocalizationCatalog.Load(variant.Localization,f=>content[f.Path],token);
                    var localization=ExtensionLocalizationCatalog.Load(parsed.Localization,f=>content[f.Path],token);
                    var assemblyDeclarations=parsed.Variants.Count==0?parsed.Assemblies.AsEnumerable():parsed.Variants.SelectMany(v=>v.Assemblies);
                    var assemblies=assemblyDeclarations.ToDictionary(a=>a.File.Path,a=>content[a.File.Path],StringComparer.Ordinal);
                    var inspected=ManagedExtensionPayloadInspector.InspectAll(parsed,assemblies,token);
                    return new ManagedExtensionZip(manifest,content,digest,inspected,localization);
                }
            }
            catch(InvalidDataException ex) { throw new ManagedExtensionValidationException("InvalidArchive",ex); }
            catch(OverflowException ex) { throw new ManagedExtensionValidationException("PayloadLimit",ex); }
        }
        private static byte[] ReadEntry(ZipArchiveEntry entry,int max,CancellationToken token)
        {
            if(entry.Length<0 || entry.Length>max) throw Error("PayloadLimit");
            byte[] bytes=new byte[(int)entry.Length];
            using(var stream=entry.Open())
            {
                int offset=0;
                while(offset<bytes.Length)
                {
                    token.ThrowIfCancellationRequested(); int read=stream.Read(bytes,offset,bytes.Length-offset);
                    if(read==0) throw Error("InvalidArchive"); offset+=read;
                }
                if(stream.ReadByte()!=-1) throw Error("PayloadLimit");
            }
            return bytes;
        }
        private static ManagedExtensionValidationException Error(string code) => ManagedExtensionJson.Error(code);
        private static void RegisterPath(Dictionary<string,string> paths,string path,bool directory)
        {
            string[] parts=path.Split('/'); string current="";
            for(int i=0;i<parts.Length;i++)
            {
                current=current.Length==0?parts[i]:current+"/"+parts[i];
                bool dir=i<parts.Length-1 || directory; string expected=(dir?"d:":"f:")+current,old;
                if(paths.TryGetValue(current,out old)) { if(old!=expected) throw Error("ArchivePathConflict"); }
                else paths.Add(current,expected);
            }
        }
    }
}
