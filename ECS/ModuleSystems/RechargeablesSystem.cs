using Improbable.Worker;
using RogueFleet.Ships.Modules;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShipWorker.ECS.ModuleSystems
{
    public struct Batteries
    {
        public long[] entityIds;
        public int[] moduleIds;

        public int firstFreeIndex;
        public int[] useDrainsTotal;
        public int[] useDrainsRate;
        public int[] thresholds;

        public int firstInactiveBatteryIndex;
        public int[] drainsApplied;
        public int[] drainsToApply;
        public int[] drainsApplyRate;
    }

    public static class RechargeablesSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static RechargeablesSystem()
        {
            dispatcher.OnAddComponent(Rechargeable.Metaclass, OnComponentAdded);
            dispatcher.OnRemoveComponent(Rechargeable.Metaclass, OnComponentRemoved);
            dispatcher.OnAuthorityChange(Rechargeable.Metaclass, OnAuthorityChanged);
        }

        static readonly ConcurrentQueue<AddComponentOp<RechargeableData>> addComponentOps = new ConcurrentQueue<AddComponentOp<RechargeableData>>();

        static void OnComponentAdded(AddComponentOp<RechargeableData> op)
        {
            addComponentOps.Enqueue(op);
        }

        static readonly HashSet<long> removeComponentOps = new HashSet<long>();

        static void OnComponentRemoved(RemoveComponentOp op)
        {
            removeComponentOps.Add(op.EntityId.Id);
        }

        static readonly ConcurrentQueue<long> authorityChangeOps = new ConcurrentQueue<long>();

        static void OnAuthorityChanged(AuthorityChangeOp op)
        {
            if (op.Authority == Authority.AuthorityLossImminent)
            {
                authorityChangeOps.Enqueue(op.EntityId.Id);
            }
        }

        const byte deltaTime = 100;
        static TimeSpan frameRate = TimeSpan.FromMilliseconds(deltaTime);
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

        static Batteries batteries = new Batteries
        {
            entityIds = new long[1000],
            moduleIds = new int[1000],

            firstFreeIndex = 0,
            useDrainsTotal = new int[1000],
            useDrainsRate = new int[1000],
            thresholds = new int[1000],

            firstInactiveBatteryIndex = 0,
            drainsApplied = new int[1000],
            drainsToApply = new int[1000],
            drainsApplyRate = new int[1000],
        };

        internal static bool HasCharge(long entityId, uint moduleId)
        {
            for (int i = 0; i < batteries.firstInactiveBatteryIndex; i++)
            {
                if (batteries.entityIds[i] == entityId && batteries.moduleIds[i] == moduleId)
                {
                    return batteries.drainsApplied[i] <= batteries.thresholds[i];
                }
            }

            return true;
        }

        static readonly ConcurrentQueue<(long entityId, byte moduleId)> useModuleOps = new ConcurrentQueue<(long entityId, byte moduleId)>();

        internal static void QueueUseModuleOp(long entityId, byte moduleId)
        {
            useModuleOps.Enqueue((entityId, moduleId));
        }

        static void Update()
        {
            ProcessRemoveComponentOps(removeComponentOps, ref batteries);
            ProcessAddComponentOps(addComponentOps, ref batteries);

            ProcessUseOps(useModuleOps, ref batteries);

            ProcessBatteryLogic(deltaTime, ref batteries);

            ProcessAuthorityChangeOps(authorityChangeOps, ref batteries);
        }

        public static void ProcessRemoveComponentOps(HashSet<long> removeComponentOps, ref Batteries batteries)
        {
            var indicesToRemove = new List<int>();

            for (int i = 0; i < batteries.firstFreeIndex; i++)
            {
                if (removeComponentOps.Contains(batteries.entityIds[i]))
                {
                    indicesToRemove.Add(i);
                }
            }

            removeComponentOps.Clear();

            var indicesToRemoveCount = indicesToRemove.Count;

            var lastIndices = new int[indicesToRemoveCount];
            for (int i = 0; i < indicesToRemoveCount; i++)
            {
                lastIndices[i] = batteries.firstFreeIndex - 1 - i;
            }

            for (int i = 0; i < indicesToRemoveCount; i++)
            {
                var indexToRemove = indicesToRemove[i];
                var lastIndex = lastIndices[i];

                batteries.entityIds[indexToRemove] = batteries.entityIds[lastIndex];
                batteries.moduleIds[indexToRemove] = batteries.moduleIds[lastIndex];

                batteries.drainsApplied[indexToRemove] = batteries.drainsApplied[lastIndex];
                batteries.drainsToApply[indexToRemove] = batteries.drainsToApply[lastIndex];
                batteries.drainsApplyRate[indexToRemove] = batteries.drainsApplyRate[lastIndex];

                batteries.useDrainsTotal[indexToRemove] = batteries.useDrainsTotal[lastIndex];
                batteries.useDrainsRate[indexToRemove] = batteries.useDrainsRate[lastIndex];
                batteries.thresholds[indexToRemove] = batteries.thresholds[lastIndex];
            }

            batteries.firstFreeIndex -= indicesToRemoveCount;
        }

        public static void ProcessAddComponentOps(ConcurrentQueue<AddComponentOp<RechargeableData>> addComponentOps, ref Batteries batteries)
        {
            var entityIds = new List<long>();
            var moduleIds = new List<int>();

            var drainsSustained = new List<int>();
            var drainsLeft = new List<int>();
            var drainsRates = new List<int>();

            var useDrainsTotal = new List<int>();
            var useDrainsRate = new List<int>();
            var thresholds = new List<int>();

            while (addComponentOps.TryDequeue(out var op))
            {
                var entityId = op.EntityId;
                var data = op.Data;

                for (int i = 0; i < data.moduleIds.Count; i++)
                {
                    entityIds.Add(entityId.Id);
                }

                moduleIds.AddRange(data.moduleIds);

                drainsSustained.AddRange(data.drainsSustained);
                drainsLeft.AddRange(data.drainsLeft);
                drainsRates.AddRange(data.drainsRates);

                useDrainsTotal.AddRange(data.useDrainsTotal);
                useDrainsRate.AddRange(data.useDrainsRate);
                thresholds.AddRange(data.thresholds);
            }

            var modulesToAddCount = moduleIds.Count;
            var freeIndex = batteries.firstFreeIndex;
            var requiredSize = freeIndex + modulesToAddCount;

            if (batteries.entityIds.Length < requiredSize)
            {
                Array.Resize(ref batteries.entityIds, requiredSize);
                Array.Resize(ref batteries.moduleIds, requiredSize);

                Array.Resize(ref batteries.drainsApplied, requiredSize);
                Array.Resize(ref batteries.drainsToApply, requiredSize);
                Array.Resize(ref batteries.drainsApplyRate, requiredSize);

                Array.Resize(ref batteries.useDrainsTotal, requiredSize);
                Array.Resize(ref batteries.useDrainsRate, requiredSize);
                Array.Resize(ref batteries.thresholds, requiredSize);
            }

            entityIds.CopyTo(batteries.entityIds, freeIndex);
            moduleIds.CopyTo(batteries.moduleIds, freeIndex);

            drainsSustained.CopyTo(batteries.drainsApplied, freeIndex);
            drainsLeft.CopyTo(batteries.drainsToApply, freeIndex);
            drainsRates.CopyTo(batteries.drainsApplyRate, freeIndex);

            useDrainsTotal.CopyTo(batteries.useDrainsTotal, freeIndex);
            useDrainsRate.CopyTo(batteries.useDrainsRate, freeIndex);
            thresholds.CopyTo(batteries.thresholds, freeIndex);

            batteries.firstFreeIndex = requiredSize;
        }

        public static void ProcessUseOps(ConcurrentQueue<(long entityId, byte moduleId)> useModuleOps, ref Batteries batteries)
        {
            while (useModuleOps.TryDequeue(out var op))
            {
                var (entityId, moduleId) = op;

                for (int j = 0; j < batteries.firstFreeIndex; j++)
                {
                    if (batteries.entityIds[j] == entityId && batteries.moduleIds[j] == moduleId)
                    {
                        if (j < batteries.firstInactiveBatteryIndex)
                        {
                            batteries.drainsToApply[j] += batteries.useDrainsTotal[j];
                            batteries.drainsApplyRate[j] = batteries.useDrainsRate[j];
                        }
                        else
                        {
                            var indexToActivate = batteries.firstInactiveBatteryIndex;

                            batteries.entityIds[indexToActivate] = batteries.entityIds[j];
                            batteries.moduleIds[indexToActivate] = batteries.moduleIds[j];

                            batteries.drainsToApply[indexToActivate] = batteries.useDrainsTotal[j];
                            batteries.drainsApplyRate[indexToActivate] = batteries.useDrainsRate[j];

                            batteries.useDrainsTotal[indexToActivate] = batteries.useDrainsTotal[j];
                            batteries.useDrainsRate[indexToActivate] = batteries.useDrainsRate[j];
                            batteries.thresholds[indexToActivate] = batteries.thresholds[j];

                            batteries.firstInactiveBatteryIndex++;
                        }

                        break;
                    }
                }
            }
        }

        public static void ProcessAuthorityChangeOps(ConcurrentQueue<long> authorityChangeOps, ref Batteries batteries)
        {
            while (authorityChangeOps.TryDequeue(out var entityId))
            {
                var drainsApplied = new Improbable.Collections.List<int>();
                var drainsToApply = new Improbable.Collections.List<int>();
                for (int index = batteries.firstFreeIndex - 1; index >= 0; index--)
                {
                    if (batteries.entityIds[index] == entityId)
                    {
                        drainsApplied.Add(batteries.drainsApplied[index]);
                        drainsToApply.Add(batteries.drainsToApply[index]);
                    }
                }

                SpatialOSConnectionSystem.updateRechargeableOps.Enqueue(
                new ComponentUpdateOp<Rechargeable.Update>
                {
                    EntityId = new EntityId(entityId),
                    Update = new Rechargeable.Update
                    {
                        drainsSustained = drainsApplied,
                        drainsLeft = drainsToApply
                    },
                });
            }
        }

        public static void ProcessBatteryLogic(byte deltaTime, ref Batteries batteries)
        {
            for (int i = batteries.firstInactiveBatteryIndex - 1; i >= 0; i--)
            {
                if (batteries.drainsToApply[i] > batteries.drainsApplyRate[i])
                {
                    batteries.drainsApplied[i] += batteries.drainsApplyRate[i];
                    batteries.drainsToApply[i] -= batteries.drainsApplyRate[i];
                }
                else if (batteries.drainsToApply[i] > 0)
                {
                    batteries.drainsApplied[i] += batteries.drainsToApply[i];
                    batteries.drainsToApply[i] = 0;
                }

                if (batteries.drainsApplied[i] > deltaTime)
                {
                    batteries.drainsApplied[i] -= deltaTime;
                }
                else if (batteries.drainsApplied[i] > 0)
                {
                    batteries.drainsApplied[i] = 0;

                    //Swapping when recharged
                    batteries.entityIds[i] = batteries.entityIds[batteries.firstInactiveBatteryIndex - 1];
                    batteries.moduleIds[i] = batteries.moduleIds[batteries.firstInactiveBatteryIndex - 1];

                    batteries.drainsApplied[i] = batteries.drainsApplied[batteries.firstInactiveBatteryIndex - 1];
                    batteries.drainsToApply[i] = batteries.drainsToApply[batteries.firstInactiveBatteryIndex - 1];
                    batteries.drainsApplyRate[i] = batteries.drainsApplyRate[batteries.firstInactiveBatteryIndex - 1];

                    batteries.useDrainsTotal[i] = batteries.useDrainsTotal[batteries.firstInactiveBatteryIndex - 1];
                    batteries.useDrainsRate[i] = batteries.useDrainsRate[batteries.firstInactiveBatteryIndex - 1];
                    batteries.thresholds[i] = batteries.thresholds[batteries.firstInactiveBatteryIndex - 1];

                    batteries.firstInactiveBatteryIndex--;
                }
            }
        }
    }
}
