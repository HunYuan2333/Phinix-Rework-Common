using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;

namespace Utils.Framework.ManagedExtensions
{
    public sealed class ManagedExtensionHostModule
    {
        public ManagedExtensionHostModule(string id, IEnumerable<string> dependencies)
        { Id=id ?? throw new ArgumentNullException(nameof(id)); DependsOn=ManagedExtensionCompatibility.Freeze(dependencies ?? throw new ArgumentNullException(nameof(dependencies))); }
        public string Id { get; }
        public ReadOnlyCollection<string> DependsOn { get; }
    }

    public sealed class ManagedExtensionHostFacts
    {
        public ManagedExtensionHostFacts(string gameVersion, string phinixVersion, string abstractionsVersion,
            IEnumerable<ManagedAssemblyIdentity> assemblies, IEnumerable<string> modules, IEnumerable<string> activeMods)
            : this(gameVersion, phinixVersion, abstractionsVersion, assemblies, modules, modules, activeMods) { }
        public ManagedExtensionHostFacts(string gameVersion, string phinixVersion, string abstractionsVersion,
            IEnumerable<ManagedAssemblyIdentity> assemblies, IEnumerable<string> modules, IEnumerable<string> availableModules, IEnumerable<string> activeMods, IEnumerable<ManagedExtensionHostModule> moduleDeclarations = null)
        {
            GameVersion = gameVersion ?? throw new ArgumentNullException(nameof(gameVersion));
            PhinixVersion = ManagedExtensionVersion.Parse(phinixVersion); AbstractionsVersion = ManagedExtensionVersion.Parse(abstractionsVersion);
            Assemblies = ManagedExtensionCompatibility.Freeze(assemblies ?? throw new ArgumentNullException(nameof(assemblies)));
            ModuleIds = ManagedExtensionCompatibility.Freeze(modules ?? throw new ArgumentNullException(nameof(modules)));
            AvailableModuleIds = ManagedExtensionCompatibility.Freeze(availableModules ?? throw new ArgumentNullException(nameof(availableModules)));
            ActiveModIds = ManagedExtensionCompatibility.Freeze(activeMods ?? throw new ArgumentNullException(nameof(activeMods)));
            ModuleDeclarations=ManagedExtensionCompatibility.Freeze(moduleDeclarations ?? new ManagedExtensionHostModule[0]);
        }
        public string GameVersion { get; }
        public ManagedExtensionVersion PhinixVersion { get; }
        public ManagedExtensionVersion AbstractionsVersion { get; }
        public ReadOnlyCollection<ManagedAssemblyIdentity> Assemblies { get; }
        public ReadOnlyCollection<string> ModuleIds { get; }
        public ReadOnlyCollection<string> AvailableModuleIds { get; }
        public ReadOnlyCollection<string> ActiveModIds { get; }
        public ReadOnlyCollection<ManagedExtensionHostModule> ModuleDeclarations { get; }
    }

    /// <summary>Safe structured audit data; no filesystem paths, endpoint URLs or exception text.</summary>
    public sealed class ManagedExtensionCandidateAudit
    {
        internal ManagedExtensionCandidateAudit(string startupId, ManagedExtensionPackageSnapshot package, string code)
        {
            StartupId = startupId; SourceId = package.SourceId; PackageId = package.PackageId; RecordKey = package.RecordKey;
            Version = package.Version; ManifestSha256 = package.ManifestSha256; CatalogSnapshotId = package.CatalogSnapshotId;
            CatalogSha256 = package.CatalogSha256; ArtifactSha256 = package.ArtifactSha256;
            InstallationTransactionId = package.InstallationTransactionId; StateOperationId = package.StateOperationId;
            Code = code ?? "CandidatePreflightPassed";
        }
        public string StartupId { get; }
        public string Stage => "preflight";
        public string SourceId { get; }
        public string PackageId { get; }
        public string RecordKey { get; }
        public string Version { get; }
        public string ManifestSha256 { get; }
        public string CatalogSnapshotId { get; }
        public string CatalogSha256 { get; }
        public string ArtifactSha256 { get; }
        public string InstallationTransactionId { get; }
        public string StateOperationId { get; }
        public string Code { get; }
    }

    public sealed class ManagedExtensionCandidateResult
    {
        internal ManagedExtensionCandidateResult(string startupId, ManagedExtensionPackageSnapshot package, string code, ManagedExtensionInspectedPayload payload,ManagedExtensionAssemblyReferenceFailure referenceFailure=null)
        { Package = package; DiagnosticCode = code; Payload = code == null ? payload : null; Audit = new ManagedExtensionCandidateAudit(startupId, package, code); ReferenceFailure=referenceFailure; }
        public ManagedExtensionCandidateAudit Audit { get; }
        public ManagedExtensionPackageSnapshot Package { get; }
        public string DiagnosticCode { get; }
        public ManagedExtensionAssemblyReferenceFailure ReferenceFailure { get; }
        /// <summary>Eligible for later startup recovery/resolution gates, not executable authorization.</summary>
        public ManagedExtensionInspectedPayload Payload { get; }
    }

    /// <summary>Pure preflight: no filesystem mutation, resolver installation, assembly loading or module construction.</summary>
    public static class ManagedExtensionCandidatePlanner
    {
        public static ReadOnlyCollection<ManagedExtensionCandidateResult> Plan(ManagedExtensionInventorySnapshot inventory,
            IReadOnlyDictionary<string, ManagedExtensionInspectedPayload> payloads, ManagedExtensionHostFacts host, string startupId, CancellationToken token)
        {
            if (inventory == null) throw new ArgumentNullException(nameof(inventory));
            if (payloads == null) throw new ArgumentNullException(nameof(payloads));
            if (host == null) throw new ArgumentNullException(nameof(host));
            token.ThrowIfCancellationRequested();
            if (startupId == null || !System.Text.RegularExpressions.Regex.IsMatch(startupId, @"\A[0-9a-f]{32}\z")) throw ManagedExtensionJson.Error("InvalidStartupId");
            var rows = inventory.Packages.ToList();
            var codes = new Dictionary<ManagedExtensionPackageSnapshot, string>();
            var inputs = new Dictionary<ManagedExtensionPackageSnapshot, ManagedExtensionInspectedPayload>();
            var referenceFailures=new Dictionary<ManagedExtensionPackageSnapshot,ManagedExtensionAssemblyReferenceFailure>();
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                string code = inventory.Diagnostics.Count != 0 ? "CandidateInventoryUncertain" :
                    row.DiagnosticCode ?? (row.ContentState != ManagedExtensionContentState.ContentVerified ? "CandidateContentInvalid" :
                    row.DesiredState != ManagedExtensionDesiredState.Enabled ? "CandidateNotEnabled" : null);
                ManagedExtensionInspectedPayload payload = null;
                if (code == null && (row.RecordKey == null || !payloads.TryGetValue(row.RecordKey, out payload) || payload == null)) code = "CandidateNotInspected";
                // Only the manifest actually inspected for this inventory row may supply executable bytes.
                if (code == null && !ReferenceEquals(payload.Manifest, row.Manifest)) code = "CandidateManifestMismatch";
                if (code == null)
                {
                    var c = row.Manifest.Compatibility;
                    if (!c.RimWorldVersions.Contains(host.GameVersion) || !c.PhinixRange.Contains(host.PhinixVersion) || !c.AbstractionsRange.Contains(host.AbstractionsVersion)) code = "CandidateHostIncompatible";
                    else if (row.Manifest.ExternalMods.Any(m => !host.ActiveModIds.Contains(m.PackageId, StringComparer.OrdinalIgnoreCase))) code = "CandidateExternalModMissing";
                }
                codes.Add(row, code); if (code == null) inputs.Add(row, payload);
            }
            var active = inputs.Keys.ToList();
            foreach (var row in active)
            {
                token.ThrowIfCancellationRequested();
                if (active.Count(other => other.PackageId == row.PackageId) != 1) codes[row] = "CandidatePackageConflict";
                else if (row.Manifest.Assemblies.SelectMany(ManagedExtensionManifestReader.AssemblyNames).Any(name =>
                    host.Assemblies.Any(h => string.Equals(h.Name, name, StringComparison.OrdinalIgnoreCase)) ||
                    active.Any(other => !ReferenceEquals(row, other) && other.Manifest.Assemblies.SelectMany(ManagedExtensionManifestReader.AssemblyNames).Contains(name, StringComparer.OrdinalIgnoreCase))))
                    codes[row] = "CandidateAssemblyConflict";
                else if (row.Manifest.Modules.Any(m => host.ModuleIds.Contains(m.Id, StringComparer.Ordinal) ||
                    active.Any(other => !ReferenceEquals(row, other) && other.Manifest.Modules.Any(n => m.Id == n.Id)))) codes[row] = "CandidateModuleConflict";
            }
            var edges = new Dictionary<ManagedExtensionPackageSnapshot, List<ManagedExtensionPackageSnapshot>>();
            foreach (var row in active)
            {
                token.ThrowIfCancellationRequested(); var providers = new List<ManagedExtensionPackageSnapshot>(); edges.Add(row, providers);
                if (codes[row] != null) continue;
                foreach (var dependency in row.Manifest.Dependencies)
                {
                    var matches = active.Where(p => p.PackageId == dependency.PackageId).ToList();
                    // An installed but disabled/damaged provider is not an absent optional package.
                    bool installed = rows.Any(p => p.PackageId == dependency.PackageId);
                    if (dependency.Optional && !installed) continue;
                    if (matches.Count != 1 || !dependency.VersionRange.Contains(matches[0].Manifest.Version))
                    { codes[row] = "CandidatePackageDependencyUnavailable"; break; }
                    providers.Add(matches[0]);
                }
                if (codes[row] != null) continue;
                foreach (var assembly in inputs[row].Assemblies)
                foreach (var reference in assembly.Metadata.References)
                {
                    var own = row.Manifest.Assemblies.Where(a => string.Equals(a.Name, reference.Name, StringComparison.OrdinalIgnoreCase)).Select(a => a.FullName);
                    var packageRefs = providers.SelectMany(p => p.Manifest.Assemblies).Where(a => string.Equals(a.Name, reference.Name, StringComparison.OrdinalIgnoreCase)).Select(a => a.FullName);
                    var hostRefs = host.Assemblies.Where(a => string.Equals(a.Name, reference.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    var ownedRefs = own.Concat(packageRefs).ToList();
                    // Owned/package references retain exact locks. Only declared host libraries
                    // may upgrade within one major; manifest compatibility remains independent.
                    bool satisfied = ownedRefs.Count != 0 ? ownedRefs.Count(n => n == reference.FullName) == 1 :
                        ManagedAssemblyIdentity.SelectHostReference(reference, hostRefs) != null;
                    if (!satisfied)
                    {
                        codes[row] = "CandidateAssemblyReferenceUnavailable";
                        if(!referenceFailures.ContainsKey(row)) referenceFailures.Add(row,new ManagedExtensionAssemblyReferenceFailure(assembly.Metadata.Identity.Name,reference,ownedRefs.Concat(hostRefs.Select(a=>a.FullName))));
                        break;
                    }
                }
                if (codes[row] != null) continue;
                foreach (var dependency in row.Manifest.Modules.SelectMany(m => m.DependsOn))
                {
                    if (row.Manifest.Modules.Any(m => m.Id == dependency) || host.AvailableModuleIds.Contains(dependency)) continue;
                    var matches = active.Where(p => p.Manifest.Modules.Any(m => m.Id == dependency)).ToList();
                    if (matches.Count != 1 || !providers.Contains(matches[0])) { codes[row] = "CandidateModuleDependencyUnavailable"; break; }
                }
            }
            // Cycle detection is bounded by the inventory limit, including transitive dependencies.
            foreach (var row in active)
                if (codes[row] == null && Reaches(row, row, edges, new HashSet<ManagedExtensionPackageSnapshot>(), token)) codes[row] = "CandidatePackageDependencyCycle";
            bool changed;
            do
            {
                token.ThrowIfCancellationRequested(); changed = false;
                foreach (var row in active)
                    if (codes[row] == null && edges[row].Any(p => codes[p] != null))
                    { codes[row] = "CandidateDependencyRejected"; changed = true; }
            } while (changed);
            token.ThrowIfCancellationRequested();
            return rows.Select(row => new ManagedExtensionCandidateResult(startupId, row, codes[row], inputs.ContainsKey(row) ? inputs[row] : null,referenceFailures.ContainsKey(row)?referenceFailures[row]:null)).ToList().AsReadOnly();
        }
        private static bool Reaches(ManagedExtensionPackageSnapshot target, ManagedExtensionPackageSnapshot current,
            Dictionary<ManagedExtensionPackageSnapshot, List<ManagedExtensionPackageSnapshot>> edges,
            HashSet<ManagedExtensionPackageSnapshot> visited, CancellationToken token)
        {
            // Explicit worklist avoids recursion depth depending on attacker-controlled package graphs.
            var pending = new Stack<ManagedExtensionPackageSnapshot>(); pending.Push(current);
            while (pending.Count != 0)
            {
                token.ThrowIfCancellationRequested(); var next = pending.Pop(); if (!visited.Add(next)) continue;
                foreach (var dependency in edges[next]) { if (ReferenceEquals(dependency, target)) return true; pending.Push(dependency); }
            }
            return false;
        }
    }
}
