using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Utils.Framework
{
    public static class PhinixExtensionRegistry
    {
        /// <summary>
        /// 扫描已加载程序集中的候选扩展模块类型（IPhinixExtensionModule 可分配、有无参构造），
        /// 按 Type.FullName 字母序排序。纯反射，不实例化任何模块。
        /// 设计哲学 §4.7：供 host 在 DiscoverExtensions 之前构建依赖图和激活策略。
        /// </summary>
        public static List<Type> ScanCandidateModuleTypes()
        {
            return scanCandidateTypes()
                .Where(type => typeof(IPhinixExtensionModule).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();
        }

        public static DiscoveredPhinixExtensions DiscoverExtensions(ExtensionHostContext hostContext = null)
        {
            return DiscoverExtensions(hostContext, activationPolicy: null);
        }

        /// <summary>
        /// 带激活策略的扩展发现重载。policy 为 null 时尝试从 hostContext 服务容器解析；
        /// 仍为 null 则全部激活（行为与原签名一致——向后兼容）。
        /// 设计哲学 §6：保留原签名，新增重载，不破坏既有调用点。
        /// </summary>
        public static DiscoveredPhinixExtensions DiscoverExtensions(
            ExtensionHostContext hostContext,
            IExtensionActivationPolicy activationPolicy)
        {
            hostContext = hostContext ?? ExtensionHostContext.Empty;
            DiscoveredPhinixExtensions discovered = new DiscoveredPhinixExtensions();
            ExtensionApiRegistry apiRegistry = hostContext.ApiRegistry as ExtensionApiRegistry ?? new ExtensionApiRegistry();
            hostContext.ApiRegistry = apiRegistry;
            discovered.ApiRegistry = apiRegistry;
            HashSet<string> seenExtensionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<Type> candidateTypes = scanCandidateTypes();

            List<Type> moduleTypes = candidateTypes
                .Where(type => typeof(IPhinixExtensionModule).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            // 构建依赖图（纯反射，不实例化模块）——供 UI 展示依赖关系和禁用影响
            ExtensionDependencyGraph dependencyGraph = ExtensionDependencyGraph.Build(moduleTypes);
            discovered.DependencyGraph = dependencyGraph;
            foreach (string graphWarning in dependencyGraph.BuildWarnings)
            {
                discovered.Warnings.Add(graphWarning);
            }

            // 如果未显式提供 policy，从 hostContext 服务容器解析
            if (activationPolicy == null)
            {
                hostContext.TryGetService<IExtensionActivationPolicy>(out activationPolicy);
            }

            foreach (Type moduleType in moduleTypes)
            {
                ExtensionDiscoveryResult result = ExtensionDiscoveryResult.FromModuleType(moduleType, hostContext);

                // 在实例化之前从特性读取 ExtensionId 和 DependsOn（被禁用的模块不会实例化）
                PhinixExtensionAttribute attr = moduleType.GetCustomAttribute<PhinixExtensionAttribute>();
                string extensionId = attr?.ExtensionId ?? moduleType.Name;
                result.ExtensionId = extensionId;
                if (attr?.DependsOn != null && attr.DependsOn.Length > 0)
                {
                    result.DependsOn = new List<string>(attr.DependsOn);
                }

                // 咨询激活策略——被禁用的扩展不实例化，但仍创建 ExtensionResult 供 UI 展示
                if (activationPolicy != null && !activationPolicy.ShouldActivate(extensionId, out string reason))
                {
                    bool isDirectlyDisabled = false;
                    foreach (string disabledId in activationPolicy.DisabledExtensions)
                    {
                        if (string.Equals(disabledId, extensionId, StringComparison.OrdinalIgnoreCase))
                        {
                            isDirectlyDisabled = true;
                            break;
                        }
                    }
                    result.State = isDirectlyDisabled
                        ? ExtensionModuleState.Disabled
                        : ExtensionModuleState.DependencyDisabled;
                    result.StateDetail = isDirectlyDisabled
                        ? $"{reason} Enable it in Phinix mod settings → Extensions to activate its features."
                        : $"{reason} (a required dependency is missing or disabled, so this extension will not run).";
                    discovered.ExtensionResults.Add(result);
                    discovered.Diagnostics.Add(
                        $"Extension '{extensionId}' will NOT be loaded: {result.StateDetail}");
                    continue;
                }

                IPhinixExtensionModule module;
                try
                {
                    module = Activator.CreateInstance(moduleType) as IPhinixExtensionModule;
                }
                catch (Exception exception)
                {
                    result.State = ExtensionModuleState.Failed;
                    result.StateDetail = $"FAILED to initialize: {exception.Message}";
                    discovered.ExtensionResults.Add(result);
                    discovered.Warnings.Add($"Extension '{extensionId}' FAILED to load (this is a bug, please report it): {exception.Message}");
                    continue;
                }

                if (module == null)
                {
                    result.State = ExtensionModuleState.Failed;
                    result.StateDetail = "FAILED to load: Activator.CreateInstance returned null (type is not IPhinixExtensionModule).";
                    discovered.ExtensionResults.Add(result);
                    continue;
                }

                result.ExtensionId = module.ExtensionId;

                ExtensionBuilder builder = new ExtensionBuilder(module.ExtensionId, hostContext, discovered, apiRegistry, result);
                if (!seenExtensionIds.Add(module.ExtensionId))
                {
                    discovered.Warnings.Add($"Duplicate extension ID '{module.ExtensionId}' discovered in '{moduleType.FullName}'.");
                }

                discovered.Extensions.Add(module);
                discovered.Modules.Add(module);

                try
                {
                    module.Register(builder);
                    result.State = ExtensionModuleState.Registered;
                    discovered.Diagnostics.Add(
                        $"Framework module '{module.ExtensionId}' registered from '{moduleType.FullName}' " +
                        $"for host '{hostContext.HostKind ?? "unknown"}'.");
                }
                catch (Exception exception)
                {
                    result.State = ExtensionModuleState.Failed;
                    result.StateDetail = $"FAILED to register: {exception.Message}";
                    discovered.Warnings.Add($"Extension '{module.ExtensionId}' FAILED to register (this is a bug, please report it): {exception.Message}");
                }

                discovered.ExtensionResults.Add(result);
            }

            List<Type> legacyComponentTypes = candidateTypes
                .Where(type => !typeof(IPhinixExtensionModule).IsAssignableFrom(type))
                .Where(isLegacyDiscoverableType)
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            foreach (Type legacyComponentType in legacyComponentTypes)
            {
                object instance;
                try
                {
                    instance = Activator.CreateInstance(legacyComponentType);
                }
                catch (Exception exception)
                {
                    discovered.Warnings.Add($"Failed to initialize legacy extension component '{legacyComponentType.FullName}': {exception.Message}");
                    continue;
                }

                bool registeredLegacyComponent = false;
                if (instance is IPhinixExtension extension)
                {
                    if (!seenExtensionIds.Add(extension.ExtensionId))
                    {
                        discovered.Warnings.Add($"Duplicate extension ID '{extension.ExtensionId}' discovered in '{legacyComponentType.FullName}'.");
                    }

                    discovered.Extensions.Add(extension);
                    registeredLegacyComponent = true;
                }
                if (instance is ICapabilityProvider capabilityProvider)
                {
                    discovered.CapabilityProviders.Add(capabilityProvider);
                    registeredLegacyComponent = true;
                }
                if (instance is IMessageInterceptor interceptor)
                {
                    discovered.MessageInterceptors.Add(interceptor);
                    registeredLegacyComponent = true;
                }
                if (instance is IMessageRenderer renderer)
                {
                    discovered.MessageRenderers.Add(renderer);
                    registeredLegacyComponent = true;
                }
                if (instance is IClientMessageHandler clientHandler)
                {
                    discovered.ClientMessageHandlers.Add(clientHandler);
                    registeredLegacyComponent = true;
                }
                if (instance is IServerMessageHandler serverHandler)
                {
                    discovered.ServerMessageHandlers.Add(serverHandler);
                    if (instance is IServerDefaultMessageHandler defaultMessageHandler)
                    {
                        discovered.ServerDefaultMessageHandlers.Add(defaultMessageHandler);
                    }
                    else
                    {
                        discovered.ServerDefaultMessageHandlers.Add(new LegacyServerDefaultMessageHandlerAdapter(serverHandler));
                    }
                    registeredLegacyComponent = true;
                }
                if (instance is IServerMessageObserver messageObserver)
                {
                    discovered.ServerMessageObservers.Add(messageObserver);
                    registeredLegacyComponent = true;
                }
                if (instance is IClientCommandHandler clientCommandHandler)
                {
                    discovered.ClientCommandHandlers.Add(clientCommandHandler);
                    registeredLegacyComponent = true;
                }
                if (instance is IServerCommandHandler serverCommandHandler)
                {
                    discovered.ServerCommandHandlers.Add(serverCommandHandler);
                    if (instance is IServerDefaultCommandHandler defaultCommandHandler)
                    {
                        discovered.ServerDefaultCommandHandlers.Add(defaultCommandHandler);
                    }
                    else
                    {
                        discovered.ServerDefaultCommandHandlers.Add(new LegacyServerDefaultCommandHandlerAdapter(serverCommandHandler));
                    }
                    registeredLegacyComponent = true;
                }
                if (instance is IServerCommandObserver commandObserver)
                {
                    discovered.ServerCommandObservers.Add(commandObserver);
                    registeredLegacyComponent = true;
                }
                if (instance is IServerInboundMessageInterceptor inboundMessageInterceptor)
                {
                    discovered.ServerInboundMessageInterceptors.Add(inboundMessageInterceptor);
                    registeredLegacyComponent = true;
                }
                if (instance is IServerInboundCommandInterceptor inboundCommandInterceptor)
                {
                    discovered.ServerInboundCommandInterceptors.Add(inboundCommandInterceptor);
                    registeredLegacyComponent = true;
                }
                if (instance is IServerOutboundPacketInterceptor outboundPacketInterceptor)
                {
                    discovered.ServerOutboundPacketInterceptors.Add(outboundPacketInterceptor);
                    registeredLegacyComponent = true;
                }
                if (instance is IItemCodec itemCodec)
                {
                    discovered.ItemCodecs.Add(itemCodec);
                    registeredLegacyComponent = true;
                }
                if (instance is IClientIncomingItemHandler clientItemHandler)
                {
                    discovered.ClientIncomingItemHandlers.Add(clientItemHandler);
                    registeredLegacyComponent = true;
                }
                if (instance is IClientOutgoingItemHandler clientOutgoingItemHandler)
                {
                    discovered.ClientOutgoingItemHandlers.Add(clientOutgoingItemHandler);
                    registeredLegacyComponent = true;
                }
                if (instance is IServerInboundItemInterceptor inboundItemInterceptor)
                {
                    discovered.ServerInboundItemInterceptors.Add(inboundItemInterceptor);
                    registeredLegacyComponent = true;
                }
                if (instance is IServerItemHandler serverItemHandler)
                {
                    discovered.ServerItemHandlers.Add(serverItemHandler);
                    if (serverItemHandler is IServerDefaultItemHandler defaultItemHandler)
                    {
                        discovered.ServerDefaultItemHandlers.Add(defaultItemHandler);
                    }
                    else
                    {
                        discovered.ServerDefaultItemHandlers.Add(new LegacyServerDefaultItemHandlerAdapter(serverItemHandler));
                    }
                    registeredLegacyComponent = true;
                }
                if (instance is IServerItemObserver itemObserver)
                {
                    discovered.ServerItemObservers.Add(itemObserver);
                    registeredLegacyComponent = true;
                }

                if (registeredLegacyComponent)
                {
                    discovered.Warnings.Add(
                        $"Framework auto-discovered legacy extension component '{legacyComponentType.FullName}'. " +
                        "Migrate it to IPhinixExtensionModule so registration stays module-first.");
                }
            }

            discovered.MessageInterceptors.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ClientMessageHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerMessageHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerInboundMessageInterceptors.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerDefaultMessageHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerMessageObservers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ClientCommandHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerCommandHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerInboundCommandInterceptors.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerDefaultCommandHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerCommandObservers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerOutboundPacketInterceptors.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ClientIncomingItemHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ClientOutgoingItemHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerItemHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerInboundItemInterceptors.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerDefaultItemHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerItemObservers.Sort((left, right) => left.Priority.CompareTo(right.Priority));

            return discovered;
        }

        public static void ActivateExtensions(DiscoveredPhinixExtensions discovered, ExtensionHostContext hostContext = null)
        {
            hostContext = hostContext ?? ExtensionHostContext.Empty;

            foreach (IActivatablePhinixExtensionModule module in discovered?.Modules?.OfType<IActivatablePhinixExtensionModule>() ?? Enumerable.Empty<IActivatablePhinixExtensionModule>())
            {
                ExtensionDiscoveryResult result = discovered.ExtensionResults
                    .Find(r => string.Equals(r.ExtensionId, module.ExtensionId, StringComparison.OrdinalIgnoreCase));

                try
                {
                    module.Activate(hostContext);
                    if (result != null) result.State = ExtensionModuleState.Active;
                    discovered.Diagnostics.Add($"Framework module '{module.ExtensionId}' activated for host '{hostContext.HostKind ?? "unknown"}'.");
                }
                catch (Exception exception)
                {
                    if (result != null)
                    {
                        result.State = ExtensionModuleState.Failed;
                        result.StateDetail = $"FAILED to activate: {exception.Message}";
                    }
                    discovered.Warnings.Add($"Extension '{module.ExtensionId}' FAILED to activate (this is a bug, please report it): {exception.Message}");
                }
            }
        }

        public static void ShutdownExtensions(DiscoveredPhinixExtensions discovered, ExtensionHostContext hostContext = null)
        {
            hostContext = hostContext ?? ExtensionHostContext.Empty;

            foreach (IActivatablePhinixExtensionModule module in discovered?.Modules?.OfType<IActivatablePhinixExtensionModule>() ?? Enumerable.Empty<IActivatablePhinixExtensionModule>())
            {
                ExtensionDiscoveryResult result = discovered.ExtensionResults
                    .Find(r => string.Equals(r.ExtensionId, module.ExtensionId, StringComparison.OrdinalIgnoreCase));

                try
                {
                    module.Shutdown(hostContext);
                    if (result != null) result.State = ExtensionModuleState.Shutdown;
                    discovered.Diagnostics.Add($"Framework module '{module.ExtensionId}' shut down for host '{hostContext.HostKind ?? "unknown"}'.");
                }
                catch (Exception exception)
                {
                    if (result != null)
                    {
                        result.State = ExtensionModuleState.Failed;
                        result.StateDetail = $"FAILED to shut down: {exception.Message}";
                    }
                    discovered.Warnings.Add($"Extension '{module.ExtensionId}' FAILED to shut down (this is a bug, please report it): {exception.Message}");
                }
            }
        }

        public static string[] CollectCapabilities(DiscoveredPhinixExtensions discovered)
        {
            HashSet<string> capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "core.framework.v2",
                "core.message-pipeline",
                "core.command-pipeline",
                "core.item-pipeline",
                "core.outbound-pipeline"
            };

            foreach (ICapabilityProvider capabilityProvider in discovered.CapabilityProviders)
            {
                foreach (string capability in capabilityProvider.GetCapabilities() ?? Enumerable.Empty<string>())
                {
                    if (string.IsNullOrEmpty(capability)) continue;

                    if (!capabilities.Add(capability))
                    {
                        discovered.Warnings.Add($"Duplicate capability ID '{capability}' discovered during framework registration.");
                    }
                }
            }

            return capabilities.OrderBy(capability => capability).ToArray();
        }

        private static List<Type> scanCandidateTypes()
        {
            return AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(isCandidateExtensionAssembly)
                .SelectMany(getLoadableTypes)
                .Where(type => type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                .ToList();
        }

        private static IEnumerable<Type> getLoadableTypes(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException exception)
            {
                return exception.Types.Where(type => type != null);
            }
        }

        private static bool isCandidateExtensionAssembly(Assembly assembly)
        {
            if (assembly == null || assembly.IsDynamic)
            {
                return false;
            }

            string assemblyName = assembly.GetName().Name;
            if (string.IsNullOrEmpty(assemblyName))
            {
                return false;
            }

            if (assembly == typeof(PhinixExtensionRegistry).Assembly)
            {
                return true;
            }

            if (assemblyName.StartsWith("System.", StringComparison.Ordinal) ||
                assemblyName.StartsWith("Microsoft.", StringComparison.Ordinal) ||
                assemblyName.StartsWith("runtime.", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("mscorlib", StringComparison.OrdinalIgnoreCase) ||
                assemblyName.StartsWith("netstandard", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            try
            {
                string frameworkAssemblyName = typeof(IPhinixExtensionModule).Assembly.GetName().Name;
                return assembly
                    .GetReferencedAssemblies()
                    .Any(reference => string.Equals(reference.Name, frameworkAssemblyName, StringComparison.OrdinalIgnoreCase));
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[Phinix] PhinixExtensionRegistry.isCandidateExtensionAssembly failed for '{assembly?.GetName()?.Name}': {ex.Message}");
                return false;
            }
        }

        private static bool isLegacyDiscoverableType(Type type)
        {
            return type.GetCustomAttribute<PhinixExtensionAttribute>() != null ||
                   typeof(IPhinixExtension).IsAssignableFrom(type);
        }

        private sealed class ExtensionBuilder : IExtensionBuilder
        {
            private readonly string extensionId;
            private readonly ExtensionHostContext hostContext;
            private readonly DiscoveredPhinixExtensions discovered;
            private readonly ExtensionApiRegistry apiRegistry;
            private readonly ExtensionDiscoveryResult result;

            public ExtensionBuilder(string extensionId, ExtensionHostContext hostContext, DiscoveredPhinixExtensions discovered, ExtensionApiRegistry apiRegistry, ExtensionDiscoveryResult result = null)
            {
                this.extensionId = extensionId ?? string.Empty;
                this.hostContext = hostContext ?? ExtensionHostContext.Empty;
                this.discovered = discovered;
                this.apiRegistry = apiRegistry ?? new ExtensionApiRegistry();
                this.result = result;
            }

            public string ExtensionId => extensionId;

            public ExtensionHostContext HostContext => hostContext;

            public IExtensionApiRegistry ApiRegistry => apiRegistry;

            public void AddCapabilityProvider(ICapabilityProvider capabilityProvider) => addIfMissing(discovered.CapabilityProviders, capabilityProvider);

            public void AddMessageInterceptor(IMessageInterceptor interceptor) => addIfMissing(discovered.MessageInterceptors, interceptor);

            public void AddMessageRenderer(IMessageRenderer renderer) => addIfMissing(discovered.MessageRenderers, renderer);

            public void AddClientMessageHandler(IClientMessageHandler handler) => addIfMissing(discovered.ClientMessageHandlers, handler);

            public void AddServerMessageHandler(IServerMessageHandler handler)
            {
                addIfMissing(discovered.ServerMessageHandlers, handler);
                if (handler is IServerDefaultMessageHandler defaultHandler)
                {
                    addIfMissing(discovered.ServerDefaultMessageHandlers, defaultHandler);
                }
                else
                {
                    addIfMissing(discovered.ServerDefaultMessageHandlers, new LegacyServerDefaultMessageHandlerAdapter(handler));
                }
            }

            public void AddServerInboundMessageInterceptor(IServerInboundMessageInterceptor interceptor) => addIfMissing(discovered.ServerInboundMessageInterceptors, interceptor);

            public void AddServerDefaultMessageHandler(IServerDefaultMessageHandler handler)
            {
                addIfMissing(discovered.ServerDefaultMessageHandlers, handler);
                addIfMissing(discovered.ServerMessageHandlers, handler);
            }

            public void AddServerMessageObserver(IServerMessageObserver observer) => addIfMissing(discovered.ServerMessageObservers, observer);

            public void AddItemCodec(IItemCodec codec) => addIfMissing(discovered.ItemCodecs, codec);

            public void AddClientItemHandler(IClientIncomingItemHandler handler) => addIfMissing(discovered.ClientIncomingItemHandlers, handler);

            public void AddClientOutgoingItemHandler(IClientOutgoingItemHandler handler) => addIfMissing(discovered.ClientOutgoingItemHandlers, handler);

            public void AddServerItemHandler(IServerItemHandler handler)
            {
                addIfMissing(discovered.ServerItemHandlers, handler);
                if (handler is IServerDefaultItemHandler defaultHandler)
                {
                    addIfMissing(discovered.ServerDefaultItemHandlers, defaultHandler);
                }
                else
                {
                    addIfMissing(discovered.ServerDefaultItemHandlers, new LegacyServerDefaultItemHandlerAdapter(handler));
                }
            }

            public void AddServerInboundItemInterceptor(IServerInboundItemInterceptor interceptor) => addIfMissing(discovered.ServerInboundItemInterceptors, interceptor);

            public void AddServerDefaultItemHandler(IServerDefaultItemHandler handler)
            {
                addIfMissing(discovered.ServerDefaultItemHandlers, handler);
                addIfMissing(discovered.ServerItemHandlers, handler);
            }

            public void AddServerItemObserver(IServerItemObserver observer) => addIfMissing(discovered.ServerItemObservers, observer);

            public void AddClientCommandHandler(IClientCommandHandler handler) => addIfMissing(discovered.ClientCommandHandlers, handler);

            public void AddServerCommandHandler(IServerCommandHandler handler)
            {
                addIfMissing(discovered.ServerCommandHandlers, handler);
                if (handler is IServerDefaultCommandHandler defaultHandler)
                {
                    addIfMissing(discovered.ServerDefaultCommandHandlers, defaultHandler);
                }
                else
                {
                    addIfMissing(discovered.ServerDefaultCommandHandlers, new LegacyServerDefaultCommandHandlerAdapter(handler));
                }
            }

            public void AddServerInboundCommandInterceptor(IServerInboundCommandInterceptor interceptor) => addIfMissing(discovered.ServerInboundCommandInterceptors, interceptor);

            public void AddServerDefaultCommandHandler(IServerDefaultCommandHandler handler)
            {
                addIfMissing(discovered.ServerDefaultCommandHandlers, handler);
                addIfMissing(discovered.ServerCommandHandlers, handler);
            }

            public void AddServerCommandObserver(IServerCommandObserver observer) => addIfMissing(discovered.ServerCommandObservers, observer);

            public void AddServerOutboundPacketInterceptor(IServerOutboundPacketInterceptor interceptor) => addIfMissing(discovered.ServerOutboundPacketInterceptors, interceptor);

            public void AddConsoleCommandProvider(IServerConsoleCommandProvider provider)
            {
                addIfMissing(discovered.ConsoleCommandProviders, provider);
                apiRegistry.TryRegisterApi(extensionId, provider);
            }

            public void RegisterApi<T>(T implementation) where T : class
            {
                if (result != null) result.RegisteredApis.Add(typeof(T).Name);
                ExtensionApiRegistrationResult regResult = apiRegistry.TryRegisterApi(extensionId, implementation);
                if (!string.IsNullOrEmpty(regResult.Diagnostic))
                {
                    discovered.Diagnostics.Add(regResult.Diagnostic);
                }

                if (!string.IsNullOrEmpty(regResult.Warning))
                {
                    discovered.Warnings.Add(regResult.Warning);
                }
            }

            public bool TryResolveApi<T>(out T implementation) where T : class
            {
                if (result != null) result.ConsumedApis.Add(typeof(T).Name);
                return apiRegistry.TryResolve(out implementation);
            }

            public IReadOnlyList<T> ResolveApis<T>() where T : class
            {
                return apiRegistry.ResolveAll<T>();
            }

            private static void addIfMissing<T>(ICollection<T> collection, T item)
            {
                if (item == null || collection.Contains(item))
                {
                    return;
                }

                collection.Add(item);
            }
        }

        private sealed class LegacyServerDefaultMessageHandlerAdapter : IServerDefaultMessageHandler
        {
            private readonly IServerMessageHandler handler;

            public LegacyServerDefaultMessageHandlerAdapter(IServerMessageHandler handler)
            {
                this.handler = handler;
            }

            public int Priority => handler.Priority;

            public bool CanHandleIncomingMessage(FrameworkPacket message) => handler.CanHandleIncomingMessage(message);

            public ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket message, ServerFrameworkContext context) => handler.HandleIncomingMessage(message, context);
        }

        private sealed class LegacyServerDefaultCommandHandlerAdapter : IServerDefaultCommandHandler
        {
            private readonly IServerCommandHandler handler;

            public LegacyServerDefaultCommandHandlerAdapter(IServerCommandHandler handler)
            {
                this.handler = handler;
            }

            public int Priority => handler.Priority;

            public bool CanHandleIncomingCommand(FrameworkPacket command) => handler.CanHandleIncomingCommand(command);

            public ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ServerFrameworkContext context) => handler.HandleIncomingCommand(command, context);
        }

        private sealed class LegacyServerDefaultItemHandlerAdapter : IServerDefaultItemHandler
        {
            private readonly IServerItemHandler handler;

            public LegacyServerDefaultItemHandlerAdapter(IServerItemHandler handler)
            {
                this.handler = handler;
            }

            public int Priority => handler.Priority;

            public bool CanHandleIncomingItem(FrameworkPacket itemPacket) => handler.CanHandleIncomingItem(itemPacket);

            public ServerIncomingItemResult HandleIncomingItem(FrameworkPacket itemPacket, ServerFrameworkContext context, IReadOnlyList<IItemCodec> codecs) => handler.HandleIncomingItem(itemPacket, context, codecs);
        }
    }
}
