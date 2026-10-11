using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using Utils.Framework.ManagedExtensions;

namespace Utils.Framework.Themes
{
    public sealed class ThemePackageInput
    {
        private readonly byte[] document;
        public ThemePackageInput(string id,string name,byte[] document)
        { Id=id; Name=name; this.document=(byte[])(document??throw new ArgumentNullException(nameof(document))).Clone(); }
        public string Id { get; }
        public string Name { get; }
        public byte[] CopyDocument() => (byte[])document.Clone();
    }
    public static class ThemePackageBuilder
    {
        public static ThemePackage Build(string id,string name,string author,string version,string hostRange,IEnumerable<ThemePackageInput> inputs)
        {
            var files=new Dictionary<string,byte[]>(StringComparer.Ordinal); var declarations=new List<string>();
            foreach(var theme in inputs??throw new ArgumentNullException(nameof(inputs)))
            {
                if(declarations.Count>=16) throw new ManagedExtensionValidationException("ThemeEntryLimit");
                string path="Themes/"+theme.Id+".xml"; byte[] bytes=theme.CopyDocument(); int schema=ThemePackageDocument.Validate(bytes);
                files.Add(path,bytes);
                declarations.Add("{\"id\":"+Quote(theme.Id)+",\"name\":"+Quote(theme.Name)+",\"path\":"+Quote(path)+",\"schemaVersion\":"+schema+
                    ",\"length\":"+bytes.Length+",\"sha256\":"+Quote(ThemePackageJson.Hash(bytes))+"}");
            }
            byte[] manifest=Encoding.UTF8.GetBytes("{\"schemaVersion\":1,\"management\":\"phinix-theme\",\"packageId\":"+Quote(id)+",\"name\":"+Quote(name)+
                ",\"author\":"+Quote(author)+",\"version\":"+Quote(version)+",\"phinixRange\":"+Quote(hostRange)+",\"themes\":["+string.Join(",",declarations)+"]}");
            ThemePackageManifest.Read(manifest); files.Add("manifest.json",manifest);
            using(var memory=new MemoryStream())
            {
                using(var zip=new ZipArchive(memory,ZipArchiveMode.Create,true)) foreach(var pair in files.OrderBy(p=>p.Key,StringComparer.Ordinal))
                {
                    var entry=zip.CreateEntry(pair.Key,CompressionLevel.Optimal); entry.LastWriteTime=new DateTimeOffset(2026,10,11,0,0,0,TimeSpan.Zero); entry.ExternalAttributes=0;
                    using(var output=entry.Open()) output.Write(pair.Value,0,pair.Value.Length);
                }
                memory.Position=0; return ThemePackage.Read(memory,CancellationToken.None);
            }
        }
        public static void WriteNew(ThemePackage package,string destination)
        {
            destination=Path.GetFullPath(destination);
            if(!string.Equals(Path.GetExtension(destination),".zip",StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Theme output must be ZIP.");
            ManagedExtensionInstallationRecovery.ValidateFilePath(destination); ManagedExtensionInventoryReader.NoLinks(destination);
            if(File.Exists(destination) || Directory.Exists(destination)) throw new IOException("Export target already exists. Choose another path.");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)); ManagedExtensionInventoryReader.NoLinks(destination);
            string temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            ManagedExtensionInstallationRecovery.ValidateFilePath(temporary); byte[] bytes=package.CopyArchive(); bool created=false;
            try
            {
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {created=true;output.Write(bytes,0,bytes.Length);output.Flush(true);}
                ManagedExtensionInventoryReader.NoLinks(destination); File.Move(temporary,destination);
            }
            finally { if(created && File.Exists(temporary)) { ManagedExtensionInventoryReader.NoLinks(temporary); File.Delete(temporary); } }
        }
        private static string Quote(string value)
        {
            if(value==null) throw new ArgumentNullException(nameof(value));
            var result=new StringBuilder("\"");
            foreach(char c in value)
            {
                if(c=='"' || c=='\\') {result.Append('\\');result.Append(c);}
                else if(c<32) result.Append("\\u").Append(((int)c).ToString("x4"));
                else result.Append(c);
            }
            return result.Append('"').ToString();
        }
    }
}
