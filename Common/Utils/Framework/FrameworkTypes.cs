using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;

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

    public enum ItemHandlingResultAction
    {
        Continue = 0,
        Handled = 1,
        ReplacePayload = 2,
        SuppressDefault = 3,
        StopPropagation = 4,
        LegacyFallback = 5
    }

    public enum ExtensionModuleState
    {
        Unknown,
        Discovered,
        Registered,
        Active,
        Failed,
        Shutdown,
        Disabled,
        DependencyDisabled
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

    public interface IFrameworkServerBroadcaster
    {
        void Broadcast(string sourceExtensionId, FrameworkPacket packet, string[] excludedConnectionIds = null);
    }

    public interface IServerConsoleCommandProvider
    {
        string CommandName { get; }

        bool Execute(List<string> args);
    }

    public interface IExtensionConfigSection
    {
        string SectionName { get; }

        void LoadDefaults();

        void Validate();
    }

    public interface IExtensionConfigProvider
    {
        T GetConfig<T>() where T : IExtensionConfigSection, new();

        void SaveConfig<T>(T config) where T : IExtensionConfigSection;
    }

    public interface ILegacyExtensionConfigMigrator
    {
        bool TryMigrateLegacyConfig(IReadOnlyDictionary<string, string> legacyValues);
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

        void AddClientItemHandler(IClientIncomingItemHandler handler);

        void AddClientOutgoingItemHandler(IClientOutgoingItemHandler handler);

        void AddServerItemHandler(IServerItemHandler handler);

        void AddServerInboundItemInterceptor(IServerInboundItemInterceptor interceptor);

        void AddServerDefaultItemHandler(IServerDefaultItemHandler handler);

        void AddServerItemObserver(IServerItemObserver observer);

        void AddClientCommandHandler(IClientCommandHandler handler);

        void AddServerCommandHandler(IServerCommandHandler handler);

        void AddServerInboundCommandInterceptor(IServerInboundCommandInterceptor interceptor);

        void AddServerDefaultCommandHandler(IServerDefaultCommandHandler handler);

        void AddServerCommandObserver(IServerCommandObserver observer);

        void AddServerOutboundPacketInterceptor(IServerOutboundPacketInterceptor interceptor);

        void AddConsoleCommandProvider(IServerConsoleCommandProvider provider);

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

        public Func<Assembly, string> ResolveSourcePackageId { get; set; }

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

        /// <summary>该扩展声明依赖的其他扩展 ID（ExtensionId 字符串匹配）。</summary>
        public string[] DependsOn { get; set; } = Array.Empty<string>();

        public PhinixExtensionAttribute(string extensionId)
        {
            if (string.IsNullOrEmpty(extensionId)) throw new ArgumentException("Extension ID cannot be null or empty.", nameof(extensionId));

            ExtensionId = extensionId;
        }
    }

    /// <summary>
    /// 决定一个已发现的扩展是否应被激活。
    /// v1 使用 <see cref="StaticActivationPolicy"/>（从设置读取一次性快照）。
    /// v2 可替换为运行时可变实现，支持运行时激活/关闭扩展。
    /// 设计哲学 §2.1 松耦合：发现机制通过此接口咨询，不绑定具体实现。
    /// </summary>
    public interface IExtensionActivationPolicy
    {
        /// <summary>
        /// 判断指定扩展是否应被激活。在 DiscoverExtensions 实例化模块之前调用。
        /// </summary>
        /// <param name="extensionId">扩展唯一标识符</param>
        /// <param name="reason">若返回 false，输出不激活的原因（用于日志和 UI 展示）</param>
        /// <returns>true 表示应激活；false 表示应跳过</returns>
        bool ShouldActivate(string extensionId, out string reason);

        /// <summary>
        /// 当前被用户显式禁用的扩展 ID 快照（不含因依赖被禁用而连锁的）。
        /// 用于 UI 展示和区分 <see cref="ExtensionModuleState.Disabled"/> 与 <see cref="ExtensionModuleState.DependencyDisabled"/>。
        /// </summary>
        IReadOnlyCollection<string> DisabledExtensions { get; }
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

    public interface IItemCodecProvider
    {
        IReadOnlyList<IItemCodec> ItemCodecs { get; }
    }

    public interface IItemHandler
    {
        int Priority { get; }
    }

    public interface IClientIncomingItemHandler : IItemHandler
    {
        bool CanHandleIncomingItem(FrameworkPacket itemPacket);

        ClientIncomingItemResult HandleIncomingItem(FrameworkPacket itemPacket, ClientFrameworkContext context);
    }

    public interface IClientOutgoingItemHandler : IItemHandler
    {
        bool CanHandleOutgoingItem(FrameworkItemPayload itemPayload);

        ClientOutgoingItemResult HandleOutgoingItem(FrameworkItemPayload itemPayload, ClientFrameworkContext context);
    }

    public interface IServerItemHandler : IItemHandler
    {
        bool CanHandleIncomingItem(FrameworkPacket itemPacket);

        ServerIncomingItemResult HandleIncomingItem(FrameworkPacket itemPacket, ServerFrameworkContext context, IReadOnlyList<IItemCodec> codecs);
    }

    public interface IServerInboundItemInterceptor : IItemHandler
    {
        bool CanInterceptIncomingItem(FrameworkPacket itemPacket);

        ServerIncomingItemResult InterceptIncomingItem(FrameworkPacket itemPacket, ServerFrameworkContext context);
    }

    public interface IServerDefaultItemHandler : IServerItemHandler
    {
    }

    public interface IServerItemObserver : IItemHandler
    {
        bool CanObserveIncomingItem(FrameworkPacket itemPacket);

        void ObserveIncomingItem(FrameworkPacket itemPacket, ServerFrameworkContext context, ItemHandlingResultAction terminalAction);
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

    /// <summary>
    /// 客户端出站命令管线接口。Handler 按 Priority 排序依次执行。
    /// 与 IClientCommandHandler（入站）正交——插件可以只实现其一或同时实现。
    ///
    /// 设计哲学 §3.7：所有通信必须通过 handler 管线，不得直连传输层。
    /// 设计哲学 §6：此接口为增量新增，不修改或删除现有 IClientCommandHandler。
    /// </summary>
    public interface IClientOutgoingCommandHandler : ICommandHandler
    {
        /// <summary>判断此 handler 是否可以处理该出站命令。</summary>
        bool CanHandleOutgoingCommand(FrameworkPacket command);

        /// <summary>
        /// 处理出站命令。返回 FrameworkPacket 由框架发送。
        /// 返回 null 或 Action == Continue 则传递给下一个 handler。
        /// </summary>
        ClientOutgoingCommandResult HandleOutgoingCommand(FrameworkPacket command, ClientFrameworkContext context);
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

    public sealed class ClientOutgoingCommandResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkPacket Command { get; set; }
    }

    public sealed class ServerIncomingCommandResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkPacket Command { get; set; }
    }

    public sealed class ClientIncomingItemResult
    {
        public ItemHandlingResultAction Action { get; set; } = ItemHandlingResultAction.Handled;

        public FrameworkPacket Item { get; set; }

        public FrameworkDisplayMessage DisplayMessage { get; set; }
    }

    public sealed class ClientOutgoingItemResult
    {
        public ItemHandlingResultAction Action { get; set; } = ItemHandlingResultAction.Handled;

        public FrameworkPacket Item { get; set; }
    }

    public sealed class ServerIncomingItemResult
    {
        public ItemHandlingResultAction Action { get; set; } = ItemHandlingResultAction.Handled;

        public FrameworkPacket Item { get; set; }

        public FrameworkItemPayload ReplacedPayload { get; set; }

        public object DecodedItem { get; set; }

        public string HandledByHandlerId { get; set; }

        public string FailureReason { get; set; }

        public Exception FailureException { get; set; }
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

    public sealed class ExtensionDiscoveryResult
    {
        public string ExtensionId { get; set; }
        public string DisplayName { get; set; }
        public string Version { get; set; }
        public string AssemblyName { get; set; }
        public ExtensionModuleState State { get; set; }
        public string StateDetail { get; set; }
        public string SourcePackageId { get; set; }
        public List<string> RegisteredApis { get; set; } = new List<string>();
        public List<string> ConsumedApis { get; set; } = new List<string>();
        public List<string> DependsOn { get; set; } = new List<string>();

        public static ExtensionDiscoveryResult FromModuleType(Type moduleType, ExtensionHostContext hostContext)
        {
            ExtensionDiscoveryResult result = new ExtensionDiscoveryResult
            {
                DisplayName = moduleType.Name,
                AssemblyName = moduleType.Assembly.GetName().Name,
                Version = moduleType.Assembly.GetName().Version?.ToString() ?? "0.0.0.0",
                State = ExtensionModuleState.Discovered
            };

            if (hostContext?.ResolveSourcePackageId != null)
            {
                result.SourcePackageId = hostContext.ResolveSourcePackageId(moduleType.Assembly);
            }

            return result;
        }
    }

    /// <summary>
    /// 扩展管理面板日志条目。由框架客户端的 RaiseLogEntry 写入有界环形缓冲，
    /// ExtensionManagerTab 只读快照渲染。设计哲学 §3.6：缓冲有容量上限，防止无限增长。
    /// </summary>
    public sealed class FrameworkLogEntry
    {
        public FrameworkLogEntry(string message, LogLevel level, long timestampUtcTicks)
        {
            Message = message ?? string.Empty;
            Level = level;
            TimestampUtcTicks = timestampUtcTicks;
        }

        /// <summary>日志文本（不含时间戳前缀，时间戳前缀由 UI 渲染时拼接）。</summary>
        public string Message { get; }

        /// <summary>日志级别，用于 UI 着色。</summary>
        public LogLevel Level { get; }

        /// <summary>UTC ticks，用于 UI 显示本地时间。</summary>
        public long TimestampUtcTicks { get; }
    }

    public sealed class DiscoveredPhinixExtensions
    {
        public IExtensionApiRegistry ApiRegistry { get; internal set; } = new ExtensionApiRegistry();

        public List<IPhinixExtension> Extensions { get; } = new List<IPhinixExtension>();

        public List<IPhinixExtensionModule> Modules { get; } = new List<IPhinixExtensionModule>();

        public List<string> Diagnostics { get; } = new List<string>();

        public List<string> Warnings { get; } = new List<string>();

        public List<ExtensionDiscoveryResult> ExtensionResults { get; } = new List<ExtensionDiscoveryResult>();

        /// <summary>
        /// 本次发现过程中构建的扩展依赖图。纯反射构建，用于 UI 展示依赖关系和禁用影响。
        /// </summary>
        public ExtensionDependencyGraph DependencyGraph { get; internal set; }

        public List<ICapabilityProvider> CapabilityProviders { get; } = new List<ICapabilityProvider>();

        public List<IMessageInterceptor> MessageInterceptors { get; } = new List<IMessageInterceptor>();

        public List<IMessageRenderer> MessageRenderers { get; } = new List<IMessageRenderer>();

        public List<IClientMessageHandler> ClientMessageHandlers { get; } = new List<IClientMessageHandler>();

        public List<IServerMessageHandler> ServerMessageHandlers { get; } = new List<IServerMessageHandler>();

        public List<IServerInboundMessageInterceptor> ServerInboundMessageInterceptors { get; } = new List<IServerInboundMessageInterceptor>();

        public List<IServerDefaultMessageHandler> ServerDefaultMessageHandlers { get; } = new List<IServerDefaultMessageHandler>();

        public List<IServerMessageObserver> ServerMessageObservers { get; } = new List<IServerMessageObserver>();

        public List<IItemCodec> ItemCodecs { get; } = new List<IItemCodec>();

        public List<IClientIncomingItemHandler> ClientIncomingItemHandlers { get; } = new List<IClientIncomingItemHandler>();

        public List<IClientOutgoingItemHandler> ClientOutgoingItemHandlers { get; } = new List<IClientOutgoingItemHandler>();

        public List<IServerItemHandler> ServerItemHandlers { get; } = new List<IServerItemHandler>();

        public List<IServerInboundItemInterceptor> ServerInboundItemInterceptors { get; } = new List<IServerInboundItemInterceptor>();

        public List<IServerDefaultItemHandler> ServerDefaultItemHandlers { get; } = new List<IServerDefaultItemHandler>();

        public List<IServerItemObserver> ServerItemObservers { get; } = new List<IServerItemObserver>();

        public List<IClientCommandHandler> ClientCommandHandlers { get; } = new List<IClientCommandHandler>();

        public List<IServerCommandHandler> ServerCommandHandlers { get; } = new List<IServerCommandHandler>();

        public List<IServerInboundCommandInterceptor> ServerInboundCommandInterceptors { get; } = new List<IServerInboundCommandInterceptor>();

        public List<IServerDefaultCommandHandler> ServerDefaultCommandHandlers { get; } = new List<IServerDefaultCommandHandler>();

        public List<IServerCommandObserver> ServerCommandObservers { get; } = new List<IServerCommandObserver>();

        public List<IServerOutboundPacketInterceptor> ServerOutboundPacketInterceptors { get; } = new List<IServerOutboundPacketInterceptor>();

        public List<IServerConsoleCommandProvider> ConsoleCommandProviders { get; } = new List<IServerConsoleCommandProvider>();

    }
}
