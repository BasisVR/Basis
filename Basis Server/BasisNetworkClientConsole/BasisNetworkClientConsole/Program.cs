using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.Compression;
using BasisNetworkClient;
using BasisNetworkClientConsole;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Text;
using System.Xml.Linq;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;

namespace Basis
{
    partial class Program
    {
        public static List<NetPeer> LocalPlayers = new List<NetPeer>(); // Store all clients
        public static List<NetworkClient> NetClients = new List<NetworkClient>(); // List of clients

        public static string Password = "default_password";
        public static string Ip = "localhost";
        public static int Port = 4296;
        public static int clientCount = 250;

        public static void Main(string[] args)
        {
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            LoadOrCreateConfigXml("Config.xml");

            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            NetDebug.Logger = new BasisClientLogger();

            Task clientsTask = Task.Run(async () =>
            {
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(Password);
                    AvatarNetworkLoadInformation ANLI = new AvatarNetworkLoadInformation
                    {
                        AvatarMetaUrl = "LoadingAvatar",
                        AvatarBundleUrl = "LoadingAvatar",
                        UnlockPassword = "LoadingAvatar"
                    };
                    byte[] Bytes = ANLI.EncodeToBytes();

                    List<NetworkClient> tempClients = new List<NetworkClient>();
                    List<NetPeer> tempPeers = new List<NetPeer>();

                    List<Task> connectTasks = new List<Task>();

                    for (int i = 0; i < clientCount; i++)
                    {
                        connectTasks.Add(Task.Run(() =>
                        {
                            try
                            {
                                string randomUUID = GenerateFakeUUID();
                                string randomPlayerName = GenerateRandomPlayerName();

                                ReadyMessage RM = new ReadyMessage
                                {
                                    playerMetaDataMessage = new PlayerMetaDataMessage
                                    {
                                        playerDisplayName = randomPlayerName,
                                        playerUUID = randomUUID
                                    },
                                    clientAvatarChangeMessage = new ClientAvatarChangeMessage
                                    {
                                        byteArray = Bytes,
                                        loadMode = 1
                                    },
                                    localAvatarSyncMessage = new LocalAvatarSyncMessage
                                    {
                                        array = AvatarMessage,
                                        AdditionalAvatarDatas = null
                                    }
                                };

                                NetworkClient netClient = new NetworkClient();
                                NetPeer peer = netClient.StartClient(Ip, Port, RM, bytes, true);

                                if (peer != null)
                                {
                                    netClient.listener.NetworkReceiveEvent += NetworkReceiveEvent;
                                    netClient.listener.PeerDisconnectedEvent += NetworkDisconnectionEvent;

                                    lock (tempClients) tempClients.Add(netClient);
                                    lock (tempPeers) tempPeers.Add(peer);

                                    BNL.Log($"Connected! Player Name: {randomPlayerName}, UUID: {randomUUID}");
                                }
                            }
                            catch (Exception ex)
                            {
                                BNL.LogError($"Failed to connect client: {ex.Message}");
                            }
                        }));
                    }

                    await Task.WhenAll(connectTasks);

                    NetClients.AddRange(tempClients);
                    LocalPlayers.AddRange(tempPeers);

                    BNL.Log($"All clients connected successfully: {LocalPlayers.Count}/{clientCount}");
                }
                catch (Exception ex)
                {
                    BNL.LogError($"Error during client connections: {ex.Message} {ex.StackTrace}");
                }
            }, cancellationToken);

            AppDomain.CurrentDomain.ProcessExit += async (sender, eventArgs) =>
            {
                BNL.Log("Shutting down clients...");
                cancellationTokenSource.Cancel();

                try
                {
                    await clientsTask;
                }
                catch (Exception ex)
                {
                    BNL.LogError($"Error during shutdown: {ex.Message}");
                }

                BNL.Log("Clients shut down successfully.");
            };

            // Wait for clients to finish connecting
            clientsTask.Wait();

            // Begin processing connected clients
            while (true)
            {
                List<NetPeer> peersSnapshot;
                lock (LocalPlayers)
                {
                    peersSnapshot = new List<NetPeer>(LocalPlayers);
                }

                foreach (var peer in peersSnapshot)
                {
                    SendMovement(peer);
                }

                Thread.Sleep(33);
            }
        }
        public class BasisClientLogger : INetLogger
        {
            public void WriteNet(NetLogLevel level, string str, params object[] args)
            {
                switch (level)
                {
                    case NetLogLevel.Warning:
                        BNL.LogWarning(str);
                        break;
                    case NetLogLevel.Error:
                        BNL.LogError(str);
                        break;
                    case NetLogLevel.Trace:
                        //  BNL.Log(str);
                        break;
                    case NetLogLevel.Info:
                        //   BNL.Log(str);
                        break;
                }
            }
        }
        private static void NetworkDisconnectionEvent(NetPeer peer, DisconnectInfo disconnectInfo)
        {
            Console.WriteLine($"Peer was removed ");
        }

        private static void LoadOrCreateConfigXml(string filePath)
        {
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Config file not found. Creating default config: {filePath}");

                var defaultConfig = new XElement("Configuration",
                    new XElement("Password", Password),
                    new XElement("Ip", Ip),
                    new XElement("Port", Port),
                    new XElement("ClientCount", clientCount)
                );

                var doc = new XDocument(defaultConfig);
                try
                {
                    doc.Save(filePath);
                    Console.WriteLine("Default config created.");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Failed to create config: {ex.Message}");
                    return;
                }
            }

            try
            {
                XDocument doc = XDocument.Load(filePath);
                XElement root = doc.Element("Configuration");

                if (root != null)
                {
                    Password = root.Element("Password")?.Value ?? Password;
                    Ip = root.Element("Ip")?.Value ?? Ip;
                    Port = int.TryParse(root.Element("Port")?.Value, out int portValue) ? portValue : Port;
                    clientCount = int.TryParse(root.Element("ClientCount")?.Value, out int count) ? count : clientCount;
                }

                Console.WriteLine($"Loaded Config - IP: {Ip}, Port: {Port}, Clients: {clientCount}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error loading config: {ex.Message}");
            }
        }

        private static void NetworkReceiveEvent(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod deliveryMethod)
        {
            if (peer.Id == 0)
            {
                switch (channel)
                {
                    case BasisNetworkCommons.AuthIdentityMessage:
                        AuthIdentityMessage(peer, reader, channel);
                        break;
                    default:
                        break;
                }
            }
        }

        private static void CurrentDomain_UnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            if (exception != null)
            {
                BNL.LogError($"Fatal exception: {exception.Message}");
                BNL.LogError($"Stack trace: {exception.StackTrace}");
            }
            else
            {
                BNL.LogError("An unknown fatal exception occurred.");
            }
        }

        private static void TaskScheduler_UnobservedTaskException(object sender, UnobservedTaskExceptionEventArgs e)
        {
            foreach (var exception in e.Exception.InnerExceptions)
            {
                BNL.LogError($"Unobserved task exception: {exception.Message}");
                BNL.LogError($"Stack trace: {exception.StackTrace}");
            }
            e.SetObserved();
        }
        public static void SendMovement(NetPeer LocalPLayer)
        {
            if (LocalPLayer != null)
            {
                int Offset = 0;
                Position = Randomizer.GetRandomPosition(MinPosition, MaxPosition);
                WriteVectorFloatToBytes(Position, ref AvatarMessage, ref Offset);
                WriteQuaternionToBytes(Rotation, ref AvatarMessage, ref Offset, RotationCompression);
                WriteUShortsToBytes(UshortArray, ref AvatarMessage, ref Offset);
                if (AvatarMessage.Length == 0)
                {
                    BNL.LogError("trying to sending a message without a length NetworkReceiveEvent!");
                }
                else
                {
                    LocalPLayer.Send(AvatarMessage, BasisNetworkCommons.MovementChannel, DeliveryMethod.Sequenced);
                }
            }
        }
        public static bool ValidateSize(NetPacketReader reader, NetPeer peer, byte channel)
        {
            if (reader.AvailableBytes == 0)
            {
                BNL.LogError($"Missing Data from peer! {peer.Id} with channel ID {channel}");
                return false;
            }
            return true;
        }
        public static void AuthIdentityMessage(NetPeer peer, NetPacketReader Reader, byte channel)
        {
            BNL.Log("Auth is being requested by server!");
            if (ValidateSize(Reader, peer, channel) == false)
            {
                BNL.Log("Auth Failed");
                Reader.Recycle();
                return;
            }
            BNL.Log("Validated Size " + Reader.AvailableBytes);
            if (BasisDIDAuthIdentityClient.IdentityMessage(peer, Reader, out NetDataWriter Writer))
            {
                BNL.Log("Sent Identity To Server!");
                peer.Send(Writer, BasisNetworkCommons.AuthIdentityMessage, DeliveryMethod.ReliableSequenced);
                Reader.Recycle();
            }
            else
            {
                BNL.LogError("Failed Identity Message!");
                Reader.Recycle();
            }
            BNL.Log("Completed");
        }
        public static void WriteUShortsToBytes(ushort[] values, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + LengthUshortBytes);

            // Manually copy ushort values as bytes
            for (int index = 0; index < LocalAvatarSyncMessage.StoredBones; index++)
            {
                WriteUShortToBytes(values[index], ref bytes, ref offset);
            }
        }
        // Manual ushort to bytes conversion (without BitConverter)
        private unsafe static void WriteUShortToBytes(ushort value, ref byte[] bytes, ref int offset)
        {
            // Manually write the bytes
            bytes[offset] = (byte)(value & 0xFF);
            bytes[offset + 1] = (byte)((value >> 8) & 0xFF);
            offset += 2;
        }
        private static string GenerateFakeUUID()
        {
            // Generate a fake UUID-like string
            Guid guid = Guid.NewGuid();
            return guid.ToString();
        }

        private static string GenerateRandomPlayerName()
        {
            Random random = new Random();

            // Randomly select one element from each array
            string adjective = adjectives[random.Next(adjectives.Length)];
            string noun = nouns[random.Next(nouns.Length)];
            string title = titles[random.Next(titles.Length)];
            (string Name, string Hex) color = colors[random.Next(colors.Length)];
            string animal = animals[random.Next(animals.Length)];

            // Combine elements with rich text for the color
            string colorText = $"<color={color.Hex}>{color.Name}</color>";
            string generatedName = $"{adjective}{noun} {title} of the {colorText} {animal}";

            // Ensure uniqueness by appending a counter
            return $"{generatedName}";
        }
        public static void WriteVectorFloatToBytes(Vector3 values, ref byte[] bytes, ref int offset)
        {
            EnsureSize(ref bytes, offset + 12);
            WriteFloatToBytes(values.x, ref bytes, ref offset);//4
            WriteFloatToBytes(values.y, ref bytes, ref offset);//8
            WriteFloatToBytes(values.z, ref bytes, ref offset);//12
        }

        private unsafe static void WriteFloatToBytes(float value, ref byte[] bytes, ref int offset)
        {
            // Convert the float to a uint using its bitwise representation
            uint intValue = *((uint*)&value);

            // Manually write the bytes
            bytes[offset] = (byte)(intValue & 0xFF);
            bytes[offset + 1] = (byte)((intValue >> 8) & 0xFF);
            bytes[offset + 2] = (byte)((intValue >> 16) & 0xFF);
            bytes[offset + 3] = (byte)((intValue >> 24) & 0xFF);
            offset += 4;
        }
        public static int LengthUshortBytes = LocalAvatarSyncMessage.StoredBones * 2; // Initialize LengthBytes first
        // Object pool for byte arrays to avoid allocation during runtime
        private static readonly ObjectPool<byte[]> byteArrayPool = new ObjectPool<byte[]>(() => new byte[LengthUshortBytes]);
        // Ensure the byte array is large enough to hold the data
        private static void EnsureSize(ref byte[] bytes, int requiredSize)
        {
            if (bytes == null || bytes.Length < requiredSize)
            {
                // Reuse pooled byte arrays
                bytes = byteArrayPool.Get();
                Array.Resize(ref bytes, requiredSize);
            }
        }
        // Manual conversion of quaternion to bytes (without BitConverter)
        public static void WriteQuaternionToBytes(Quaternion rotation, ref byte[] bytes, ref int offset, BasisRangedUshortFloatData compressor)
        {
            EnsureSize(ref bytes, offset + 14);
            ushort compressedW = compressor.Compress(rotation.value.w);

            // Write the quaternion's components
            WriteFloatToBytes(rotation.value.x, ref bytes, ref offset);
            WriteFloatToBytes(rotation.value.y, ref bytes, ref offset);
            WriteFloatToBytes(rotation.value.z, ref bytes, ref offset);

            // Write the compressed 'w' component
            bytes[offset] = (byte)(compressedW & 0xFF);           // Low byte
            bytes[offset + 1] = (byte)((compressedW >> 8) & 0xFF); // High byte
            offset += 2;
        }
        public static byte[] AvatarMessage = new byte[LocalAvatarSyncMessage.AvatarSyncSize + 1];
        public static Vector3 Position = new Vector3(0, 0, 0);
        public static Quaternion Rotation = new Quaternion(0, 0, 0, 1);
        public static float[] FloatArray = new float[LocalAvatarSyncMessage.StoredBones];
        public static ushort[] UshortArray = new ushort[LocalAvatarSyncMessage.StoredBones];
        private const ushort UShortMin = ushort.MinValue; // 0
        private const ushort UShortMax = ushort.MaxValue; // 65535
        private const ushort ushortRangeDifference = UShortMax - UShortMin;
        public static BasisRangedUshortFloatData RotationCompression = new BasisRangedUshortFloatData(-1f, 1f, 0.001f);
        public static Vector3 MinPosition = new Vector3(30, 30, 30);
        public static Vector3 MaxPosition = new Vector3(80, 80, 80);
        public static string[] adjectives = { "Swift", "Brave", "Clever", "Fierce", "Nimble", "Silent", "Bold", "Lucky", "Strong", "Mighty", "Sneaky", "Fearless", "Wise", "Vicious", "Daring" };
        public static string[] nouns = { "Warrior", "Hunter", "Mage", "Rogue", "Paladin", "Shaman", "Knight", "Archer", "Monk", "Druid", "Assassin", "Sorcerer", "Ranger", "Guardian", "Berserker" };
        public static string[] titles = { "the Swift", "the Bold", "the Silent", "the Brave", "the Fierce", "the Wise", "the Protector", "the Shadow", "the Flame", "the Phantom" };
        // Thread-safe unique player name generation
        public static string[] animals = { "Wolf", "Tiger", "Eagle", "Dragon", "Lion", "Bear", "Hawk", "Panther", "Raven", "Serpent", "Fox", "Falcon" };

        // Colors with their corresponding names and hex codes for Unity's Rich Text
        public static (string Name, string Hex)[] colors =
        {
            ("Red", "#FF0000"),
            ("Blue", "#0000FF"),
            ("Green", "#008000"),
            ("Yellow", "#FFFF00"),
            ("Black", "#000000"),
            ("White", "#FFFFFF"),
            ("Silver", "#C0C0C0"),
            ("Golden", "#FFD700"),
            ("Crimson", "#DC143C"),
            ("Azure", "#007FFF"),
            ("Emerald", "#50C878"),
            ("Amber", "#FFBF00")
        };
    }
}
