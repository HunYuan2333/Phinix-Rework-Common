using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace Utils.Framework.ManagedExtensions
{
    /// <summary>Frozen, inspected bytes. This is not authorization to load or activate.</summary>
    public sealed class ManagedExtensionInspectedAssembly
    {
        private readonly byte[] bytes;
        internal ManagedExtensionInspectedAssembly(ManagedExtensionAssembly declaration, byte[] bytes, ManagedAssemblyMetadata metadata)
        { Declaration = declaration; this.bytes = bytes; Metadata = metadata; }
        public ManagedExtensionAssembly Declaration { get; }
        public ManagedAssemblyMetadata Metadata { get; }
        public byte[] CopyBytes() { return (byte[])bytes.Clone(); }
    }

    public sealed class ManagedExtensionInspectedPayload
    {
        internal ManagedExtensionInspectedPayload(ManagedExtensionManifest manifest, IEnumerable<ManagedExtensionInspectedAssembly> assemblies)
        { Manifest = manifest; Assemblies = ManagedExtensionCompatibility.Freeze(assemblies); }
        public ManagedExtensionManifest Manifest { get; }
        public ReadOnlyCollection<ManagedExtensionInspectedAssembly> Assemblies { get; }
    }

    public static class ManagedExtensionPayloadInspector
    {
        public static ManagedExtensionInspectedPayload Inspect(ManagedExtensionManifest manifest,
            IReadOnlyDictionary<string, byte[]> assemblyBytes, CancellationToken cancellationToken)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (assemblyBytes == null) throw new ArgumentNullException(nameof(assemblyBytes));
            cancellationToken.ThrowIfCancellationRequested();
            if (assemblyBytes.Count != manifest.Assemblies.Count || assemblyBytes.Keys.Any(k => !manifest.Assemblies.Any(a => a.File.Path == k)))
                throw ManagedExtensionJson.Error("AssemblyFilesMismatch");
            var result = new List<ManagedExtensionInspectedAssembly>();
            long total = 0;
            foreach (var declaration in manifest.Assemblies)
            {
                cancellationToken.ThrowIfCancellationRequested();
                byte[] input;
                if (!assemblyBytes.TryGetValue(declaration.File.Path, out input) || input == null || input.LongLength != declaration.File.Length)
                    throw ManagedExtensionJson.Error("AssemblyLengthMismatch");
                total += input.LongLength;
                if (input.LongLength > ManagedExtensionManifestReader.MaxFileBytes || total > ManagedExtensionManifestReader.MaxExpandedBytes)
                    throw ManagedExtensionJson.Error("AssemblySizeInvalid");
                byte[] frozen = (byte[])input.Clone();
                if (ManagedExtensionDigest.Hash(frozen) != declaration.File.Sha256) throw ManagedExtensionJson.Error("AssemblyDigestMismatch");
                var metadata = ManagedExtensionMetadataReader.Read(frozen);
                cancellationToken.ThrowIfCancellationRequested();
                if (metadata.Identity.FullName != declaration.FullName) throw ManagedExtensionJson.Error("AssemblyIdentityMismatch");
                if (metadata.TargetFramework != ".NETFramework,Version=v4.7.2") throw ManagedExtensionJson.Error("AssemblyTargetFrameworkMismatch");
                var declaredModules = manifest.Modules.Where(m => m.AssemblyName == declaration.Name).ToList();
                if (metadata.Modules.Count != declaredModules.Count || metadata.ModuleTypes.Count != declaredModules.Count)
                    throw ManagedExtensionJson.Error("AssemblyModulesMismatch");
                foreach (var module in declaredModules)
                {
                    var actual = metadata.Modules.SingleOrDefault(m => m.EntryType == module.EntryType);
                    if (actual == null || actual.Id != module.Id || actual.DependsOn.Count != module.DependsOn.Count ||
                        !new HashSet<string>(actual.DependsOn, StringComparer.Ordinal).SetEquals(module.DependsOn))
                        throw ManagedExtensionJson.Error("AssemblyModuleDeclarationMismatch");
                }
                result.Add(new ManagedExtensionInspectedAssembly(declaration, frozen, metadata));
            }
            cancellationToken.ThrowIfCancellationRequested();
            return new ManagedExtensionInspectedPayload(manifest, result);
        }
    }
}
