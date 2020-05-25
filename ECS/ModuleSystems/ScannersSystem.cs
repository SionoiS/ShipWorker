using Improbable.Worker;
using RogueFleet.Asteroids;
using RogueFleet.Ships.Modules;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShipWorker.ECS.ModuleSystems
{
    public static class ScannersSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static ScannersSystem()
        {
            dispatcher.OnAddComponent(Scanner.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(Scanner.Metaclass, OnComponentRemoved);
            dispatcher.OnCommandResponse(Harvestable.Commands.GenerateResource.Metaclass, OnGenerateResourceResponse);
        }

        static void OnComponentAdded(AddComponentOp<ScannerData> op)
        {
            components[op.EntityId.Id] = op.Data;
        }

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            components.Remove(op.EntityId.Id);
        }

        static readonly ConcurrentQueue<CommandResponseOp<Harvestable.Commands.GenerateResource, ResourceGenerationReponse>> commandResponseOps = new ConcurrentQueue<CommandResponseOp<Harvestable.Commands.GenerateResource, ResourceGenerationReponse>>();

        static void OnGenerateResourceResponse(CommandResponseOp<Harvestable.Commands.GenerateResource, ResourceGenerationReponse> op)
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

        static readonly Dictionary<long, ScannerData> components = new Dictionary<long, ScannerData>();


        internal static readonly ConcurrentDictionary<long, long> mapRequestsToAsteroids = new ConcurrentDictionary<long, long>();
        static readonly Dictionary<long, long> mapAsteroidsToShips = new Dictionary<long, long>();

        static readonly ConcurrentQueue<(long entityId, byte moduleId)> useModuleOps = new ConcurrentQueue<(long entityId, byte moduleId)>();

        internal static void QueueUseModuleOp(long entityId, byte moduleId)
        {
            useModuleOps.Enqueue((entityId, moduleId));
        }

        static readonly ConcurrentQueue<(long entityId, byte moduleId, ScannerStat scannerStat)> addModuleOps = new ConcurrentQueue<(long entityId, byte moduleId, ScannerStat scannerStat)>();

        internal static void QueueAddModuleOp(long entityId, byte moduleId, ScannerStat scannerStat)
        {
            addModuleOps.Enqueue((entityId, moduleId, scannerStat));
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

            ProcessUseModuleOps(useModuleOps, mapAsteroidsToShips, components);

            ProcessCommandOps(commandResponseOps, mapRequestsToAsteroids, mapAsteroidsToShips);
        }

        static void ProcessAddModuleOps(ConcurrentQueue<(long entityId, byte moduleId, ScannerStat scannerStat)> addModuleOps, Dictionary<long, ScannerData> components)
        {
            while (addModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId, scannerStat) = op;

                if (components.TryGetValue(entityId, out var component))
                {
                    component.scanners.Add(moduleId, scannerStat);

                    components[entityId] = component;

                    SpatialOSConnectionSystem.updateScannerOps.Enqueue(
                        new ComponentUpdateOp<Scanner.Update>
                        {
                            EntityId = new EntityId(entityId),
                            Update = new Scanner.Update().SetScanners(component.scanners),
                        });
                }
                else
                {
                    component = new ScannerData
                    {
                        scanners = new Improbable.Collections.Map<int, ScannerStat>
                    {
                        { moduleId, scannerStat },
                    }
                    };

                    components[entityId] = component;

                    SpatialOSConnectionSystem.addScannerOps.Enqueue(
                        new AddComponentOp<ScannerData>
                        {
                            EntityId = new EntityId(entityId),
                            Data = component,
                        });
                }
            }
        }

        static void ProcessUseModuleOps(ConcurrentQueue<(long entityId, byte moduleId)> useModuleOps, Dictionary<long, long> mapAsteroidIdsToShipIds, Dictionary<long, ScannerData> components)
        {
            while (useModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId) = op;

                if (!components.TryGetValue(entityId, out var component))
                {
                    return;
                }

                if (!component.scanners.TryGetValue(moduleId, out var scannerStat))
                {
                    return;
                }

                if (!IdentificationsSystem.TryGetUserDBId(entityId, out var userDbId))
                {
                    return;
                }

                if (!ExplorationHacksSystem.TryGetScannableEntityId(entityId, out var asteroidEntityId))
                {
                    return;
                }

                if (!RechargeablesSystem.HasCharge(entityId, moduleId))
                {
                    return;
                }

                DamageablesSystem.QueueUseModuleOp(entityId, moduleId);
                RechargeablesSystem.QueueUseModuleOp(entityId, moduleId);

                mapAsteroidIdsToShipIds.Add(asteroidEntityId, entityId);

                SpatialOSConnectionSystem.requestGenerateResourceOps.Enqueue(
                    new CommandRequestOp<Harvestable.Commands.GenerateResource, ResourceGenerationRequest>
                    {
                        EntityId = new EntityId(asteroidEntityId),
                        Request = new ResourceGenerationRequest(userDbId, scannerStat),
                    }
                );
            }
        }

        static void ProcessDeleteModuleOps(ConcurrentQueue<(long entityId, byte moduleId)> deleteModuleOps, Dictionary<long, ScannerData> components)
        {
            while (deleteModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId) = op;

                if (components.TryGetValue(entityId, out var component) && component.scanners.Remove(moduleId))
                {
                    components[entityId] = component;

                    SpatialOSConnectionSystem.updateScannerOps.Enqueue(
                    new ComponentUpdateOp<Scanner.Update>
                    {
                        EntityId = new EntityId(entityId),
                        Update = new Scanner.Update().SetScanners(component.scanners),
                    });
                }
            }
        }

        static void ProcessCommandOps(ConcurrentQueue<CommandResponseOp<Harvestable.Commands.GenerateResource, ResourceGenerationReponse>> commandResponseOps, ConcurrentDictionary<long, long> mapRequestIdsToAsteroidIds, Dictionary<long, long> mapAsteroidIdsToShipIds)
        {
            while (commandResponseOps.TryDequeue(out var op))
            {
                if (op.StatusCode == StatusCode.Success && op.Response.HasValue
                    && mapRequestIdsToAsteroidIds.TryRemove(op.RequestId.Id, out var asteroidId)
                    && mapAsteroidIdsToShipIds.TryGetValue(asteroidId, out var shipId))
                {
                    mapAsteroidIdsToShipIds.Remove(asteroidId);

                    SpatialOSConnectionSystem.updateScannerOps.Enqueue(
                        new ComponentUpdateOp<Scanner.Update>
                        {
                            EntityId = new EntityId(shipId),
                            Update = new Scanner.Update().AddScanResult(new ScanResult(op.Response.Value.databaseId, op.Response.Value.quantity)),
                        }
                    );
                }
            }
        }
    }
}
