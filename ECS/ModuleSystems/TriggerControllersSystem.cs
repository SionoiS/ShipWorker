using Improbable.Worker;
using RogueFleet.Items;
using RogueFleet.Ships.Modules;
using ShipWorker.ECS.ItemSystems;
using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShipWorker.ECS.ModuleSystems
{
    public static class TriggerControllersSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static TriggerControllersSystem()
        {
            dispatcher.OnComponentUpdate(TriggerController.Metaclass, OnComponentUpdated);
        }

        static readonly ConcurrentQueue<ComponentUpdateOp<TriggerController.Update>> componentUpdateOps = new ConcurrentQueue<ComponentUpdateOp<TriggerController.Update>>();

        static void OnComponentUpdated(ComponentUpdateOp<TriggerController.Update> op)
        {
            componentUpdateOps.Enqueue(op);
        }

        static TimeSpan frameRate = TimeSpan.FromMilliseconds(20);//50hz
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

        static void Update()
        {
            ProcessUpdateOps();
        }

        public static void ProcessUpdateOps()
        {
            while (componentUpdateOps.TryDequeue(out var op))
            {
                var entityId = op.EntityId.Id;
                var triggers = op.Update.triggers;

                for (int i = triggers.Count - 1; i >= 0; i--)
                {
                    var moduleId = (byte)triggers[i].moduleId;

                    //Do not trust the data! Sent by client.

                    if (!ModularsSystem.IsInstalled(entityId, moduleId))
                    {
                        continue;
                    }

                    switch (ModulesInventorySystem.GetModuleInfo(entityId, moduleId).type)
                    {
                        case ModuleType.Sensor:
                            SensorsSystem.QueueUseModuleOp(entityId, moduleId);
                            break;
                        case ModuleType.Scanner:
                            ScannersSystem.QueueUseModuleOp(entityId, moduleId);
                            break;
                        case ModuleType.Sampler:
                            SamplersSystem.QueueUseModuleOp(entityId, moduleId);
                            break;
                    }
                }
            }
        }
    }
}
