using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Utils.Framework
{
    public static class PhinixExtensionRegistry
    {
        public static DiscoveredPhinixExtensions DiscoverExtensions(ExtensionHostContext hostContext = null)
        {
            hostContext = hostContext ?? ExtensionHostContext.Empty;
            DiscoveredPhinixExtensions discovered = new DiscoveredPhinixExtensions();
            ExtensionApiRegistry apiRegistry = hostContext.ApiRegistry as ExtensionApiRegistry ?? new ExtensionApiRegistry();
            hostContext.ApiRegistry = apiRegistry;
            discovered.ApiRegistry = apiRegistry;
            HashSet<string> seenExtensionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            List<Type> candidateTypes = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .SelectMany(getLoadableTypes)
                .Where(type => type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                .ToList();

            List<Type> moduleTypes = candidateTypes
                .Where(type => typeof(IPhinixExtensionModule).IsAssignableFrom(type))
                .OrderBy(type => type.FullName, StringComparer.Ordinal)
                .ToList();

            foreach (Type moduleType in moduleTypes)
            {
                IPhinixExtensionModule module;
                try
                {
                    module = Activator.CreateInstance(moduleType) as IPhinixExtensionModule;
                }
                catch (Exception exception)
                {
                    discovered.Warnings.Add($"Failed to initialize extension module '{moduleType.FullName}': {exception.Message}");
                    continue;
                }

                if (module == null)
                {
                    continue;
                }

                ExtensionBuilder builder = new ExtensionBuilder(module.ExtensionId, hostContext, discovered, apiRegistry);
                if (!seenExtensionIds.Add(module.ExtensionId))
                {
                    discovered.Warnings.Add($"Duplicate extension ID '{module.ExtensionId}' discovered in '{moduleType.FullName}'.");
                }

                discovered.Extensions.Add(module);
                discovered.Modules.Add(module);

                try
                {
                    module.Register(builder);
                    discovered.Diagnostics.Add(
                        $"Framework module '{module.ExtensionId}' registered from '{moduleType.FullName}' " +
                        $"for host '{hostContext.HostKind ?? "unknown"}'.");
                }
                catch (Exception exception)
                {
                    discovered.Warnings.Add($"Failed to register extension module '{moduleType.FullName}': {exception.Message}");
                }
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
                    registeredLegacyComponent = true;
                }
                if (instance is IItemCodec itemCodec)
                {
                    discovered.ItemCodecs.Add(itemCodec);
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
            discovered.ClientCommandHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerCommandHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));

            return discovered;
        }

        public static void ActivateExtensions(DiscoveredPhinixExtensions discovered, ExtensionHostContext hostContext = null)
        {
            hostContext = hostContext ?? ExtensionHostContext.Empty;

            foreach (IActivatablePhinixExtensionModule module in discovered?.Modules?.OfType<IActivatablePhinixExtensionModule>() ?? Enumerable.Empty<IActivatablePhinixExtensionModule>())
            {
                try
                {
                    module.Activate(hostContext);
                    discovered.Diagnostics.Add($"Framework module '{module.ExtensionId}' activated for host '{hostContext.HostKind ?? "unknown"}'.");
                }
                catch (Exception exception)
                {
                    discovered.Warnings.Add($"Failed to activate extension module '{module.ExtensionId}': {exception.Message}");
                }
            }
        }

        public static void ShutdownExtensions(DiscoveredPhinixExtensions discovered, ExtensionHostContext hostContext = null)
        {
            hostContext = hostContext ?? ExtensionHostContext.Empty;

            foreach (IActivatablePhinixExtensionModule module in discovered?.Modules?.OfType<IActivatablePhinixExtensionModule>() ?? Enumerable.Empty<IActivatablePhinixExtensionModule>())
            {
                try
                {
                    module.Shutdown(hostContext);
                    discovered.Diagnostics.Add($"Framework module '{module.ExtensionId}' shut down for host '{hostContext.HostKind ?? "unknown"}'.");
                }
                catch (Exception exception)
                {
                    discovered.Warnings.Add($"Failed to shut down extension module '{module.ExtensionId}': {exception.Message}");
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
                "core.item-pipeline"
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

            public ExtensionBuilder(string extensionId, ExtensionHostContext hostContext, DiscoveredPhinixExtensions discovered, ExtensionApiRegistry apiRegistry)
            {
                this.extensionId = extensionId ?? string.Empty;
                this.hostContext = hostContext ?? ExtensionHostContext.Empty;
                this.discovered = discovered;
                this.apiRegistry = apiRegistry ?? new ExtensionApiRegistry();
            }

            public string ExtensionId => extensionId;

            public ExtensionHostContext HostContext => hostContext;

            public IExtensionApiRegistry ApiRegistry => apiRegistry;

            public void AddCapabilityProvider(ICapabilityProvider capabilityProvider) => addIfMissing(discovered.CapabilityProviders, capabilityProvider);

            public void AddMessageInterceptor(IMessageInterceptor interceptor) => addIfMissing(discovered.MessageInterceptors, interceptor);

            public void AddMessageRenderer(IMessageRenderer renderer) => addIfMissing(discovered.MessageRenderers, renderer);

            public void AddClientMessageHandler(IClientMessageHandler handler) => addIfMissing(discovered.ClientMessageHandlers, handler);

            public void AddServerMessageHandler(IServerMessageHandler handler) => addIfMissing(discovered.ServerMessageHandlers, handler);

            public void AddItemCodec(IItemCodec codec) => addIfMissing(discovered.ItemCodecs, codec);

            public void AddClientCommandHandler(IClientCommandHandler handler) => addIfMissing(discovered.ClientCommandHandlers, handler);

            public void AddServerCommandHandler(IServerCommandHandler handler) => addIfMissing(discovered.ServerCommandHandlers, handler);

            public void RegisterApi<T>(T implementation) where T : class
            {
                ExtensionApiRegistrationResult result = apiRegistry.TryRegisterApi(extensionId, implementation);
                if (!string.IsNullOrEmpty(result.Diagnostic))
                {
                    discovered.Diagnostics.Add(result.Diagnostic);
                }

                if (!string.IsNullOrEmpty(result.Warning))
                {
                    discovered.Warnings.Add(result.Warning);
                }
            }

            public bool TryResolveApi<T>(out T implementation) where T : class
            {
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
    }
}
