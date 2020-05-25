using Improbable;
using Improbable.Worker;
using ItemGenerator.Resources;
using RogueFleet.Items;
using RogueFleet.Ships.Modules;
using ShipWorker.ECS.ItemSystems;
using SpherePointsCalculator;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShipWorker.ECS.ModuleSystems
{
    public static class SensorsSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static SensorsSystem()
        {
            dispatcher.OnAddComponent(Sensor.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(Sensor.Metaclass, OnComponentRemoved);
        }

        static void OnComponentAdded(AddComponentOp<SensorData> op)
        {
            components[op.EntityId.Id] = op.Data;
        }

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            components.Remove(op.EntityId.Id);
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

        static readonly ConcurrentQueue<(long entityId, byte moduleId)> useModuleOps = new ConcurrentQueue<(long entityId, byte moduleId)>();

        internal static void QueueUseModuleOp(long entityId, byte moduleId)
        {
            useModuleOps.Enqueue((entityId, moduleId));
        }

        static readonly ConcurrentQueue<(long entityId, byte moduleId, SensorStat sensorStat)> addModuleOps = new ConcurrentQueue<(long entityId, byte moduleId, SensorStat sensorStat)>();

        internal static void QueueAddModuleOp(long entityId, byte moduleId, SensorStat sensorStat)
        {
            addModuleOps.Enqueue((entityId, moduleId, sensorStat));
        }

        static readonly ConcurrentQueue<(long entityId, byte moduleId)> deleteModuleOps = new ConcurrentQueue<(long entityId, byte moduleId)>();

        internal static void QueueDeleteModuleOp(long entityId, byte moduleId)
        {
            deleteModuleOps.Enqueue((entityId, moduleId));
        }

        static readonly DateTime centuryBegin = new DateTime(2001, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        static readonly Dictionary<long, SensorData> components = new Dictionary<long, SensorData>();

        static void Update()
        {
            ProcessDeleteModuleOps(deleteModuleOps, components);

            ProcessAddModuleOps(addModuleOps, components);

            ProcessUseModuleOps(useModuleOps, components);
        }

        static void ProcessAddModuleOps(ConcurrentQueue<(long entityId, byte moduleId, SensorStat sensorStat)> addModuleOps, Dictionary<long, SensorData> components)
        {
            while (addModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId, sensorStat) = op;

                if (components.TryGetValue(entityId, out var component))
                {
                    component.sensors.Add(moduleId, sensorStat);

                    components[entityId] = component;

                    SpatialOSConnectionSystem.updateSensorOps.Enqueue(
                        new ComponentUpdateOp<Sensor.Update>
                        {
                            EntityId = new EntityId(entityId),
                            Update = new Sensor.Update().SetSensors(component.sensors),
                        });
                }
                else
                {
                    component = new SensorData
                    {
                        sensors = new Improbable.Collections.Map<int, SensorStat>
                    {
                        { moduleId, sensorStat },
                    }
                    };

                    components[entityId] = component;

                    SpatialOSConnectionSystem.addSensorOps.Enqueue(
                        new AddComponentOp<SensorData>
                        {
                            EntityId = new EntityId(entityId),
                            Data = component,
                        });
                }
            }
        }

        static void ProcessUseModuleOps(ConcurrentQueue<(long entityId, byte moduleId)> useModuleOps, Dictionary<long, SensorData> sensors)
        {
            var count = useModuleOps.Count;

            if (count < 1)
            {
                return;
            }

            var surveyShipId = new List<long>(count);
            var surveys = new List<Survey>(count);
            var tiers = new List<int>(count);
            while (useModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId) = op;

                if (!sensors.TryGetValue(entityId, out var sensorData)
                 || !sensorData.sensors.TryGetValue(moduleId, out var sensorStat))
                {
                    return;
                }

                if (!PositionsSystem.TryGetComponent(entityId, out var position))
                {
                    return;
                }

                if (!RechargeablesSystem.HasCharge(entityId, moduleId))
                {
                    return;
                }

                DamageablesSystem.QueueUseModuleOp(entityId, moduleId);
                RechargeablesSystem.QueueUseModuleOp(entityId, moduleId);

                var survey = new Survey(position.coords, (long)(DateTime.UtcNow - centuryBegin).TotalSeconds, sensorStat.dimensions);

                MapItemsSystem.QueueAddSurveyOp(entityId, survey);

                surveyShipId.Add(entityId);
                surveys.Add(survey);
                tiers.Add(sensorStat.tier);
            }

            var (samplePoints, indices) = CalculatePoints(surveys);

            var (lowSamples, medSamples, highSamples) = SamplePoints(surveys, indices, samplePoints);

            var samples = NormalizeSamples(lowSamples, medSamples, highSamples);

            SensorUpdates(tiers, surveyShipId, indices, samples);
        }

        public static (Coordinates[] samplePoints, int[] indices) CalculatePoints(List<Survey> surveys)
        {
            var offsetIndices = new int[surveys.Count];

            var total = 0;
            for (int i = 0; i < surveys.Count; i++)
            {
                offsetIndices[i] = total;

                total += SpherePoints.CalculatePointsCount(surveys[i].dimensions);
            }

            var samplePoints = new Coordinates[total];

            Parallel.For(0, surveys.Count, i =>
            {
                var survey = surveys[i];

                SpherePoints.CalculatePoints(survey.dimensions).CopyTo(samplePoints, offsetIndices[i]);
            });

            return (samplePoints, offsetIndices);
        }

        public static (double[] lowSamples, double[] medSamples, double[] highSamples) SamplePoints(List<Survey> surveys, int[] indices, Coordinates[] points)
        {
            var lowSamples = new double[points.Length];
            var medSamples = new double[points.Length];
            var highSamples = new double[points.Length];
            Parallel.For(0, points.Length, i =>
            {
                //example
                //i          => 0, 1, 2, 3, 4, 5, 6, 7, 8, 9...
                //pointIndex => 0, 0, 0, 1, 1, 1, 2, 2, 2, 3...
                //layerIndex => 0, 1, 2, 0, 1, 2, 0, 1, 2, 0...
                //surveyIndex=> 0, 0, 0, 0, 0, 0, 0, 0, 0, 1...

                var pointIndex = i / 3;
                var layerIndex = i - (pointIndex * 3);
                
                var surveyIndex = 0;
                for (int j = 0; j < indices.Length; j++)
                {
                    if (i >= indices[j])
                    {
                        surveyIndex = j;
                        break;
                    }
                }

                var point = points[pointIndex];
                var coords = surveys[surveyIndex].coordinates;
                var time = surveys[surveyIndex].time;

                switch (layerIndex)
                {
                    case 0:
                        lowSamples[i] = ProbabilityMap.EvaluateLowSample(point.x + coords.x, point.y + coords.y, point.z + coords.z, time);
                        break;
                    case 1:
                        medSamples[i] = ProbabilityMap.EvaluateMedSample(point.x + coords.x, point.y + coords.y, point.z + coords.z, time);
                        break;
                    case 2:
                        highSamples[i] = ProbabilityMap.EvaluateHighSample(point.x + coords.x, point.y + coords.y, point.z + coords.z, time);
                        break;
                }
            });

            return (lowSamples, medSamples, highSamples);
        }

        public static double[] NormalizeSamples(double[] lowSamples, double[] medSamples, double[] highSamples)
        {
            var samples = new double[lowSamples.Length];
            Parallel.For(0, samples.Length, i =>
            {
                samples[i] = ProbabilityMap.TieringSample(ProbabilityMap.LayeringSample(lowSamples[i], medSamples[i], highSamples[i]));
            });

            return samples;
        }

        static void SensorUpdates(List<int> tiers, List<long> surveyShipId, int[] indices, double[] samples)
        {
            int lastIndex = 0;
            for (int i = 0; i < surveyShipId.Count; i++)
            {
                var currentIndex = indices[i];
                var tier = tiers[i];

                var finalData = new byte[currentIndex - lastIndex];
                for (int j = lastIndex; j < currentIndex; j++)
                {
                    var value = samples[j];
                    var frac = value % 1;
                    int integer = (int)(value - frac);

                    if (integer != tier)
                    {
                        finalData[j] = 0;//wrong tier send 0
                    }
                    else
                    {
                        finalData[j] = (byte)(frac * byte.MaxValue);
                    }
                }

                lastIndex = currentIndex;

                SpatialOSConnectionSystem.updateSensorOps.Enqueue(
                    new ComponentUpdateOp<Sensor.Update>
                    {
                        EntityId = new EntityId(surveyShipId[i]),
                        Update = new Sensor.Update().AddSurveyResult(new SurveyResult(Bytes.FromBackingArray(finalData))),
                    });
            }
        }

        static void ProcessDeleteModuleOps(ConcurrentQueue<(long entityId, byte moduleId)> deleteModuleOps, Dictionary<long, SensorData> components)
        {
            while (deleteModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId) = op;

                if (components.TryGetValue(entityId, out var component) && component.sensors.Remove(moduleId))
                {
                    components[entityId] = component;

                    SpatialOSConnectionSystem.updateSensorOps.Enqueue(
                    new ComponentUpdateOp<Sensor.Update>
                    {
                        EntityId = new EntityId(entityId),
                        Update = new Sensor.Update().SetSensors(component.sensors),
                    });
                }
            }
        }
    }
}
