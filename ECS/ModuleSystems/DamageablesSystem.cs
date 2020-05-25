using Improbable.Worker;
using RogueFleet.Items;
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
    public static class DamageablesSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static DamageablesSystem()
        {
            dispatcher.OnAddComponent(Damageable.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(Damageable.Metaclass, OnComponentRemoved);
            dispatcher.OnCommandRequest(Damageable.Commands.TakeDamage.Metaclass, OnTakeDamageCommand);
        }

        static void OnComponentAdded(AddComponentOp<DamageableData> op)
        {
            components.Add(op.EntityId.Id);
        }

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            components.Remove(op.EntityId.Id);
        }

        static TimeSpan frameRate = TimeSpan.FromMilliseconds(100);//10hz
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

        static readonly ConcurrentQueue<(long entityId, byte moduleId, int damageTaken)> takeDamageOps = new ConcurrentQueue<(long entityId, byte moduleId, int damageTaken)>();
        
        internal static void QueueTakeDamageOp(long entityId, byte moduleId, int damage)
        {
            takeDamageOps.Enqueue((entityId, moduleId, damage));
        }

        internal static void QueueUseModuleOp(long entityId, byte moduleId)
        {
            takeDamageOps.Enqueue((entityId, moduleId, 1));
        }

        static readonly HashSet<long> components = new HashSet<long>();

        static void Update()
        {
            ProcessDamageModuleOps();
        }

        static void ProcessDamageModuleOps()
        {
            while (takeDamageOps.TryDequeue(out var op))
            {
                var (entityId, moduleId, damageTaken) = op;

                if (!components.Contains(entityId))//if no component not damageable
                {
                    continue;
                }

                DamageModuleOp(entityId, moduleId, damageTaken);
            }
        }

        public static void DamageModuleOp(long entityId, byte moduleId, int damageTaken)
        {
            var resourceInfos = ModulesInventorySystem.GetModuleResourceInfos(entityId, moduleId);
            
            var damageDivided = Math.DivRem(damageTaken, resourceInfos.Count, out var damageRemainder);

            var deletedCount = 0;
            for (int i = 0; i < resourceInfos.Count; i++)
            {
                var delta = damageDivided;

                if (i == 0)
                {
                    delta += damageRemainder;
                }

                if (delta < 1)
                {
                    continue;
                }

                var resourceInfo = resourceInfos[i];

                if (resourceInfo.quantity - delta < 1)
                {
                    ModulesInventorySystem.QueueDeleteModuleResourceOp(entityId, moduleId, resourceInfo.databaseId);
                    deletedCount++;
                }
                else
                {
                    ModulesInventorySystem.QueueUpdateModuleResourceOp(entityId, moduleId, resourceInfo.databaseId, delta);
                }
            }

            if (deletedCount == resourceInfos.Count)
            {
                ModulesInventorySystem.QueueDeleteModuleOp(entityId, moduleId);

                var moduleInfo = ModulesInventorySystem.GetModuleInfo(entityId, moduleId);
                switch (moduleInfo.type)
                {
                    case ModuleType.Sampler:
                        SamplersSystem.QueueDeleteModuleOp(entityId, moduleId);
                        break;
                    case ModuleType.Scanner:
                        ScannersSystem.QueueDeleteModuleOp(entityId, moduleId);
                        break;
                    case ModuleType.Sensor:
                        SensorsSystem.QueueDeleteModuleOp(entityId, moduleId);
                        break;
                }

                SpatialOSConnectionSystem.updateDamageableOps.Enqueue(
                new ComponentUpdateOp<Damageable.Update>
                {
                    EntityId = new EntityId(entityId),
                    Update = new Damageable.Update().AddModuleDestroyed(new ModuleDestroyed(moduleId)),
                });
            }
        }

        static void OnTakeDamageCommand(CommandRequestOp<Damageable.Commands.TakeDamage, DamageRequest> op)
        {
            takeDamageOps.Enqueue((op.EntityId.Id, (byte)op.Request.moduleId, op.Request.damage));

            SpatialOSConnectionSystem.responseTakeDamageOps.Enqueue(
                new CommandResponseOp<Damageable.Commands.TakeDamage, DamageResponse>
                {
                    EntityId = op.EntityId,
                    Response = new DamageResponse(),
                    RequestId = new RequestId<OutgoingCommandRequest<Damageable.Commands.TakeDamage>>(op.RequestId.Id),
                });
        }
    }
}
