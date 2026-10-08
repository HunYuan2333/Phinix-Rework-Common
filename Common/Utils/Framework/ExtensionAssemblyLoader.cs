using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Utils.Framework
{
    public static class ExtensionAssemblyLoader
    {
        /// <summary>Loads an explicit host-owned directory set. The resolver never handles
        /// foreign requesters or probes files added after preparation. Dispose detaches it;
        /// already loaded assemblies remain subject to normal CLR lifetime rules.</summary>
        public static IDisposable LoadOwnedAssemblies(IEnumerable<string> directories, Action<string, LogLevel> log = null)
        {
            var scope = new OwnedLoader(directories, log);
            try { scope.Start(); return scope; }
            catch { scope.Dispose(); throw; }
        }

        private sealed class OwnedLoader : IDisposable
        {
            private sealed class Candidate
            {
                internal string Path;
                internal AssemblyName Identity;
            }
            private readonly List<Candidate> candidates = new List<Candidate>();
            private readonly HashSet<string> paths;
            private readonly Action<string, LogLevel> log;
            private bool disposed;
            internal OwnedLoader(IEnumerable<string> directories, Action<string, LogLevel> log)
            {
                this.log = log;
                paths = new HashSet<string>(Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (string directory in (directories ?? Array.Empty<string>()).Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath).Distinct(paths.Comparer))
                {
                    if (!Directory.Exists(directory)) continue;
                    foreach (string file in Directory.EnumerateFiles(directory, "*.dll", SearchOption.TopDirectoryOnly).OrderBy(p => p, StringComparer.Ordinal))
                    {
                        try
                        {
                            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0) throw new IOException("Linked assembly file is not an owned candidate.");
                            var name = AssemblyName.GetAssemblyName(file);
                            if (paths.Add(Path.GetFullPath(file))) candidates.Add(new Candidate { Path = Path.GetFullPath(file), Identity = name });
                        }
                        catch (Exception ex) { Report("Skipped owned assembly candidate '" + file + "': " + ex.Message, LogLevel.WARNING); }
                    }
                }
                foreach (var group in candidates.GroupBy(c => c.Identity.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToArray())
                {
                    Report("Ambiguous owned assembly name: " + group.Key, LogLevel.WARNING);
                    foreach (var candidate in group) { candidates.Remove(candidate); paths.Remove(candidate.Path); }
                }
            }
            private void Report(string message, LogLevel level)
            { try { log?.Invoke(message, level); } catch { } }
            internal void Start()
            {
                // Install before the first LoadFrom so initial dependency requests are scoped too.
                AppDomain.CurrentDomain.AssemblyResolve += Resolve;
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var loaded = AppDomain.CurrentDomain.GetAssemblies().Where(a => !a.IsDynamic && string.Equals(a.GetName().Name, candidate.Identity.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (loaded.Length != 0)
                        {
                            if (loaded.Length != 1 || loaded[0].GetName().FullName != candidate.Identity.FullName)
                                Report("Owned assembly identity conflict: " + candidate.Identity.FullName, LogLevel.WARNING);
                            continue;
                        }
                        Load(candidate);
                    }
                    catch (Exception ex) { Report("Owned assembly load failed: " + candidate.Identity.FullName + ": " + ex.Message, LogLevel.WARNING); }
                }
            }
            private Assembly Load(Candidate candidate)
            {
                // Recheck identity; never substitute another identity via Assembly.Load fallback.
                if (AssemblyName.GetAssemblyName(candidate.Path).FullName != candidate.Identity.FullName)
                    throw new IOException("Owned assembly identity changed during loading.");
                var assembly = Assembly.LoadFrom(candidate.Path);
                if (assembly.GetName().FullName != candidate.Identity.FullName) throw new IOException("Loaded assembly identity differs from the candidate.");
                return assembly;
            }
            private Assembly Resolve(object sender, ResolveEventArgs args)
            {
                if (disposed || args.RequestingAssembly == null || args.RequestingAssembly.IsDynamic) return null;
                string requester;
                try { requester = args.RequestingAssembly.Location; }
                catch (NotSupportedException) { return null; }
                if (string.IsNullOrEmpty(requester) || !paths.Contains(Path.GetFullPath(requester))) return null;
                if (!args.RequestingAssembly.GetReferencedAssemblies().Any(r => r.FullName == args.Name)) return null;
                var candidate = candidates.SingleOrDefault(c => c.Identity.FullName == args.Name);
                if (candidate == null) return null;
                try { return Load(candidate); }
                catch (Exception ex) { Report("Owned dependency resolution failed: " + args.Name + ": " + ex.Message, LogLevel.WARNING); return null; }
            }
            public void Dispose()
            {
                if (disposed) return;
                disposed = true;
                AppDomain.CurrentDomain.AssemblyResolve -= Resolve;
            }
        }

        private static readonly HashSet<string> probeDirectoriesStore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, string> assemblyFileCache = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static readonly object assemblyFileCacheLock = new object();
        private static bool resolveHandlerWired;
        private static readonly Dictionary<Guid, Func<ResolveEventArgs, bool>> resolutionGuards = new Dictionary<Guid, Func<ResolveEventArgs, bool>>();
        public static IDisposable RegisterResolutionGuard(Func<ResolveEventArgs, bool> guard)
        {
            if (guard == null) throw new ArgumentNullException(nameof(guard));
            var id = Guid.NewGuid(); lock (assemblyFileCacheLock) resolutionGuards.Add(id, guard); return new GuardLease(id);
        }
        private sealed class GuardLease : IDisposable
        {
            private readonly Guid id;
            internal GuardLease(Guid id) { this.id = id; }
            public void Dispose() { lock (assemblyFileCacheLock) resolutionGuards.Remove(id); }
        }


        private static void RebuildAssemblyFileCache()
        {
            lock (assemblyFileCacheLock)
            {
                assemblyFileCache.Clear();
                foreach (string probeDir in probeDirectoriesStore)
                {
                    if (!Directory.Exists(probeDir)) continue;
                    foreach (string existingPath in Directory.EnumerateFiles(probeDir, "*.dll", SearchOption.TopDirectoryOnly))
                    {
                        string existingName = Path.GetFileNameWithoutExtension(existingPath);
                        // Also index by stripped name (e.g. "Utils" from "03-Utils.dll")
                        int dashIndex = existingName.IndexOf('-');
                        string baseName = dashIndex > 0 && dashIndex <= 3 && existingName.Substring(0, dashIndex).All(char.IsDigit)
                            ? existingName.Substring(dashIndex + 1)
                            : null;
                        if (baseName != null)
                            assemblyFileCache[baseName] = existingPath;
                        assemblyFileCache[existingName] = existingPath;
                    }
                }
            }
        }

        private static bool TryGetCachedAssemblyPath(string assemblyName, out string path)
        {
            lock (assemblyFileCacheLock)
            {
                return assemblyFileCache.TryGetValue(assemblyName, out path);
            }
        }

        public static void LoadAssemblies(IEnumerable<string> probeDirectories, Action<string, LogLevel> log = null)
        {
            foreach (string probeDirectory in (probeDirectories ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
                probeDirectoriesStore.Add(probeDirectory);

                if (!Directory.Exists(probeDirectory))
                {
                    log?.Invoke($"Skipped extension probe directory '{probeDirectory}' because it does not exist.", LogLevel.DEBUG);
                    continue;
                }

                foreach (string assemblyPath in Directory.EnumerateFiles(probeDirectory, "*.dll", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
                {
                    tryLoadAssembly(assemblyPath, log);
                }
            }

            if (!resolveHandlerWired)
            {
                resolveHandlerWired = true;
                RebuildAssemblyFileCache();
                AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                {
                    Func<ResolveEventArgs, bool>[] guards;
                    lock (assemblyFileCacheLock) guards = resolutionGuards.Values.ToArray();
                    if (guards.Any(guard => !guard(args))) return null;
                    AssemblyName requestedName = new AssemblyName(args.Name);

                    // Fast path: check the cached file index first (O(1) dictionary lookup)
                    if (TryGetCachedAssemblyPath(requestedName.Name, out string cachedPath) && File.Exists(cachedPath))
                    {
                        log?.Invoke($"AssemblyResolve: loading '{requestedName.Name}' from '{cachedPath}' (cached).", LogLevel.DEBUG);
                        try { return Assembly.LoadFrom(cachedPath); }
                        catch (Exception ex)
                        {
                            log?.Invoke($"AssemblyResolve: failed to load '{cachedPath}': {ex.Message}", LogLevel.WARNING);
                        }
                    }

                    // Slow path: probe directory scan (fallback if cache miss due to late-added DLLs)
                    foreach (string probeDir in probeDirectoriesStore)
                    {
                        if (!Directory.Exists(probeDir)) continue;

                        string candidatePath = Path.Combine(probeDir, requestedName.Name + ".dll");
                        if (File.Exists(candidatePath))
                        {
                            log?.Invoke($"AssemblyResolve: loading '{requestedName.Name}' from '{candidatePath}'.", LogLevel.DEBUG);
                            try { return Assembly.LoadFrom(candidatePath); }
                            catch (Exception ex)
                            {
                                log?.Invoke($"AssemblyResolve: failed to load '{candidatePath}': {ex.Message}", LogLevel.WARNING);
                            }
                        }

                        // Also try prefixed files like "03-Utils.dll"
                        foreach (string existingPath in Directory.EnumerateFiles(probeDir, "*.dll", SearchOption.TopDirectoryOnly))
                        {
                            string existingName = Path.GetFileNameWithoutExtension(existingPath);
                            // Strip numeric prefix like "03-" to get base name
                            int dashIndex = existingName.IndexOf('-');
                            string baseName = dashIndex > 0 && dashIndex <= 3 && existingName.Substring(0, dashIndex).All(char.IsDigit)
                                ? existingName.Substring(dashIndex + 1)
                                : existingName;

                            if (string.Equals(baseName, requestedName.Name, StringComparison.OrdinalIgnoreCase))
                            {
                                log?.Invoke($"AssemblyResolve: loading '{requestedName.Name}' from '{existingPath}' (matched via prefixed file).", LogLevel.DEBUG);
                                try { return Assembly.LoadFrom(existingPath); }
                                catch (Exception ex)
                                {
                                    log?.Invoke($"AssemblyResolve: failed to load '{existingPath}': {ex.Message}", LogLevel.WARNING);
                                }
                            }
                        }
                    }

                    return null;
                };
            }
        }

        private static void tryLoadAssembly(string assemblyPath, Action<string, LogLevel> log)
        {
            AssemblyName assemblyName;
            try
            {
                assemblyName = AssemblyName.GetAssemblyName(assemblyPath);
            }
            catch (Exception exception)
            {
                log?.Invoke($"Skipped extension assembly candidate '{assemblyPath}': {exception.Message}", LogLevel.DEBUG);
                return;
            }

            if (AppDomain.CurrentDomain
                .GetAssemblies()
                .Any(assembly => string.Equals(assembly.GetName().Name, assemblyName.Name, StringComparison.OrdinalIgnoreCase)))
            {
                return;
            }

            // LoadFrom first so all extension assemblies land in the same LoadFrom
            // context. If Assembly.Load succeeds first (via RimWorld's global
            // AssemblyResolve handler), the assembly ends up in the Load context
            // and can't see its dependencies that were loaded via LoadFrom.
            try
            {
                Assembly.LoadFrom(assemblyPath);
                log?.Invoke($"Loaded extension assembly '{assemblyName.Name}' from '{assemblyPath}'.", LogLevel.DEBUG);
                return;
            }
            catch (Exception loadFromException)
            {
                try
                {
                    Assembly.Load(assemblyName);
                    log?.Invoke($"Loaded extension assembly '{assemblyName.Name}' via Assembly.Load (LoadFrom fallback).", LogLevel.DEBUG);
                }
                catch (Exception loadException)
                {
                    log?.Invoke(
                        $"Failed to load extension assembly '{assemblyPath}'. " +
                        $"Assembly.LoadFrom error: {loadFromException.Message}. Assembly.Load error: {loadException.Message}",
                        LogLevel.WARNING);
                }
            }
        }
    }
}
