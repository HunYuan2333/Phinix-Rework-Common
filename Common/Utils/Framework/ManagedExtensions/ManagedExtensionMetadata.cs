using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Utils.Framework.ManagedExtensions
{
    public enum ManagedHostReferenceVersionPolicy { CompatibleUpgrade, SameReleaseFamily }

    public sealed class ManagedHostReferenceRule
    {
        public ManagedHostReferenceRule(string assemblyName, ManagedHostReferenceVersionPolicy versionPolicy)
        {
            if (string.IsNullOrWhiteSpace(assemblyName)) throw new ArgumentException("Assembly name is required.", nameof(assemblyName));
            if (!Enum.IsDefined(typeof(ManagedHostReferenceVersionPolicy), versionPolicy)) throw new ArgumentOutOfRangeException(nameof(versionPolicy));
            AssemblyName = assemblyName; VersionPolicy = versionPolicy;
        }
        public string AssemblyName { get; }
        public ManagedHostReferenceVersionPolicy VersionPolicy { get; }
    }

    public sealed class ManagedAssemblyIdentity
    {
        internal ManagedAssemblyIdentity(string name, string version, string culture, string token)
        { Name = name; Version = version; Culture = culture; PublicKeyToken = token; }
        public string Name { get; }
        public string Version { get; }
        public string Culture { get; }
        public string PublicKeyToken { get; }
        public string FullName => Name + ", Version=" + Version + ", Culture=" + Culture + ", PublicKeyToken=" + PublicKeyToken;
        /// <summary>Host libraries may satisfy older references within the same major version.
        /// This policy does not change payload identity or package dependency locks.</summary>
        public bool CanProvideHostReference(ManagedAssemblyIdentity required)
            => CanProvideHostReference(required, ManagedHostReferenceVersionPolicy.CompatibleUpgrade);
        public bool CanProvideHostReference(ManagedAssemblyIdentity required, ManagedHostReferenceVersionPolicy policy)
        {
            if (!Enum.IsDefined(typeof(ManagedHostReferenceVersionPolicy), policy)) throw new ArgumentOutOfRangeException(nameof(policy));
            if (required == null || !string.Equals(Name, required.Name, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(Culture, required.Culture, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(PublicKeyToken, required.PublicKeyToken, StringComparison.OrdinalIgnoreCase)) return false;
            System.Version availableVersion, requiredVersion;
            if (!System.Version.TryParse(Version, out availableVersion) || !System.Version.TryParse(required.Version, out requiredVersion)) return false;
            if (policy == ManagedHostReferenceVersionPolicy.SameReleaseFamily)
                return availableVersion.Major == requiredVersion.Major && availableVersion.Minor == requiredVersion.Minor;
            return availableVersion.Major == requiredVersion.Major && availableVersion.CompareTo(requiredVersion) >= 0;
        }
        public static ManagedAssemblyIdentity SelectHostReference(ManagedAssemblyIdentity required, IEnumerable<ManagedAssemblyIdentity> available)
            => SelectHostReference(required, available, new ManagedHostReferenceRule[0]);
        public static ManagedAssemblyIdentity SelectHostReference(ManagedAssemblyIdentity required, IEnumerable<ManagedAssemblyIdentity> available,
            IEnumerable<ManagedHostReferenceRule> rules)
        {
            if (required == null) throw new ArgumentNullException(nameof(required));
            if (available == null) throw new ArgumentNullException(nameof(available));
            if (rules == null) throw new ArgumentNullException(nameof(rules));
            var matchingRules = rules.Where(r => string.Equals(r.AssemblyName, required.Name, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matchingRules.Count > 1) return null;
            var policy = matchingRules.Count == 0 ? ManagedHostReferenceVersionPolicy.CompatibleUpgrade : matchingRules[0].VersionPolicy;
            var identities = available.ToList();
            var exact = identities.Where(a => a.FullName == required.FullName).ToList();
            if (exact.Count != 0) return exact.Count == 1 ? exact[0] : null;
            var compatible = identities.Where(a => a.CanProvideHostReference(required, policy)).ToList();
            // Do not silently choose among multiple loaded host versions.
            return compatible.Count == 1 ? compatible[0] : null;
        }
        public static ManagedAssemblyIdentity FromAssemblyName(AssemblyName name)
        {
            if (name == null) throw new ArgumentNullException(nameof(name));
            if (string.IsNullOrEmpty(name.Name) || name.Version == null) throw new ArgumentException("A complete assembly identity is required.", nameof(name));
            byte[] token = name.GetPublicKeyToken();
            return new ManagedAssemblyIdentity(name.Name, name.Version.ToString(), string.IsNullOrEmpty(name.CultureName) ? "neutral" : name.CultureName,
                token == null || token.Length == 0 ? "null" : ManagedExtensionDigest.Hex(token));
        }
    }

    public sealed class ManagedModuleMetadata
    {
        internal ManagedModuleMetadata(string type, string id, IEnumerable<string> dependencies)
        { EntryType = type; Id = id; DependsOn = ManagedExtensionCompatibility.Freeze(dependencies); }
        public string EntryType { get; }
        public string Id { get; }
        public ReadOnlyCollection<string> DependsOn { get; }
    }

    public sealed class ManagedAssemblyMetadata
    {
        internal ManagedAssemblyMetadata(ManagedAssemblyIdentity identity, IEnumerable<ManagedAssemblyIdentity> references,
            string targetFramework, IEnumerable<ManagedModuleMetadata> modules, IEnumerable<string> types, IEnumerable<string> moduleTypes)
        { Identity = identity; References = ManagedExtensionCompatibility.Freeze(references); TargetFramework = targetFramework;
            Modules = ManagedExtensionCompatibility.Freeze(modules); Types = ManagedExtensionCompatibility.Freeze(types); ModuleTypes = ManagedExtensionCompatibility.Freeze(moduleTypes); }
        public ManagedAssemblyIdentity Identity { get; }
        public ReadOnlyCollection<ManagedAssemblyIdentity> References { get; }
        public string TargetFramework { get; }
        public ReadOnlyCollection<ManagedModuleMetadata> Modules { get; }
        public ReadOnlyCollection<string> Types { get; }
        public ReadOnlyCollection<string> ModuleTypes { get; }
    }

    /// <summary>Bounded ECMA-335 metadata inspection. Never loads an assembly or executes attributes.</summary>
    public static class ManagedExtensionMetadataReader
    {
        /// <summary>Read a local CLR name without interpreting plugin types or custom attributes.
        /// This is collision evidence only, never payload approval.</summary>
        public static ManagedAssemblyIdentity ReadIdentity(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length == 0 || bytes.Length > ManagedExtensionManifestReader.MaxFileBytes)
                throw ManagedExtensionJson.Error("AssemblySizeInvalid");
            return new Reader((byte[])bytes.Clone()).ReadIdentity();
        }

        public static ManagedAssemblyMetadata Read(byte[] bytes)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (bytes.Length == 0 || bytes.Length > ManagedExtensionManifestReader.MaxFileBytes)
                throw ManagedExtensionJson.Error("AssemblySizeInvalid");
            // Snapshot the caller's mutable buffer before interpreting offsets.
            return new Reader((byte[])bytes.Clone()).Read();
        }

        private sealed class Reader
        {
            private readonly byte[] bytes;
            private readonly int[] counts = new int[45], sizes = new int[45], starts = new int[45];
            private int strings, stringSize, blobs, blobSize, stringWidth, blobWidth, guidWidth;
            private readonly Dictionary<int, int> nested = new Dictionary<int, int>();
            private readonly Dictionary<int, List<int>> interfaces = new Dictionary<int, List<int>>();
            private readonly Dictionary<int, ManagedAssemblyIdentity> identityCache = new Dictionary<int, ManagedAssemblyIdentity>();
            private readonly Dictionary<int, bool> moduleCache = new Dictionary<int, bool>();
            private readonly HashSet<int> genericTypes = new HashSet<int>();
            private readonly Dictionary<int, string> typeNames = new Dictionary<int, string>();
            private readonly UTF8Encoding utf8 = new UTF8Encoding(false, true);
            internal Reader(byte[] bytes) { this.bytes = bytes; }
            private static ManagedExtensionValidationException Bad() { return ManagedExtensionJson.Error("AssemblyMetadataInvalid"); }
            private void Bound(int offset, long length)
            { if (offset < 0 || length < 0 || offset > bytes.Length || length > bytes.Length - offset) throw Bad(); }
            private int U16(int offset) { Bound(offset, 2); return bytes[offset] | bytes[offset + 1] << 8; }
            private uint U32(int offset) { Bound(offset, 4); return (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24); }
            private int N32(int offset) { uint n = U32(offset); if (n > int.MaxValue) throw Bad(); return (int)n; }
            private ulong U64(int offset) { return U32(offset) | (ulong)U32(offset + 4) << 32; }
            private int Index(ref int offset, int width) { int n = width == 2 ? U16(offset) : N32(offset); offset += width; return n; }
            private int Ti(int table) { return counts[table] < 65536 ? 2 : 4; }
            private int Ci(int bits, params int[] tables) { return tables.Max(t => counts[t]) < (1 << (16 - bits)) ? 2 : 4; }
            private int Row(int table, int row)
            { if (row < 1 || row > counts[table]) throw Bad(); return starts[table] + (row - 1) * sizes[table]; }
            private string Str(int index)
            {
                if (index < 0 || index >= stringSize) throw Bad();
                int end = index;
                while (end < stringSize && bytes[strings + end] != 0 && end - index <= 4096) end++;
                if (end == stringSize || end - index > 4096) throw Bad();
                try { return utf8.GetString(bytes, strings + index, end - index); }
                catch (DecoderFallbackException) { throw Bad(); }
            }
            private int Compressed(ref int offset, int end)
            {
                if (offset >= end) throw Bad();
                int first = bytes[offset++];
                if (first < 128) return first;
                int count = (first & 0xc0) == 0x80 ? 1 : (first & 0xe0) == 0xc0 ? 3 : -1;
                if (count < 0 || end - offset < count) throw Bad();
                int n = first & (count == 1 ? 0x3f : 0x1f);
                for (int i = 0; i < count; i++) n = (n << 8) | bytes[offset++];
                if (n < (count == 1 ? 128 : 16384)) throw Bad();
                return n;
            }
            private byte[] Blob(int index)
            {
                if (index < 0 || index >= blobSize) throw Bad();
                int at = blobs + index, end = blobs + blobSize;
                int length = Compressed(ref at, end);
                if (length > end - at || length > 16384) throw Bad();
                var result = new byte[length]; Buffer.BlockCopy(bytes, at, result, 0, length); return result;
            }
            private int MapRva(int sectionStart, int sectionCount, uint rva, int length)
            {
                int result = -1;
                for (int i = 0; i < sectionCount; i++)
                {
                    int row = sectionStart + i * 40;
                    uint start = U32(row + 12), rawSize = U32(row + 16), raw = U32(row + 20);
                    if (rva < start || (ulong)rva - start + (ulong)length > rawSize) continue;
                    long at = (long)raw + rva - start;
                    if (at > int.MaxValue || result != -1) throw Bad();
                    Bound((int)at, length); result = (int)at;
                }
                if (result < 0) throw Bad(); return result;
            }
            private void OpenTables()
            {
                if (U16(0) != 0x5a4d) throw Bad();
                int pe = N32(0x3c); Bound(pe, 24);
                if (U32(pe) != 0x4550 || (U16(pe + 22) & 0x2000) == 0) throw Bad();
                int sections = U16(pe + 6), optionalSize = U16(pe + 20), optional = pe + 24;
                if (sections < 1 || sections > 96) throw Bad();
                Bound(optional, optionalSize);
                int magic = U16(optional), dir = magic == 0x10b ? 96 : magic == 0x20b ? 112 : -1;
                if (dir < 0 || optionalSize < dir + 15 * 8 || U32(optional + dir - 4) < 15) throw Bad();
                int sectionStart = optional + optionalSize; Bound(sectionStart, sections * 40);
                int cli = MapRva(sectionStart, sections, U32(optional + dir + 14 * 8), 72);
                if (U32(optional + dir + 14 * 8 + 4) < 72 || U32(cli) < 72 ||
                    (U32(cli + 16) & 1) == 0 || (U32(cli + 16) & 0x12) != 0) throw Bad();
                int metaSize = N32(cli + 12), meta = MapRva(sectionStart, sections, U32(cli + 8), metaSize);
                if (metaSize < 20 || U32(meta) != 0x424a5342) throw Bad();
                int versionLength = N32(meta + 12);
                if (versionLength < 1 || versionLength > 256 || 16L + versionLength + 4 > metaSize) throw Bad();
                int at = meta + 16 + ((versionLength + 3) & ~3), end = meta + metaSize;
                if (end - at < 4) throw Bad();
                int streamCount = U16(at + 2); at += 4;
                if (streamCount < 3 || streamCount > 16) throw Bad();
                var streams = new Dictionary<string, Tuple<int, int>>(StringComparer.Ordinal);
                for (int i = 0; i < streamCount; i++)
                {
                    if (end - at < 9) throw Bad();
                    int offset = N32(at), size = N32(at + 4), name = at + 8, cursor = name;
                    while (cursor < end && cursor - name < 32 && bytes[cursor] != 0) cursor++;
                    if (cursor == end || cursor - name == 32 || (long)offset + size > metaSize) throw Bad();
                    string key = Encoding.ASCII.GetString(bytes, name, cursor - name);
                    if (streams.ContainsKey(key)) throw Bad();
                    streams.Add(key, Tuple.Create(meta + offset, size));
                    at = name + ((cursor - name + 1 + 3) & ~3);
                }
                foreach (var stream in streams.Values)
                    if (stream.Item1 < at || streams.Values.Any(other => !ReferenceEquals(stream, other) &&
                        stream.Item1 < (long)other.Item1 + other.Item2 && other.Item1 < (long)stream.Item1 + stream.Item2)) throw Bad();
                Tuple<int, int> stringStream, blobStream, tableStream;
                if (!streams.TryGetValue("#Strings", out stringStream) || !streams.TryGetValue("#Blob", out blobStream) ||
                    !streams.TryGetValue("#~", out tableStream) && !streams.TryGetValue("#-", out tableStream) ||
                    streams.ContainsKey("#~") && streams.ContainsKey("#-")) throw Bad();
                strings = stringStream.Item1; stringSize = stringStream.Item2; blobs = blobStream.Item1; blobSize = blobStream.Item2;
                if (stringSize < 1 || blobSize < 1 || bytes[strings] != 0 || bytes[blobs] != 0 || tableStream.Item2 < 24) throw Bad();
                at = tableStream.Item1; end = at + tableStream.Item2;
                int heaps = bytes[at + 6]; if ((heaps & ~7) != 0) throw Bad();
                stringWidth = (heaps & 1) == 0 ? 2 : 4; guidWidth = (heaps & 2) == 0 ? 2 : 4; blobWidth = (heaps & 4) == 0 ? 2 : 4;
                ulong valid = U64(at + 8); if ((valid >> 45) != 0) throw Bad(); at += 24;
                long total = 0;
                for (int i = 0; i < 45; i++) if ((valid & (1UL << i)) != 0)
                { if (end - at < 4) throw Bad(); counts[i] = N32(at); at += 4;
                    total += counts[i]; if (counts[i] > 200000 || total > 1000000) throw Bad(); }
                if (counts[2] > 20000 || counts[35] > 256) throw ManagedExtensionJson.Error("AssemblyMetadataLimit");
                ComputeSizes();
                for (int i = 0; i < 45; i++)
                { starts[i] = at; long length = (long)counts[i] * sizes[i]; if (length > end - at) throw Bad(); at += (int)length; }
                if (counts[32] != 1 || counts[0] != 1 || counts[38] != 0 || new[] { 3, 5, 7, 19, 22, 30, 31 }.Any(t => counts[t] != 0))
                    throw ManagedExtensionJson.Error("AssemblyUnsupportedLayout"); // No multi-module payloads or edit-and-continue/pointer layouts.
            }
            private void ComputeSizes()
            {
                int s = stringWidth, b = blobWidth, g = guidWidth;
                int td = Ci(2, 2, 1, 27), mr = Ci(1, 6, 10), impl = Ci(2, 38, 35, 39);
                sizes[0] = 2 + s + 3 * g; sizes[1] = Ci(2, 0, 26, 35, 1) + 2 * s;
                sizes[2] = 4 + 2 * s + td + Ti(4) + Ti(6); sizes[3] = Ti(4); sizes[4] = 2 + s + b;
                sizes[5] = Ti(6); sizes[6] = 8 + s + b + Ti(8); sizes[7] = Ti(8); sizes[8] = 4 + s;
                sizes[9] = Ti(2) + td; sizes[10] = Ci(3, 2, 1, 26, 6, 27) + s + b;
                sizes[11] = 2 + Ci(2, 4, 8, 23) + b;
                sizes[12] = Ci(5, 6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43) + Ci(3, 6, 10) + b;
                sizes[13] = Ci(1, 4, 8) + b; sizes[14] = 2 + Ci(2, 2, 6, 32) + b;
                sizes[15] = 6 + Ti(2); sizes[16] = 4 + Ti(4); sizes[17] = b;
                sizes[18] = Ti(2) + Ti(20); sizes[19] = Ti(20); sizes[20] = 2 + s + td;
                sizes[21] = Ti(2) + Ti(23); sizes[22] = Ti(23); sizes[23] = 2 + s + b;
                sizes[24] = 2 + Ti(6) + Ci(1, 20, 23); sizes[25] = Ti(2) + 2 * mr;
                sizes[26] = s; sizes[27] = b; sizes[28] = 2 + Ci(1, 4, 6) + s + Ti(26);
                sizes[29] = 4 + Ti(4); sizes[30] = 8; sizes[31] = 4;
                sizes[32] = 16 + b + 2 * s; sizes[33] = 4; sizes[34] = 12;
                sizes[35] = 12 + 2 * b + 2 * s; sizes[36] = 4 + Ti(35); sizes[37] = 12 + Ti(35);
                sizes[38] = 4 + s + b; sizes[39] = 8 + 2 * s + impl; sizes[40] = 8 + s + impl;
                sizes[41] = 2 * Ti(2); sizes[42] = 4 + Ci(1, 2, 6) + s;
                sizes[43] = mr + b; sizes[44] = Ti(42) + td;
            }
            private ManagedAssemblyIdentity Identity(int table, int row)
            {
                ManagedAssemblyIdentity cached; int cacheKey = table << 24 | row;
                if (identityCache.TryGetValue(cacheKey, out cached)) return cached;
                int at = Row(table, row);
                if (table == 32) at += 4;
                string version = U16(at) + "." + U16(at + 2) + "." + U16(at + 4) + "." + U16(at + 6); at += 8;
                uint flags = U32(at); at += 4;
                byte[] key = Blob(Index(ref at, blobWidth));
                string name = Str(Index(ref at, stringWidth)), culture = Str(Index(ref at, stringWidth));
                if (name.Length == 0 || name.Length > 128 || culture.Length > 128 || (flags & 0xe00) != 0) throw Bad();
                string token = "null";
                if (key.Length != 0)
                {
                    if ((flags & 1) != 0)
                    { using (var sha = SHA1.Create()) key = sha.ComputeHash(key).Reverse().Take(8).ToArray(); }
                    else if (key.Length != 8) throw Bad();
                    token = ManagedExtensionDigest.Hex(key);
                }
                var result = new ManagedAssemblyIdentity(name, version, culture.Length == 0 ? "neutral" : culture, token);
                identityCache.Add(cacheKey, result); return result;
            }
            private string TypeName(int row, HashSet<int> visiting)
            {
                string known; if (typeNames.TryGetValue(row, out known)) return known;
                if (visiting.Count > 64 || !visiting.Add(row)) throw Bad();
                int at = Row(2, row) + 4;
                string name = Str(Index(ref at, stringWidth)), ns = Str(Index(ref at, stringWidth));
                int parent;
                string full = nested.TryGetValue(row, out parent) ? TypeName(parent, visiting) + "+" + name : (ns.Length == 0 ? name : ns + "." + name);
                if (full.Length > 1024 || name.Length == 0) throw Bad();
                visiting.Remove(row); typeNames.Add(row, full); return full;
            }
            private string AttributeType(int encoded, out string scope)
            {
                scope = null;
                if ((encoded & 7) != 3) return null; // MemberRef; local constructors never establish a trusted framework attribute.
                int at = Row(10, encoded >> 3);
                int parent = Index(ref at, Ci(3, 2, 1, 26, 6, 27));
                string name = Str(Index(ref at, stringWidth));
                int signatureIndex = Index(ref at, blobWidth);
                if ((parent & 7) != 1 || name != ".ctor") return null;
                at = Row(1, parent >> 3);
                int resolution = Index(ref at, Ci(2, 0, 26, 35, 1));
                string type = Str(Index(ref at, stringWidth)), ns = Str(Index(ref at, stringWidth));
                if ((resolution & 3) != 2) return null;
                scope = Identity(35, resolution >> 2).Name;
                string full = ns.Length == 0 ? type : ns + "." + type;
                if (full == "Utils.Framework.PhinixExtensionAttribute" || full == "System.Runtime.Versioning.TargetFrameworkAttribute")
                    if (!Blob(signatureIndex).SequenceEqual(new byte[] { 0x20, 1, 1, 0x0e })) throw Bad();
                return full;
            }
            private string SerString(byte[] blob, ref int at)
            {
                if (at >= blob.Length || blob[at] == 0xff) throw Bad();
                // Compressed integers use the same encoding as heap lengths, but this buffer is separate.
                int first = blob[at++], length = first;
                if (first >= 128)
                {
                    int extra = (first & 0xc0) == 0x80 ? 1 : (first & 0xe0) == 0xc0 ? 3 : -1;
                    if (extra < 0 || blob.Length - at < extra) throw Bad();
                    length = first & (extra == 1 ? 0x3f : 0x1f);
                    for (int i = 0; i < extra; i++) length = length << 8 | blob[at++];
                    if (length < (extra == 1 ? 128 : 16384)) throw Bad();
                }
                if (length > 4096 || length > blob.Length - at) throw Bad();
                try { string value = utf8.GetString(blob, at, length); at += length; return value; }
                catch (DecoderFallbackException) { throw Bad(); }
            }
            private string ReadAttribute(byte[] blob, bool module, out List<string> dependencies)
            {
                dependencies = new List<string>();
                if (blob.Length < 4 || blob[0] != 1 || blob[1] != 0) throw Bad();
                int at = 2; string value = SerString(blob, ref at);
                if (blob.Length - at < 2) throw Bad();
                int named = blob[at] | blob[at + 1] << 8; at += 2;
                if (named > 1) throw Bad();
                if (named == 1)
                {
                    if (blob.Length - at < 2 || blob[at++] != 0x54) throw Bad();
                    if (module)
                    {
                        if (blob.Length - at < 2 || blob[at++] != 0x1d || blob[at++] != 0x0e || SerString(blob, ref at) != "DependsOn" || blob.Length - at < 4) throw Bad();
                        uint count = (uint)(blob[at] | blob[at + 1] << 8 | blob[at + 2] << 16 | blob[at + 3] << 24); at += 4;
                        if (count > 64) throw Bad();
                        for (int i = 0; i < count; i++) dependencies.Add(SerString(blob, ref at));
                    }
                    else
                    { if (blob[at++] != 0x0e || SerString(blob, ref at) != "FrameworkDisplayName") throw Bad(); SerString(blob, ref at); }
                }
                if (at != blob.Length) throw Bad(); return value;
            }
            private bool IsModuleReference(int encoded)
            {
                if ((encoded & 3) != 1) return false;
                int at = Row(1, encoded >> 2), scope = Index(ref at, Ci(2, 0, 26, 35, 1));
                string name = Str(Index(ref at, stringWidth)), ns = Str(Index(ref at, stringWidth));
                if ((scope & 3) != 2) return false;
                string assembly = Identity(35, scope >> 2).Name;
                return name == "IPhinixExtensionModule" && ns == "Utils.Framework" && assembly == "Utils" ||
                    name == "IClientExtensionModule" && ns == "PhinixClient.Framework" && assembly == "ClientExtensionAbstractions";
            }
            private bool IsClientModuleBase(int encoded)
            {
                if ((encoded & 3) != 1) return false;
                int at = Row(1, encoded >> 2), scope = Index(ref at, Ci(2, 0, 26, 35, 1));
                string name = Str(Index(ref at, stringWidth)), ns = Str(Index(ref at, stringWidth));
                return (name == "ClientExtensionModule" || name == "LegacyClientExtensionModule") && ns == "PhinixClient.Framework" &&
                    (scope & 3) == 2 && Identity(35, scope >> 2).Name == "ClientExtensionAbstractions";
            }
            private bool ImplementsModule(int row, HashSet<int> visiting)
            {
                bool cached; if (moduleCache.TryGetValue(row, out cached)) return cached;
                if (visiting.Count > 64 || !visiting.Add(row)) throw Bad();
                List<int> implemented;
                if (interfaces.TryGetValue(row, out implemented))
                    foreach (int iface in implemented)
                        if (IsModuleReference(iface) || (iface & 3) == 0 && iface != 0 && ImplementsModule(iface >> 2, new HashSet<int>(visiting))) { moduleCache[row] = true; return true; }
                int type = Row(2, row) + 4 + 2 * stringWidth;
                int parent = Index(ref type, Ci(2, 2, 1, 27));
                // Only the known client contract bases may be inherited externally.
                // Arbitrary inheritance from another package remains unsupported.
                bool result = IsClientModuleBase(parent) || parent != 0 && (parent & 3) == 0 && ImplementsModule(parent >> 2, visiting);
                moduleCache[row] = result; return result;
            }
            private bool IsEntry(int row)
            {
                int at = Row(2, row); uint flags = U32(at);
                if ((flags & 0xa0) != 0) return false;
                int visibility = (int)(flags & 7), parent;
                if (visibility != 1 && visibility != 2) return false;
                if (nested.TryGetValue(row, out parent) && !IsPublicType(parent)) return false;
                if (genericTypes.Contains(row)) return false;
                at += 4 + 2 * stringWidth + Ci(2, 2, 1, 27) + Ti(4);
                int first = Index(ref at, Ti(6)), last = counts[6] + 1;
                if (row < counts[2]) { at = Row(2, row + 1) + 4 + 2 * stringWidth + Ci(2, 2, 1, 27) + Ti(4); last = Index(ref at, Ti(6)); }
                if (first < 1 || last < first || last > counts[6] + 1) throw Bad();
                for (int i = first; i < last; i++)
                {
                    at = Row(6, i); int methodFlags = U16(at + 6); at += 8;
                    string name = Str(Index(ref at, stringWidth)); byte[] signature = Blob(Index(ref at, blobWidth));
                    if (name == ".ctor" && (methodFlags & 0x17) == 6 && signature.SequenceEqual(new byte[] { 0x20, 0, 1 })) return true;
                }
                return false;
            }
            private bool IsPublicType(int row)
            {
                int depth = 0;
                while (true)
                {
                    if (++depth > 64) throw Bad();
                    int visibility = (int)(U32(Row(2, row)) & 7), parent;
                    if (!nested.TryGetValue(row, out parent)) return visibility == 1;
                    if (visibility != 2) return false;
                    row = parent;
                }
            }
            internal ManagedAssemblyIdentity ReadIdentity()
            {
                OpenTables();
                return Identity(32, 1);
            }
            internal ManagedAssemblyMetadata Read()
            {
                OpenTables();
                for (int i = 1; i <= counts[9]; i++)
                {
                    int at = Row(9, i), owner = Index(ref at, Ti(2)), iface = Index(ref at, Ci(2, 2, 1, 27)); Row(2, owner);
                    List<int> list; if (!interfaces.TryGetValue(owner, out list)) { list = new List<int>(); interfaces.Add(owner, list); }
                    list.Add(iface);
                }
                for (int i = 1; i <= counts[42]; i++)
                { int at = Row(42, i) + 4, owner = Index(ref at, Ci(1, 2, 6)); if ((owner & 1) == 0) { Row(2, owner >> 1); genericTypes.Add(owner >> 1); } }
                for (int i = 1; i <= counts[41]; i++)
                { int at = Row(41, i), child = Index(ref at, Ti(2)), parent = Index(ref at, Ti(2));
                    Row(2, child); Row(2, parent); if (nested.ContainsKey(child)) throw Bad(); nested.Add(child, parent); }
                var types = new List<string>();
                long typeCharacters = 0;
                for (int i = 1; i <= counts[2]; i++)
                { string name = TypeName(i, new HashSet<int>()); typeCharacters += name.Length;
                    if (typeCharacters > 2 * 1024 * 1024) throw ManagedExtensionJson.Error("AssemblyMetadataLimit"); types.Add(name); }
                if (types.Distinct(StringComparer.Ordinal).Count() != types.Count) throw Bad();
                var references = new List<ManagedAssemblyIdentity>();
                for (int i = 1; i <= counts[35]; i++) references.Add(Identity(35, i));
                if (references.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != references.Count) throw Bad();
                var modules = new List<ManagedModuleMetadata>(); string framework = null;
                int parentWidth = Ci(5, 6, 4, 1, 2, 8, 9, 10, 0, 14, 23, 20, 17, 26, 27, 32, 35, 38, 39, 40, 42, 44, 43);
                for (int i = 1; i <= counts[12]; i++)
                {
                    int at = Row(12, i), parent = Index(ref at, parentWidth), ctor = Index(ref at, Ci(3, 6, 10));
                    int blobIndex = Index(ref at, blobWidth); string scope;
                    string type = AttributeType(ctor, out scope);
                    bool module = type == "Utils.Framework.PhinixExtensionAttribute";
                    bool target = type == "System.Runtime.Versioning.TargetFrameworkAttribute";
                    if (!module && !target) continue;
                    if (module && scope != "Utils" || target && scope != "mscorlib" && scope != "System.Runtime") throw Bad();
                    if (module && (parent & 31) != 3 || target && ((parent & 31) != 14 || (parent >> 5) != 1)) throw Bad();
                    List<string> dependencies; string value = ReadAttribute(Blob(blobIndex), module, out dependencies);
                    if (module)
                    { string full = TypeName(parent >> 5, new HashSet<int>());
                        if (modules.Any(m => m.EntryType == full)) throw Bad(); modules.Add(new ManagedModuleMetadata(full, value, dependencies));
                        if (modules.Count > 64) throw ManagedExtensionJson.Error("AssemblyMetadataLimit"); }
                    else { if (framework != null) throw Bad(); framework = value; }
                }
                var moduleTypes = new List<string>();
                for (int i = 1; i <= counts[2]; i++)
                    if (ImplementsModule(i, new HashSet<int>()) && IsEntry(i)) moduleTypes.Add(TypeName(i, new HashSet<int>()));
                if (modules.Any(m => !moduleTypes.Contains(m.EntryType))) throw ManagedExtensionJson.Error("ModuleEntryInvalid");
                return new ManagedAssemblyMetadata(Identity(32, 1), references, framework, modules, types, moduleTypes);
            }
        }
    }
}
