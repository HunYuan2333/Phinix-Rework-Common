using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Utils.Framework.ManagedExtensions
{
    public static class ManagedExtensionManifestReader
    {
        public const int MaxManifestBytes = 512 * 1024;
        public const long MaxFileBytes = 64 * 1024 * 1024;
        public const long MaxExpandedBytes = 256 * 1024 * 1024;
        private static readonly HashSet<string> protectedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Assembly-CSharp", "mscorlib", "netstandard", "System", "UnityEngine", "0Harmony", "LiteNetLib", "Google.Protobuf", "Protobuf",
            "Utils", "Connections", "Connections.Client", "Authentication", "Authentication.Client", "UserManagement", "UserManagement.Client",
            "ClientExtensionAbstractions", "ClientPresentation.Contracts", "PhinixClient",
            "Phinix.ClientComposition", "Autofac", "Microsoft.Bcl.AsyncInterfaces", "Stateless"
        };

        public static ManagedExtensionManifest Read(byte[] bytes)
        {
            var f = ManagedExtensionJson.Object(ManagedExtensionJson.Read(bytes, MaxManifestBytes),
                "schemaVersion", "management", "packageId", "name", "version", "targetFramework", "compatibility",
                "dependencies", "modules", "assemblies", "resources", "externalMods", "localization", "variants");
            long schema=ManagedExtensionJson.Integer(Get(f, "schemaVersion"), 1, int.MaxValue);
            if(schema==ManagedExtensionManifest.VariantSchemaVersion) return ReadVariants(f);
            if (schema != ManagedExtensionManifest.SchemaVersion)
                throw ManagedExtensionJson.Error("UnsupportedSchema");
            if(f.ContainsKey("variants")) throw ManagedExtensionJson.Error("InvalidVariants");
            if (ManagedExtensionJson.Text(Get(f, "management"), 32) != ManagedExtensionManifest.Management) throw ManagedExtensionJson.Error("UnsupportedManagement");
            string targetFramework = ManagedExtensionJson.Text(Get(f, "targetFramework"), 32);
            ManagedExtensionTargetFramework.MetadataName(targetFramework);
            string id = ManagedExtensionJson.Id(Get(f, "packageId"));
            string name = ManagedExtensionJson.Text(Get(f, "name"), 160);
            var version = ManagedExtensionVersion.Parse(ManagedExtensionJson.Text(Get(f, "version"), 32));
            var c = ManagedExtensionJson.Object(Get(f, "compatibility"), "rimWorldVersions", "phinixRange", "abstractionsRange");
            var versions = Strings(Get(c, "rimWorldVersions"), 16, false);
            if (versions.Count == 0 || versions.Any(v => !Regex.IsMatch(v, @"\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z", RegexOptions.CultureInvariant)))
                throw ManagedExtensionJson.Error("InvalidCompatibility");
            var compatibility = new ManagedExtensionCompatibility(versions,
                ManagedExtensionVersionRange.Parse(ManagedExtensionJson.Text(Get(c, "phinixRange"), 80)),
                ManagedExtensionVersionRange.Parse(ManagedExtensionJson.Text(Get(c, "abstractionsRange"), 80)));

            var dependencies = new List<ManagedExtensionDependency>();
            var depIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in ManagedExtensionJson.Array(Get(f, "dependencies"), 64))
            {
                var d = ManagedExtensionJson.Object(item, "packageId", "versionRange", "optional");
                string dep = ManagedExtensionJson.Id(Get(d, "packageId"));
                if (dep == id || !depIds.Add(dep)) throw ManagedExtensionJson.Error("InvalidDependency");
                dependencies.Add(new ManagedExtensionDependency(dep,
                    ManagedExtensionVersionRange.Parse(ManagedExtensionJson.Text(Get(d, "versionRange"), 80)),
                    ManagedExtensionJson.Boolean(Get(d, "optional"))));
            }

            var assemblies = new List<ManagedExtensionAssembly>();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var occupiedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long expanded = 0;
            foreach (var item in ManagedExtensionJson.Array(Get(f, "assemblies"), 64))
            {
                var a = ManagedExtensionJson.Object(item, "name", "version", "culture", "publicKeyToken", "path", "length", "sha256");
                string asmName = AssemblyName(Get(a, "name"));
                if (IsProtectedAssembly(asmName)) throw ManagedExtensionJson.Error("ProtectedAssembly");
                string asmVersion = ManagedExtensionJson.Text(Get(a, "version"), 40);
                Version parsed;
                if (!Version.TryParse(asmVersion, out parsed) || parsed.Build < 0 || parsed.Revision < 0 || parsed.ToString() != asmVersion)
                    throw ManagedExtensionJson.Error("InvalidAssembly");
                string culture = ManagedExtensionJson.Text(Get(a, "culture"), 64);
                // First format supports neutral code assemblies, not satellite DLLs.
                if (culture != "neutral") throw ManagedExtensionJson.Error("InvalidAssembly");
                string token = ManagedExtensionJson.Text(Get(a, "publicKeyToken"), 16);
                if (token != "null") ManagedExtensionJson.Hex(token, 16);
                ManagedExtensionFile file = File(a);
                if (file.Length == 0) throw ManagedExtensionJson.Error("InvalidAssembly");
                if (!file.Path.StartsWith("Assemblies/", StringComparison.Ordinal) || file.Path.Split('/').Length != 2 ||
                    !file.Path.EndsWith(".dll", StringComparison.Ordinal)) throw ManagedExtensionJson.Error("InvalidAssemblyPath");
                // Filename aliases must not conceal a protected assembly during CLR probing.
                if (IsProtectedAssembly(System.IO.Path.GetFileNameWithoutExtension(file.Path))) throw ManagedExtensionJson.Error("ProtectedAssembly");
                if (!names.Add(asmName) || !paths.Add(file.Path)) throw ManagedExtensionJson.Error("DuplicateAssembly");
                expanded = checked(expanded + file.Length);
                var declaration = new ManagedExtensionAssembly(asmName, asmVersion, culture, token, file);
                if (AssemblyNames(declaration).Any(occupiedNames.Contains)) throw ManagedExtensionJson.Error("DuplicateAssembly");
                occupiedNames.UnionWith(AssemblyNames(declaration));
                assemblies.Add(declaration);
            }
            if (assemblies.Count == 0) throw ManagedExtensionJson.Error("MissingAssembly");

            var modules = new List<ManagedExtensionModule>();
            var moduleIds = new HashSet<string>(StringComparer.Ordinal);
            var entries = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in ManagedExtensionJson.Array(Get(f, "modules"), 64))
            {
                var m = ManagedExtensionJson.Object(item, "id", "assemblyName", "entryType", "dependsOn");
                string moduleId = ManagedExtensionJson.Id(Get(m, "id"));
                string owner = AssemblyName(Get(m, "assemblyName"));
                string entry = ManagedExtensionJson.Text(Get(m, "entryType"), 256);
                if (!Regex.IsMatch(entry, @"\A[A-Za-z_][A-Za-z0-9_]*(?:[.+][A-Za-z_][A-Za-z0-9_]*)*\z", RegexOptions.CultureInvariant) ||
                    !assemblies.Any(a => a.Name == owner)) throw ManagedExtensionJson.Error("InvalidModuleEntry");
                var dependsOn = Strings(Get(m, "dependsOn"), 64, true);
                if (dependsOn.Contains(moduleId) || !moduleIds.Add(moduleId) || !entries.Add(owner + "\n" + entry))
                    throw ManagedExtensionJson.Error("InvalidModule");
                modules.Add(new ManagedExtensionModule(moduleId, owner, entry, dependsOn));
            }
            if (modules.Count == 0) throw ManagedExtensionJson.Error("MissingModule");

            var resources = new List<ManagedExtensionFile>();
            foreach (var item in ManagedExtensionJson.Array(Get(f, "resources"), 512))
            {
                var r = ManagedExtensionJson.Object(item, "path", "length", "sha256");
                var file = File(r);
                if (!file.Path.StartsWith("Resources/", StringComparison.Ordinal) ||
                    !Regex.IsMatch(file.Path, @"\.(?:txt|md|json|xml|png|jpg|jpeg|dds|wav|ogg)\z", RegexOptions.CultureInvariant))
                    throw ManagedExtensionJson.Error("InvalidResourcePath");
                if (!paths.Add(file.Path)) throw ManagedExtensionJson.Error("DuplicateFile");
                expanded = checked(expanded + file.Length);
                resources.Add(file);
            }
            if (expanded > MaxExpandedBytes - bytes.Length) throw ManagedExtensionJson.Error("ExpandedLimit");
            ValidatePathTree(paths);

            var externalMods = new List<ManagedExtensionExternalMod>();
            var modIds = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in ManagedExtensionJson.Array(Get(f, "externalMods"), 64))
            {
                var m = ManagedExtensionJson.Object(item, "packageId", "workshopId");
                string modId = ManagedExtensionJson.Id(Get(m, "packageId"));
                if (!modIds.Add(modId)) throw ManagedExtensionJson.Error("DuplicateExternalMod");
                string workshopId = null;
                if (m.ContainsKey("workshopId"))
                {
                    workshopId = ManagedExtensionJson.Text(m["workshopId"], 20);
                    ulong number;
                    if (!ulong.TryParse(workshopId, NumberStyles.None, CultureInfo.InvariantCulture, out number) || number == 0 ||
                        number.ToString(CultureInfo.InvariantCulture) != workshopId) throw ManagedExtensionJson.Error("InvalidWorkshopId");
                }
                externalMods.Add(new ManagedExtensionExternalMod(modId, workshopId));
            }
            var localization = f.ContainsKey("localization") ? ExtensionLocalizationDeclaration.Read(f["localization"], resources) : null;
            return new ManagedExtensionManifest(id, name, version, compatibility, dependencies, modules, assemblies, resources, externalMods, localization, targetFramework);
        }

        // The envelope carries complete declarations. No ambient game/CLR state
        // influences parsing, and every payload remains owned by the one receipt.
        private static ManagedExtensionManifest ReadVariants(Dictionary<string,XElement> fields)
        {
            if(fields.Count!=2 || !fields.ContainsKey("variants")) throw ManagedExtensionJson.Error("InvalidVariants");
            var variants=new List<ManagedExtensionManifest>();
            var games=new HashSet<string>(StringComparer.Ordinal);
            ManagedExtensionManifest first=null;
            foreach(var node in ManagedExtensionJson.Array(fields["variants"],2))
            {
                var clone=new XElement(node); clone.Name="root";
                byte[] bytes;
                using(var stream=new System.IO.MemoryStream())
                {
                    using(var writer=System.Runtime.Serialization.Json.JsonReaderWriterFactory.CreateJsonWriter(stream,System.Text.Encoding.UTF8,false)) clone.WriteTo(writer);
                    bytes=stream.ToArray();
                }
                var declaration=Read(bytes);
                if(declaration.Variants.Count!=0 || declaration.Compatibility.RimWorldVersions.Count!=1) throw ManagedExtensionJson.Error("InvalidVariants");
                string game=declaration.Compatibility.RimWorldVersions[0];
                if(!games.Add(game) || !ManagedExtensionTargetFramework.SupportsGame(declaration.TargetFramework,game)) throw ManagedExtensionJson.Error("InvalidVariantProfile");
                if(first==null)first=declaration;
                else if(!SameVariantContract(first,declaration))throw ManagedExtensionJson.Error("VariantContractMismatch");
                Func<ManagedExtensionFile,ManagedExtensionFile> prefix=file=>new ManagedExtensionFile(game+"/"+file.Path,file.Length,file.Sha256);
                variants.Add(new ManagedExtensionManifest(declaration.PackageId,declaration.Name,declaration.Version,declaration.Compatibility,
                    declaration.Dependencies,declaration.Modules,declaration.Assemblies.Select(a=>new ManagedExtensionAssembly(a.Name,a.Version,a.Culture,a.PublicKeyToken,prefix(a.File))),
                    declaration.Resources.Select(prefix),declaration.ExternalMods,declaration.Localization==null?null:new ExtensionLocalizationDeclaration(declaration.Localization.DefaultLocale,declaration.Localization.Files.Select(prefix)),declaration.TargetFramework));
            }
            if(first==null)throw ManagedExtensionJson.Error("InvalidVariants");
            var compatibility=new ManagedExtensionCompatibility(games.OrderBy(g=>g,StringComparer.Ordinal),first.Compatibility.PhinixRange,first.Compatibility.AbstractionsRange);
            var primary=variants[0];
            var result=new ManagedExtensionManifest(first.PackageId,first.Name,first.Version,compatibility,first.Dependencies,first.Modules,
                primary.Assemblies,primary.Resources,first.ExternalMods,primary.Localization,primary.TargetFramework,variants);
            ValidatePathTree(result.AllFiles.Select(f=>f.Path));
            if(result.AllFiles.Sum(f=>f.Length)>MaxExpandedBytes)throw ManagedExtensionJson.Error("PayloadLimit");
            return result;
        }
        private static bool SameVariantContract(ManagedExtensionManifest a,ManagedExtensionManifest b)
        {
            return a.PackageId==b.PackageId && a.Name==b.Name && a.Version.ToString()==b.Version.ToString() &&
                a.Compatibility.PhinixRange.Text==b.Compatibility.PhinixRange.Text && a.Compatibility.AbstractionsRange.Text==b.Compatibility.AbstractionsRange.Text &&
                a.Assemblies.Select(x=>x.FullName).OrderBy(x=>x,StringComparer.Ordinal).SequenceEqual(b.Assemblies.Select(x=>x.FullName).OrderBy(x=>x,StringComparer.Ordinal)) &&
                a.Modules.Select(m=>m.Id+"|"+m.AssemblyName+"|"+m.EntryType+"|"+string.Join(",",m.DependsOn)).SequenceEqual(b.Modules.Select(m=>m.Id+"|"+m.AssemblyName+"|"+m.EntryType+"|"+string.Join(",",m.DependsOn))) &&
                a.Dependencies.Select(d=>d.PackageId+"|"+d.VersionRange.Text+"|"+d.Optional).SequenceEqual(b.Dependencies.Select(d=>d.PackageId+"|"+d.VersionRange.Text+"|"+d.Optional)) &&
                a.ExternalMods.Select(m=>m.PackageId+"|"+m.WorkshopId).SequenceEqual(b.ExternalMods.Select(m=>m.PackageId+"|"+m.WorkshopId));
        }

        internal static XElement Get(Dictionary<string, XElement> fields, string name) { return ManagedExtensionJson.Required(fields, name); }
        private static string AssemblyName(XElement node)
        {
            string name = ManagedExtensionJson.Text(node, 128);
            if (!Regex.IsMatch(name, @"\A[A-Za-z][A-Za-z0-9._-]*\z", RegexOptions.CultureInvariant)) throw ManagedExtensionJson.Error("InvalidAssembly");
            return name;
        }
        internal static ManagedExtensionFile File(Dictionary<string, XElement> fields)
        {
            return new ManagedExtensionFile(ManagedExtensionJson.Path(ManagedExtensionJson.Text(Get(fields, "path"), 240)),
                ManagedExtensionJson.Integer(Get(fields, "length"), 0, MaxFileBytes), ManagedExtensionJson.Hex(Get(fields, "sha256"), 64));
        }
        private static List<string> Strings(XElement node, int maximum, bool ids)
        {
            var values = ManagedExtensionJson.Array(node, maximum).Select(n => ids ? ManagedExtensionJson.Id(n) : ManagedExtensionJson.Text(n, 128)).ToList();
            if (values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Count) throw ManagedExtensionJson.Error("DuplicateValue");
            return values;
        }
        internal static void ValidatePathTree(IEnumerable<string> paths)
        {
            var all = paths.ToArray();
            var spelling = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string path in all)
            {
                string current = "";
                foreach (string part in path.Split('/'))
                {
                    current = current.Length == 0 ? part : current + "/" + part;
                    string previous;
                    if (spelling.TryGetValue(current, out previous) && previous != current) throw ManagedExtensionJson.Error("PathAlias");
                    spelling[current] = current;
                }
                if (all.Any(other => other.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase))) throw ManagedExtensionJson.Error("PathConflict");
            }
        }
        // Actual identity and filename alias both participate in host/package occupancy.
        // Business assemblies are discovered at the host boundary, not reserved by name here.
        public static IEnumerable<string> AssemblyNames(ManagedExtensionAssembly assembly)
        {
            if (assembly == null) throw new ArgumentNullException(nameof(assembly));
            yield return assembly.Name;
            string alias = System.IO.Path.GetFileNameWithoutExtension(assembly.File.Path);
            if (!string.Equals(alias, assembly.Name, StringComparison.OrdinalIgnoreCase)) yield return alias;
        }
        public static bool IsProtectedAssembly(string name)
        {
            return name == null || protectedNames.Contains(name) || name.StartsWith("System.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("Unity.", StringComparison.OrdinalIgnoreCase) || name.StartsWith("UnityEngine.", StringComparison.OrdinalIgnoreCase) ||
                name.StartsWith("com.rlabrecque.steamworks", StringComparison.OrdinalIgnoreCase);
        }
    }
}
