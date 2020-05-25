using Improbable.Worker;
using RogueFleet.Ships;
using System.Collections.Generic;

namespace ShipWorker.ECS.ModuleSystems
{
    static class ExplorationHacksSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static ExplorationHacksSystem()
        {
            dispatcher.OnAddComponent(ExplorationPhysics.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(ExplorationPhysics.Metaclass, OnComponentRemoved);
            dispatcher.OnComponentUpdate(ExplorationPhysics.Metaclass, OnComponentUpdated);
        }

        static void OnComponentAdded(AddComponentOp<ExplorationPhysicsData> op)
        {
            components[op.EntityId.Id] = op.Data;
        }

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            components.Remove(op.EntityId.Id);
        }

        static void OnComponentUpdated(ComponentUpdateOp<ExplorationPhysics.Update> op)
        {
            var data = components[op.EntityId.Id];

            op.Update.ApplyTo(ref data);

            components[op.EntityId.Id] = data;
        }

        static readonly Dictionary<long, ExplorationPhysicsData> components = new Dictionary<long, ExplorationPhysicsData>();

        internal static bool TryGetSampleableEntityId(long entityId, out long asteroidEntityId)
        {
            if (components.TryGetValue(entityId, out var explorationPhysicsData) && explorationPhysicsData.sampleableAsteroidId.HasValue)
            {
                asteroidEntityId = explorationPhysicsData.sampleableAsteroidId.Value.Id;
                return true;
            }

            asteroidEntityId = 0;
            return false;
        }

        internal static bool TryGetScannableEntityId(long entityId, out long asteroidEntityId)
        {
            if (components.TryGetValue(entityId, out var explorationPhysicsData) && explorationPhysicsData.scanableAsteroidId.HasValue)
            {
                asteroidEntityId = explorationPhysicsData.scanableAsteroidId.Value.Id;
                return true;
            }

            asteroidEntityId = 0;
            return false;
        }
    }
}
