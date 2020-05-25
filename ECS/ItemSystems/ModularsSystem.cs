using Improbable.Worker;
using RogueFleet.Items;
using RogueFleet.Ships.Modules;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShipWorker.ECS.ItemSystems
{
    static class ModularsSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static ModularsSystem()
        {
            dispatcher.OnAddComponent(Modular.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(Modular.Metaclass, OnComponentRemoved);
            dispatcher.OnComponentUpdate(ModularController.Metaclass, OnComponentUpdated);
        }

        static readonly ConcurrentQueue<AddComponentOp<ModularData>> addComponentOps = new ConcurrentQueue<AddComponentOp<ModularData>>();

        static void OnComponentAdded(AddComponentOp<ModularData> op)
        {
            addComponentOps.Enqueue(op);
        }

        static readonly ConcurrentQueue<RemoveComponentOp> removeComponentOps = new ConcurrentQueue<RemoveComponentOp>();

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            removeComponentOps.Enqueue(op);
        }

        static readonly ConcurrentQueue<ComponentUpdateOp<ModularController.Update>> updateOps = new ConcurrentQueue<ComponentUpdateOp<ModularController.Update>>();

        static void OnComponentUpdated(ComponentUpdateOp<ModularController.Update> op)
        {
            updateOps.Enqueue(op);
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

        static readonly Dictionary<long, ModularData> components = new Dictionary<long, ModularData>();

        //Ok for now since there's only 1 type of ship
        //Prolly move this to ItemGenerator later
        static readonly Dictionary<ModuleType, int> moduleSlots = new Dictionary<ModuleType, int>
        {
            { ModuleType.Sensor, 1 },
            { ModuleType.Scanner, 1 },
            { ModuleType.Sampler, 1 },
        };

        internal static bool IsInstalled(long entityId, byte moduleId)
        {
            if (components.TryGetValue(entityId, out var modularData))
            {
                return modularData.installedModuleIds.Contains(moduleId);
            }

            return false;
        }

        static void Update()
        {
            while (removeComponentOps.TryDequeue(out var op))
            {
                components.Remove(op.EntityId.Id);
            }

            while (addComponentOps.TryDequeue(out var op))
            {
                components[op.EntityId.Id] = op.Data;
            }

            ProcessUpdateOps();
        }

        static void ProcessUpdateOps()
        {
            while (updateOps.TryDequeue(out var op))
            {
                var entityId = op.EntityId.Id;
                var component = components[entityId];

                var update = new Modular.Update();

                var count = op.Update.uninstallModule.Count;
                for (int i = 0; i < count; i++)
                {
                    var uninstallModule = op.Update.uninstallModule[i];

                    if (component.installedModuleIds.Contains(uninstallModule.moduleId))
                    {
                        component.installedModuleIds.Remove(uninstallModule.moduleId);
                        update.SetInstalledModuleIds(component.installedModuleIds);
                    }
                }

                count = op.Update.installModule.Count;
                for (int i = 0; i < count; i++)
                {
                    var installModule = op.Update.installModule[i];
                    var moduleId = (byte)installModule.moduleId;

                    if (!ModulesInventorySystem.TryGetModuleInfo(entityId, moduleId, out var moduleInfo))
                    {
                        continue;
                    }

                    var moduleType = moduleInfo.type;

                    var tally = 0;
                    for (int j = 0; j < component.installedModuleIds.Count; j++)
                    {
                        var id = (byte)component.installedModuleIds[j];
                        var info = ModulesInventorySystem.GetModuleInfo(entityId, id);

                        if (moduleType != info.type)
                        {
                            continue;
                        }

                        tally++;
                    }

                    if (tally >= moduleSlots[moduleType])
                    {
                        continue;
                    }

                    if (!component.installedModuleIds.Contains(moduleId))
                    {
                        component.installedModuleIds.Add(moduleId);
                        update.SetInstalledModuleIds(component.installedModuleIds);
                    }
                }

                components[op.EntityId.Id] = component;

                SpatialOSConnectionSystem.updateModularOps.Enqueue(
                    new ComponentUpdateOp<Modular.Update>
                    {
                        EntityId = op.EntityId,
                        Update = update,
                    });
            }
        }
    }
}
