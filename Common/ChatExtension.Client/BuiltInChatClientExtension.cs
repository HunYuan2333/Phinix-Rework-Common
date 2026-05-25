using System.Collections.Generic;
using PhinixClient.Framework;
using Utils.Framework;

namespace Phinix.ChatExtension.Client
{
    [PhinixExtension("builtin.chat")]
    public class BuiltInChatClientExtension : IPhinixExtensionModule, ICapabilityProvider, IClientMessageHandler, IClientCommandHandler, IMessageRenderer
    {
        private IFrameworkChatClientApi chatApi;

        public string ExtensionId => "builtin.chat";

        public int Priority => 1000;

        public void Register(IExtensionBuilder builder)
        {
            chatApi = chatApi ?? new PhinixFrameworkChatService();
            builder.RegisterApi(chatApi);
            builder.AddCapabilityProvider(this);
            builder.AddClientMessageHandler(this);
            builder.AddClientCommandHandler(this);
            builder.AddMessageRenderer(this);
        }

        public IEnumerable<string> GetCapabilities()
        {
            yield return FrameworkChatProtocol.MessageType;
            yield return FrameworkChatProtocol.HistoryRequestType;
            yield return FrameworkChatProtocol.HistorySyncCompleteType;
        }

        public bool CanHandleOutgoingText(string rawMessage)
        {
            return chatApi != null &&
                   !string.IsNullOrWhiteSpace(rawMessage);
        }

        public ClientOutgoingMessageResult HandleOutgoingText(string rawMessage, ClientFrameworkContext context)
        {
            return new ClientOutgoingMessageResult
            {
                Action = MessageHandlingResultAction.Handled,
                Message = chatApi.CreateOutgoingMessage(rawMessage, context)
            };
        }

        public bool CanHandleIncomingMessage(FrameworkPacket message)
        {
            return message != null && message.MessageType == FrameworkChatProtocol.MessageType;
        }

        public ClientIncomingMessageResult HandleIncomingMessage(FrameworkPacket message, ClientFrameworkContext context)
        {
            return new ClientIncomingMessageResult
            {
                Action = MessageHandlingResultAction.Handled,
                DisplayMessage = chatApi.RenderMessage(message)
            };
        }

        public bool CanHandleIncomingCommand(FrameworkPacket command)
        {
            return command != null && command.MessageType == FrameworkChatProtocol.HistorySyncCompleteType;
        }

        public ClientIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ClientFrameworkContext context)
        {
            chatApi?.NotifyHistorySynced();

            return new ClientIncomingCommandResult
            {
                Action = MessageHandlingResultAction.Handled
            };
        }

        public bool CanRender(FrameworkPacket message)
        {
            return message != null && message.MessageType == FrameworkChatProtocol.MessageType;
        }

        public FrameworkDisplayMessage Render(FrameworkPacket message)
        {
            return chatApi.RenderMessage(message);
        }
    }
}
