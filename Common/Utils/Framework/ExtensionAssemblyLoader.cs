using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Utils.Framework
{
    public static class ExtensionAssemblyLoader
    {
        private static readonly HashSet<string> probeDirectoriesStore = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static bool resolveHandlerWired;

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
                AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
                {
                    AssemblyName requestedName = new AssemblyName(args.Name);
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
