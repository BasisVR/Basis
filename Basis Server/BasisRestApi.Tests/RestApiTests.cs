using Basis.Network.Server;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Xunit;
using static SerializableBasis;

namespace BasisRestApi.Tests
{
    [Collection("RestApi")]
    public class RestApiTests : IDisposable
    {
        private const string ApiKey = "test-secret-key";
        private readonly BasisRestApiHandler _handler;
        private readonly HttpClient _authed;
        private readonly HttpClient _anon;
        private readonly string _base;

        public RestApiTests()
        {
            BasisNetworkResourceManagement.UshortNetworkDatabase.Clear();
            BasisNetworkPreloadResourceManagement.Reset();

            ushort port = FreePort();
            _base = $"http://localhost:{port}";

            _handler = new BasisRestApiHandler(new Configuration
            {
                ApiEnabled = true,
                ApiKey = ApiKey,
                ApiHost = "localhost",
                ApiPort = port,
            });

            _authed = new HttpClient { BaseAddress = new Uri(_base) };
            _authed.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");

            _anon = new HttpClient { BaseAddress = new Uri(_base) };
        }

        public void Dispose()
        {
            _handler.Dispose();
            _authed.Dispose();
            _anon.Dispose();
            BasisNetworkResourceManagement.UshortNetworkDatabase.Clear();
            BasisNetworkPreloadResourceManagement.Reset();
        }

        // ── Auth ──────────────────────────────────────────────────────────────

        [Fact]
        public async Task NoAuthHeader_Returns401()
        {
            var res = await _anon.GetAsync("/api/worlds");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }

        [Fact]
        public async Task WrongToken_Returns401()
        {
            using var client = new HttpClient { BaseAddress = new Uri(_base) };
            client.DefaultRequestHeaders.Add("Authorization", "Bearer wrong-token");
            var res = await client.GetAsync("/api/worlds");
            Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
        }

        [Fact]
        public async Task ValidToken_DoesNotReturn401()
        {
            var res = await _authed.GetAsync("/api/worlds");
            Assert.NotEqual(HttpStatusCode.Unauthorized, res.StatusCode);
        }

        // ── Routing ───────────────────────────────────────────────────────────

        [Fact]
        public async Task UnknownPath_Returns404()
        {
            var res = await _authed.GetAsync("/api/doesnotexist");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        [Fact]
        public async Task WrongMethod_Returns405()
        {
            var res = await _authed.DeleteAsync("/api/announce");
            Assert.Equal(HttpStatusCode.MethodNotAllowed, res.StatusCode);
        }

        // ── GET /api/worlds ───────────────────────────────────────────────────

        [Fact]
        public async Task GetWorlds_Empty_ReturnsEmptyList()
        {
            var res = await _authed.GetAsync("/api/worlds");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(0, doc.RootElement.GetProperty("worlds").GetArrayLength());
        }

        [Fact]
        public async Task GetWorlds_ReturnsOnlyScenes()
        {
            BasisNetworkResourceManagement.UshortNetworkDatabase.TryAdd("scene1", new LocalLoadResource
                { LoadedNetID = "scene1", Mode = 1, CombinedURL = "https://example.com/world.bee", Persist = false });
            BasisNetworkResourceManagement.UshortNetworkDatabase.TryAdd("prop1", new LocalLoadResource
                { LoadedNetID = "prop1", Mode = 0, CombinedURL = "https://example.com/prop.bee", Persist = false });

            var res = await _authed.GetAsync("/api/worlds");
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var worlds = doc.RootElement.GetProperty("worlds");

            Assert.Equal(1, worlds.GetArrayLength());
            Assert.Equal("scene1", worlds[0].GetProperty("netId").GetString());
        }

        [Fact]
        public async Task GetWorlds_FieldsAreMappedCorrectly()
        {
            BasisNetworkResourceManagement.UshortNetworkDatabase.TryAdd("w1", new LocalLoadResource
            {
                LoadedNetID = "w1", Mode = 1,
                CombinedURL = "https://example.com/world.bee",
                Persist = true, IsAdminLocked = true, LoadStrategy = 0,
            });

            var res = await _authed.GetAsync("/api/worlds");
            var world = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
                .RootElement.GetProperty("worlds")[0];

            Assert.Equal("w1", world.GetProperty("netId").GetString());
            Assert.Equal("https://example.com/world.bee", world.GetProperty("url").GetString());
            Assert.True(world.GetProperty("persistent").GetBoolean());
            Assert.True(world.GetProperty("adminLocked").GetBoolean());
            Assert.Equal(0, world.GetProperty("strategy").GetInt32());
        }

        // ── POST /api/worlds ──────────────────────────────────────────────────

        [Fact]
        public async Task LoadWorld_MissingUrl_Returns400()
        {
            var res = await PostJson("/api/worlds", """{"password":"pass"}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task LoadWorld_MissingPassword_Returns400()
        {
            var res = await PostJson("/api/worlds", """{"url":"https://example.com/w.bee"}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task LoadWorld_Immediate_Returns200AndAddsToDatabase()
        {
            var res = await PostJson("/api/worlds",
                """{"url":"https://example.com/world.bee","password":"pass","strategy":"immediate"}""");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

            string netId = doc.RootElement.GetProperty("netId").GetString()!;
            Assert.True(BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(netId));
        }

        [Fact]
        public async Task LoadWorld_Synchronized_Returns200AndAddsToDatabase()
        {
            var res = await PostJson("/api/worlds",
                """{"url":"https://example.com/world.bee","password":"pass","strategy":"synchronized"}""");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            string netId = doc.RootElement.GetProperty("netId").GetString()!;
            Assert.True(BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(netId));
        }

        // ── DELETE /api/worlds/{netId} ────────────────────────────────────────

        [Fact]
        public async Task UnloadWorld_NotFound_Returns404()
        {
            var res = await _authed.DeleteAsync("/api/worlds/nonexistent-id");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        [Fact]
        public async Task UnloadWorld_Found_Returns200AndRemovesFromDatabase()
        {
            BasisNetworkResourceManagement.UshortNetworkDatabase.TryAdd("to-delete", new LocalLoadResource
                { LoadedNetID = "to-delete", Mode = 1, CombinedURL = "https://example.com/w.bee" });

            var res = await _authed.DeleteAsync("/api/worlds/to-delete");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            Assert.False(BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey("to-delete"));
        }

        // ── POST /api/announce ────────────────────────────────────────────────

        [Fact]
        public async Task AnnounceAll_MissingMessage_Returns400()
        {
            var res = await PostJson("/api/announce", """{}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task AnnounceAll_EmptyMessage_Returns400()
        {
            var res = await PostJson("/api/announce", """{"message":""}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task AnnounceAll_MessageTooLong_Returns400()
        {
            var body = JsonSerializer.Serialize(new { message = new string('a', 513) });
            var res = await PostJson("/api/announce", body);
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task AnnounceAll_NonStringMessage_Returns400()
        {
            var res = await PostJson("/api/announce", """{"message":42}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task AnnounceAll_ValidMessage_Returns200()
        {
            var res = await PostJson("/api/announce", """{"message":"hello world"}""");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        }

        // ── GET /api/players ──────────────────────────────────────────────────

        [Fact]
        public async Task GetPlayers_Empty_ReturnsEmptyList()
        {
            var res = await _authed.GetAsync("/api/players");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.Equal(0, doc.RootElement.GetProperty("players").GetArrayLength());
        }

        [Fact]
        public async Task GetPlayers_PositionIsArrayWhenKnown_NullWhenNot()
        {
            ushort port = FreePort();
            using var handler = new BasisRestApiHandler(new Configuration
            {
                ApiEnabled = true, ApiKey = ApiKey, ApiHost = "localhost", ApiPort = port,
            }, new FakeControl
            {
                Players = new[]
                {
                    new PlayerInfo(1, "uuid-1", "Alice", "desktop", new[] { 1.5f, 2f, -3.25f }),
                    new PlayerInfo(2, "uuid-2", "Bob", "vr"),
                },
            });
            using var client = new HttpClient { BaseAddress = new Uri($"http://localhost:{port}") };
            client.DefaultRequestHeaders.Add("Authorization", $"Bearer {ApiKey}");

            var res = await client.GetAsync("/api/players");
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var players = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
                .RootElement.GetProperty("players");

            var pos = players[0].GetProperty("position");
            Assert.Equal(3, pos.GetArrayLength());
            Assert.Equal(1.5f, pos[0].GetSingle());
            Assert.Equal(2f, pos[1].GetSingle());
            Assert.Equal(-3.25f, pos[2].GetSingle());
            Assert.Equal(JsonValueKind.Null, players[1].GetProperty("position").ValueKind);
        }

        private sealed class FakeControl : IServerControl
        {
            public IReadOnlyList<PlayerInfo> Players = Array.Empty<PlayerInfo>();
            public void AnnounceAll(string message) { }
            public bool AnnouncePlayer(string uuid, string message) => false;
            public string LoadWorld(WorldLoadParams p) => "0";
            public bool UnloadWorld(string netId) => false;
            public int ClearAllWorlds() => 0;
            public IReadOnlyList<WorldInfo> ListWorlds() => Array.Empty<WorldInfo>();
            public IReadOnlyList<PlayerInfo> ListPlayers() => Players;
            public string SwitchWorld(SwitchWorldParams p, CancellationToken cancellationToken = default) => "0";
        }

        // ── POST /api/announce/{uuid} ─────────────────────────────────────────

        [Fact]
        public async Task AnnouncePlayer_UnknownUuid_Returns404()
        {
            var res = await PostJson("/api/announce/unknown-uuid-123", """{"message":"hi"}""");
            Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        }

        [Fact]
        public async Task LoadWorld_PasswordEmbeddedInUrl_Returns200AndStoresCleanUrl()
        {
            const string cleanUrl = "https://example.com/world.bee";
            var res = await PostJson("/api/worlds", """{"url":"https://example.com/world.bee#secretpassword"}""");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            string netId = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
                .RootElement.GetProperty("netId").GetString()!;

            Assert.True(BasisNetworkResourceManagement.UshortNetworkDatabase.TryGetValue(netId, out var r));
            Assert.Equal(cleanUrl, r!.CombinedURL);
            Assert.Equal("secretpassword", Basis.Network.Server.Generic.BasisContentKeyRelay.SecretFor(r));
            Assert.True(Basis.Network.Core.BasisContentKeyProtocol.TryParseMarker(r.UnlockPassword, out ushort origin, out _) && origin == Basis.Network.Core.BasisContentKeyProtocol.ServerOrigin);
        }

        [Fact]
        public async Task LoadWorld_ExplicitPasswordOverridesEmbedded()
        {
            var res = await PostJson("/api/worlds",
                """{"url":"https://example.com/world.bee#embedded","password":"explicit"}""");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            string netId = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
                .RootElement.GetProperty("netId").GetString()!;

            Assert.True(BasisNetworkResourceManagement.UshortNetworkDatabase.TryGetValue(netId, out var r));
            Assert.Equal("explicit", Basis.Network.Server.Generic.BasisContentKeyRelay.SecretFor(r!));
            Assert.True(Basis.Network.Core.BasisContentKeyProtocol.IsMarker(r.UnlockPassword));
        }

        // ── POST /api/worlds/switch ───────────────────────────────────────────

        [Fact]
        public async Task SwitchWorld_MissingUrl_Returns400()
        {
            var res = await PostJson("/api/worlds/switch", """{"password":"pass"}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task SwitchWorld_MissingPasswordNoFragment_Returns400()
        {
            var res = await PostJson("/api/worlds/switch", """{"url":"https://example.com/next.bee"}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task SwitchWorld_Valid_Returns200AndAddsToDatabase()
        {
            var res = await PostJson("/api/worlds/switch",
                """{"url":"https://example.com/next.bee","password":"pass","announceMessage":"Switching!"}""");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            Assert.True(doc.RootElement.GetProperty("ok").GetBoolean());

            string netId = doc.RootElement.GetProperty("netId").GetString()!;
            Assert.True(BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(netId));
        }

        [Fact]
        public async Task SwitchWorld_PasswordEmbeddedInUrl_Returns200AndStoresCleanUrl()
        {
            const string cleanUrl = "https://beefile.io/7ec036b1a8fdd4e7f439339be9cbf54d";
            const string password = "MGFmZWU0Y2ZlMjExMzlkY2Y5MDJlMjQ3NTc1ZDhiODAwODk3ZjZiZWM4NWVmMzkyODA5YTk3NDRhMjE3NTQzZQ==";
            var res = await PostJson("/api/worlds/switch",
                $$"""{"url":"{{cleanUrl}}#{{password}}"}""");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            string netId = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
                .RootElement.GetProperty("netId").GetString()!;

            Assert.True(BasisNetworkResourceManagement.UshortNetworkDatabase.TryGetValue(netId, out var r));
            Assert.Equal(cleanUrl, r!.CombinedURL);
            // Fragment passwords are base64-encoded; server decodes them before storing.
            string decoded = Encoding.UTF8.GetString(Convert.FromBase64String(password));
            Assert.Equal(decoded, Basis.Network.Server.Generic.BasisContentKeyRelay.SecretFor(r));
            Assert.True(Basis.Network.Core.BasisContentKeyProtocol.IsMarker(r.UnlockPassword));
        }

        [Fact]
        public async Task SwitchWorld_InvalidDelay_Returns400()
        {
            var res = await PostJson("/api/worlds/switch",
                """{"url":"https://example.com/next.bee","password":"pass","delay":-1}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task SwitchWorld_DelayTooLarge_Returns400()
        {
            var res = await PostJson("/api/worlds/switch",
                """{"url":"https://example.com/next.bee","password":"pass","delay":301}""");
            Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        }

        [Fact]
        public async Task SwitchWorld_WithDelay_NetIdReturnedImmediatelyLoadDeferred()
        {
            // delay > 0: announce is sent first (cross-channel ordering), load starts after delay
            var res = await PostJson("/api/worlds/switch",
                """{"url":"https://example.com/next.bee","password":"pass","delay":1,"announceMessage":"Loading in 1s"}""");

            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            string netId = JsonDocument.Parse(await res.Content.ReadAsStringAsync())
                .RootElement.GetProperty("netId").GetString()!;

            Assert.False(BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(netId),
                "load should not be in DB until delay expires");

            await Task.Delay(TimeSpan.FromMilliseconds(1500));
            Assert.True(BasisNetworkResourceManagement.UshortNetworkDatabase.ContainsKey(netId),
                "load should be in DB after delay expires");
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private Task<HttpResponseMessage> PostJson(string path, string json) =>
            _authed.PostAsync(path, new StringContent(json, Encoding.UTF8, "application/json"));

        private static ushort FreePort()
        {
            using var tmp = new TcpListener(IPAddress.Loopback, 0);
            tmp.Start();
            ushort port = (ushort)((IPEndPoint)tmp.LocalEndpoint).Port;
            tmp.Stop();
            return port;
        }
    }

    [CollectionDefinition("RestApi", DisableParallelization = true)]
    public class RestApiCollection { }
}
