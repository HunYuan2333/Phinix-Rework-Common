using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml;
using System.Xml.Linq;
using Utils.Framework.ManagedExtensions;

namespace Utils.Framework.Themes
{
    // Data package boundary. No Unity, Verse, assembly loading, module discovery or host API binding.
    public sealed class ThemePackageVariant
    {
        internal ThemePackageVariant(string id, string name, string path, int schema, int length, string digest)
        { Id=id; Name=name; Path=path; SchemaVersion=schema; Length=length; Sha256=digest; }
        public string Id { get; }
        public string Name { get; }
        public string Path { get; }
        public int SchemaVersion { get; }
        public int Length { get; }
        public string Sha256 { get; }
    }

    public sealed class ThemePackageManifest
    {
        public const string Management="phinix-theme";
        internal ThemePackageManifest(string id,string name,string author,string version,string range,IEnumerable<ThemePackageVariant> themes)
        { PackageId=id; Name=name; Author=author; Version=ManagedExtensionVersion.Parse(version);
          PhinixRange=ManagedExtensionVersionRange.Parse(range); Themes=new List<ThemePackageVariant>(themes).AsReadOnly(); }
        public string PackageId { get; }
        public string Name { get; }
        public string Author { get; }
        public ManagedExtensionVersion Version { get; }
        public ManagedExtensionVersionRange PhinixRange { get; }
        public ReadOnlyCollection<ThemePackageVariant> Themes { get; }
        public string ThemeIdentity(ThemePackageVariant variant) => "package/"+PackageId+"/"+variant.Id;
        public static ThemePackageManifest Read(byte[] bytes)
        {
            var f=ThemePackageJson.Object(ThemePackageJson.Read(bytes,65536),"schemaVersion","management","packageId","name","author","version","phinixRange","themes");
            if(ThemePackageJson.Integer(ThemePackageJson.Get(f,"schemaVersion"),1,1)!=1 ||
                ThemePackageJson.Text(ThemePackageJson.Get(f,"management"),32)!=Management) throw ThemePackageJson.Error("UnsupportedThemePackage");
            var themes=new List<ThemePackageVariant>(); var ids=new HashSet<string>(StringComparer.Ordinal);
            var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var item in ThemePackageJson.Array(ThemePackageJson.Get(f,"themes"),16))
            {
                var t=ThemePackageJson.Object(item,"id","name","path","schemaVersion","length","sha256");
                string id=ThemePackageJson.Id(ThemePackageJson.Get(t,"id"),64);
                string path=ThemePackageJson.Path(ThemePackageJson.Text(ThemePackageJson.Get(t,"path"),160));
                if(!path.StartsWith("Themes/",StringComparison.Ordinal) || !path.EndsWith(".xml",StringComparison.Ordinal) ||
                    path.Split('/').Length!=2 || !ids.Add(id) || !paths.Add(path)) throw ThemePackageJson.Error("InvalidThemeDeclaration");
                themes.Add(new ThemePackageVariant(id,ThemePackageJson.Text(ThemePackageJson.Get(t,"name"),160),path,
                    ThemePackageJson.Integer(ThemePackageJson.Get(t,"schemaVersion"),2,3),
                    ThemePackageJson.Integer(ThemePackageJson.Get(t,"length"),1,1048576),
                    ThemePackageJson.Digest(ThemePackageJson.Get(t,"sha256"))));
            }
            if(themes.Count==0) throw ThemePackageJson.Error("EmptyThemePackage");
            return new ThemePackageManifest(ThemePackageJson.Id(ThemePackageJson.Get(f,"packageId"),128),
                ThemePackageJson.Text(ThemePackageJson.Get(f,"name"),160),ThemePackageJson.Text(ThemePackageJson.Get(f,"author"),160),
                ThemePackageJson.Text(ThemePackageJson.Get(f,"version"),32),ThemePackageJson.Text(ThemePackageJson.Get(f,"phinixRange"),128),themes);
        }
    }

    public sealed class ThemePackage
    {
        public const int MaximumPackageBytes=8*1024*1024;
        public const int MaximumExpandedBytes=8*1024*1024;
        private readonly Dictionary<string,byte[]> files;
        private readonly byte[] archive;
        private ThemePackage(ThemePackageManifest manifest,Dictionary<string,byte[]> files,byte[] archive)
        { Manifest=manifest; this.files=files; this.archive=archive; Sha256=ThemePackageJson.Hash(archive); ManifestSha256=ThemePackageJson.Hash(files["manifest.json"]); }
        public ThemePackageManifest Manifest { get; }
        public string Sha256 { get; }
        public string ManifestSha256 { get; }
        public int Length => archive.Length;
        // Defensive copies keep a verified payload frozen until a future storage transaction takes ownership.
        public byte[] CopyFile(string path) => (byte[])files[path].Clone();
        public byte[] CopyArchive() => (byte[])archive.Clone();
        public static ThemePackage Read(Stream input,CancellationToken token)
        {
            if(input==null || !input.CanRead) throw new ArgumentException("Readable package stream required.");
            byte[] bytes;
            using(var copy=new MemoryStream())
            {
                var buffer=new byte[65536]; int read;
                while((read=input.Read(buffer,0,buffer.Length))>0)
                { token.ThrowIfCancellationRequested(); if(copy.Length+read>MaximumPackageBytes) throw ThemePackageJson.Error("ThemePackageLimit"); copy.Write(buffer,0,read); }
                if(copy.Length==0) throw ThemePackageJson.Error("InvalidThemeArchive");
                bytes=copy.ToArray();
            }
            var files=new Dictionary<string,byte[]>(StringComparer.Ordinal); var aliases=new HashSet<string>(StringComparer.OrdinalIgnoreCase); long total=0;
            try
            {
                using(var memory=new MemoryStream(bytes,false))
                using(var zip=new ZipArchive(memory,ZipArchiveMode.Read,false))
                {
                    if(zip.Entries.Count<2 || zip.Entries.Count>17) throw ThemePackageJson.Error("ThemeEntryLimit");
                    foreach(var entry in zip.Entries)
                    {
                        token.ThrowIfCancellationRequested(); string path=ThemePackageJson.Path(entry.FullName);
                        int unix=(entry.ExternalAttributes>>16)&0xf000;
                        if((unix!=0 && unix!=0x8000) || (entry.ExternalAttributes&0x410)!=0 || !aliases.Add(path)) throw ThemePackageJson.Error("InvalidThemeEntry");
                        if(path!="manifest.json" && !(path.StartsWith("Themes/",StringComparison.Ordinal) && path.EndsWith(".xml",StringComparison.Ordinal) && path.Split('/').Length==2))
                            throw ThemePackageJson.Error("ThemeExtraFile");
                        long limit=path=="manifest.json"?65536:1048576;
                        if(entry.Length<1 || entry.Length>limit || (total+=entry.Length)>MaximumExpandedBytes) throw ThemePackageJson.Error("ThemeExpandedLimit");
                        byte[] content=new byte[(int)entry.Length];
                        using(var source=entry.Open())
                        {
                            int position=0,count;
                            while(position<content.Length && (count=source.Read(content,position,Math.Min(65536,content.Length-position)))>0)
                            { token.ThrowIfCancellationRequested(); position+=count; }
                            if(position!=content.Length || source.ReadByte()!=-1) throw ThemePackageJson.Error("ThemeLengthMismatch");
                        }
                        files.Add(path,content);
                    }
                }
            }
            catch(InvalidDataException ex) { throw new ManagedExtensionValidationException("InvalidThemeArchive",ex); }
            if(!files.TryGetValue("manifest.json",out var manifestBytes)) throw ThemePackageJson.Error("ThemeManifestMissing");
            var manifest=ThemePackageManifest.Read(manifestBytes);
            if(files.Count!=manifest.Themes.Count+1) throw ThemePackageJson.Error("ThemeExtraFile");
            foreach(var theme in manifest.Themes)
            {
                token.ThrowIfCancellationRequested();
                if(!files.TryGetValue(theme.Path,out var content) || content.Length!=theme.Length || ThemePackageJson.Hash(content)!=theme.Sha256)
                    throw ThemePackageJson.Error("ThemeDigestMismatch");
                ThemePackageDocument.Validate(content,theme.SchemaVersion);
            }
            return new ThemePackage(manifest,files,bytes);
        }
    }

    // Only canonical v2/v3 documents enter a managed package. Historical XML remains the Host input adapter's responsibility.
    public static class ThemePackageDocument
    {
        public static int Validate(byte[] bytes,int expectedSchema=0)
        {
            if(bytes==null || bytes.Length<1 || bytes.Length>1048576) throw ThemePackageJson.Error("ThemeDocumentLimit");
            try
            {
                using(var input=new MemoryStream(bytes,false))
                using(var reader=XmlReader.Create(input,new XmlReaderSettings { DtdProcessing=DtdProcessing.Prohibit,XmlResolver=null,MaxCharactersInDocument=1048576 }))
                {
                    var document=XDocument.Load(reader); var root=document.Root;
                    if(document.DescendantNodes().Any(n=>n is XProcessingInstruction)) throw ThemePackageJson.Error("InvalidThemeXml");
                    if(root==null || root.Name!="PhinixTheme" || root.Attributes().Any(a=>a.Name!="version" && a.Name!="name" && a.Name!="author")) throw ThemePackageJson.Error("InvalidThemeRoot");
                    int schema; if(!int.TryParse((string)root.Attribute("version"),out schema) || schema<2 || schema>3 || (expectedSchema!=0 && schema!=expectedSchema)) throw ThemePackageJson.Error("ThemeSchemaMismatch");
                    if(root.Attribute("version").Value!=schema.ToString(CultureInfo.InvariantCulture)) throw ThemePackageJson.Error("ThemeSchemaMismatch");
                    foreach(var attr in root.Attributes().Where(a=>a.Name!="version"))
                        if(attr.Value.Length>160 || attr.Value.Any(char.IsControl)) throw ThemePackageJson.Error("InvalidThemeMetadata");
                    var keys=new HashSet<string>(StringComparer.Ordinal);
                    foreach(var node in root.Elements())
                    {
                        string key=(string)node.Attribute("key");
                        if(string.IsNullOrWhiteSpace(key) || key.Length>160 || key.Trim()!=key || key.Any(char.IsControl) || !keys.Add(key) || keys.Count>512)
                            throw ThemePackageJson.Error("InvalidThemeKey");
                        if(key.StartsWith("nameColor.",StringComparison.Ordinal) || new[]{"primaryText","secondaryText","background","surface","separator","hoverHighlight","pending","error","success","warning"}.Contains(key))
                            throw ThemePackageJson.Error("NonCanonicalThemeKey");
                        string[] attributes;
                        if(node.Name=="color") attributes=new[]{"key","r","g","b","a"};
                        else if(node.Name=="param") attributes=new[]{"key","value"};
                        else if(node.Name=="inherit" && schema==3) attributes=new[]{"key"};
                        else throw ThemePackageJson.Error("InvalidThemeElement");
                        if(node.HasElements || node.Nodes().Any(n=>n is XText text && !string.IsNullOrWhiteSpace(text.Value)) || node.Attributes().Count()!=attributes.Length || node.Attributes().Any(a=>!attributes.Contains(a.Name.ToString())))
                            throw ThemePackageJson.Error("InvalidThemeElement");
                        foreach(string name in attributes.Where(a=>a!="key"))
                        {
                            float number;
                            if(!float.TryParse((string)node.Attribute(name),NumberStyles.Float,CultureInfo.InvariantCulture,out number) || float.IsNaN(number) || float.IsInfinity(number) ||
                                (node.Name=="color" && (number<0 || number>1))) throw ThemePackageJson.Error("InvalidThemeNumber");
                        }
                    }
                    if(root.Nodes().Any(n=>n is XText text && !string.IsNullOrWhiteSpace(text.Value))) throw ThemePackageJson.Error("InvalidThemeElement");
                    return schema;
                }
            }
            catch(XmlException ex) { throw new ManagedExtensionValidationException("InvalidThemeXml",ex); }
        }
    }

    public static class ThemePackageJson
    {
        public static XElement Read(byte[] bytes,int maximum)
        {
            if(bytes==null || bytes.Length<1 || bytes.Length>maximum) throw Error("ThemeManifestLimit");
            try
            {
                new UTF8Encoding(false,true).GetString(bytes);
                Boundary(bytes);
                using(var reader=JsonReaderWriterFactory.CreateJsonReader(bytes,0,bytes.Length,new UTF8Encoding(false,true),new XmlDictionaryReaderQuotas
                    { MaxDepth=12,MaxArrayLength=64,MaxStringContentLength=maximum,MaxBytesPerRead=4096,MaxNameTableCharCount=16384 },null))
                {
                    var root=XElement.Load(reader);
                    if(reader.MoveToContent()!=XmlNodeType.None || root.DescendantsAndSelf().Any(n=>n.Name.NamespaceName.Length!=0 || n.Attributes().Any(a=>a.Name!="type"))) throw Error("InvalidThemeJson");
                    return root;
                }
            }
            catch(XmlException ex) { throw new ManagedExtensionValidationException("InvalidThemeJson",ex); }
            catch(DecoderFallbackException ex) { throw new ManagedExtensionValidationException("InvalidThemeJson",ex); }
        }
        private static bool Space(byte c) => c==' ' || c=='\t' || c=='\r' || c=='\n';
        private static void Boundary(byte[] bytes)
        {
            int start=0; while(start<bytes.Length && Space(bytes[start])) start++;
            if(start==bytes.Length || bytes[start]!='{') throw Error("InvalidThemeJson");
            var stack=new Stack<byte>(); bool quoted=false,escaped=false;
            for(int i=start;i<bytes.Length;i++)
            {
                byte c=bytes[i];
                if(quoted)
                {
                    if(escaped) { escaped=false; continue; }
                    if(c=='\\') escaped=true;
                    else if(c=='"') quoted=false;
                    else if(c<32) throw Error("InvalidThemeJson");
                    continue;
                }
                if(c=='"') quoted=true;
                else if(c=='{' || c=='[') { stack.Push(c); if(stack.Count>12) throw Error("InvalidThemeJson"); }
                else if(c=='}' || c==']')
                {
                    int previous=i-1; while(previous>=start && Space(bytes[previous])) previous--;
                    if(bytes[previous]==',' || stack.Count==0 || stack.Pop()!=(c=='}'?'{':'[')) throw Error("InvalidThemeJson");
                    if(stack.Count!=0) continue;
                    for(int j=i+1;j<bytes.Length;j++) if(!Space(bytes[j])) throw Error("InvalidThemeJson");
                    return;
                }
                else if(c=='/' || c=='\'') throw Error("InvalidThemeJson");
            }
            throw Error("InvalidThemeJson");
        }
        public static Dictionary<string,XElement> Object(XElement node,params string[] allowed)
        {
            Type(node,"object"); var fields=new Dictionary<string,XElement>(StringComparer.Ordinal);
            foreach(var field in node.Elements())
            { if(!allowed.Contains(field.Name.ToString()) || fields.ContainsKey(field.Name.ToString())) throw Error("InvalidThemeField"); fields.Add(field.Name.ToString(),field); }
            return fields;
        }
        public static XElement Get(Dictionary<string,XElement> fields,string name)
        { if(!fields.TryGetValue(name,out var value)) throw Error("MissingThemeField"); return value; }
        public static List<XElement> Array(XElement node,int maximum)
        { Type(node,"array"); var result=node.Elements().ToList(); if(result.Count>maximum || result.Any(n=>n.Name!="item")) throw Error("ThemeCollectionLimit"); return result; }
        private static void Type(XElement node,string type)
        { if((string)node.Attribute("type")!=type) throw Error("InvalidThemeType"); }
        public static string Text(XElement node,int maximum)
        { Type(node,"string"); string text=node.Value; if(text.Length<1 || text.Length>maximum || text.Trim()!=text || text.Any(char.IsControl)) throw Error("InvalidThemeText"); return text; }
        public static string Id(XElement node,int maximum)
        { string text=Text(node,maximum); if(!Regex.IsMatch(text,@"\A[a-z0-9]+(?:[._-][a-z0-9]+)*\z",RegexOptions.CultureInvariant)) throw Error("InvalidThemeId"); return text; }
        public static int Integer(XElement node,int minimum,int maximum)
        { Type(node,"number"); int value; if(!int.TryParse(node.Value,NumberStyles.None,CultureInfo.InvariantCulture,out value) || value<minimum || value>maximum || value.ToString(CultureInfo.InvariantCulture)!=node.Value) throw Error("InvalidThemeInteger"); return value; }
        public static string Digest(XElement node)
        { string text=Text(node,64); if(!Regex.IsMatch(text,@"\A[a-f0-9]{64}\z",RegexOptions.CultureInvariant)) throw Error("InvalidThemeDigest"); return text; }
        public static string Path(string path)
        {
            if(string.IsNullOrEmpty(path) || path.Length>160 || path.Contains("\\")) throw Error("InvalidThemePath");
            foreach(string part in path.Split('/'))
                if(!Regex.IsMatch(part,@"\A[A-Za-z0-9][A-Za-z0-9._-]*\z",RegexOptions.CultureInvariant) || part.Contains("..") || part.EndsWith(".",StringComparison.Ordinal) ||
                    Regex.IsMatch(part,@"\A(CON|PRN|AUX|NUL|COM[0-9]|LPT[0-9])(?:\.|\z)",RegexOptions.IgnoreCase|RegexOptions.CultureInvariant)) throw Error("InvalidThemePath");
            return path;
        }
        public static string Hash(byte[] bytes)
        { using(var algorithm=SHA256.Create()) return BitConverter.ToString(algorithm.ComputeHash(bytes)).Replace("-","").ToLowerInvariant(); }
        public static ManagedExtensionValidationException Error(string code) => new ManagedExtensionValidationException(code);
    }
}
