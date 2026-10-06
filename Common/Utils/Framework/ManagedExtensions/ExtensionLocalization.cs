using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Xml.Linq;

namespace Utils.Framework.ManagedExtensions
{
    public static class ExtensionLocale
    {
        public static string Normalize(string value)
        {
            if(value==null || value.Length>32 || !Regex.IsMatch(value,@"\A[A-Za-z]{2,3}(?:-[A-Za-z]{4})?(?:-(?:[A-Za-z]{2}|[0-9]{3}))?\z",RegexOptions.CultureInvariant))
                throw ManagedExtensionJson.Error("InvalidLocale");
            var parts=value.Split('-'); parts[0]=parts[0].ToLowerInvariant();
            for(int i=1;i<parts.Length;i++) parts[i]=parts[i].Length==4?char.ToUpperInvariant(parts[i][0])+parts[i].Substring(1).ToLowerInvariant():parts[i].ToUpperInvariant();
            return string.Join("-",parts);
        }
        private static string Script(string locale)
        {
            var parts=locale.Split('-');
            if(parts.Length>1 && parts[1].Length==4) return parts[1];
            if(parts[0]=="zh")
            {
                if(parts.Contains("CN") || parts.Contains("SG")) return "Hans";
                if(parts.Contains("TW") || parts.Contains("HK") || parts.Contains("MO")) return "Hant";
            }
            return null;
        }
        public static IReadOnlyList<string> Preference(IEnumerable<string> supplied,string requested,string fallback)
        {
            var locales=supplied.OrderBy(s=>s,StringComparer.Ordinal).ToArray(); var result=new List<string>();
            string locale=null; try { locale=Normalize(requested); } catch(ManagedExtensionValidationException) { }
            if(locale!=null)
            {
                if(locales.Contains(locale)) result.Add(locale);
                string language=locale.Split('-')[0],script=Script(locale);
                result.AddRange(locales.Where(s=>s.Split('-')[0]==language && (script==null || Script(s)==null || Script(s)==script))
                    .OrderBy(s=>script!=null && Script(s)==script?0:s==language?1:2).ThenBy(s=>s,StringComparer.Ordinal));
            }
            result.AddRange(locales.Where(s=>s=="en" || s.StartsWith("en-",StringComparison.Ordinal))
                .OrderBy(s=>s=="en"?0:s=="en-US"?1:2).ThenBy(s=>s,StringComparer.Ordinal));
            if(fallback!=null) result.Add(fallback);
            result.AddRange(locales);
            return result.Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    public sealed class ExtensionLocalizationDeclaration
    {
        public const int MaxLocales=16,MaxFileBytes=128*1024,MaxTotalBytes=1024*1024;
        private ExtensionLocalizationDeclaration(string locale,IEnumerable<ManagedExtensionFile> files)
        { DefaultLocale=locale; Files=ManagedExtensionCompatibility.Freeze(files); }
        public string DefaultLocale { get; }
        public ReadOnlyCollection<ManagedExtensionFile> Files { get; }
        internal static ExtensionLocalizationDeclaration Read(XElement node,IEnumerable<ManagedExtensionFile> resources)
        {
            var fields=ManagedExtensionJson.Object(node,"defaultLocale","files");
            string fallback=fields.ContainsKey("defaultLocale")?ExtensionLocale.Normalize(ManagedExtensionJson.Text(fields["defaultLocale"],32)):null;
            var files=new List<ManagedExtensionFile>(); var locales=new HashSet<string>(StringComparer.Ordinal); long size=0;
            foreach(var item in ManagedExtensionJson.Array(ManagedExtensionJson.Required(fields,"files"),MaxLocales))
            {
                string path=ManagedExtensionJson.Path(ManagedExtensionJson.Text(item,240));
                var resource=resources.SingleOrDefault(r=>r.Path==path);
                if(resource==null || !path.EndsWith(".json",StringComparison.Ordinal) || resource.Length<1 || resource.Length>MaxFileBytes)
                    throw ManagedExtensionJson.Error("InvalidLocalizationResource");
                string locale=ExtensionLocale.Normalize(Path.GetFileNameWithoutExtension(path));
                if(Path.GetFileNameWithoutExtension(path)!=locale) throw ManagedExtensionJson.Error("NonCanonicalLocalePath");
                if(!locales.Add(locale)) throw ManagedExtensionJson.Error("DuplicateLocale");
                size+=resource.Length; if(size>MaxTotalBytes) throw ManagedExtensionJson.Error("LocalizationLimit");
                files.Add(resource);
            }
            if(files.Count==0) throw ManagedExtensionJson.Error("MissingLanguageFile");
            if(fallback!=null && !locales.Contains(fallback)) throw ManagedExtensionJson.Error("InvalidDefaultLocale");
            return new ExtensionLocalizationDeclaration(fallback,files);
        }
    }

    public sealed class ExtensionLanguageFile
    {
        private ExtensionLanguageFile(string locale,Dictionary<string,string> display,Dictionary<string,string> strings)
        { Locale=locale; Display=new ReadOnlyDictionary<string,string>(display); Strings=new ReadOnlyDictionary<string,string>(strings); }
        public string Locale { get; }
        public IReadOnlyDictionary<string,string> Display { get; }
        public IReadOnlyDictionary<string,string> Strings { get; }
        public static ExtensionLanguageFile Read(byte[] bytes)
        {
            var fields=ManagedExtensionJson.Object(ManagedExtensionJson.Read(bytes,ExtensionLocalizationDeclaration.MaxFileBytes),"schemaVersion","locale","display","strings");
            if(ManagedExtensionJson.Integer(ManagedExtensionJson.Required(fields,"schemaVersion"),1,int.MaxValue)!=1) throw ManagedExtensionJson.Error("UnsupportedLanguageSchema");
            string locale=ExtensionLocale.Normalize(ManagedExtensionJson.Text(ManagedExtensionJson.Required(fields,"locale"),32));
            var display=ReadDisplay(ManagedExtensionJson.Required(fields,"display"));
            XElement map=ManagedExtensionJson.Required(fields,"strings"); ManagedExtensionJson.Type(map,"object");
            var strings=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var pair in map.Elements())
            {
                string key=pair.Name.LocalName;
                if(!ValidKey(key)) throw ManagedExtensionJson.Error("InvalidLocalizationKey");
                if(strings.Count>=2048) throw ManagedExtensionJson.Error("LocalizationLimit");
                if(strings.ContainsKey(key)) throw ManagedExtensionJson.Error("DuplicateField");
                string value=Text(pair,8192,true); Parameters(value); strings.Add(key,value);
            }
            return new ExtensionLanguageFile(locale,display,strings);
        }
        public static bool ValidKey(string key)
        { return key!=null && Regex.IsMatch(key,@"\A[A-Za-z][A-Za-z0-9_.-]{0,127}\z",RegexOptions.CultureInvariant); }
        internal static Dictionary<string,string> ReadDisplay(XElement node)
        {
            var fields=ManagedExtensionJson.Object(node,"name","summary","changelog");
            var result=new Dictionary<string,string>(StringComparer.Ordinal);
            foreach(var pair in fields) result.Add(pair.Key,Text(pair.Value,pair.Key=="name"?160:pair.Key=="summary"?1024:8192,pair.Key!="name"));
            return result;
        }
        private static string Text(XElement node,int maximum,bool multiline)
        {
            ManagedExtensionJson.Type(node,"string"); string value=node.Value;
            if(value.Length==0 || value.Length>maximum || value.Trim()!=value || value.Any(c=>char.IsControl(c) && !(multiline && (c=='\r' || c=='\n' || c=='\t'))))
                throw ManagedExtensionJson.Error("InvalidLocalizationText");
            for(int i=0;i<value.Length;i++) if(char.IsSurrogate(value[i]))
            { if(!char.IsHighSurrogate(value[i]) || i+1>=value.Length || !char.IsLowSurrogate(value[++i])) throw ManagedExtensionJson.Error("InvalidLocalizationText"); }
            if(Regex.IsMatch(value,@"</?[A-Za-z][^>]*>",RegexOptions.CultureInvariant)) throw ManagedExtensionJson.Error("InvalidLocalizationMarkup");
            return value;
        }
        // Bounded numbered placeholders; intentionally no alignment or format-specifier grammar.
        public static IReadOnlyList<int> Parameters(string value)
        {
            var indexes=new SortedSet<int>();
            for(int i=0;i<value.Length;i++)
            {
                char c=value[i]; if(c!='{' && c!='}') continue;
                if(i+1<value.Length && value[i+1]==c) { i++; continue; }
                if(c=='}') throw ManagedExtensionJson.Error("InvalidLocalizationFormat");
                int start=++i; while(i<value.Length && value[i]>='0' && value[i]<='9') i++;
                int index;
                if(i>=value.Length || value[i]!='}' || !int.TryParse(value.Substring(start,i-start),NumberStyles.None,CultureInfo.InvariantCulture,out index) || index>31 || index.ToString(CultureInfo.InvariantCulture)!=value.Substring(start,i-start))
                    throw ManagedExtensionJson.Error("InvalidLocalizationFormat");
                indexes.Add(index);
            }
            return indexes.ToArray();
        }
    }

    /// <summary>Bounded display-only projection; IDs and canonical manifests are never translated.</summary>
    public sealed class ExtensionDisplayLocalization
    {
        private readonly ReadOnlyDictionary<string,IReadOnlyDictionary<string,string>> translations;
        private ExtensionDisplayLocalization(Dictionary<string,IReadOnlyDictionary<string,string>> values,string fallback)
        { translations=new ReadOnlyDictionary<string,IReadOnlyDictionary<string,string>>(values); DefaultLocale=fallback; }
        public string DefaultLocale { get; }
        public IReadOnlyDictionary<string,IReadOnlyDictionary<string,string>> Translations => translations;
        public static ExtensionDisplayLocalization Read(XElement node)
        {
            var fields=ManagedExtensionJson.Object(node,"defaultLocale","translations");
            string fallback=fields.ContainsKey("defaultLocale")?ExtensionLocale.Normalize(ManagedExtensionJson.Text(fields["defaultLocale"],32)):null;
            XElement map=ManagedExtensionJson.Required(fields,"translations"); ManagedExtensionJson.Type(map,"object");
            var values=new Dictionary<string,IReadOnlyDictionary<string,string>>(StringComparer.Ordinal); int total=0;
            foreach(var item in map.Elements())
            {
                string locale=ExtensionLocale.Normalize(item.Name.LocalName);
                if(values.ContainsKey(locale)) throw ManagedExtensionJson.Error("DuplicateLocale");
                if(values.Count>=ExtensionLocalizationDeclaration.MaxLocales) throw ManagedExtensionJson.Error("LocalizationLimit");
                var display=ExtensionLanguageFile.ReadDisplay(item);
                total+=display.Values.Sum(s=>s.Length); if(total>32768) throw ManagedExtensionJson.Error("LocalizationLimit");
                values.Add(locale,new ReadOnlyDictionary<string,string>(display));
            }
            if(fallback!=null && !values.ContainsKey(fallback)) throw ManagedExtensionJson.Error("InvalidDefaultLocale");
            if(!values.Values.Any(v=>v.ContainsKey("name")) || !values.Values.Any(v=>v.ContainsKey("summary"))) throw ManagedExtensionJson.Error("MissingDisplayText");
            return new ExtensionDisplayLocalization(values,fallback);
        }
        public string Resolve(string key,string requested)
        {
            foreach(string locale in ExtensionLocale.Preference(translations.Keys,requested,DefaultLocale))
            { string value; if(translations[locale].TryGetValue(key,out value)) return value; }
            return null;
        }
        public void VerifyProjection(ExtensionLocalizationCatalog languages)
        {
            var files=languages.Languages.ToDictionary(l=>l.Locale,StringComparer.Ordinal);
            if(DefaultLocale!=languages.DefaultLocale || files.Count!=translations.Count) throw ManagedExtensionJson.Error("CatalogLocalizationMismatch");
            foreach(var pair in translations)
            {
                ExtensionLanguageFile file;
                if(!files.TryGetValue(pair.Key,out file) || pair.Value.Count!=file.Display.Count || pair.Value.Any(v=>!file.Display.ContainsKey(v.Key) || file.Display[v.Key]!=v.Value))
                    throw ManagedExtensionJson.Error("CatalogLocalizationMismatch");
            }
        }
    }

    public sealed partial class ExtensionLocalizationCatalog
    {
        private readonly ReadOnlyDictionary<string,ExtensionLanguageFile> languages;
        private ExtensionLocalizationCatalog(Dictionary<string,ExtensionLanguageFile> languages,string locale)
        { this.languages=new ReadOnlyDictionary<string,ExtensionLanguageFile>(languages); DefaultLocale=locale; }
        public static ExtensionLocalizationCatalog Empty { get; }=new ExtensionLocalizationCatalog(new Dictionary<string,ExtensionLanguageFile>(),null);
        public string DefaultLocale { get; }
        public IEnumerable<ExtensionLanguageFile> Languages => languages.Values;
        public static ExtensionLocalizationCatalog Load(ExtensionLocalizationDeclaration declaration,Func<ManagedExtensionFile,byte[]> read,CancellationToken token)
        {
            if(declaration==null) return Empty;
            var languages=new Dictionary<string,ExtensionLanguageFile>(StringComparer.Ordinal); var parameters=new Dictionary<string,string>(); int displaySize=0;
            foreach(var file in declaration.Files)
            {
                try
                {
                    token.ThrowIfCancellationRequested(); byte[] bytes=read(file);
                    if(bytes==null || bytes.Length!=file.Length) throw ManagedExtensionJson.Error("LocalizationLengthMismatch");
                    if(ManagedExtensionDigest.Hash(bytes)!=file.Sha256) throw ManagedExtensionJson.Error("LocalizationDigestMismatch");
                    var language=ExtensionLanguageFile.Read(bytes);
                    if(language.Locale!=Path.GetFileNameWithoutExtension(file.Path)) throw ManagedExtensionJson.Error("LocalizationLocaleMismatch");
                    if(languages.ContainsKey(language.Locale)) throw ManagedExtensionJson.Error("DuplicateLocale");
                    languages.Add(language.Locale,language);
                    displaySize+=language.Display.Values.Sum(s=>s.Length); if(displaySize>32768) throw ManagedExtensionJson.Error("LocalizationLimit");
                    foreach(var pair in language.Strings)
                    {
                        string signature=string.Join(",",ExtensionLanguageFile.Parameters(pair.Value)); string old;
                        if(parameters.TryGetValue(pair.Key,out old) && signature!=old) throw ManagedExtensionJson.Error("LocalizationParametersMismatch");
                        parameters[pair.Key]=signature;
                        if(parameters.Count>2048) throw ManagedExtensionJson.Error("LocalizationLimit");
                    }
                }
                catch(ManagedExtensionValidationException error) { error.ResourcePath=error.ResourcePath??file.Path; throw; }
                catch(IOException error) { throw new ManagedExtensionValidationException("LocalizationReadFailed",error) {ResourcePath=file.Path}; }
                catch(UnauthorizedAccessException error) { throw new ManagedExtensionValidationException("LocalizationReadFailed",error) {ResourcePath=file.Path}; }
            }
            if(!languages.Values.Any(l=>l.Display.ContainsKey("name")) || !languages.Values.Any(l=>l.Display.ContainsKey("summary"))) throw ManagedExtensionJson.Error("MissingDisplayText");
            token.ThrowIfCancellationRequested(); return new ExtensionLocalizationCatalog(languages,declaration.DefaultLocale);
        }
        public string Resolve(string key,string requested,bool display=false)
        {
            foreach(string locale in ExtensionLocale.Preference(languages.Keys,requested,DefaultLocale))
            { string value; if((display?languages[locale].Display:languages[locale].Strings).TryGetValue(key,out value)) return value; }
            return null;
        }
    }
}
