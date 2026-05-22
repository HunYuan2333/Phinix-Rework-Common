using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;

namespace Utils.Framework
{
    public static class PhinixExtensionRegistry
    {
        public static DiscoveredPhinixExtensions DiscoverExtensions()
        {
            DiscoveredPhinixExtensions discovered = new DiscoveredPhinixExtensions();
            HashSet<string> seenExtensionIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            IEnumerable<Type> candidateTypes = AppDomain.CurrentDomain
                .GetAssemblies()
                .Where(assembly => !assembly.IsDynamic)
                .SelectMany(getLoadableTypes)
                .Where(type => type.IsClass && !type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                .Where(type =>
                    type.GetCustomAttribute<PhinixExtensionAttribute>() != null ||
                    typeof(IPhinixExtension).IsAssignableFrom(type) ||
                    typeof(ICapabilityProvider).IsAssignableFrom(type) ||
                    typeof(IMessageInterceptor).IsAssignableFrom(type) ||
                    typeof(IMessageRenderer).IsAssignableFrom(type) ||
                    typeof(IClientMessageHandler).IsAssignableFrom(type) ||
                    typeof(IServerMessageHandler).IsAssignableFrom(type) ||
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
                if (instance is IItemCodec itemCodec) discovered.ItemCodecs.Add(itemCodec);
                if (instance is ITradeCompletionHandler tradeCompletionHandler) discovered.TradeCompletionHandlers.Add(tradeCompletionHandler);
            }

            discovered.MessageInterceptors.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ClientMessageHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.ServerMessageHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));
            discovered.TradeCompletionHandlers.Sort((left, right) => left.Priority.CompareTo(right.Priority));

            return discovered;
        }

        public static string[] CollectCapabilities(DiscoveredPhinixExtensions discovered)
        {
            HashSet<string> capabilities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "core.framework.v2",
                "core.message-pipeline"
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
    }
}
