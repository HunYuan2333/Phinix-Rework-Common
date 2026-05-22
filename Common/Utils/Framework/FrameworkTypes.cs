using System;
using System.Collections.Generic;

namespace Utils.Framework
{
    public static class FrameworkProtocol
    {
        public const string ModuleName = "PhinixFramework";
        public const int Version = 2;
        public const string KindHello = "hello";
        public const string KindCapabilities = "capabilities";
        public const string KindExtension = "extension";
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
        ReplacePayload = 2,
        SuppressDefault = 3,
        StopPropagation = 4,
        LegacyFallback = 5
    }

    public interface IPhinixExtension
    {
        string ExtensionId { get; }
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
        bool CanRender(FrameworkEnvelope envelope);

        FrameworkDisplayMessage Render(FrameworkEnvelope envelope);
    }

    public interface IClientMessageHandler : IMessageHandler
    {
        bool CanHandleOutgoingText(string rawMessage);

        ClientOutgoingMessageResult HandleOutgoingText(string rawMessage, ClientFrameworkContext context);

        bool CanHandleIncomingEnvelope(FrameworkEnvelope envelope);

        ClientIncomingMessageResult HandleIncomingEnvelope(FrameworkEnvelope envelope, ClientFrameworkContext context);
    }

    public interface IServerMessageHandler : IMessageHandler
    {
        bool CanHandleIncomingEnvelope(FrameworkEnvelope envelope);

        ServerIncomingMessageResult HandleIncomingEnvelope(FrameworkEnvelope envelope, ServerFrameworkContext context);
    }

    public interface IItemCodec
    {
        string CodecId { get; }

        bool CanEncode(object item, ItemCodecContext context);

        FrameworkItemPayload Encode(object item, ItemCodecContext context);

        bool CanDecode(FrameworkItemPayload payload, ItemCodecContext context);

        object Decode(FrameworkItemPayload payload, ItemCodecContext context);
    }

    public interface ITradeCompletionHandler
    {
        string HandlerId { get; }

        int Priority { get; }

        bool CanHandle(TradeCompletionContext context);

        void Handle(TradeCompletionContext context);
    }

    public sealed class ClientOutgoingMessageResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkEnvelope Envelope { get; set; }
    }

    public sealed class ClientIncomingMessageResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkEnvelope Envelope { get; set; }

        public FrameworkDisplayMessage DisplayMessage { get; set; }
    }

    public sealed class ServerIncomingMessageResult
    {
        public MessageHandlingResultAction Action { get; set; } = MessageHandlingResultAction.Handled;

        public FrameworkEnvelope Envelope { get; set; }
    }

    public sealed class ItemCodecContext
    {
        public FrameworkCompatibilityMode CompatibilityMode { get; set; }

        public Action<string, LogLevel> Log { get; set; }
    }

    public sealed class TradeCompletionContext
    {
        public string TradeId { get; set; }

        public string OtherPartyUuid { get; set; }

        public IReadOnlyCollection<FrameworkItemPayload> Items { get; set; } = Array.Empty<FrameworkItemPayload>();

        public Action<string, LogLevel> Log { get; set; }
    }

    public sealed class ClientFrameworkContext
    {
        public string SessionId { get; set; }

        public string SenderUuid { get; set; }

        public FrameworkCompatibilityMode CompatibilityMode { get; set; }

        public Action<FrameworkEnvelope> SendEnvelope { get; set; }

        public IReadOnlyCollection<string> RemoteCapabilities { get; set; } = Array.Empty<string>();

        public Func<string, bool> HasRemoteCapability { get; set; }

        public Action<string, LogLevel> Log { get; set; }
    }

    public sealed class ServerFrameworkContext
    {
        public string ConnectionId { get; set; }

        public string SessionId { get; set; }

        public string SenderUuid { get; set; }

        public Action<string, FrameworkEnvelope> SendEnvelope { get; set; }

        public Action<FrameworkEnvelope, string[]> BroadcastEnvelope { get; set; }

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

        public List<string> Warnings { get; } = new List<string>();

        public List<ICapabilityProvider> CapabilityProviders { get; } = new List<ICapabilityProvider>();

        public List<IMessageInterceptor> MessageInterceptors { get; } = new List<IMessageInterceptor>();

        public List<IMessageRenderer> MessageRenderers { get; } = new List<IMessageRenderer>();

        public List<IClientMessageHandler> ClientMessageHandlers { get; } = new List<IClientMessageHandler>();

        public List<IServerMessageHandler> ServerMessageHandlers { get; } = new List<IServerMessageHandler>();

        public List<IItemCodec> ItemCodecs { get; } = new List<IItemCodec>();

        public List<ITradeCompletionHandler> TradeCompletionHandlers { get; } = new List<ITradeCompletionHandler>();
    }
}
