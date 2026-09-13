using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Utils.Framework
{
    /// <summary>
    /// Immutable extension dependency graph. It validates unique IDs, missing dependencies and
    /// cycles before modules are instantiated, and supplies a stable dependency-first order.
    /// </summary>
    public sealed class ExtensionDependencyGraph
    {
        private readonly Dictionary<string, List<string>> dependencies;
        private readonly Dictionary<string, List<string>> dependents;
        private readonly HashSet<string> undeclared;
        private readonly List<string> warnings;
        private readonly Dictionary<string, string> validationErrors;
        private readonly Dictionary<Type, int> sourceOrder;

        private ExtensionDependencyGraph(Dictionary<string, List<string>> dependencies,
            Dictionary<string, List<string>> dependents, HashSet<string> undeclared,
            List<string> warnings, Dictionary<string, string> validationErrors,
            Dictionary<Type, int> sourceOrder)
        {
            this.dependencies = dependencies;
            this.dependents = dependents;
            this.undeclared = undeclared;
            this.warnings = warnings;
            this.validationErrors = validationErrors;
            this.sourceOrder = sourceOrder;
        }

        public static ExtensionDependencyGraph Build(IEnumerable<Type> moduleTypes)
        {
            Dictionary<string, List<string>> dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> undeclared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> warnings = new List<string>();
            Dictionary<string, string> errors = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            Dictionary<Type, int> order = new Dictionary<Type, int>();
            Dictionary<string, List<Type>> typesById = new Dictionary<string, List<Type>>(StringComparer.OrdinalIgnoreCase);
            List<Type> types = (moduleTypes ?? Enumerable.Empty<Type>()).Where(type => type != null)
                .OrderBy(type => type.FullName, StringComparer.Ordinal).ToList();

            for (int index = 0; index < types.Count; index++)
            {
                Type moduleType = types[index];
                order[moduleType] = index;
                try
                {
                    PhinixExtensionAttribute attribute = moduleType.GetCustomAttribute<PhinixExtensionAttribute>();
                    string extensionId = attribute?.ExtensionId ?? moduleType.Name;
                    if (!typesById.TryGetValue(extensionId, out List<Type> matchingTypes))
                    {
                        matchingTypes = new List<Type>();
                        typesById[extensionId] = matchingTypes;
                    }
                    matchingTypes.Add(moduleType);
                    List<string> declared = (attribute?.DependsOn ?? Array.Empty<string>())
                        .Where(id => !string.IsNullOrWhiteSpace(id))
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    dependencies[extensionId] = declared;
                    if (declared.Count == 0) undeclared.Add(extensionId);
                }
                catch (Exception exception)
                {
                    string fallbackId = moduleType.Name;
                    errors[fallbackId] = "Failed to read extension dependency metadata: " + exception.Message;
                    warnings.Add($"Failed to read extension metadata from '{moduleType.FullName}': {exception.GetType().Name}: {exception.Message}");
                }
            }

            foreach (KeyValuePair<string, List<Type>> entry in typesById)
            {
                if (entry.Value.Count <= 1) continue;
                string message = $"Duplicate extension ID '{entry.Key}' is declared by {entry.Value.Count} modules.";
                errors[entry.Key] = message;
                warnings.Add(message);
            }

            foreach (KeyValuePair<string, List<string>> entry in dependencies)
            {
                foreach (string dependencyId in entry.Value)
                {
                    if (!dependents.TryGetValue(dependencyId, out List<string> dependentIds))
                    {
                        dependentIds = new List<string>();
                        dependents[dependencyId] = dependentIds;
                    }
                    dependentIds.Add(entry.Key);
                    if (dependencies.ContainsKey(dependencyId)) continue;
                    string message = $"Extension '{entry.Key}' requires missing extension '{dependencyId}'.";
                    errors[entry.Key] = message;
                    warnings.Add(message);
                }
            }

            detectCycles(dependencies, errors, warnings);
            propagateInvalidDependencies(dependencies, errors, warnings);
            return new ExtensionDependencyGraph(dependencies, dependents, undeclared, warnings, errors, order);
        }

        private static void detectCycles(Dictionary<string, List<string>> dependencies,
            Dictionary<string, string> errors, List<string> warnings)
        {
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> stack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> path = new List<string>();
            foreach (string extensionId in dependencies.Keys.OrderBy(id => id, StringComparer.OrdinalIgnoreCase))
                detectCyclesDfs(extensionId, dependencies, visited, stack, path, errors, warnings);
        }

        private static void detectCyclesDfs(string extensionId, Dictionary<string, List<string>> dependencies,
            HashSet<string> visited, HashSet<string> stack, List<string> path,
            Dictionary<string, string> errors, List<string> warnings)
        {
            if (visited.Contains(extensionId)) return;
            visited.Add(extensionId);
            stack.Add(extensionId);
            path.Add(extensionId);
            foreach (string dependencyId in dependencies[extensionId])
            {
                if (!dependencies.ContainsKey(dependencyId)) continue;
                if (stack.Contains(dependencyId))
                {
                    int start = path.FindIndex(id => string.Equals(id, dependencyId, StringComparison.OrdinalIgnoreCase));
                    List<string> cycle = path.Skip(start).ToList();
                    string message = "Circular dependency detected: " + string.Join(" -> ", cycle) + " -> " + dependencyId + ".";
                    foreach (string cycleId in cycle) errors[cycleId] = message;
                    if (!warnings.Contains(message)) warnings.Add(message);
                }
                else detectCyclesDfs(dependencyId, dependencies, visited, stack, path, errors, warnings);
            }
            path.RemoveAt(path.Count - 1);
            stack.Remove(extensionId);
        }

        private static void propagateInvalidDependencies(Dictionary<string, List<string>> dependencies,
            Dictionary<string, string> errors, List<string> warnings)
        {
            bool changed;
            do
            {
                changed = false;
                foreach (KeyValuePair<string, List<string>> entry in dependencies)
                {
                    if (errors.ContainsKey(entry.Key)) continue;
                    string invalidDependency = entry.Value.FirstOrDefault(errors.ContainsKey);
                    if (invalidDependency == null) continue;
                    string message = $"Extension '{entry.Key}' cannot load because dependency '{invalidDependency}' is invalid.";
                    errors[entry.Key] = message;
                    warnings.Add(message);
                    changed = true;
                }
            }
            while (changed);
        }

        public IReadOnlyList<Type> GetStableTopologicalOrder(IEnumerable<Type> moduleTypes)
        {
            List<Type> types = (moduleTypes ?? Enumerable.Empty<Type>()).Where(type => type != null)
                .Where(type => !TryGetValidationError(getExtensionId(type), out _))
                .OrderBy(type => sourceOrder.TryGetValue(type, out int index) ? index : int.MaxValue).ToList();
            Dictionary<string, Type> typeById = types.ToDictionary(getExtensionId, StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> indegree = types.ToDictionary(getExtensionId, type => 0, StringComparer.OrdinalIgnoreCase);
            foreach (Type type in types)
            {
                string extensionId = getExtensionId(type);
                indegree[extensionId] = dependencies[extensionId].Count(typeById.ContainsKey);
            }

            List<Type> result = new List<Type>();
            while (result.Count < types.Count)
            {
                Type next = types.FirstOrDefault(type => !result.Contains(type) && indegree[getExtensionId(type)] == 0);
                if (next == null) break;
                result.Add(next);
                if (!dependents.TryGetValue(getExtensionId(next), out List<string> dependentIds)) continue;
                foreach (string dependentId in dependentIds)
                    if (indegree.ContainsKey(dependentId)) indegree[dependentId]--;
            }
            return result;
        }

        private static string getExtensionId(Type type)
        {
            PhinixExtensionAttribute attribute = type.GetCustomAttribute<PhinixExtensionAttribute>();
            return attribute?.ExtensionId ?? type.Name;
        }

        public bool TryGetValidationError(string extensionId, out string error)
        {
            if (extensionId != null) return validationErrors.TryGetValue(extensionId, out error);
            error = null;
            return false;
        }

        public IReadOnlyList<string> GetDependencies(string extensionId) =>
            extensionId != null && dependencies.TryGetValue(extensionId, out List<string> values) ? values : (IReadOnlyList<string>)Array.Empty<string>();
        public IReadOnlyList<string> GetDependents(string extensionId) =>
            extensionId != null && dependents.TryGetValue(extensionId, out List<string> values) ? values : (IReadOnlyList<string>)Array.Empty<string>();
        public IReadOnlyList<string> GetDisabledDependencies(string extensionId, IReadOnlyCollection<string> disabledSet)
        {
            if (extensionId == null || disabledSet == null || !dependencies.TryGetValue(extensionId, out List<string> values)) return Array.Empty<string>();
            return values.Where(dependencyId => disabledSet.Any(disabledId =>
                string.Equals(disabledId, dependencyId, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        public bool IsUndeclared(string extensionId) => extensionId != null && undeclared.Contains(extensionId);
        public IReadOnlyCollection<string> UndeclaredExtensions => undeclared;
        public IReadOnlyList<string> BuildWarnings => warnings;
        public IReadOnlyCollection<string> KnownExtensionIds => dependencies.Keys;
    }
}
