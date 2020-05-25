using Improbable.Collections;
using Improbable.Worker;
using RogueFleet.Ships;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace ShipWorker.ECS
{
    public static class CommunicationSystem
    {
        internal static readonly Dispatcher dispatcher = new Dispatcher();

        static CommunicationSystem()
        {
            dispatcher.OnRemoveComponent(CommunicationController.Metaclass, OnRemoveComponent);
            dispatcher.OnComponentUpdate(CommunicationController.Metaclass, OnControllerUpdate);
            dispatcher.OnAddComponent(CommunicationController.Metaclass, OnAddComponent);

            dispatcher.OnCommandRequest(Communication.Commands.Chat.Metaclass, OnChatRequest);
            dispatcher.OnCommandResponse(Communication.Commands.Chat.Metaclass, OnChatResponse);
        }

        static void OnControllerUpdate(ComponentUpdateOp<CommunicationController.Update> op) => componentUpdateOps.Enqueue(op);
        static readonly ConcurrentQueue<ComponentUpdateOp<CommunicationController.Update>> componentUpdateOps = new ConcurrentQueue<ComponentUpdateOp<CommunicationController.Update>>();

        static void OnRemoveComponent(RemoveComponentOp op) => removeComponentOps.Enqueue(op);
        static readonly ConcurrentQueue<RemoveComponentOp> removeComponentOps = new ConcurrentQueue<RemoveComponentOp>();

        static void OnAddComponent(AddComponentOp<CommunicationControllerData> op) => addComponentOps.Enqueue(op);
        static readonly ConcurrentQueue<AddComponentOp<CommunicationControllerData>> addComponentOps = new ConcurrentQueue<AddComponentOp<CommunicationControllerData>>();

        static void OnChatRequest(CommandRequestOp<Communication.Commands.Chat, ChatMessage> op) => chatMessageOps.Enqueue(op);
        static readonly ConcurrentQueue<CommandRequestOp<Communication.Commands.Chat, ChatMessage>> chatMessageOps = new ConcurrentQueue<CommandRequestOp<Communication.Commands.Chat, ChatMessage>>();

        static void OnChatResponse(CommandResponseOp<Communication.Commands.Chat, ChatMessage> op) => chatMessageResponseOps.Enqueue(op);
        static readonly ConcurrentQueue<CommandResponseOp<Communication.Commands.Chat, ChatMessage>> chatMessageResponseOps = new ConcurrentQueue<CommandResponseOp<Communication.Commands.Chat, ChatMessage>>();

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

        static void Update()
        {
            ProcessChatMessageOps();

            while (removeComponentOps.TryDequeue(out var op))
            {
                modes.Remove(op.EntityId.Id);
            }

            ProcessComponentUpdateOps();

            while (addComponentOps.TryDequeue(out var op))
            {
                modes[op.EntityId.Id] = op.Data.hailResponseMode;
            }
        }

        static readonly Dictionary<long, HailResponseMode> modes = new Dictionary<long, HailResponseMode>();

        static void ProcessComponentUpdateOps()
        {
            while (componentUpdateOps.TryDequeue(out var op))
            {
                var entityId = op.EntityId.Id;

                if (op.Update.hailResponseMode.HasValue)
                {
                    modes[entityId] = op.Update.hailResponseMode.Value;
                }

                for (int i = 0; i < op.Update.sendMessage.Count; i++)
                {
                    var chatEvent = op.Update.sendMessage[i];

                    SendChatMessage(entityId, chatEvent);
                }
            }
        }

        const string Salutation = "Salutation";
        const string Wait = "Wait";
        const string DoNotDistrub = "DoNotDisturb";

        static readonly Dictionary<long, HashSet<long>> listSalutationTo = new Dictionary<long, HashSet<long>>();
        static readonly Dictionary<long, HashSet<long>> listSalutationBy = new Dictionary<long, HashSet<long>>();

        static void SendChatMessage(long entityId, ChatMessage chatEvent)
        {
            if (chatEvent.text == Salutation)
            {
                if (listSalutationTo.TryGetValue(entityId, out var set))
                {
                    if (!set.Add(chatEvent.shipId.Id))
                    {
                        return;
                    }
                }
                else
                {
                    listSalutationTo[entityId] = new HashSet<long> { chatEvent.shipId.Id };
                }
            }
            else if (!listSalutationBy.TryGetValue(entityId, out var set) || !set.Contains(chatEvent.shipId.Id))
            {
                return;
            }

            SpatialOSConnectionSystem.requestChatOps.Enqueue(
                    new CommandRequestOp<Communication.Commands.Chat, ChatMessage>
                    {
                        EntityId = new EntityId(chatEvent.shipId.Id),
                        Request = new ChatMessage(new EntityId(entityId), chatEvent.text),
                    });
        }

        static void ProcessChatMessageOps()
        {
            while (chatMessageResponseOps.TryDequeue(out var op))
            {
                if (!op.Response.HasValue)
                {
                    continue;
                }

                var receiverId = op.EntityId.Id;
                var senderId = op.Response.Value.shipId.Id;
                var text = op.Response.Value.text;

                if (text == Salutation)
                {
                    if (listSalutationBy.TryGetValue(receiverId, out var set))
                    {
                        set.Add(senderId);
                    }
                    else
                    {
                        listSalutationBy[receiverId] = new HashSet<long> { senderId };
                    }
                }
            }

            while (chatMessageOps.TryDequeue(out var op))
            {
                var receiverId = op.EntityId.Id;
                var senderId = op.Request.shipId.Id;
                var text = op.Request.text;

                Option<ChatMessage> response = null;

                if (text == Salutation)
                {
                    if (listSalutationBy.TryGetValue(receiverId, out var set))
                    {
                        set.Add(senderId);
                    }
                    else
                    {
                        listSalutationBy[receiverId] = new HashSet<long> { senderId };
                    }

                    switch (modes[receiverId])
                    {
                        case HailResponseMode.Wait:
                            response = new ChatMessage(new EntityId(receiverId), Wait);
                            break;
                        case HailResponseMode.DoNotDisturd:
                            response = new ChatMessage(new EntityId(receiverId), DoNotDistrub);
                            break;
                        case HailResponseMode.HailBack:
                            response = new ChatMessage(new EntityId(receiverId), Salutation);
                            break;
                    }
                }
                else
                {
                    if (!listSalutationBy.TryGetValue(receiverId, out var set) || !set.Contains(senderId))
                    {
                        continue;
                    }

                    SpatialOSConnectionSystem.updateCommunicationOps.Enqueue(
                        new ComponentUpdateOp<Communication.Update>
                        {
                            EntityId = new EntityId(receiverId),
                            Update = new Communication.Update().AddReceivedChat(new ChatMessage(new EntityId(senderId), text)),
                        });
                }

                SpatialOSConnectionSystem.responseChatMessageOps.Enqueue(
                        new CommandResponseOp<Communication.Commands.Chat, ChatMessage>
                        {
                            EntityId = new EntityId(receiverId),
                            Response = response,
                            RequestId = new RequestId<OutgoingCommandRequest<Communication.Commands.Chat>>(op.RequestId.Id),
                        });
            }
        }
    }
}
