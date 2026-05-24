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
            HashSet<string> seenExtensionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            ExtensionComponentSink sink = new ExtensionComponentSink(discovered);

            IEnumerable<Type> candidateTypes = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .SelectMany(getLoadableTypes)
                .Where(type => type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                .Where(type =>
                    typeof(IPhinixExtensionModule).IsAssignableFrom(type) ||
                    type.GetCustomAttribute<PhinixExtensionAttribute>() != null ||
                    typeof(IPhinixExtension).IsAssignableFrom(type) ||
                    typeof(ICapabilityProvider).IsAssignableFrom(type) ||
                    typeof(IMessageInterceptor).IsAssignableFrom(type) ||
                    typeof(IMessageRenderer).IsAssignableFrom(type) ||
                    typeof(IClientMessageHandler).IsAssignableFrom(type) ||
                    typeof(IServerMessageHandler).IsAssignableFrom(type) ||
                    typeof(IClientCommandHandler).IsAssignableFrom(type) ||
                    typeof(IServerCommandHandler).IsAssignableFrom(type) ||
                    typeof(IItemCodec).IsAssignableFrom(type) ||
                    typeof(ITradeCompletionHandler).IsAssignableFrom(type)
                );

            foreach (Type candidateType in candidateTypes)
            {
                object instance;
                try
                {
                    instance = Activator.CreateInstance(candidateType);
                }
                catch (Exception exception)
                {
                    discovered.Warnings.Add($"Failed to initialize extension type '{candidateType.FullName}': {exception.Message}");
                    continue;
                }

                if (instance is IPhinixExtensionModule module)
                {
                    if (!seenExtensionIds.Add(module.ExtensionId))
                    {
                        discovered.Warnings.Add($"Duplicate extension ID '{module.ExtensionId}' discovered in '{candidateType.FullName}'.");
                    }

                    discovered.Extensions.Add(module);
                    discovered.Modules.Add(module);

                    try
                    {
                        module.Register(sink, hostContext);
                        discovered.Diagnostics.Add(
                            $"Framework module '{module.ExtensionId}' registered from '{candidateType.FullName}' " +
                            $"for host '{hostContext.HostKind ?? "unknown"}'.");
                    }
                    catch (Exception exception)
                    {
                        discovered.Warnings.Add($"Failed to register extension module '{candidateType.FullName}': {exception.Message}");
                    }

                    continue;
                }

                if (instance is IPhinixExtension extension)
                {
                    if (!seenExtensionIds.Add(extension.ExtensionId))
                    {
                        discovered.Warnings.Add($"Duplicate extension ID '{extension.ExtensionId}' discovered in '{candidateType.FullName}'.");
                    }

                    discovered.Extensions.Add(extension);
                }
                if (instance is ICapabilityProvider capabilityProvider) discovered.CapabilityProviders.Add(capabilityProvider);
                if (instance is IMessageInterceptor interceptor) discovered.MessageInterceptors.Add(interceptor);
                if (instance is IMessageRenderer renderer) discovered.MessageRenderers.Add(renderer);
                if (instance is IClientMessageHandler clientHandler) discovered.ClientMessageHandlers.Add(clientHandler);
                if (instance is IServerMessageHandler serverHandler) discovered.ServerMessageHandlers.Add(serverHandler);
                if (instance is IClientCommandHandler clientCommandHandler) discovered.ClientCommandHandlers.Add(clientCommandHandler);
                if (instance is IServerCommandHandler serverCommandHandler) discovered.ServerCommandHandlers.Add(serverCommandHandler);
                if (instance is IItemCodec itemCodec) discovered.ItemCodecs.Add(itemCodec);
                if (instance is ITradeCompletionHandler tradeCompletionHandler) discovered.TradeCompletionHandlers.Add(tradeCompletionHandler);
            }

            discovered.MessageInterceptors.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ClientMessageHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerMessageHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ClientCommandHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerCommandHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.TradeCompletionHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));

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

        private sealed class ExtensionComponentSink : IExtensionComponentSink
        {
            private readonly DiscoveredPhinixExtensions discovered;

            public ExtensionComponentSink(DiscoveredPhinixExtensions discovered)
            {
                this.discovered = discovered;
            }

            public void AddCapabilityProvider(ICapabilityProvider capabilityProvider) => addIfMissing(discovered.CapabilityProviders, capabilityProvider);

            public void AddMessageInterceptor(IMessageInterceptor interceptor) => addIfMissing(discovered.MessageInterceptors, interceptor);

            public void AddMessageRenderer(IMessageRenderer renderer) => addIfMissing(discovered.MessageRenderers, renderer);

            public void AddClientMessageHandler(IClientMessageHandler handler) => addIfMissing(discovered.ClientMessageHandlers, handler);

            public void AddServerMessageHandler(IServerMessageHandler handler) => addIfMissing(discovered.ServerMessageHandlers, handler);

            public void AddItemCodec(IItemCodec codec) => addIfMissing(discovered.ItemCodecs, codec);

            public void AddClientCommandHandler(IClientCommandHandler handler) => addIfMissing(discovered.ClientCommandHandlers, handler);

            public void AddServerCommandHandler(IServerCommandHandler handler) => addIfMissing(discovered.ServerCommandHandlers, handler);

            public void AddTradeCompletionHandler(ITradeCompletionHandler handler) => addIfMissing(discovered.TradeCompletionHandlers, handler);

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
