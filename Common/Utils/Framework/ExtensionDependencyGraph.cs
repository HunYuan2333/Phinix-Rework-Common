using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Utils.Framework
{
    /// <summary>
    /// 扩展依赖关系图。在 DiscoverExtensions 实例化模块之前通过纯反射构建。
    /// 用于查询：禁用某扩展时，哪些扩展会受到影响。
    /// 设计哲学 §1.2：依赖图通过 ExtensionId 字符串匹配，不通过类型引用。
    /// 设计哲学 §3.5：构建失败仅记录 warning，不中断发现流程。
    /// </summary>
    public sealed class ExtensionDependencyGraph
    {
        // extensionId -> 它声明的依赖列表
        private readonly Dictionary<string, List<string>> _dependencies;
        // extensionId -> 依赖它的扩展列表（反向索引）
        private readonly Dictionary<string, List<string>> _dependents;
        // 未声明 DependsOn 的扩展 ID
        private readonly HashSet<string> _undeclared;
        // 构建过程中收集的警告（循环依赖等），不阻断
        private readonly List<string> _warnings;

        private ExtensionDependencyGraph(
            Dictionary<string, List<string>> dependencies,
            Dictionary<string, List<string>> dependents,
            HashSet<string> undeclared,
            List<string> warnings)
        {
            _dependencies = dependencies;
            _dependents = dependents;
            _undeclared = undeclared;
            _warnings = warnings;
        }

        /// <summary>
        /// 从候选模块类型列表构建依赖图。纯反射，不实例化任何模块。
        /// 设计哲学 §3.5：单个类型读取异常仅记录 warning，不中断构建。
        /// </summary>
        public static ExtensionDependencyGraph Build(IEnumerable<Type> moduleTypes)
        {
            Dictionary<string, List<string>> dependencies = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, List<string>> dependents = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> undeclared = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> warnings = new List<string>();

            if (moduleTypes == null)
            {
                return new ExtensionDependencyGraph(dependencies, dependents, undeclared, warnings);
            }

            foreach (Type moduleType in moduleTypes)
            {
                string extensionId;
                try
                {
                    PhinixExtensionAttribute attr = moduleType.GetCustomAttribute<PhinixExtensionAttribute>();
                    extensionId = attr?.ExtensionId ?? moduleType.Name;
                }
                catch (Exception ex)
                {
                    // §3.5 错误隔离：单个类型元数据读取失败不中断构建，但必须可观测
                    warnings.Add($"Failed to read extension ID from '{moduleType.FullName}': {ex.GetType().Name}: {ex.Message}");
                    continue;
                }

                string[] declaredDeps;
                try
                {
                    PhinixExtensionAttribute attr = moduleType.GetCustomAttribute<PhinixExtensionAttribute>();
                    declaredDeps = attr?.DependsOn ?? Array.Empty<string>();
                }
                catch (Exception ex)
                {
                    warnings.Add($"Failed to read DependsOn from '{moduleType.FullName}': {ex.GetType().Name}: {ex.Message}");
                    declaredDeps = Array.Empty<string>();
                }

                if (declaredDeps == null || declaredDeps.Length == 0)
                {
                    undeclared.Add(extensionId);
                    dependencies[extensionId] = new List<string>();
                }
                else
                {
                    List<string> deps = new List<string>(declaredDeps);
                    dependencies[extensionId] = deps;
                    foreach (string depId in deps)
                    {
                        if (!dependents.TryGetValue(depId, out List<string> dependentsList))
                        {
                            dependentsList = new List<string>();
                            dependents[depId] = dependentsList;
                        }
                        dependentsList.Add(extensionId);
                    }
                }
            }

            // 轻量循环依赖检测——仅记录 warning，不阻断（设计哲学 §1.3 非目标）
            detectCycles(dependencies, warnings);

            return new ExtensionDependencyGraph(dependencies, dependents, undeclared, warnings);
        }

        private static void detectCycles(Dictionary<string, List<string>> dependencies, List<string> warnings)
        {
            HashSet<string> visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            HashSet<string> inStack = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string nodeId in dependencies.Keys)
            {
                if (!visited.Contains(nodeId))
                {
                    detectCyclesDfs(nodeId, dependencies, visited, inStack, new List<string>(), warnings);
                }
            }
        }

        private static void detectCyclesDfs(string nodeId, Dictionary<string, List<string>> dependencies,
            HashSet<string> visited, HashSet<string> inStack, List<string> path, List<string> warnings)
        {
            visited.Add(nodeId);
            inStack.Add(nodeId);
            path.Add(nodeId);

            if (dependencies.TryGetValue(nodeId, out List<string> deps))
            {
                foreach (string depId in deps)
                {
                    if (inStack.Contains(depId))
                    {
                        warnings.Add($"Circular dependency detected involving '{depId}' (chain: {string.Join(" -> ", path)} -> {depId}).");
                    }
                    else if (!visited.Contains(depId) && dependencies.ContainsKey(depId))
                    {
                        detectCyclesDfs(depId, dependencies, visited, inStack, path, warnings);
                    }
                }
            }

            inStack.Remove(nodeId);
            path.RemoveAt(path.Count - 1);
        }

        /// <summary>
        /// 获取指定扩展依赖的所有扩展 ID（正向查询）。
        /// </summary>
        public IReadOnlyList<string> GetDependencies(string extensionId)
        {
            if (extensionId != null && _dependencies.TryGetValue(extensionId, out List<string> deps))
            {
                return deps;
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// 获取依赖指定扩展的所有扩展 ID（反向查询——谁依赖我）。
        /// </summary>
        public IReadOnlyList<string> GetDependents(string extensionId)
        {
            if (extensionId != null && _dependents.TryGetValue(extensionId, out List<string> dependentsList))
            {
                return dependentsList;
            }
            return Array.Empty<string>();
        }

        /// <summary>
        /// 给定一组被禁用的扩展 ID，返回指定扩展的依赖项中有多少被禁用。
        /// 用于 StaticActivationPolicy.ShouldActivate 判断。
        /// </summary>
        public IReadOnlyList<string> GetDisabledDependencies(
            string extensionId, IReadOnlyCollection<string> disabledSet)
        {
            if (extensionId == null || disabledSet == null || !_dependencies.TryGetValue(extensionId, out List<string> deps))
            {
                return Array.Empty<string>();
            }

            List<string> result = new List<string>();
            foreach (string depId in deps)
            {
                foreach (string disabledId in disabledSet)
                {
                    if (string.Equals(disabledId, depId, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Add(depId);
                        break;
                    }
                }
            }
            return result;
        }

        /// <summary>
        /// 该扩展是否未声明 DependsOn（老插件兼容提示用）。
        /// </summary>
        public bool IsUndeclared(string extensionId)
        {
            return extensionId != null && _undeclared.Contains(extensionId);
        }

        /// <summary>
        /// 所有未声明依赖关系的扩展 ID（用于 UI 批量提示）。
        /// </summary>
        public IReadOnlyCollection<string> UndeclaredExtensions => _undeclared;

        /// <summary>
        /// 构建过程中收集的警告（循环依赖等）。不阻断发现流程。
        /// </summary>
        public IReadOnlyList<string> BuildWarnings => _warnings;

        /// <summary>
        /// 图中所有已知的扩展 ID。
        /// </summary>
        public IReadOnlyCollection<string> KnownExtensionIds => _dependencies.Keys;
    }
}
