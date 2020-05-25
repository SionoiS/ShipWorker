using Google.Cloud.Firestore;
using Improbable.Worker;
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
    public static class ResourcesInventorySystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static ResourcesInventorySystem()
        {
            dispatcher.OnAddComponent(ResourceInventory.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(ResourceInventory.Metaclass, OnComponentRemoved);
        }

        static readonly ConcurrentQueue<AddComponentOp<ResourceInventoryData>> addComponentOps = new ConcurrentQueue<AddComponentOp<ResourceInventoryData>>();

        static void OnComponentAdded(AddComponentOp<ResourceInventoryData> op)
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

        static readonly ConcurrentQueue<(long entityId, string databaseId, Resource resource)> addResourceOps = new ConcurrentQueue<(long entityId, string databaseId, Resource resource)>();

        internal static void QueueAddResourceOp(long entityId, string databaseId, Resource resource)
        {
            addResourceOps.Enqueue((entityId, databaseId, resource));
        }

        static readonly ConcurrentQueue<(long entityId, byte resourceId, int quantityDelta)> resourceQuantityDeltaOps = new ConcurrentQueue<(long entityId, byte resourceId, int quantityDelta)>();

        internal static void QueueResourceQuantityDeltaOp(long entityId, byte resourceId, int amountDelta)
        {
            resourceQuantityDeltaOps.Enqueue((entityId, resourceId, amountDelta));
        }

        static readonly Dictionary<long, ResourceInventoryData> components = new Dictionary<long, ResourceInventoryData>();

        internal static bool TryGetResourceInfo(long entityId, byte resourceId, out ResourceInfo resourceInfo)
        {
            if (components.TryGetValue(entityId, out var component))
            {
                return component.resources.TryGetValue(resourceId, out resourceInfo);
            }

            resourceInfo = new ResourceInfo();
            return false;
        }

        internal static ResourceInfo GetResourceInfo(long entityId, byte resourceId)
        {
            return components[entityId].resources[resourceId];
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

            ProcessResourceQuantityDeltaOps();

            ProcessAddResourceItemOps();
        }

        static void ProcessAddResourceItemOps()
        {
            while (addResourceOps.TryDequeue(out var op))
            {
                var (entityId, databaseId, resource) = op;

                var resourceRef = CloudFirestoreInfo.Database
                    .Collection(CloudFirestoreInfo.UsersCollection).Document(IdentificationsSystem.GetUserDBId(entityId))
                    .Collection(CloudFirestoreInfo.ShipsCollection).Document(IdentificationsSystem.GetEntityDBId(entityId))
                    .Collection(CloudFirestoreInfo.ResourcesCollection).Document(databaseId);

                if (components.TryGetValue(entityId, out var component))
                {
                    var index = 0;
                    foreach (var item in component.resources)
                    {
                        if (item.Value.databaseId == databaseId)
                        {
                            index = item.Key;
                            break;
                        }
                    }

                    if (component.resources.TryGetValue(index, out var resourceInfo))
                    {
                        resourceInfo.quantity += resource.Quantity;
                        component.resources[index] = resourceInfo;

                        resourceRef.UpdateAsync(CloudFirestoreInfo.QuantityField, FieldValue.Increment(resource.Quantity));
                    }
                    else
                    {
                        var freeIndex = 0;
                        while (component.resources.ContainsKey(freeIndex))
                        {
                            freeIndex++;
                        }

                        component.resources.Add(freeIndex, new ResourceInfo(databaseId, resource.Type, resource.Quantity));

                        resourceRef.CreateAsync(resource);
                    }

                    components[entityId] = component;

                    SpatialOSConnectionSystem.updateResourceInventoryOps.Enqueue(
                        new ComponentUpdateOp<ResourceInventory.Update>
                        {
                            EntityId = new EntityId(entityId),
                            Update = new ResourceInventory.Update().SetResources(component.resources),
                        });
                }
                else
                {
                    component.resources = new Improbable.Collections.Map<int, ResourceInfo>
                    {
                        { 0, new ResourceInfo(databaseId, resource.Type, resource.Quantity) },
                    };

                    components[entityId] = component;

                    SpatialOSConnectionSystem.addResourceInventoryOps.Enqueue(
                    new AddComponentOp<ResourceInventoryData>
                    {
                        EntityId = new EntityId(entityId),
                        Data = component,
                    });

                    resourceRef.CreateAsync(resource);
                }
            }
        }

        static void ProcessResourceQuantityDeltaOps()
        {
            while (resourceQuantityDeltaOps.TryDequeue(out var op))
            {
                var (entityId, resourceId, delta) = op;

                if (components.TryGetValue(entityId, out var component) && component.resources.TryGetValue(resourceId, out var resourceInfo))
                {
                    var resourceRef = CloudFirestoreInfo.Database
                   .Collection(CloudFirestoreInfo.UsersCollection).Document(IdentificationsSystem.GetUserDBId(entityId))
                   .Collection(CloudFirestoreInfo.ShipsCollection).Document(IdentificationsSystem.GetEntityDBId(entityId))
                   .Collection(CloudFirestoreInfo.ResourcesCollection).Document(resourceInfo.databaseId);

                    resourceInfo.quantity += delta;

                    if (resourceInfo.quantity < 1)
                    {
                        component.resources.Remove(resourceId);

                        resourceRef.DeleteAsync();
                    }
                    else
                    {
                        component.resources[resourceId] = resourceInfo;

                        resourceRef.UpdateAsync(CloudFirestoreInfo.QuantityField, FieldValue.Increment(delta));
                    }

                    components[entityId] = component;
                }
            }
        }
    }
}
