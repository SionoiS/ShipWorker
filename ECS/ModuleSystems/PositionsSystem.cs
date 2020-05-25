using WorldGenerator;
using Improbable;
using Improbable.Worker;
using RogueFleet.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading.Tasks;
using Xoshiro.Base;
using Xoshiro.PRNG32;
using System.Threading;

namespace ShipWorker.ECS.ModuleSystems
{
    static class PositionsSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static PositionsSystem()
        {
            dispatcher.OnAddComponent(Position.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(Position.Metaclass, OnComponentRemoved);
            dispatcher.OnComponentUpdate(Position.Metaclass, OnComponentUpdated);
            dispatcher.OnFlagUpdate(OnFlagUpdate);
        }

        static readonly ConcurrentQueue<AddComponentOp<PositionData>> addComponentOps = new ConcurrentQueue<AddComponentOp<PositionData>>();

        static void OnComponentAdded(AddComponentOp<PositionData> op)
        {
            addComponentOps.Enqueue(op);
        }

        static readonly ConcurrentQueue<RemoveComponentOp> removeComponentOps = new ConcurrentQueue<RemoveComponentOp>();

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            removeComponentOps.Enqueue(op);
        }

        static readonly ConcurrentQueue<ComponentUpdateOp<Position.Update>> componentUpdateOps = new ConcurrentQueue<ComponentUpdateOp<Position.Update>>();

        static void OnComponentUpdated(ComponentUpdateOp<Position.Update> op)
        {
            componentUpdateOps.Enqueue(op);
        }

        const string Flag = "rogue_fleet_online_db_position_update_timing";

        static void OnFlagUpdate(FlagUpdateOp op)
        {
            if (op.Name == Flag && op.Value.HasValue)
            {
                positionMaxTime = Convert.ToInt32(op.Value.Value);

                CalculateFrameRate();
            }
        }

        static int positionMaxTime = 60000;//in milliseconds
        static TimeSpan frameRate = TimeSpan.FromMilliseconds(positionMaxTime);

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

        static readonly Dictionary<long, PositionData> components = new Dictionary<long, PositionData>();
        static readonly Dictionary<long, GridCoords> shipCells = new Dictionary<long, GridCoords>();

        internal static bool TryGetComponent(long entityId, out PositionData position)
        {
            return components.TryGetValue(entityId, out position);
        }

        static void Update()
        {
            //Can not run in parallel
            while (removeComponentOps.TryDequeue(out var op))
            {
                components.Remove(op.EntityId.Id);
                shipCells.Remove(op.EntityId.Id);

                CalculateFrameRate();
            }

            while (addComponentOps.TryDequeue(out var op))
            {
                components[op.EntityId.Id] = op.Data;

                CalculateFrameRate();
            }

            while(componentUpdateOps.TryDequeue(out var op))
            {
                var component = components[op.EntityId.Id];

                op.Update.ApplyTo(ref component);

                components[op.EntityId.Id] = component;
            }

            PeriodicSendPositionToDB();

            PeriodicProcGenCellCheck();
        }

        static readonly IRandomU random = new XoShiRo128starstar();

        static void PeriodicProcGenCellCheck()
        {
            var count = components.Count;

            if (count < 1)
            {
                return;
            }

            var array = new long[count];
            components.Keys.CopyTo(array, 0);

            var entityId = array[random.Next(count)];

            var coords = components[entityId].coords;

            var cellCoords = AsteroidGeneration.CellCoordsFromPosition(coords.x, coords.z);

            shipCells.TryGetValue(entityId, out var gridCoords);//gridCoords defaults if not found
            
            if (cellCoords.x != gridCoords.x || cellCoords.z != gridCoords.z)
            {
                shipCells[entityId] = cellCoords;

                SpatialOSConnectionSystem.requestPopulateGridCellOps.Enqueue(
                    new CommandRequestOp<AsteroidSpawner.Commands.PopulateGridCell, PopulateGridCellRequest>
                    {
                        EntityId = new EntityId(1),//1 is the Spawner id according to the init. snapshot
                        Request = new PopulateGridCellRequest(cellCoords.x, cellCoords.z),
                    });
            }
        }

        static void PeriodicSendPositionToDB()
        {
            var count = components.Count;

            if (count < 1)
            {
                return;
            }

            var array = new long[count];
            components.Keys.CopyTo(array, 0);

            var entityId = array[random.Next(count)];//choosing randomly could result in more than 1 update to firestore per seconds

            if (!components.TryGetValue(entityId, out var positionData))
            {
                return;
            }

            var userDBId = IdentificationsSystem.GetUserDBId(entityId);
            var shipDBId = IdentificationsSystem.GetEntityDBId(entityId);

            var shipRef = CloudFirestoreInfo.Database
                .Collection(CloudFirestoreInfo.UsersCollection).Document(userDBId)
                .Collection(CloudFirestoreInfo.ShipsCollection).Document(shipDBId);

            var position = positionData.coords;

            shipRef.UpdateAsync(CloudFirestoreInfo.CoordinatesField, new double[3] { position.x, position.y, position.z });
        }

        static void CalculateFrameRate()
        {
            var count = components.Count;
            if (count > 0)
            {
                frameRate = TimeSpan.FromMilliseconds(positionMaxTime / count);
            }
        }
    }
}
