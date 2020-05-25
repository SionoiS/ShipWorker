using Improbable.Worker;
using RogueFleet.Items;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShipWorker.ECS.ItemSystems
{
    public static class MapItemsSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static MapItemsSystem()
        {
            dispatcher.OnAddComponent(MapItems.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(MapItems.Metaclass, OnComponentRemoved);
            dispatcher.OnComponentUpdate(MappingController.Metaclass, OnControllerUpdated);
        }

        static readonly ConcurrentQueue<AddComponentOp<MapItemsData>> addComponentOps = new ConcurrentQueue<AddComponentOp<MapItemsData>>();

        static void OnComponentAdded(AddComponentOp<MapItemsData> op)
        {
            addComponentOps.Enqueue(op);
        }

        static readonly ConcurrentQueue<RemoveComponentOp> removeComponentOps = new ConcurrentQueue<RemoveComponentOp>();

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            removeComponentOps.Enqueue(op);
        }

        static readonly Dictionary<long, int> recordingIndices = new Dictionary<long, int>();

        static void OnControllerUpdated(ComponentUpdateOp<MappingController.Update> op)
        {
            if (op.Update.recordingIndex.HasValue)
            {
                recordingIndices[op.EntityId.Id] = op.Update.recordingIndex.Value;
            }
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

        static readonly ConcurrentQueue<(long entityId, Survey survey)> addSurveyOps = new ConcurrentQueue<(long entityId, Survey survey)>();

        internal static void QueueAddSurveyOp(long entityId, Survey newSurvey)
        {
            addSurveyOps.Enqueue((entityId, newSurvey));
        }

        static readonly Dictionary<long, MapItemsData> components = new Dictionary<long, MapItemsData>();

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

            ProcessAddMapOps();
        }

        static void ProcessAddMapOps()
        {
            while (addSurveyOps.TryDequeue(out var op))
            {
                var (entityId, survey) = op;

                if (components.TryGetValue(entityId, out var component))
                {
                    UpdateComponent(entityId, survey, ref component);

                    components[entityId] = component;

                    SpatialOSConnectionSystem.updateMapItemsOps.Enqueue(
                        new ComponentUpdateOp<MapItems.Update>
                        {
                            EntityId = new EntityId(entityId),
                            Update = new MapItems.Update().SetSurveyMaps(component.surveyMaps),
                        });
                }
                else
                {
                    AddNewComponent(survey, ref component);

                    components[entityId] = component;

                    SpatialOSConnectionSystem.addMapItemsOps.Enqueue(
                        new AddComponentOp<MapItemsData>
                        {
                            EntityId = new EntityId(entityId),
                            Data = component,
                        });
                }
            }
        }

        public static void UpdateComponent(long entityId, Survey survey, ref MapItemsData component)
        {
            component.surveyMaps[recordingIndices[entityId]].surveys.Add(survey);
        }

        public static void AddNewComponent(Survey survey, ref MapItemsData component)
        {
            component = new MapItemsData
            {
                surveyMaps = new Improbable.Collections.List<Map> { new Map { surveys = new Improbable.Collections.List<Survey> { survey } } }
            };
        }
    }
}
