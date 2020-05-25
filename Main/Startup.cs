using Google.Apis.Auth.OAuth2;
using Google.Cloud.Firestore;
using Google.Cloud.Firestore.V1;
using Grpc.Auth;
using Grpc.Core;
using Improbable.Worker;
using ShipWorker.ECS;
using ShipWorker.ECS.ItemSystems;
using ShipWorker.ECS.ModuleSystems;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;

namespace ShipWorker
{
    static class Startup
    {
        static readonly Stopwatch stopwatch = new Stopwatch();

        internal static bool Connected
        {
            get;
            private set;
        }

        static int Main(string[] args)
        {
            stopwatch.Restart();

            if (args.Length != 4)
            {
                PrintUsage();
                return 1;
            }

            // Avoid missing component errors because no components are directly used in this project
            // and the GeneratedCode assembly is not loaded but it should be
            Assembly.Load("GeneratedCode");

            using (var connection = ConnectWithReceptionist(args[1], Convert.ToUInt16(args[2]), args[3]))
            {
                SpatialOSConnectionSystem.connection = connection;
                Connected = true;

                var channel = new Channel(FirestoreClient.DefaultEndpoint.Host, GoogleCredential.FromFile(Path.Combine(Directory.GetCurrentDirectory(), CloudFirestoreInfo.GoogleCredentialFile)).ToChannelCredentials());
                CloudFirestoreInfo.Database = FirestoreDb.Create(CloudFirestoreInfo.FirebaseProjectId, FirestoreClient.Create(channel));

                var tasks = new Task[15];

                tasks[1] = TriggerControllersSystem.UpdateLoop();

                tasks[2] = DamageablesSystem.UpdateLoop();
                tasks[3] = RechargeablesSystem.UpdateLoop();

                tasks[4] = SensorsSystem.UpdateLoop();
                tasks[5] = ScannersSystem.UpdateLoop();
                tasks[6] = SamplersSystem.UpdateLoop();

                tasks[7] = PingsSenderSystem.UpdateLoop();
                tasks[8] = PositionsSystem.UpdateLoop();

                tasks[9] = CraftingSystem.UpdateLoop();
                tasks[10] = MapItemsSystem.UpdateLoop();
                tasks[11] = ModularsSystem.UpdateLoop();
                tasks[12] = ModulesInventorySystem.UpdateLoop();
                tasks[13] = ResourcesInventorySystem.UpdateLoop();

                tasks[14] = CommunicationSystem.UpdateLoop();

                var dispatcher = new Dispatcher();

                dispatcher.OnDisconnect(op =>
                {
                    connection.SendLogMessage(LogLevel.Info, "Disconnect", string.Format("Reason for diconnect {0}", op.Reason));
                    Connected = false;
                });

                stopwatch.Stop();

                for (int i = 1; i < tasks.Length; i++)
                {
                    tasks[i].Start();
                }

                connection.SendLogMessage(LogLevel.Info, "Initialization", string.Format("Init Time {0}ms", stopwatch.Elapsed.TotalMilliseconds.ToString("N0")));

                while (Connected)
                {
                    using (var opList = connection.GetOpList(100))
                    {
                        stopwatch.Restart();

                        Parallel.Invoke(
                            () => dispatcher.Process(opList),
                            () => PingsSenderSystem.dispatcher.Process(opList),
                            () => PositionsSystem.dispatcher.Process(opList),
                            () => IdentificationsSystem.dispatcher.Process(opList),
                            () => ExplorationHacksSystem.dispatcher.Process(opList),
                            () => DamageablesSystem.dispatcher.Process(opList),
                            () => RechargeablesSystem.dispatcher.Process(opList),
                            () => SamplersSystem.dispatcher.Process(opList),
                            () => ScannersSystem.dispatcher.Process(opList),
                            () => SensorsSystem.dispatcher.Process(opList),
                            () => TriggerControllersSystem.dispatcher.Process(opList),
                            () => ModularsSystem.dispatcher.Process(opList),
                            () => ModulesInventorySystem.dispatcher.Process(opList),
                            () => ResourcesInventorySystem.dispatcher.Process(opList),
                            () => MapItemsSystem.dispatcher.Process(opList),
                            () => CraftingSystem.dispatcher.Process(opList),
                            () => CommunicationSystem.dispatcher.Process(opList)
                            );

                        stopwatch.Stop();

                        connection.SendLogMessage(LogLevel.Info, "Process OpList", string.Format("Time {0}ms", stopwatch.ElapsedMilliseconds.ToString("N0")));
                    }

                    stopwatch.Restart();
                    SpatialOSConnectionSystem.Update();
                    stopwatch.Stop();

                    connection.SendLogMessage(LogLevel.Info, "SpatialOSConnectionSystem.Update", string.Format("Time {0}ms", stopwatch.ElapsedMilliseconds.ToString("N0")));
                }

                tasks[0] = channel.ShutdownAsync();
                tasks[0].Start();

                Task.WaitAll(tasks);
            }
            
            return 1;
        }

        const string WorkerType = "ship_worker";

        static Connection ConnectWithReceptionist(string hostname, ushort port, string workerId)
        {
            var connectionParameters = new ConnectionParameters
            {
                EnableDynamicComponents = true,
                WorkerType = WorkerType,
                Network =
                {
                    ConnectionType = NetworkConnectionType.Tcp,
                    UseExternalIp = false,
                }
            };

            using (var future = Connection.ConnectAsync(hostname, port, workerId, connectionParameters))
            {
                return future.Get();
            }
        }

        static void PrintUsage()
        {
            Console.WriteLine("Usage: mono ShipWorker.exe receptionist <hostname> <port> <worker_id>");
            Console.WriteLine("Connects to SpatialOS");
            Console.WriteLine("    <hostname>      - hostname of the receptionist to connect to.");
            Console.WriteLine("    <port>          - port to use");
            Console.WriteLine("    <worker_id>     - name of the worker assigned by SpatialOS.");
        }
    }
}