using Improbable.Worker;
using RogueFleet.Asteroids;
using RogueFleet.Core;
using RogueFleet.Items;
using RogueFleet.Ships;
using RogueFleet.Ships.Modules;
using ShipWorker.ECS.ModuleSystems;
using System.Collections.Concurrent;

namespace ShipWorker.ECS
{
    internal readonly struct LogMessage
    {
        internal readonly LogLevel logLevel;
        internal readonly string logger;
        internal readonly string message;

        internal LogMessage(LogLevel logLevel, string logger, string message)
        {
            this.logLevel = logLevel;
            this.logger = logger;
            this.message = message;
        }
    }

    public static class SpatialOSConnectionSystem
    {
        internal static Connection connection;

        static readonly UpdateParameters updateParameterNoLoopback = new UpdateParameters() { Loopback = ComponentUpdateLoopback.None };
        //static readonly UpdateParameters updateParameterLoopback = new UpdateParameters() { Loopback = ComponentUpdateLoopback.ShortCircuited };

        //static readonly CommandParameters commandParameterShortcircuit = new CommandParameters() { AllowShortCircuit = true };
        static readonly CommandParameters commandParameterNoShortCircuit = new CommandParameters() { AllowShortCircuit = false };

        internal static void Update()
        {
            SendCommandResponses();

            SendAddComponents();

            SendComponentUpdates();

            SendCommandRequests();

            SendLogMessages();
        }

        #region AddComponents
        internal static readonly ConcurrentQueue<AddComponentOp<ResourceInventoryData>> addResourceInventoryOps = new ConcurrentQueue<AddComponentOp<ResourceInventoryData>>();
        internal static readonly ConcurrentQueue<AddComponentOp<ModuleInventoryData>> addModuleInventoryOps = new ConcurrentQueue<AddComponentOp<ModuleInventoryData>>();
        internal static readonly ConcurrentQueue<AddComponentOp<MapItemsData>> addMapItemsOps = new ConcurrentQueue<AddComponentOp<MapItemsData>>();
        internal static readonly ConcurrentQueue<AddComponentOp<SamplerData>> addSamplerOps = new ConcurrentQueue<AddComponentOp<SamplerData>>();
        internal static readonly ConcurrentQueue<AddComponentOp<ScannerData>> addScannerOps = new ConcurrentQueue<AddComponentOp<ScannerData>>();
        internal static readonly ConcurrentQueue<AddComponentOp<SensorData>> addSensorOps = new ConcurrentQueue<AddComponentOp<SensorData>>();

        static void SendAddComponents()
        {
            ProcessAdds(ResourceInventory.Metaclass, addResourceInventoryOps);
            ProcessAdds(ModuleInventory.Metaclass, addModuleInventoryOps);
            ProcessAdds(MapItems.Metaclass, addMapItemsOps);
            ProcessAdds(Sampler.Metaclass, addSamplerOps);
            ProcessAdds(Scanner.Metaclass, addScannerOps);
            ProcessAdds(Sensor.Metaclass, addSensorOps);
        }

        static void ProcessAdds<TComponent, TData, TUpdate>(IComponentMetaclass<TComponent, TData, TUpdate> metaclass, ConcurrentQueue<AddComponentOp<TData>> addOps) where TComponent : IComponentMetaclass
        {
            while (addOps.Count > 0)
            {
                if (addOps.TryDequeue(out var op))
                {
                    connection.SendAddComponent(metaclass, op.EntityId, op.Data, updateParameterNoLoopback);
                }
            }
        }
        #endregion

        #region Command Requests
        internal static readonly ConcurrentQueue<CommandRequestOp<Communication.Commands.Chat, ChatMessage>> requestChatOps = new ConcurrentQueue<CommandRequestOp<Communication.Commands.Chat, ChatMessage>>();
        internal static readonly ConcurrentQueue<CommandRequestOp<AsteroidSpawner.Commands.PopulateGridCell, PopulateGridCellRequest>> requestPopulateGridCellOps = new ConcurrentQueue<CommandRequestOp<AsteroidSpawner.Commands.PopulateGridCell, PopulateGridCellRequest>>();
        internal static readonly ConcurrentQueue<CommandRequestOp<Harvestable.Commands.ExtractResource, ResourceExtractionRequest>> requestExtractResourceOps = new ConcurrentQueue<CommandRequestOp<Harvestable.Commands.ExtractResource, ResourceExtractionRequest>>();
        internal static readonly ConcurrentQueue<CommandRequestOp<Harvestable.Commands.GenerateResource, ResourceGenerationRequest>> requestGenerateResourceOps = new ConcurrentQueue<CommandRequestOp<Harvestable.Commands.GenerateResource, ResourceGenerationRequest>>();
        internal static readonly ConcurrentQueue<CommandRequestOp<ClientConnection.Commands.Ping, PingRequest>> pingEntityIdOps = new ConcurrentQueue<CommandRequestOp<ClientConnection.Commands.Ping, PingRequest>>();
        internal static readonly ConcurrentQueue<EntityId> deleteEntityIdOps = new ConcurrentQueue<EntityId>();

        static void SendCommandRequests()
        {
            ProcessRequests(Communication.Commands.Chat.Metaclass, requestChatOps);
            ProcessRequests(AsteroidSpawner.Commands.PopulateGridCell.Metaclass, requestPopulateGridCellOps);
            ProcessRequests(Harvestable.Commands.ExtractResource.Metaclass, requestExtractResourceOps, SamplersSystem.mapRequestIdsToAsteroidIds);
            ProcessRequests(Harvestable.Commands.GenerateResource.Metaclass, requestGenerateResourceOps, ScannersSystem.mapRequestsToAsteroids);
            ProcessRequests(ClientConnection.Commands.Ping.Metaclass, pingEntityIdOps);

            while (deleteEntityIdOps.Count > 0)
            {
                if (deleteEntityIdOps.TryDequeue(out var op))
                {
                    connection.SendDeleteEntityRequest(op, null);
                }
            }
        }

        static void ProcessRequests<TCommand, TRequest, TResponse>(ICommandMetaclass<TCommand, TRequest, TResponse> metaclass, ConcurrentQueue<CommandRequestOp<TCommand, TRequest>> requestOps, ConcurrentDictionary<long, long> requestIds) where TCommand : ICommandMetaclass
        {
            while (requestOps.Count > 0)
            {
                if (requestOps.TryDequeue(out var op))
                {
                    var outgoingRequest = connection.SendCommandRequest(metaclass, op.EntityId, op.Request, null, commandParameterNoShortCircuit);

                    requestIds[outgoingRequest.Id] = op.EntityId.Id;
                }
            }
        }

        static void ProcessRequests<TCommand, TRequest, TResponse>(ICommandMetaclass<TCommand, TRequest, TResponse> metaclass, ConcurrentQueue<CommandRequestOp<TCommand, TRequest>> requestOps) where TCommand : ICommandMetaclass
        {
            while (requestOps.Count > 0)
            {
                if (requestOps.TryDequeue(out var op))
                {
                    connection.SendCommandRequest(metaclass, op.EntityId, op.Request, null, commandParameterNoShortCircuit);
                }
            }
        }
        #endregion

        #region Command Responses
        internal static readonly ConcurrentQueue<CommandResponseOp<Communication.Commands.Chat, ChatMessage>> responseChatMessageOps = new ConcurrentQueue<CommandResponseOp<Communication.Commands.Chat, ChatMessage>>();
        internal static readonly ConcurrentQueue<CommandResponseOp<Damageable.Commands.TakeDamage, DamageResponse>> responseTakeDamageOps = new ConcurrentQueue<CommandResponseOp<Damageable.Commands.TakeDamage, DamageResponse>>();

        static void SendCommandResponses()
        {
            ProcessResponses(Communication.Commands.Chat.Metaclass, responseChatMessageOps);
            ProcessResponses(Damageable.Commands.TakeDamage.Metaclass, responseTakeDamageOps);
        }

        static void ProcessResponses<TCommand, TRequest, TResponse>(ICommandMetaclass<TCommand, TRequest, TResponse> metaclass, ConcurrentQueue<CommandResponseOp<TCommand, TResponse>> responseOps) where TCommand : ICommandMetaclass
        {
            while (responseOps.Count > 0)
            {
                if (responseOps.TryDequeue(out var op))
                {
                    var id = new RequestId<IncomingCommandRequest<TCommand>>(op.RequestId.Id);

                    connection.SendCommandResponse(metaclass, id, op.Response.Value);
                }
            }
        }
        #endregion

        #region Updates
        internal static readonly ConcurrentQueue<ComponentUpdateOp<Communication.Update>> updateCommunicationOps = new ConcurrentQueue<ComponentUpdateOp<Communication.Update>>();

        internal static readonly ConcurrentQueue<ComponentUpdateOp<Rechargeable.Update>> updateRechargeableOps = new ConcurrentQueue<ComponentUpdateOp<Rechargeable.Update>>();
        internal static readonly ConcurrentQueue<ComponentUpdateOp<Damageable.Update>> updateDamageableOps = new ConcurrentQueue<ComponentUpdateOp<Damageable.Update>>();

        internal static readonly ConcurrentQueue<ComponentUpdateOp<ResourceInventory.Update>> updateResourceInventoryOps = new ConcurrentQueue<ComponentUpdateOp<ResourceInventory.Update>>();
        internal static readonly ConcurrentQueue<ComponentUpdateOp<ModuleInventory.Update>> updateModuleInventoryOps = new ConcurrentQueue<ComponentUpdateOp<ModuleInventory.Update>>();
        internal static readonly ConcurrentQueue<ComponentUpdateOp<MapItems.Update>> updateMapItemsOps = new ConcurrentQueue<ComponentUpdateOp<MapItems.Update>>();
        internal static readonly ConcurrentQueue<ComponentUpdateOp<Modular.Update>> updateModularOps = new ConcurrentQueue<ComponentUpdateOp<Modular.Update>>();

        internal static readonly ConcurrentQueue<ComponentUpdateOp<Scanner.Update>> updateScannerOps = new ConcurrentQueue<ComponentUpdateOp<Scanner.Update>>();
        internal static readonly ConcurrentQueue<ComponentUpdateOp<Sensor.Update>> updateSensorOps = new ConcurrentQueue<ComponentUpdateOp<Sensor.Update>>();
        internal static readonly ConcurrentQueue<ComponentUpdateOp<Sampler.Update>> updateSamplerOps = new ConcurrentQueue<ComponentUpdateOp<Sampler.Update>>();

        static void SendComponentUpdates()
        {
            ProcessUpdates(Communication.Metaclass, updateCommunicationOps);

            ProcessUpdates(Rechargeable.Metaclass, updateRechargeableOps);
            ProcessUpdates(Damageable.Metaclass, updateDamageableOps);

            ProcessUpdates(ResourceInventory.Metaclass, updateResourceInventoryOps);
            ProcessUpdates(ModuleInventory.Metaclass, updateModuleInventoryOps);
            ProcessUpdates(MapItems.Metaclass, updateMapItemsOps);
            ProcessUpdates(Modular.Metaclass, updateModularOps);

            ProcessUpdates(Scanner.Metaclass, updateScannerOps);
            ProcessUpdates(Sensor.Metaclass, updateSensorOps);
            ProcessUpdates(Sampler.Metaclass, updateSamplerOps);
        }

        static void ProcessUpdates<TComponent, TData, TUpdate>(IComponentMetaclass<TComponent, TData, TUpdate> metaclass, ConcurrentQueue<ComponentUpdateOp<TUpdate>> updateOps) where TComponent : IComponentMetaclass
        {
            while (updateOps.Count > 0)
            {
                if (updateOps.TryDequeue(out var op))
                {
                    connection.SendComponentUpdate(metaclass, op.EntityId, op.Update, updateParameterNoLoopback);
                }
            }
        }
        #endregion

        #region Logging
        internal static readonly ConcurrentQueue<LogMessage> logMessages = new ConcurrentQueue<LogMessage>();

        static void SendLogMessages()
        {
            while (logMessages.Count > 0)
            {
                if (logMessages.TryDequeue(out var op))
                {
                    //TODO add a worker flag to change at what minimum level logs are sent.

                    connection.SendLogMessage(op.logLevel, op.logger, op.message);
                }
            }
        }
        #endregion
    }
}
