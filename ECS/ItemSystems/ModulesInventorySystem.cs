using Google.Cloud.Firestore;
using Improbable.Worker;
using ItemGenerator.Modules;
using ItemGenerator.Resources;
using RogueFleet.Items;
using ShipWorker.ECS.ModuleSystems;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShipWorker.ECS.ItemSystems
{
    public static class ModulesInventorySystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static ModulesInventorySystem()
        {
            dispatcher.OnAddComponent(ModuleInventory.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(ModuleInventory.Metaclass, OnComponentRemoved);
        }

        static readonly ConcurrentQueue<AddComponentOp<ModuleInventoryData>> addComponentOps = new ConcurrentQueue<AddComponentOp<ModuleInventoryData>>();

        static void OnComponentAdded(AddComponentOp<ModuleInventoryData> op)
        {
            addComponentOps.Enqueue(op);
        }

        static readonly ConcurrentQueue<RemoveComponentOp> removeComponentOps = new ConcurrentQueue<RemoveComponentOp>();

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            removeComponentOps.Enqueue(op);
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

        static readonly Dictionary<long, ModuleInventoryData> components = new Dictionary<long, ModuleInventoryData>();

        internal static bool TryGetModuleInfo(long entityId, byte moduleId, out ModuleInfo moduleInfo)
        {
            if (components.TryGetValue(entityId, out var component) && component.modules.TryGetValue(moduleId, out moduleInfo))
            {
                return true;
            }

            moduleInfo = new ModuleInfo();
            return false;
        }

        internal static ModuleInfo GetModuleInfo(long entityId, byte moduleId)
        {
            return components[entityId].modules[moduleId];
        }

        internal static Improbable.Collections.List<ResourceInfo> GetModuleResourceInfos(long entityId, byte moduleId)
        {
            return components[entityId].modulesResources[moduleId].resourceInfos;
        }

        static readonly ConcurrentQueue<(long entityId, byte moduleId)> deleteModuleOps = new ConcurrentQueue<(long entityId, byte moduleId)>();

        internal static void QueueDeleteModuleOp(long entityId, byte moduleId)
        {
            deleteModuleOps.Enqueue((entityId, moduleId));
        }

        static readonly ConcurrentQueue<(long entityId, string databaseId, Module module, ResourceInfo[] resourceInfos)> addModuleOps = new ConcurrentQueue<(long entityId, string databaseId, Module module, ResourceInfo[] resourceInfos)>();

        internal static void QueueAddModuleOp(long entityId, string databaseId, Module module, ResourceInfo[] resourceInfos)
        {
            addModuleOps.Enqueue((entityId, databaseId, module, resourceInfos));
        }

        static readonly ConcurrentQueue<(long entityId, byte moduleId, string databaseId)> deleteModuleResourceOps = new ConcurrentQueue<(long entityId, byte moduleId, string databaseId)>();

        internal static void QueueDeleteModuleResourceOp(long entityId, byte moduleId, string databaseId)
        {
            deleteModuleResourceOps.Enqueue((entityId, moduleId, databaseId));
        }

        static readonly ConcurrentQueue<(long entityId, byte moduleId, string databaseId, int delta)> updateModuleResourceOps = new ConcurrentQueue<(long entityId, byte moduleId, string databaseId, int delta)>();

        internal static void QueueUpdateModuleResourceOp(long entityId, byte moduleId, string databaseId, int delta)
        {
            updateModuleResourceOps.Enqueue((entityId, moduleId, databaseId, delta));
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

            //To parallelize, per component aggregate of actions would be necessary.

            ProcessDeleteModuleOps();

            ProcessDeleteModuleResourceOps();

            ProcessUpdateModuleResourceOps();

            ProcessAddModuleOps();
        }

        static void ProcessDeleteModuleOps()
        {
            while (deleteModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId) = op;

                if (!components.TryGetValue(entityId, out var component))
                {
                    continue;
                }

                if (!component.modules.TryGetValue(moduleId, out var moduleInfo))
                {
                    continue;
                }

                DeleteModuleFromComponent(moduleId, ref component);

                SpatialOSConnectionSystem.updateModuleInventoryOps.Enqueue(
                    new ComponentUpdateOp<ModuleInventory.Update>
                    {
                        EntityId = new EntityId(entityId),
                        Update = new ModuleInventory.Update
                        {
                            modules = component.modules,
                            modulesResources = component.modulesResources,
                        },
                    });

                var moduleRef = CloudFirestoreInfo.Database
                    .Collection(CloudFirestoreInfo.UsersCollection).Document(IdentificationsSystem.GetUserDBId(entityId))
                    .Collection(CloudFirestoreInfo.ShipsCollection).Document(IdentificationsSystem.GetEntityDBId(entityId))
                    .Collection(CloudFirestoreInfo.ModulesCollection).Document(moduleInfo.databaseId);

                moduleRef.DeleteAsync();
            }
        }

        static void ProcessAddModuleOps()
        {
            while (addModuleOps.TryDequeue(out var op))
            {
                AddModule(op);
            }
        }
        
        static void AddModule((long entityId, string databaseId, Module module, ResourceInfo[] resourceInfos) op)
        {
            var (entityId, databaseId, module, resourceInfos) = op;

            byte newModuleId = 0;
            if (components.TryGetValue(entityId, out var component))
            {
                newModuleId = UpdateComponentWithModule(ref op, ref component);

                components[entityId] = component;

                SpatialOSConnectionSystem.updateModuleInventoryOps.Enqueue(
                new ComponentUpdateOp<ModuleInventory.Update>
                {
                    EntityId = new EntityId(entityId),
                    Update = new ModuleInventory.Update
                    {
                        modules = component.modules,
                        modulesResources = component.modulesResources,
                    },
                });
            }
            else
            {
                AddNewComponentWithModule(ref op, ref component);

                components[entityId] = component;

                SpatialOSConnectionSystem.addModuleInventoryOps.Enqueue(
                new AddComponentOp<ModuleInventoryData>
                {
                    EntityId = new EntityId(entityId),
                    Data = component,
                });
            }

            switch (module.Type)
            {
                case ModuleType.Sampler:
                    SamplersSystem.QueueAddModuleOp(entityId, newModuleId, Samplers.Craft(module.Properties.BackingArray));
                    break;
                case ModuleType.Scanner:
                    ScannersSystem.QueueAddModuleOp(entityId, newModuleId, Scanners.Craft(module.Properties.BackingArray));
                    break;
                case ModuleType.Sensor:
                    SensorsSystem.QueueAddModuleOp(entityId, newModuleId, Sensors.Craft(module.Properties.BackingArray));
                    break;
            }

            var moduleRef = CloudFirestoreInfo.Database
                .Collection(CloudFirestoreInfo.UsersCollection).Document(IdentificationsSystem.GetUserDBId(entityId))
                .Collection(CloudFirestoreInfo.ShipsCollection).Document(IdentificationsSystem.GetEntityDBId(entityId))
                .Collection(CloudFirestoreInfo.ModulesCollection).Document(databaseId);

            var writeBatch = CloudFirestoreInfo.Database.StartBatch();

            writeBatch.Create(moduleRef, module);

            for (int j = 0; j < resourceInfos.Length; j++)
            {
                var info = resourceInfos[j];

                var resourceRef = moduleRef.Collection(CloudFirestoreInfo.ResourcesCollection).Document(info.databaseId);

                writeBatch.Create(resourceRef, new Resource(info.type, info.quantity));
            }

            writeBatch.CommitAsync();
        }

        static void ProcessUpdateModuleResourceOps()
        {
            while (updateModuleResourceOps.TryDequeue(out var op))
            {
                var(entityId, moduleId, databaseId, delta) = op;

                var component = components[entityId];
                var moduleInfo = component.modules[moduleId];
                var moduleResources = component.modulesResources[moduleId];

                UpdateModuleResource(databaseId, delta, ref moduleResources);

                component.modulesResources[moduleId] = moduleResources;
                components[entityId] = component;

                SpatialOSConnectionSystem.updateModuleInventoryOps.Enqueue(
                    new ComponentUpdateOp<ModuleInventory.Update>
                    {
                        EntityId = new EntityId(entityId),
                        Update = new ModuleInventory.Update().SetModulesResources(component.modulesResources),
                    });

                var resourceRef = CloudFirestoreInfo.Database
                    .Collection(CloudFirestoreInfo.UsersCollection).Document(IdentificationsSystem.GetUserDBId(entityId))
                    .Collection(CloudFirestoreInfo.ShipsCollection).Document(IdentificationsSystem.GetEntityDBId(entityId))
                    .Collection(CloudFirestoreInfo.ModulesCollection).Document(moduleInfo.databaseId)
                    .Collection(CloudFirestoreInfo.ResourcesCollection).Document(databaseId);

                resourceRef.UpdateAsync(CloudFirestoreInfo.QuantityField, FieldValue.Increment(delta));
            }
        }

        static void ProcessDeleteModuleResourceOps()
        {
            while (deleteModuleResourceOps.TryDequeue(out var op))
            {
                var (entityId, moduleId, databaseId) = op;

                var component = components[entityId];
                var moduleInfo = component.modules[moduleId];
                var moduleResources = component.modulesResources[moduleId];

                DeleteModuleResource(databaseId, ref moduleResources);

                component.modulesResources[moduleId] = moduleResources;
                components[entityId] = component;

                SpatialOSConnectionSystem.updateModuleInventoryOps.Enqueue(
                    new ComponentUpdateOp<ModuleInventory.Update>
                    {
                        EntityId = new EntityId(entityId),
                        Update = new ModuleInventory.Update().SetModulesResources(component.modulesResources),
                    });

                var resourceRef = CloudFirestoreInfo.Database
                    .Collection(CloudFirestoreInfo.UsersCollection).Document(IdentificationsSystem.GetUserDBId(entityId))
                    .Collection(CloudFirestoreInfo.ShipsCollection).Document(IdentificationsSystem.GetEntityDBId(entityId))
                    .Collection(CloudFirestoreInfo.ModulesCollection).Document(moduleInfo.databaseId)
                    .Collection(CloudFirestoreInfo.ResourcesCollection).Document(databaseId);

                resourceRef.DeleteAsync();
            }
        }

        public static void DeleteModuleResource(string databaseId, ref ModuleResources moduleResources)
        {
            for (int i = 0; i < moduleResources.resourceInfos.Count; i++)
            {
                if (moduleResources.resourceInfos[i].databaseId == databaseId)
                {
                    moduleResources.resourceInfos.RemoveAt(i);
                    break;
                }
            }
        }

        public static void UpdateModuleResource(string databaseId, int delta, ref ModuleResources moduleResources)
        {
            for (int i = 0; i < moduleResources.resourceInfos.Count; i++)
            {
                var resourceInfo = moduleResources.resourceInfos[i];

                if (resourceInfo.databaseId == databaseId)
                {
                    resourceInfo.quantity += delta;
                    moduleResources.resourceInfos[i] = resourceInfo;
                    break;
                }
            }
        }

        public static void DeleteModuleFromComponent(byte moduleId, ref ModuleInventoryData component)
        {
            component.modules.Remove(moduleId);
            component.modulesResources.Remove(moduleId);
        }

        public static byte UpdateComponentWithModule(ref (long entityId, string databaseId, Module module, ResourceInfo[] resourceInfos) addModuleOp, ref ModuleInventoryData component)
        {
            var freeIndex = 0;
            while (component.modules.ContainsKey(freeIndex))
            {
                freeIndex++;
            }

            component.modules.Add(freeIndex, new ModuleInfo(addModuleOp.databaseId, addModuleOp.module.Name, addModuleOp.module.Type, addModuleOp.module.Creator, addModuleOp.module.Properties));
            component.modulesResources.Add(freeIndex, new ModuleResources(new Improbable.Collections.List<ResourceInfo>(addModuleOp.resourceInfos)));

            return (byte)freeIndex;
        }

        public static void AddNewComponentWithModule(ref (long entityId, string databaseId, Module module, ResourceInfo[] resourceInfos) addModuleOp, ref ModuleInventoryData component)
        {
            component = new ModuleInventoryData
            {
                modules = new Improbable.Collections.Map<int, ModuleInfo>
                {
                    { 0, new ModuleInfo(addModuleOp.databaseId, addModuleOp.module.Name, addModuleOp.module.Type, addModuleOp.module.Creator, addModuleOp.module.Properties) },
                },
                modulesResources = new Improbable.Collections.Map<int, ModuleResources>
                {
                    { 0, new ModuleResources(new Improbable.Collections.List<ResourceInfo>(addModuleOp.resourceInfos)) }
                },
            };
        }
    }
}
