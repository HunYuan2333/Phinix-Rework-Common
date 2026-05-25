using System.Collections.Generic;
using PhinixClient.Framework;
using Utils.Framework;

namespace Phinix.TradeExtension.Client
{
    [PhinixExtension(FrameworkTradeProtocol.Capability)]
    public sealed class BuiltInTradeClientExtension : IPhinixExtensionModule, ICapabilityProvider, IClientCommandHandler
    {
        private IFrameworkTradeClientApi tradeApi;

        public string ExtensionId => FrameworkTradeProtocol.Capability;

        public int Priority => 1100;

        public void Register(IExtensionBuilder builder)
        {
            tradeApi = tradeApi ?? new PhinixFrameworkTradeClientService(
                builder.HostContext.GetRequiredService<ITradeItemPayloadEncoder>(),
                builder.HostContext.GetRequiredService<IClientUserDirectory>(),
                logEvent => builder.HostContext.Log?.Invoke(logEvent.Message, logEvent.LogLevel));
            builder.RegisterApi(tradeApi);
            builder.AddCapabilityProvider(this);
            builder.AddClientCommandHandler(this);
        }

        public IEnumerable<string> GetCapabilities()
        {
            yield return FrameworkTradeProtocol.Capability;
            yield return FrameworkTradeProtocol.CreateRequestType;
            yield return FrameworkTradeProtocol.CreateResponseType;
            yield return FrameworkTradeProtocol.SnapshotType;
            yield return FrameworkTradeProtocol.OfferUpdateRequestType;
            yield return FrameworkTradeProtocol.OfferUpdateResponseType;
            yield return FrameworkTradeProtocol.StatusUpdateRequestType;
            yield return FrameworkTradeProtocol.StatusUpdateResponseType;
            yield return FrameworkTradeProtocol.CompletedEventType;
            yield return FrameworkTradeProtocol.CancelledEventType;
        }

        public bool CanHandleIncomingCommand(FrameworkPacket command)
        {
            return command != null &&
                   (command.MessageType == FrameworkTradeProtocol.SnapshotType ||
                    command.MessageType == FrameworkTradeProtocol.CreateResponseType ||
                    command.MessageType == FrameworkTradeProtocol.OfferUpdateResponseType ||
                    command.MessageType == FrameworkTradeProtocol.StatusUpdateResponseType ||
                    command.MessageType == FrameworkTradeProtocol.CompletedEventType ||
                    command.MessageType == FrameworkTradeProtocol.CancelledEventType);
        }

        public ClientIncomingCommandResult HandleIncomingCommand(FrameworkPacket command, ClientFrameworkContext context)
        {
            switch (command.MessageType)
            {
                case FrameworkTradeProtocol.SnapshotType:
                    tradeApi.HandleSnapshot(command);
                    break;
                case FrameworkTradeProtocol.CreateResponseType:
                    tradeApi.HandleCreateResponse(command);
                    break;
                case FrameworkTradeProtocol.OfferUpdateResponseType:
                    tradeApi.HandleOfferUpdateResponse(command);
                    break;
                case FrameworkTradeProtocol.StatusUpdateResponseType:
                    tradeApi.HandleStatusUpdateResponse(command);
                    break;
                case FrameworkTradeProtocol.CompletedEventType:
                    tradeApi.HandleCompletedEvent(command);
                    break;
                case FrameworkTradeProtocol.CancelledEventType:
                    tradeApi.HandleCancelledEvent(command);
                    break;
            }

            return new ClientIncomingCommandResult
            {
                Action = MessageHandlingResultAction.Handled
            };
        }
    }
}
