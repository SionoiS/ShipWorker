using Improbable.Worker;
using ItemGenerator;
using ItemGenerator.Modules;
using RogueFleet.Items;
using ShipWorker.ECS.ModuleSystems;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xoshiro.Base;
using Xoshiro.PRNG32;

namespace ShipWorker.ECS.ItemSystems
{
    public static class CraftingSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static CraftingSystem()
        {
            dispatcher.OnAddComponent(Crafting.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(Crafting.Metaclass, OnComponentRemoved);
            dispatcher.OnComponentUpdate(CraftingController.Metaclass, OnControllerUpdated);
        }

        static void OnComponentAdded(AddComponentOp<CraftingData> op) => components[op.EntityId.Id] = op.Data;
        static void OnComponentRemoved(RemoveComponentOp op) => components.Remove(op.EntityId.Id);

        static void OnControllerUpdated(ComponentUpdateOp<CraftingController.Update> op) => componentUpdateOps.Enqueue(op);
        static readonly ConcurrentQueue<ComponentUpdateOp<CraftingController.Update>> componentUpdateOps = new ConcurrentQueue<ComponentUpdateOp<CraftingController.Update>>();

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

        static readonly Dictionary<long, CraftingData> components = new Dictionary<long, CraftingData>();

        static void Update()
        {
            ProcessControllerUpdateOps();
        }

        static void ProcessControllerUpdateOps()
        {
            while (componentUpdateOps.TryDequeue(out var op))
            {
                var entityId = op.EntityId.Id;

                foreach (var craft in op.Update.craftModule)
                {
                    Craft(entityId, craft);
                }
            }
        }

        static readonly IRandomU random = new XoShiRo128starstar();

        static void Craft(long entityId, CraftModule craft)
        {
            if (craft.type == ModuleType.None || craft.type == ModuleType.Count)
            {
                return;
            }

            var properties = craft.propertyLevels;

            if (!Helpers.CheckCraftingAccess(components[entityId].propertiesUnlocked[craft.type].BackingArray, properties.BackingArray))
            {
                return;
            }

            var requirements = Helpers.GetRequirements(craft.type, properties.BackingArray);

            var resourceIds = new byte[requirements.Count];
            var resourceInfos = new ResourceInfo[requirements.Count];
            for (int i = 0; i < requirements.Count; i++)
            {
                var resourceId = (byte)craft.resourceIds[i];
                var requirement = requirements[i];

                if (!ResourcesInventorySystem.TryGetResourceInfo(entityId, resourceId, out var resourceInfo))
                {
                    return;
                }

                if (resourceInfo.type != requirement.Type)
                {
                    return;
                }

                if (resourceInfo.quantity < requirement.Quantity)
                {
                    return;
                }

                resourceInfo.quantity = requirement.Quantity;

                resourceIds[i] = resourceId;
                resourceInfos[i] = resourceInfo;
            }

            for (int i = 0; i < resourceInfos.Length; i++)
            {
                ResourcesInventorySystem.QueueResourceQuantityDeltaOp(entityId, resourceIds[i], -resourceInfos[i].quantity);
            }

            var userDBId = IdentificationsSystem.GetUserDBId(entityId);

            var moduleFullName = craft.name.HasValue ? craft.name.Value + craft.type.ToString() : "Prototype " + craft.type.ToString();
            var craftedModuleDBId = Helpers.GenerateCloudFireStoreRandomDocumentId(random);
            var craftedModule = new Module(moduleFullName, craft.type, userDBId, properties, false);

            ModulesInventorySystem.QueueAddModuleOp(entityId, craftedModuleDBId, craftedModule, resourceInfos);
        }
    }
}
