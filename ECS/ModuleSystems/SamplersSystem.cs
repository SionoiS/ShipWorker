using Improbable.Worker;
using ItemGenerator.Resources;
using RogueFleet.Asteroids;
using RogueFleet.Ships.Modules;
using ShipWorker.ECS.ItemSystems;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShipWorker.ECS.ModuleSystems
{
    public static class SamplersSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static SamplersSystem()
        {
            dispatcher.OnAddComponent(Sampler.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(Sampler.Metaclass, OnComponentRemoved);
            dispatcher.OnCommandResponse(Harvestable.Commands.ExtractResource.Metaclass, OnExtractResourceResponse);
        }

        static void OnComponentAdded(AddComponentOp<SamplerData> op)
        {
            components[op.EntityId.Id] = op.Data;
        }

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            components.Remove(op.EntityId.Id);
        }

        static readonly ConcurrentQueue<CommandResponseOp<Harvestable.Commands.ExtractResource, ResourceExtractionReponse>> commandResponseOps = new ConcurrentQueue<CommandResponseOp<Harvestable.Commands.ExtractResource, ResourceExtractionReponse>>();

        static void OnExtractResourceResponse(CommandResponseOp<Harvestable.Commands.ExtractResource, ResourceExtractionReponse> op)
        {
            commandResponseOps.Enqueue(op);
        }

        static TimeSpan frameRate = TimeSpan.FromMilliseconds(100);
        static readonly Stopwatch stopwatch = new Stopwatch();

        internal static Task UpdateLoop()
        {
            return new Task(LocalImplemtation, TaskCreationOptions.LongRunning);

            void LocalImplemtation()
            {
                while (Startup.Connected)
                {
                    stopwatch.Restart();
                    Update();
                    stopwatch.Stop();

                    var frameTime = frameRate - stopwatch.Elapsed;
                    if (frameTime > TimeSpan.Zero)
                    {
                        Task.Delay(frameTime).Wait();
                    }
                    else
                    {
                        //connection.SendLogMessage(LogLevel.Warn, "Game Loop", string.Format("Frame Time {0}ms", frameTime.TotalMilliseconds.ToString("N0")));
                    }
                }
            }
        }

        static readonly Dictionary<long, SamplerData> components = new Dictionary<long, SamplerData>();

        internal static ConcurrentDictionary<long, long> mapRequestIdsToAsteroidIds = new ConcurrentDictionary<long, long>();
        static readonly Dictionary<long, long> mapAsteroidIdsToShipIds = new Dictionary<long, long>();

        static readonly ConcurrentQueue<(long entityId, byte moduleId)> useModuleOps = new ConcurrentQueue<(long entityId, byte moduleId)>();
        
        internal static void QueueUseModuleOp(long entityId, byte moduleId)
        {
            useModuleOps.Enqueue((entityId, moduleId));
        }

        static readonly ConcurrentQueue<(long entityId, byte moduleId, SamplerStat samplerStat)> addModuleOps = new ConcurrentQueue<(long entityId, byte moduleId, SamplerStat samplerStat)>();

        internal static void QueueAddModuleOp(long entityId, byte moduleId, SamplerStat samplerStat)
        {
            addModuleOps.Enqueue((entityId, moduleId, samplerStat));
        }

        static readonly ConcurrentQueue<(long entityId, byte moduleId)> deleteModuleOps = new ConcurrentQueue<(long entityId, byte moduleId)>();

        internal static void QueueDeleteModuleOp(long entityId, byte moduleId)
        {
            deleteModuleOps.Enqueue((entityId, moduleId));
        }

        static void Update()
        {
            ProcessDeleteModuleOps(deleteModuleOps, components);

            ProcessAddModuleOps(addModuleOps, components);

            ProcessUseModuleOps(useModuleOps, mapAsteroidIdsToShipIds, components);

            ProcessCommandOps(commandResponseOps, mapRequestIdsToAsteroidIds, mapAsteroidIdsToShipIds);
        }

        static void ProcessAddModuleOps(ConcurrentQueue<(long entityId, byte moduleId, SamplerStat samplerStat)> addModuleOps, Dictionary<long, SamplerData> components)
        {
            while (addModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId, samplerStat) = op;

                if (components.TryGetValue(entityId, out var component))
                {
                    component.samplers.Add(moduleId, samplerStat);

                    components[entityId] = component;

                    SpatialOSConnectionSystem.updateSamplerOps.Enqueue(
                        new ComponentUpdateOp<Sampler.Update>
                        {
                            EntityId = new EntityId(entityId),
                            Update = new Sampler.Update().SetSamplers(component.samplers),
                        });
                }
                else
                {
                    component = new SamplerData
                    {
                        samplers = new Improbable.Collections.Map<int, SamplerStat>
                    {
                        { moduleId, samplerStat },
                    }
                    };

                    components[entityId] = component;

                    SpatialOSConnectionSystem.addSamplerOps.Enqueue(
                        new AddComponentOp<SamplerData>
                        {
                            EntityId = new EntityId(entityId),
                            Data = component,
                        });
                }
            }
        }

        static void ProcessUseModuleOps(ConcurrentQueue<(long entityId, byte moduleId)> useModuleOps, Dictionary<long, long> mapAsteroidsToShips, Dictionary<long, SamplerData> components)
        {
            while (useModuleOps.TryDequeue(out var op))
            {
                var (shipEntityId, moduleId) = op;

                if (!components.TryGetValue(shipEntityId, out var component))
                {
                    return;
                }

                if (!component.samplers.TryGetValue(moduleId, out var samplerStat))
                {
                    return;
                }

                if (!IdentificationsSystem.TryGetEntityDBId(shipEntityId, out var shipDBPath))
                {
                    return;
                }

                if (!ExplorationHacksSystem.TryGetSampleableEntityId(shipEntityId, out var asteroidEntityId))
                {
                    return;
                }

                if (!RechargeablesSystem.HasCharge(shipEntityId, moduleId))
                {
                    return;
                }

                DamageablesSystem.QueueUseModuleOp(shipEntityId, moduleId);
                RechargeablesSystem.QueueUseModuleOp(shipEntityId, moduleId);

                mapAsteroidsToShips[asteroidEntityId] = shipEntityId;

                SpatialOSConnectionSystem.requestExtractResourceOps.Enqueue(
                    new CommandRequestOp<Harvestable.Commands.ExtractResource, ResourceExtractionRequest>
                    {
                        EntityId = new EntityId(asteroidEntityId),
                        Request = new ResourceExtractionRequest(shipDBPath, samplerStat.extractionRate),
                    });
            }
        }

        static void ProcessDeleteModuleOps(ConcurrentQueue<(long entityId, byte moduleId)> deleteModuleOps, Dictionary<long, SamplerData> components)
        {
            while (deleteModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId) = op;

                if (components.TryGetValue(entityId, out var component) && component.samplers.Remove(moduleId))
                {
                    components[entityId] = component;

                    SpatialOSConnectionSystem.updateSamplerOps.Enqueue(
                    new ComponentUpdateOp<Sampler.Update>
                    {
                        EntityId = new EntityId(entityId),
                        Update = new Sampler.Update().SetSamplers(component.samplers),
                    });
                }
            }
        }

        static void ProcessCommandOps(ConcurrentQueue<CommandResponseOp<Harvestable.Commands.ExtractResource, ResourceExtractionReponse>> commandResponseOps, ConcurrentDictionary<long, long> mapRequestIdsToAsteroidIds, Dictionary<long, long> mapAsteroidIdsToShipIds)
        {
            while (commandResponseOps.TryDequeue(out var op))
            {
                if (op.StatusCode == StatusCode.Success && op.Response.HasValue
                    && mapRequestIdsToAsteroidIds.TryRemove(op.RequestId.Id, out var asteroidEntityId)
                    && mapAsteroidIdsToShipIds.TryGetValue(asteroidEntityId, out var shipEntityId))
                {
                    var response = op.Response.Value;

                    mapAsteroidIdsToShipIds.Remove(asteroidEntityId);

                    ResourcesInventorySystem.QueueAddResourceOp(shipEntityId, response.databaseId, new Resource(response.type, response.quantity));
                }
            }
        }
    }
}
