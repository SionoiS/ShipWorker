using Improbable.Worker;
using RogueFleet.Core;
using System.Collections.Generic;

namespace ShipWorker.ECS.ModuleSystems
{
    static class IdentificationsSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static IdentificationsSystem()
        {
            dispatcher.OnAddComponent(Identification.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(Identification.Metaclass, OnComponentRemoved);
            dispatcher.OnComponentUpdate(Identification.Metaclass, OnComponentUpdated);
        }

        static void OnComponentAdded(AddComponentOp<IdentificationData> op)
        {
            components[op.EntityId.Id] = op.Data;
        }

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            components.Remove(op.EntityId.Id);
        }

        static void OnComponentUpdated(ComponentUpdateOp<Identification.Update> op)
        {
            var data = components[op.EntityId.Id];

            op.Update.ApplyTo(ref data);

            components[op.EntityId.Id] = data;
        }

        static readonly Dictionary<long, IdentificationData> components = new Dictionary<long, IdentificationData>();

        internal static string GetEntityDBId(long entityId)
        {
            return components[entityId].entityDatabaseId;
        }

        internal static bool TryGetEntityDBId(long entityId, out string entityDBId)
        {
            if (components.TryGetValue(entityId, out var identification))
            {
                entityDBId = identification.entityDatabaseId;
                return true;
            }

            entityDBId = string.Empty;
            return false;
        }

        internal static string GetUserDBId(long entityId)
        {
            return components[entityId].userDatabaseId;
        }

        internal static bool TryGetUserDBId(long entityId, out string userDBId)
        {
            if (components.TryGetValue(entityId, out var identification))
            {
                userDBId = identification.userDatabaseId;
                return true;
            }

            userDBId = string.Empty;
            return false;
        }
    }
}
