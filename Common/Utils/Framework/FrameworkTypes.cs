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
        public const string BuiltInChatMessageType = "builtin.chat.message";
        public const string BuiltInChatHistoryRequestType = "builtin.chat.history.request";
        public const string BuiltInChatHistorySyncCompleteType = "builtin.chat.history.sync-complete";
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
        ReplacePayload = 2,
        SuppressDefault = 3,
        StopPropagation = 4,
        LegacyFallback = 5
    }

    public interface IPhinixExtension
    {
        string ExtensionId { get; }
    }

    public interface IPhinixExtensionModule : IPhinixExtension
    {
        void Register(IExtensionComponentSink sink, ExtensionHostContext hostContext);
    }

    public interface IActivatablePhinixExtensionModule : IPhinixExtension
    {
        void Activate(ExtensionHostContext hostContext);

        void Shutdown(ExtensionHostContext hostContext);
    }

    public interface IExtensionComponentSink
    {
        void AddCapabilityProvider(ICapabilityProvider capabilityProvider);

        void AddMessageInterceptor(IMessageInterceptor interceptor);

        void AddMessageRenderer(IMessageRenderer renderer);

        void AddClientMessageHandler(IClientMessageHandler handler);

        void AddServerMessageHandler(IServerMessageHandler handler);

        void AddItemCodec(IItemCodec codec);

        void AddClientCommandHandler(IClientCommandHandler handler);

        void AddServerCommandHandler(IServerCommandHandler handler);
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

        public static ExtensionHostContext Empty { get; } = new ExtensionHostContext();

        public string HostKind { get; set; } = "unknown";

        public Action<string, LogLevel> Log { get; set; }

        public Func<string> CreateMessageId { get; set; } = () => Guid.NewGuid().ToString();

        public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

        public IExtensionStorageProvider StorageProvider { get; set; }

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

        public Action<string, FrameworkPacket> SendMessage { get; set; }

        public Action<FrameworkPacket, string[]> BroadcastMessage { get; set; }

        public Func<string, bool> IsConnectionFrameworkCapable { get; set; }

        public IReadOnlyCollection<string> RemoteCapabilities { get; set; } = Array.Empty<string>();

        public IReadOnlyCollection<string> ServerCapabilities { get; set; } = Array.Empty<string>();

        public Func<string, bool> HasRemoteCapability { get; set; }

        public Func<string, string, bool> ConnectionHasCapability { get; set; }

        public Action<string, LogLevel> Log { get; set; }
    }

    public sealed class DiscoveredPhinixExtensions
    {
        public List<IPhinixExtension> Extensions { get; } = new List<IPhinixExtension>();

        public List<IPhinixExtensionModule> Modules { get; } = new List<IPhinixExtensionModule>();

        public List<string> Diagnostics { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public List<ICapabilityProvider> CapabilityProviders { get; } = new List<ICapabilityProvider>();

        public List<IMessageInterceptor> MessageInterceptors { get; } = new List<IMessageInterceptor>();

        public List<IMessageRenderer> MessageRenderers { get; } = new List<IMessageRenderer>();

        public List<IClientMessageHandler> ClientMessageHandlers { get; } = new List<IClientMessageHandler>();

        public List<IServerMessageHandler> ServerMessageHandlers { get; } = new List<IServerMessageHandler>();

        public List<IItemCodec> ItemCodecs { get; } = new List<IItemCodec>();

        public List<IClientCommandHandler> ClientCommandHandlers { get; } = new List<IClientCommandHandler>();

        public List<IServerCommandHandler> ServerCommandHandlers { get; } = new List<IServerCommandHandler>();

    }
}
