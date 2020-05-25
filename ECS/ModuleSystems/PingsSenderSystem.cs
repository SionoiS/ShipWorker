using Improbable.Worker;
using RogueFleet.Core;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Xoshiro.Base;
using Xoshiro.PRNG32;

namespace ShipWorker.ECS.ModuleSystems
{
    public static class PingsSenderSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static PingsSenderSystem()
        {
            dispatcher.OnAddComponent(ClientConnection.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(ClientConnection.Metaclass, OnComponentRemoved);
            dispatcher.OnCommandResponse(ClientConnection.Commands.Ping.Metaclass, OnPingReponse);
            dispatcher.OnFlagUpdate(OnFlagUpdate);
        }

        static readonly ConcurrentQueue<AddComponentOp<ClientConnectionData>> addComponentOps = new ConcurrentQueue<AddComponentOp<ClientConnectionData>>();

        static void OnComponentAdded(AddComponentOp<ClientConnectionData> op)
        {
            addComponentOps.Enqueue(op);
        }

        static readonly ConcurrentQueue<RemoveComponentOp> removeComponentOps = new ConcurrentQueue<RemoveComponentOp>();

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            removeComponentOps.Enqueue(op);
        }

        static readonly ConcurrentQueue<CommandResponseOp<ClientConnection.Commands.Ping, PingResponse>> pingResponseOps = new ConcurrentQueue<CommandResponseOp<ClientConnection.Commands.Ping, PingResponse>>();

        static void OnPingReponse(CommandResponseOp<ClientConnection.Commands.Ping, PingResponse> op)
        {
            pingResponseOps.Enqueue(op);
        }

        const string PingFlag = "rogue_fleet_online_ping_timing";

        static void OnFlagUpdate(FlagUpdateOp op)
        {
            if (op.Name == PingFlag && op.Value.HasValue)
            {
                pingMaxTime = Convert.ToInt32(op.Value.Value);

                CalculateFrameRate();
            }
        }

        static int pingMaxTime = 5000;//in milliseconds
        static TimeSpan frameRate = TimeSpan.FromMilliseconds(pingMaxTime);//interval between pings requests

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

        static readonly List<long> entityIds = new List<long>();
        static readonly List<int> missedPings = new List<int>();

        static void Update()
        {
            //Can not run in parallel
            while (removeComponentOps.TryDequeue(out var op))
            {
                var index = entityIds.IndexOf(op.EntityId.Id);

                if (index < 0)
                {
                    continue;
                }

                entityIds.RemoveAt(index);
                missedPings.RemoveAt(index);

                CalculateFrameRate();
            }

            while (addComponentOps.TryDequeue(out var op))
            {
                entityIds.Add(op.EntityId.Id);
                missedPings.Add(0);

                CalculateFrameRate();
            }

            //Could run in parallel
            PeriodicPingRequests();
            ProcessResponses();
        }

        static readonly IRandomU random = new XoShiRo128starstar();

        static void PeriodicPingRequests()
        {
            var count = entityIds.Count;

            if (count < 1)
            {
                return;
            }

            var randomEntityId = entityIds[random.Next(count)];

            SpatialOSConnectionSystem.pingEntityIdOps.Enqueue(
                new CommandRequestOp<ClientConnection.Commands.Ping, PingRequest>
                {
                    EntityId = new EntityId(randomEntityId),
                    Request = new PingRequest(),
                });
        }

        static void ProcessResponses()
        {
            //Could do 1 dequeue at a time?
            while (pingResponseOps.TryDequeue(out var op))
            {
                if (op.StatusCode == StatusCode.Success)
                {
                    continue;
                }

                for (int i = 0; i < entityIds.Count; i++)
                {
                    if (entityIds[i] != op.EntityId.Id)
                    {
                        continue;
                    }

                    missedPings[i] += 1;

                    if (missedPings[i] > 5)
                    {
                        SpatialOSConnectionSystem.deleteEntityIdOps.Enqueue(op.EntityId);
                    }

                    break;
                }
            }
        }

        static void CalculateFrameRate()
        {
            var count = entityIds.Count;
            if (count > 0)
            {
                frameRate = TimeSpan.FromMilliseconds(pingMaxTime / count);
            }
        }
    }
}
