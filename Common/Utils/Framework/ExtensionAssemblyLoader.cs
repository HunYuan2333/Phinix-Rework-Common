using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Utils.Framework
{
    public static class ExtensionAssemblyLoader
    {
        public static void LoadAssemblies(IEnumerable<string> probeDirectories, Action<string, LogLevel> log = null)
        {
            foreach (string probeDirectory in (probeDirectories ?? Array.Empty<string>())
                .Where(path => !string.IsNullOrWhiteSpace(path))
                .Select(Path.GetFullPath)
                .Distinct(StringComparer.OrdinalIgnoreCase))
            {
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

            try
            {
                Assembly.Load(assemblyName);
                log?.Invoke($"Loaded extension assembly '{assemblyName.Name}' via Assembly.Load.", LogLevel.DEBUG);
                return;
            }
            catch (Exception firstException)
            {
                try
                {
                    Assembly.LoadFrom(assemblyPath);
                    log?.Invoke($"Loaded extension assembly '{assemblyName.Name}' from '{assemblyPath}'.", LogLevel.DEBUG);
                }
                catch (Exception secondException)
                {
                    log?.Invoke(
                        $"Failed to load extension assembly '{assemblyPath}'. " +
                        $"Assembly.Load error: {firstException.Message}. Assembly.LoadFrom error: {secondException.Message}",
                        LogLevel.WARNING);
                }
            }
        }
    }
}
