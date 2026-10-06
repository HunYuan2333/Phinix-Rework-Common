using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;

namespace Utils.Framework.ManagedExtensions
{
    // Stable package versions deliberately differ from four-component CLR identities.
    public sealed class ManagedExtensionVersion : IComparable<ManagedExtensionVersion>
    {
        private readonly Version value;
        private ManagedExtensionVersion(Version value) { this.value = value; }
        public static ManagedExtensionVersion Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 32) throw ManagedExtensionJson.Error("InvalidVersion");
            string[] parts = text.Split('.');
            var numbers = new int[3];
            if (parts.Length != 3) throw ManagedExtensionJson.Error("InvalidVersion");
            for (int i = 0; i < 3; i++)
                if (parts[i].Length == 0 || parts[i].Length > 1 && parts[i][0] == '0' ||
                    !int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out numbers[i]) ||
                    numbers[i].ToString(CultureInfo.InvariantCulture) != parts[i]) throw ManagedExtensionJson.Error("InvalidVersion");
            return new ManagedExtensionVersion(new Version(numbers[0], numbers[1], numbers[2]));
        }
        public int CompareTo(ManagedExtensionVersion other) { return other == null ? 1 : value.CompareTo(other.value); }
        public override string ToString() { return value.ToString(3); }
    }

    public sealed class ManagedExtensionVersionRange
    {
        private ManagedExtensionVersionRange(string text, ManagedExtensionVersion exact, ManagedExtensionVersion lower, ManagedExtensionVersion upper)
        { Text = text; Exact = exact; Lower = lower; Upper = upper; }
        public string Text { get; }
        public ManagedExtensionVersion Exact { get; }
        public ManagedExtensionVersion Lower { get; }
        public ManagedExtensionVersion Upper { get; }
        public static ManagedExtensionVersionRange Parse(string text)
        {
            if (string.IsNullOrEmpty(text) || text.Length > 80) throw ManagedExtensionJson.Error("InvalidRange");
            try
            {
                if (!text.StartsWith(">=", StringComparison.Ordinal))
                    return new ManagedExtensionVersionRange(text, ManagedExtensionVersion.Parse(text), null, null);
                string[] parts = text.Split(' ');
                if (parts.Length != 2 || !parts[1].StartsWith("<", StringComparison.Ordinal)) throw ManagedExtensionJson.Error("InvalidRange");
                var lower = ManagedExtensionVersion.Parse(parts[0].Substring(2));
                var upper = ManagedExtensionVersion.Parse(parts[1].Substring(1));
                if (lower.CompareTo(upper) >= 0) throw ManagedExtensionJson.Error("InvalidRange");
                return new ManagedExtensionVersionRange(text, null, lower, upper);
            }
            catch (ManagedExtensionValidationException ex)
            { if (ex.Code == "InvalidRange") throw; throw ManagedExtensionJson.Error("InvalidRange"); }
        }
        public bool Contains(ManagedExtensionVersion version)
        { return version != null && (Exact != null ? Exact.CompareTo(version) == 0 : Lower.CompareTo(version) <= 0 && Upper.CompareTo(version) > 0); }
    }

    public sealed class ManagedExtensionCompatibility
    {
        internal ManagedExtensionCompatibility(IEnumerable<string> versions, ManagedExtensionVersionRange phinix, ManagedExtensionVersionRange abstractions)
        { RimWorldVersions = Freeze(versions); PhinixRange = phinix; AbstractionsRange = abstractions; }
        public ReadOnlyCollection<string> RimWorldVersions { get; }
        public ManagedExtensionVersionRange PhinixRange { get; }
        public ManagedExtensionVersionRange AbstractionsRange { get; }
        internal static ReadOnlyCollection<T> Freeze<T>(IEnumerable<T> values) { return new List<T>(values).AsReadOnly(); }
    }

    public sealed class ManagedExtensionDependency
    {
        internal ManagedExtensionDependency(string id, ManagedExtensionVersionRange range, bool optional)
        { PackageId = id; VersionRange = range; Optional = optional; }
        public string PackageId { get; }
        public ManagedExtensionVersionRange VersionRange { get; }
        public bool Optional { get; }
    }

    public sealed class ManagedExtensionModule
    {
        internal ManagedExtensionModule(string id, string assemblyName, string entryType, IEnumerable<string> dependencies)
        { Id = id; AssemblyName = assemblyName; EntryType = entryType; DependsOn = ManagedExtensionCompatibility.Freeze(dependencies); }
        public string Id { get; }
        public string AssemblyName { get; }
        public string EntryType { get; }
        public ReadOnlyCollection<string> DependsOn { get; }
    }

    public sealed class ManagedExtensionFile
    {
        internal ManagedExtensionFile(string path, long length, string hash) { Path = path; Length = length; Sha256 = hash; }
        public string Path { get; }
        public long Length { get; }
        public string Sha256 { get; }
    }

    public sealed class ManagedExtensionAssembly
    {
        internal ManagedExtensionAssembly(string name, string version, string culture, string token, ManagedExtensionFile file)
        { Name = name; Version = version; Culture = culture; PublicKeyToken = token; File = file; }
        public string Name { get; }
        public string Version { get; }
        public string Culture { get; }
        public string PublicKeyToken { get; }
        public ManagedExtensionFile File { get; }
        public string FullName => Name + ", Version=" + Version + ", Culture=" + Culture + ", PublicKeyToken=" + PublicKeyToken;
    }

    public sealed class ManagedExtensionExternalMod
    {
        internal ManagedExtensionExternalMod(string id, string workshopId) { PackageId = id; WorkshopId = workshopId; }
        public string PackageId { get; }
        public string WorkshopId { get; }
    }

    public sealed class ManagedExtensionManifest
    {
        public const int SchemaVersion = 1;
        public const string Management = "phinix-dll";
        public const string FileName = "manifest.json";
        internal ManagedExtensionManifest(string id, string name, ManagedExtensionVersion version,
            ManagedExtensionCompatibility compatibility, IEnumerable<ManagedExtensionDependency> dependencies,
            IEnumerable<ManagedExtensionModule> modules, IEnumerable<ManagedExtensionAssembly> assemblies,
            IEnumerable<ManagedExtensionFile> resources, IEnumerable<ManagedExtensionExternalMod> externalMods, ExtensionLocalizationDeclaration localization = null)
        {
            PackageId = id; Name = name; Version = version; Compatibility = compatibility;
            Dependencies = ManagedExtensionCompatibility.Freeze(dependencies); Modules = ManagedExtensionCompatibility.Freeze(modules);
            Assemblies = ManagedExtensionCompatibility.Freeze(assemblies); Resources = ManagedExtensionCompatibility.Freeze(resources);
            ExternalMods = ManagedExtensionCompatibility.Freeze(externalMods); Localization = localization;
        }
        public ExtensionLocalizationDeclaration Localization { get; }
        public string PackageId { get; }
        public string Name { get; }
        public ManagedExtensionVersion Version { get; }
        public ManagedExtensionCompatibility Compatibility { get; }
        public ReadOnlyCollection<ManagedExtensionDependency> Dependencies { get; }
        public ReadOnlyCollection<ManagedExtensionModule> Modules { get; }
        public ReadOnlyCollection<ManagedExtensionAssembly> Assemblies { get; }
        public ReadOnlyCollection<ManagedExtensionFile> Resources { get; }
        public ReadOnlyCollection<ManagedExtensionExternalMod> ExternalMods { get; }
    }
}
