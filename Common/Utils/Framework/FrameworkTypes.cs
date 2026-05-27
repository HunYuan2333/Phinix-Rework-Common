using System;
using System.Collections.Generic;
using System.IO;

namespace Utils.Framework
{
    public static class FrameworkProtocol
    {
        public const string ModuleName = "PhinixFramework";
        public const int Version = 2;
        public const string KindHello = "hello";
        public const string KindCapabilities = "capabilities";
        public const string KindMessage = "message";
        public const string KindCommand = "command";
        public const string KindItem = "item";
        public const string SystemSenderUuid = "__phinix_system__";
    }

    public enum FrameworkCompatibilityMode
    {
        Unknown = 0,
        FrameworkV2 = 1,
        Legacy = 2
    }

    public enum MessageHandlingResultAction
    {
        Continue = 0,
        Handled = 1,
        Handle = 1,
        ReplacePayload = 2,
        Replace = 2,
        SuppressDefault = 3,
        StopPropagation = 4,
        Block = 4,
        LegacyFallback = 5,
        Observe = 6
    }

    public interface IPhinixExtension
    {
        string ExtensionId { get; }
    }

    public interface IPhinixExtensionModule : IPhinixExtension
    {
        void Register(IExtensionBuilder builder);
    }

    public interface IActivatablePhinixExtensionModule : IPhinixExtension
    {
        void Activate(ExtensionHostContext hostContext);

        void Shutdown(ExtensionHostContext hostContext);
    }

    public interface IExtensionApiRegistry
    {
        void RegisterApi<T>(string extensionId, T implementation) where T : class;

        bool TryResolve<T>(out T implementation) where T : class;

        IReadOnlyList<T> ResolveAll<T>() where T : class;
    }

    public interface IFrameworkServerPacketDispatcher
    {
        void Send(string connectionId, FrameworkPacket packet);

        void Send(string connectionId, FrameworkPacket packet, string sourceExtensionId);
    }

    public interface IExtensionBuilder
    {
        string ExtensionId { get; }

        ExtensionHostContext HostContext { get; }

        IExtensionApiRegistry ApiRegistry { get; }

        void AddCapabilityProvider(ICapabilityProvider capabilityProvider);

        void AddMessageInterceptor(IMessageInterceptor interceptor);

        void AddMessageRenderer(IMessageRenderer renderer);

        void AddClientMessageHandler(IClientMessageHandler handler);

        void AddServerMessageHandler(IServerMessageHandler handler);

        void AddServerInboundMessageInterceptor(IServerInboundMessageInterceptor interceptor);

        void AddServerDefaultMessageHandler(IServerDefaultMessageHandler handler);

        void AddServerMessageObserver(IServerMessageObserver observer);

        void AddItemCodec(IItemCodec codec);

        void AddClientCommandHandler(IClientCommandHandler handler);

        void AddServerCommandHandler(IServerCommandHandler handler);

        void AddServerInboundCommandInterceptor(IServerInboundCommandInterceptor interceptor);

        void AddServerDefaultCommandHandler(IServerDefaultCommandHandler handler);

        void AddServerCommandObserver(IServerCommandObserver observer);

        void AddServerOutboundPacketInterceptor(IServerOutboundPacketInterceptor interceptor);

        void RegisterApi<T>(T implementation) where T : class;

        bool TryResolveApi<T>(out T implementation) where T : class;

        IReadOnlyList<T> ResolveApis<T>() where T : class;
    }

    public sealed class ExtensionApiRegistry : IExtensionApiRegistry
    {
        private readonly Dictionary<Type, List<ApiRegistration>> registrations = new Dictionary<Type, List<ApiRegistration>>();
        private readonly object syncRoot = new object();

        public void RegisterApi<T>(string extensionId, T implementation) where T : class
        {
            registerApi(typeof(T), extensionId, implementation);
        }

        public bool TryResolve<T>(out T implementation) where T : class
        {
            lock (syncRoot)
            {
                if (registrations.TryGetValue(typeof(T), out List<ApiRegistration> providers) && providers.Count > 0)
                {
                    implementation = providers[0].Implementation as T;
                    return implementation != null;
                }
            }

            implementation = null;
            return false;
        }

        public IReadOnlyList<T> ResolveAll<T>() where T : class
        {
            List<T> resolved = new List<T>();
            lock (syncRoot)
            {
                if (!registrations.TryGetValue(typeof(T), out List<ApiRegistration> providers))
                {
                    return resolved;
                }

                foreach (ApiRegistration provider in providers)
                {
                    if (provider.Implementation is T typedImplementation)
                    {
                        resolved.Add(typedImplementation);
                    }
                }
            }

            return resolved;
        }

        internal ExtensionApiRegistrationResult TryRegisterApi<T>(string extensionId, T implementation) where T : class
        {
            return registerApi(typeof(T), extensionId, implementation);
        }

        private ExtensionApiRegistrationResult registerApi(Type apiType, string extensionId, object implementation)
        {
            if (apiType == null)
            {
                return ExtensionApiRegistrationResult.CreateFailure("Framework API registration skipped because the API type was null.");
            }

            if (implementation == null)
            {
                return ExtensionApiRegistrationResult.CreateFailure($"Framework API registration skipped for '{apiType.FullName}' because the implementation was null.");
            }

            lock (syncRoot)
            {
                if (!registrations.TryGetValue(apiType, out List<ApiRegistration> providers))
                {
                    providers = new List<ApiRegistration>();
                    registrations[apiType] = providers;
                }

                if (providers.Exists(candidate =>
                    string.Equals(candidate.ExtensionId, extensionId, StringComparison.OrdinalIgnoreCase) &&
                    ReferenceEquals(candidate.Implementation, implementation)))
                {
                    return ExtensionApiRegistrationResult.CreateFailure(
                        $"Framework API '{apiType.FullName}' from extension '{extensionId}' was already registered.");
                }

                providers.Add(new ApiRegistration(extensionId, implementation));
                if (providers.Count > 1)
                {
                    return ExtensionApiRegistrationResult.CreateSuccess(
                        $"Framework API '{apiType.FullName}' registered from extension '{extensionId}'.",
                        $"Framework API '{apiType.FullName}' now has {providers.Count} providers. Resolution will prefer the first registered provider.");
                }

                return ExtensionApiRegistrationResult.CreateSuccess(
                    $"Framework API '{apiType.FullName}' registered from extension '{extensionId}'.");
            }
        }

        private sealed class ApiRegistration
        {
            public ApiRegistration(string extensionId, object implementation)
            {
                ExtensionId = extensionId ?? string.Empty;
                Implementation = implementation;
            }

            public string ExtensionId { get; }

            public object Implementation { get; }
        }
    }

    internal sealed class ExtensionApiRegistrationResult
    {
        private ExtensionApiRegistrationResult(bool success, string diagnostic, string warning)
        {
            Success = success;
            Diagnostic = diagnostic;
            Warning = warning;
        }

        public bool Success { get; }

        public string Diagnostic { get; }

        public string Warning { get; }

        public static ExtensionApiRegistrationResult CreateSuccess(string diagnostic, string warning = null)
        {
            return new ExtensionApiRegistrationResult(true, diagnostic, warning);
        }

        public static ExtensionApiRegistrationResult CreateFailure(string warning)
        {
            return new ExtensionApiRegistrationResult(false, null, warning);
        }
    }

    public interface IExtensionStorageProvider
    {
        string GetStoragePath(string extensionId, string logicalName);
    }

    public sealed class FileSystemExtensionStorageProvider : IExtensionStorageProvider
    {
        private readonly string rootPath;

        public FileSystemExtensionStorageProvider(string rootPath)
        {
            this.rootPath = string.IsNullOrWhiteSpace(rootPath)
                ? Path.Combine(AppDomain.CurrentDomain.BaseDirectory ?? ".", "framework-extensions")
                : rootPath;
        }

        public string GetStoragePath(string extensionId, string logicalName)
        {
            string safeExtensionId = sanitizePathPart(extensionId, "unknown-extension");
            string safeLogicalName = sanitizePathPart(logicalName, "default");
            string extensionDirectory = Path.Combine(rootPath, safeExtensionId);
            Directory.CreateDirectory(extensionDirectory);
            return Path.Combine(extensionDirectory, safeLogicalName);
        }

        private static string sanitizePathPart(string value, string fallback)
        {
            string candidate = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
            foreach (char invalidChar in Path.GetInvalidFileNameChars())
            {
                candidate = candidate.Replace(invalidChar, '_');
            }

            return string.IsNullOrWhiteSpace(candidate) ? fallback : candidate;
        }
    }

    public sealed class ExtensionHostContext
    {
        private readonly Dictionary<Type, object> services = new Dictionary<Type, object>();
        private readonly Dictionary<string, string> options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<ExtensionPersistenceRegistration> persistents = new List<ExtensionPersistenceRegistration>();

        public static ExtensionHostContext Empty { get; } = new ExtensionHostContext();

        public string HostKind { get; set; } = "unknown";

        public Action<string, LogLevel> Log { get; set; }

        public Func<string> CreateMessageId { get; set; } = () => Guid.NewGuid().ToString();

        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public IExtensionStorageProvider StorageProvider { get; set; }

        public IExtensionApiRegistry ApiRegistry { get; internal set; } = new ExtensionApiRegistry();

        public IReadOnlyList<ExtensionPersistenceRegistration> Persistents => persistents;

        public void AddService<T>(T service) where T : class
        {
            if (service == null) return;

            services[typeof(T)] = service;
        }

        public bool TryGetService<T>(out T service) where T : class
        {
            if (services.TryGetValue(typeof(T), out object resolved) && resolved is T typedService)
            {
                service = typedService;
                return true;
            }

            service = null;
            return false;
        }

        public T GetRequiredService<T>() where T : class
        {
            if (TryGetService<T>(out T service))
            {
                return service;
            }

            throw new InvalidOperationException($"Required extension host service '{typeof(T).FullName}' is not available for host '{HostKind}'.");
        }

        public string GetStoragePath(string extensionId, string logicalName)
        {
            return StorageProvider?.GetStoragePath(extensionId, logicalName);
        }

        public void SetOption(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return;
            }

            options[key] = value ?? string.Empty;
        }

        public bool TryGetOption(string key, out string value)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                value = null;
                return false;
            }

            return options.TryGetValue(key, out value);
        }

        public int GetIntOption(string key, int defaultValue)
        {
            return TryGetOption(key, out string value) && int.TryParse(value, out int parsedValue)
                ? parsedValue
                : defaultValue;
        }

        public void RegisterPersistent(string extensionId, string logicalName, IPersistent persistent)
        {
            if (string.IsNullOrWhiteSpace(extensionId) || string.IsNullOrWhiteSpace(logicalName) || persistent == null)
            {
                return;
            }

            if (persistents.Exists(candidate =>
                string.Equals(candidate.ExtensionId, extensionId, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(candidate.LogicalName, logicalName, StringComparison.OrdinalIgnoreCase) &&
                ReferenceEquals(candidate.Persistent, persistent)))
            {
                return;
            }

            persistents.Add(new ExtensionPersistenceRegistration(extensionId, logicalName, persistent));
        }

        public bool TryResolveApi<T>(out T implementation) where T : class
        {
            if (ApiRegistry != null)
            {
                return ApiRegistry.TryResolve(out implementation);
            }

            implementation = null;
            return false;
        }

        public IReadOnlyList<T> ResolveApis<T>() where T : class
        {
            return ApiRegistry?.ResolveAll<T>() ?? Array.Empty<T>();
        }
    }

    public sealed class ExtensionPersistenceRegistration
    {
        public ExtensionPersistenceRegistration(string extensionId, string logicalName, IPersistent persistent)
        {
            ExtensionId = extensionId ?? string.Empty;
            LogicalName = logicalName ?? string.Empty;
            Persistent = persistent;
        }

        public string ExtensionId { get; }

        public string LogicalName { get; }

        public IPersistent Persistent { get; }
    }

    [AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
    public sealed class PhinixExtensionAttribute : Attribute
    {
        public string ExtensionId { get; }

        public PhinixExtensionAttribute(string extensionId)
        {
            if (string.IsNullOrEmpty(extensionId)) throw new ArgumentException("Extension ID cannot be null or empty.", nameof(extensionId));

            ExtensionId = extensionId;
        }
    }

    public interface ICapabilityProvider
    {
        IEnumerable<string> GetCapabilities();
    }

    public interface IMessageHandler
    {
        int Priority { get; }
    }

    public interface IMessageInterceptor
    {
        int Priority { get; }

        MessageHandlingResultAction Intercept(FrameworkDisplayMessage message);
    }

    public interface IMessageRenderer
    {
        bool CanRender(FrameworkPacket message);

        FrameworkDisplayMessage Render(FrameworkPacket message);
    }

    public interface IClientMessageHandler : IMessageHandler
    {
        bool CanHandleOutgoingText(string rawMessage);

        ClientOutgoingMessageResult HandleOutgoingText(string rawMessage, ClientFrameworkContext context);

        bool CanHandleIncomingMessage(FrameworkPacket message);

        ClientIncomingMessageResult HandleIncomingMessage(FrameworkPacket message, ClientFrameworkContext context);
    }

    public interface IServerMessageHandler : IMessageHandler
    {
        bool CanHandleIncomingMessage(FrameworkPacket message);

        ServerIncomingMessageResult HandleIncomingMessage(FrameworkPacket message, ServerFrameworkContext context);
    }

    public interface IServerInboundMessageInterceptor : IMessageHandler
    {
        bool CanInterceptIncomingMessage(FrameworkPacket message);

        ServerIncomingMessageResult InterceptIncomingMessage(FrameworkPacket message, ServerFrameworkContext context);
    }

    public interface IServerDefaultMessageHandler : IServerMessageHandler
    {
    }

    public interface IServerMessageObserver : IMessageHandler
    {
        bool CanObserveIncomingMessage(FrameworkPacket message);

        void ObserveIncomingMessage(FrameworkPacket message, ServerFrameworkContext context, MessageHandlingResultAction terminalAction);
    }

    public interface IItemCodec
    {
        string CodecId { get; }

        bool CanEncode(object item, ItemCodecContext context);

        FrameworkItemPayload Encode(object item, ItemCodecContext context);

        bool CanDecode(FrameworkItemPayload payload, ItemCodecContext context);

        object Decode(FrameworkItemPayload payload, ItemCodecContext context);
    }

    public interface ICommandHandler
    {
        int Priority { get; }
    }

    public interface IClientCommandHandler : ICommandHandler
    {
        bool CanHandleIncomingCommand(FrameworkPacket command);

        ClientIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ClientFrameworkContext context);
    }

    public interface IServerCommandHandler : ICommandHandler
    {
        bool CanHandleIncomingCommand(FrameworkPacket command);

        ServerIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ServerFrameworkContext context);
    }

    public interface IServerInboundCommandInterceptor : ICommandHandler
    {
        bool CanInterceptIncomingCommand(FrameworkPacket command);

        ServerIncomingCommandResult InterceptIncomingCommand(FrameworkPacket command, ServerFrameworkContext context);
    }

    public interface IServerDefaultCommandHandler : IServerCommandHandler
    {
    }

    public interface IServerCommandObserver : ICommandHandler
    {
        bool CanObserveIncomingCommand(FrameworkPacket command);

        void ObserveIncomingCommand(FrameworkPacket command, ServerFrameworkContext context, MessageHandlingResultAction terminalAction);
    }

    public interface IServerOutboundPacketInterceptor
    {
        int Priority { get; }

        bool CanInterceptOutgoingPacket(FrameworkPacket packet, ServerOutboundPacketContext context);

        ServerOutgoingPacketResult InterceptOutgoingPacket(FrameworkPacket packet, ServerOutboundPacketContext context);
    }

    public sealed class ClientOutgoingMessageResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkPacket Message { get; set; }
    }

    public sealed class ClientIncomingMessageResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkPacket Message { get; set; }

        public FrameworkDisplayMessage DisplayMessage { get; set; }
    }

    public sealed class ServerIncomingMessageResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkPacket Message { get; set; }
    }

    public sealed class ClientIncomingCommandResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkPacket Command { get; set; }

        public FrameworkDisplayMessage DisplayMessage { get; set; }
    }

    public sealed class ServerIncomingCommandResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkPacket Command { get; set; }
    }

    public sealed class ServerOutgoingPacketResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Continue;

        public FrameworkPacket Packet { get; set; }

        public IReadOnlyCollection<string> TargetConnectionIds { get; set; }
    }

    public sealed class ItemCodecContext
    {
        public FrameworkCompatibilityMode CompatibilityMode { get; set; }

        public Action<string, LogLevel> Log { get; set; }
    }

    public sealed class ClientFrameworkContext
    {
        public string SessionId { get; set; }

        public string SenderUuid { get; set; }

        public FrameworkCompatibilityMode CompatibilityMode { get; set; }

        public Action<FrameworkPacket> SendMessage { get; set; }

        public IReadOnlyCollection<string> RemoteCapabilities { get; set; } = Array.Empty<string>();

        public Func<string, bool> HasRemoteCapability { get; set; }

        public Action<string, LogLevel> Log { get; set; }
    }

    public sealed class ServerFrameworkContext
    {
        public string ConnectionId { get; set; }

        public string SessionId { get; set; }

        public string SenderUuid { get; set; }

        public string SourceExtensionId { get; set; }

        public Action<string, FrameworkPacket> SendMessage { get; set; }

        public Action<FrameworkPacket, string[]> BroadcastMessage { get; set; }

        public Func<string, bool> IsConnectionFrameworkCapable { get; set; }

        public IReadOnlyCollection<string> RemoteCapabilities { get; set; } = Array.Empty<string>();

        public IReadOnlyCollection<string> ServerCapabilities { get; set; } = Array.Empty<string>();

        public Func<string, bool> HasRemoteCapability { get; set; }

        public Func<string, string, bool> ConnectionHasCapability { get; set; }

        public Action<string, LogLevel> Log { get; set; }
    }

    public sealed class ServerOutboundPacketContext
    {
        public string SourceExtensionId { get; set; }

        public IReadOnlyCollection<string> TargetConnectionIds { get; set; } = Array.Empty<string>();

        public Action<string, FrameworkPacket> DeliverToConnection { get; set; }

        public Func<string, bool> IsConnectionFrameworkCapable { get; set; }

        public Func<string, string, bool> ConnectionHasCapability { get; set; }

        public Action<string, LogLevel> Log { get; set; }
    }

    public sealed class DiscoveredPhinixExtensions
    {
        public IExtensionApiRegistry ApiRegistry { get; internal set; } = new ExtensionApiRegistry();

        public List<IPhinixExtension> Extensions { get; } = new List<IPhinixExtension>();

        public List<IPhinixExtensionModule> Modules { get; } = new List<IPhinixExtensionModule>();

        public List<string> Diagnostics { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public List<ICapabilityProvider> CapabilityProviders { get; } = new List<ICapabilityProvider>();

        public List<IMessageInterceptor> MessageInterceptors { get; } = new List<IMessageInterceptor>();

        public List<IMessageRenderer> MessageRenderers { get; } = new List<IMessageRenderer>();

        public List<IClientMessageHandler> ClientMessageHandlers { get; } = new List<IClientMessageHandler>();

        public List<IServerMessageHandler> ServerMessageHandlers { get; } = new List<IServerMessageHandler>();

        public List<IServerInboundMessageInterceptor> ServerInboundMessageInterceptors { get; } = new List<IServerInboundMessageInterceptor>();

        public List<IServerDefaultMessageHandler> ServerDefaultMessageHandlers { get; } = new List<IServerDefaultMessageHandler>();

        public List<IServerMessageObserver> ServerMessageObservers { get; } = new List<IServerMessageObserver>();

        public List<IItemCodec> ItemCodecs { get; } = new List<IItemCodec>();

        public List<IClientCommandHandler> ClientCommandHandlers { get; } = new List<IClientCommandHandler>();

        public List<IServerCommandHandler> ServerCommandHandlers { get; } = new List<IServerCommandHandler>();

        public List<IServerInboundCommandInterceptor> ServerInboundCommandInterceptors { get; } = new List<IServerInboundCommandInterceptor>();

        public List<IServerDefaultCommandHandler> ServerDefaultCommandHandlers { get; } = new List<IServerDefaultCommandHandler>();

        public List<IServerCommandObserver> ServerCommandObservers { get; } = new List<IServerCommandObserver>();

        public List<IServerOutboundPacketInterceptor> ServerOutboundPacketInterceptors { get; } = new List<IServerOutboundPacketInterceptor>();

    }
}
