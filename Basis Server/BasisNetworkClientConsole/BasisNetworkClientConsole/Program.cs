using Basis.Network.Core;
using Basis.Scripts.BasisSdk.Players;
using Basis.Scripts.Networking.Compression;
using BasisNetworkClientConsole;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Text;
using static Basis.Network.Core.Serializable.SerializableBasis;
using static BasisNetworkPrimitiveCompression;
using static SerializableBasis;

namespace Basis
{
    class Program
    {
        public static string Password = "default_password";
        private static readonly object nameLock = new object(); // To synchronize name generation
        public static NetPeer LocalPLayer;

        public static string Ip = "localhost";//server1.basisvr.org //localhost
        public static int Port = 4296;

        public static byte[] AvatarMessage = new byte[LocalAvatarSyncMessage.AvatarSyncSize +1];
        public static Vector3 Position = new Vector3(0, 0, 0);
        public static Quaternion Rotation = new Quaternion(0, 0, 0, 1);
        public static float[] FloatArray = new float[LocalAvatarSyncMessage.StoredBones];
        public static ushort[] UshortArray = new ushort[LocalAvatarSyncMessage.StoredBones];
        private const ushort UShortMin = ushort.MinValue; // 0
        private const ushort UShortMax = ushort.MaxValue; // 65535
        private const ushort ushortRangeDifference = UShortMax - UShortMin;
        public static BasisRangedUshortFloatData RotationCompression = new BasisRangedUshortFloatData(-1f, 1f, 0.001f);
        public static void Main(string[] args)
        {
            // Set up global exception handlers
            AppDomain.CurrentDomain.UnhandledException += CurrentDomain_UnhandledException;
            TaskScheduler.UnobservedTaskException += TaskScheduler_UnobservedTaskException;

            // Create a cancellation token source
            var cancellationTokenSource = new CancellationTokenSource();
            var cancellationToken = cancellationTokenSource.Token;

            // Start the server in a background task and prevent it from exiting
            Task serverTask = Task.Run(() =>
            {
                try
                {
                    // Generate random UUID and player name
                    string randomUUID = GenerateFakeUUID();
                    string randomPlayerName = GenerateRandomPlayerName();

                    ReadyMessage RM = new ReadyMessage
                    {
                        playerMetaDataMessage = new PlayerMetaDataMessage()
                    };
                    RM.playerMetaDataMessage.playerDisplayName = randomPlayerName;
                    RM.playerMetaDataMessage.playerUUID = randomUUID;

                    AvatarNetworkLoadInformation ANLI = new AvatarNetworkLoadInformation
                    {
                        AvatarMetaUrl = "LoadingAvatar",
                        AvatarBundleUrl = "LoadingAvatar",
                        UnlockPassword = "LoadingAvatar"
                    };
                    byte[] Bytes = ANLI.EncodeToBytes();
                    //0 downloading 1 local
                    RM.clientAvatarChangeMessage = new ClientAvatarChangeMessage
                    {
                        byteArray = Bytes,
                        loadMode = 1,//0 is normal 
                    };
                    RM.localAvatarSyncMessage = new LocalAvatarSyncMessage
                    {
                        array = AvatarMessage,
                        hasAdditionalAvatarData = false,
                       AdditionalAvatarDatas = null,
                    };
                    AuthenticationMessage Authmessage = new AuthenticationMessage
                    {
                        bytes = Encoding.UTF8.GetBytes(Password)
                    };
                    BasisNetworkClient.AuthenticationMessage = Authmessage;
                    LocalPLayer = BasisNetworkClient.StartClient(Ip, Port, RM, true);
                    //   BasisNetworkClient.listener.NetworkReceiveEvent += NetworkReceiveEvent;
                    BNL.Log($"Connecting! Player Name: {randomPlayerName}, UUID: {randomUUID}");
                }
                catch (Exception ex)
                {
                    BNL.LogError($"Server encountered an error: {ex.Message} {ex.StackTrace}");
                }
            }, cancellationToken);

            // Register a shutdown hook to clean up resources when the application is terminated
            AppDomain.CurrentDomain.ProcessExit += async (sender, eventArgs) =>
            {
                BNL.Log("Shutting down server...");

                // Perform graceful shutdown of the server and logging
                cancellationTokenSource.Cancel();

                try
                {
                    await serverTask; // Wait for the server to finish
                }
                catch (Exception ex)
                {
                    BNL.LogError($"Error during server shutdown: {ex.Message}");
                }
                BNL.Log("Server shut down successfully.");
            };
            ushort[] UshortArray = new ushort[LocalAvatarSyncMessage.StoredBones];
            // Keep the application running
            while (true)
            {
                SendMovement();
                Thread.Sleep(33);
            }
        }
        private static void NetworkReceiveEvent(NetPeer peer, NetPacketReader Reader, byte channel, DeliveryMethod deliveryMethod)
        {

            //loop back index 0 (0 being real player)
            if (peer.Id == 0)
            {
                if (BasisNetworkCommons.MovementChannel == channel)
                {
                    ServerSideSyncPlayerMessage SSM = new ServerSideSyncPlayerMessage();
                    SSM.Deserialize(Reader,true);
                    Reader.Recycle();
                    NetDataWriter Writer = new NetDataWriter(true, 202);
                    SSM.avatarSerialization.Serialize(Writer, true);
                    LocalPLayer.Send(Writer, BasisNetworkCommons.MovementChannel, deliveryMethod);
                }
                else
                {
                    if(BasisNetworkCommons.FallChannel == channel)
                    {
                        if (deliveryMethod == DeliveryMethod.Unreliable)
                        {
                            if (Reader.TryGetByte(out byte Byte))
                            {
                              //  NetworkReceiveEvent(peer, Reader, Byte, deliveryMethod);
                            }
                            else
                            {
                                BNL.LogError($"Unknown channel no data remains: {channel} " + Reader.AvailableBytes);
                                Reader.Recycle();
                            }
                        }
                        else
                        {
                            BNL.LogError($"Unknown channel: {channel} " + Reader.AvailableBytes);
                            Reader.Recycle();
                        }
                    }
                }

            }
        }
        public static bool[] boolArray = new bool[8];
        public static void SendMovement()
        {
            if (LocalPLayer != null)
            {
                Position = Randomizer.GetRandomPosition(new Vector3(30,30,30),new Vector3(80,80,80));
                AvatarMessage[0] = ConvertBoolsToByte();
                Array.Fill(boolArray,false);
                int Offset = 1;
                WriteVectorFloatToBytes(Position, ref AvatarMessage, ref Offset);
                WriteQuaternionToBytes(Rotation, ref AvatarMessage, ref Offset, RotationCompression);
                WriteUShortsToBytes(UshortArray, ref AvatarMessage, ref Offset);
                LocalPLayer.Send(AvatarMessage, BasisNetworkCommons.MovementChannel, DeliveryMethod.Sequenced);
            }
        }
        public static byte ConvertBoolsToByte()
        {
            if (boolArray == null)
            {
                boolArray = new bool[8];
            }
            byte byteValue = 0;

            for (int i = 0; i < 8; i++)
            {
                if (boolArray[i])
                {
                    byteValue |= (byte)(1 << i);
                }
            }

            return byteValue;
        }
        public static ushort Compress(float value, float MinValue, float MaxValue, float valueDiffence)
        {
            // Clamp the value to ensure it's within the specified range
            value = Math.Clamp(value, MinValue, MaxValue);

            // Map the float value to the ushort range
            float normalized = (value - MinValue) / (valueDiffence); // 0..1
            return (ushort)(normalized * ushortRangeDifference);//+ UShortMin (its always zero)
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
            e.SetObserved(); // Prevents the application from crashing
        }

        private static string GenerateFakeUUID()
        {
            // Generate a fake UUID-like string
            Guid guid = Guid.NewGuid();
            return guid.ToString();
        }

        private static string GenerateRandomPlayerName()
        {
            // Thread-safe unique player name generation
            lock (nameLock)
            {
                string[] adjectives = { "Swift", "Brave", "Clever", "Fierce", "Nimble", "Silent", "Bold", "Lucky", "Strong", "Mighty", "Sneaky", "Fearless", "Wise", "Vicious", "Daring" };
                string[] nouns = { "Warrior", "Hunter", "Mage", "Rogue", "Paladin", "Shaman", "Knight", "Archer", "Monk", "Druid", "Assassin", "Sorcerer", "Ranger", "Guardian", "Berserker" };
                string[] titles = { "the Swift", "the Bold", "the Silent", "the Brave", "the Fierce", "the Wise", "the Protector", "the Shadow", "the Flame", "the Phantom" };

                // Colors with their corresponding names and hex codes for Unity's Rich Text
                (string Name, string Hex)[] colors =
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

                string[] animals = { "Wolf", "Tiger", "Eagle", "Dragon", "Lion", "Bear", "Hawk", "Panther", "Raven", "Serpent", "Fox", "Falcon" };

                Random random = new Random();

                // Randomly select one element from each array
                string adjective = adjectives[random.Next(adjectives.Length)];
                string noun = nouns[random.Next(nouns.Length)];
                string title = titles[random.Next(titles.Length)];
                var color = colors[random.Next(colors.Length)];
                string animal = animals[random.Next(animals.Length)];

                // Combine elements with rich text for the color
                string colorText = $"<color={color.Hex}>{color.Name}</color>";
                string generatedName = $"{adjective}{noun} {title} of the {colorText} {animal}";

                // Ensure uniqueness by appending a counter
                return $"{generatedName}";
            }
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

        // Ensure the byte array is large enough for reading
        private static void EnsureSize(byte[] bytes, int requiredSize)
        {
            if (bytes.Length < requiredSize)
            {
                throw new ArgumentException("Byte array is too small for the required size. Current Size is " + bytes.Length + " But Required " + requiredSize);
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
        // Object pool for byte arrays to avoid allocation during runtime
        private class ObjectPool<T>
        {
            private readonly Func<T> createFunc;
            private readonly Stack<T> pool;

            public ObjectPool(Func<T> createFunc)
            {
                this.createFunc = createFunc;
                this.pool = new Stack<T>();
            }

            public T Get()
            {
                return pool.Count > 0 ? pool.Pop() : createFunc();
            }

            public void Return(T item)
            {
                pool.Push(item);
            }
        }
    }
}
