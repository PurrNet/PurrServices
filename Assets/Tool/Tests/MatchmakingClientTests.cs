using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using NUnit.Framework;
using UnityEngine.Networking;

namespace PurrNet.Services.Tests
{
    public class MatchmakingClientTests
    {
        sealed class RecordingHttp : ServiceHttp
        {
            public string method;
            public string path;
            public JObject body;
            public CancellationToken token;
            public string response = "{}";
            public RecordingHttp() : base(() => "http://localhost", () => "pk_pub_test", () => "test", () => "build", () => "session", () => null) { }
            public override Task<HttpResult<T>> SendAsync<T>(string requestMethod, string requestPath, object requestBody, CancellationToken cancellationToken = default)
            {
                method = requestMethod; path = requestPath; token = cancellationToken;
                body = requestBody == null ? null : JObject.Parse(JsonConvert.SerializeObject(requestBody));
                return Task.FromResult(new HttpResult<T> { success = true, statusCode = 200, data = JsonConvert.DeserializeObject<T>(response) });
            }
        }

        [Test]
        public async Task TicketRetryRetainsIdentityAndDoesNotSerializeRatingsOrSessions()
        {
            var http = new RecordingHttp(); var client = new MatchmakingService(http);
            var request = new MatchmakingTicketRequest { queueId = "ranked", requestId = "same-retry-id", partySize = 2, latencies = new Dictionary<string, float> { ["eu"] = 32.5f } };
            await client.CreateTicketAsync(request);
            var first = http.body.ToString();
            await client.CreateTicketAsync(request);
            Assert.AreEqual(first, http.body.ToString());
            Assert.AreEqual("/api/matchmaking/tickets", http.path);
            CollectionAssert.AreEquivalent(new[] { "queueId", "requestId", "partySize", "latencies" }, http.body.Properties().Select(p => p.Name));
            Assert.AreEqual(32.5f, (float)http.body["latencies"]["eu"]);
            Assert.Throws<ArgumentException>(() => client.CreateTicketAsync(new MatchmakingTicketRequest { queueId = "ranked" }));
        }

        [Test]
        public async Task PartyJoinAndCancellationUsePlayerEndpointsAndForwardCancellation()
        {
            var http = new RecordingHttp(); var client = new MatchmakingService(http);
            using var stop = new CancellationTokenSource();
            await client.JoinTicketAsync("ticket", "invitation", new Dictionary<string, float> { ["eu"] = 40 }, stop.Token);
            Assert.AreEqual("/api/matchmaking/tickets/ticket/join", http.path);
            Assert.AreEqual(stop.Token, http.token);
            CollectionAssert.AreEquivalent(new[] { "inviteCode", "latencies" }, http.body.Properties().Select(p => p.Name));
            await client.CancelTicketAsync("ticket", stop.Token);
            Assert.AreEqual("DELETE", http.method); Assert.IsNull(http.body);
            Assert.AreEqual(stop.Token, http.token);
        }

        [Test]
        public async Task ConnectionPublishNeverEchoesAdmissionTokenOrHostId()
        {
            var http = new RecordingHttp();
            var connection = new MatchmakingConnection { serverAddress = "room", serverPort = 0, connectionToken = "private-token", hostId = "host" };
            await new MatchmakingService(http).PublishConnectionAsync("match", connection);
            CollectionAssert.AreEquivalent(new[] { "serverAddress", "serverPort" }, ((JObject)http.body["connection"]).Properties().Select(p => p.Name));
            Assert.IsFalse(http.body.ToString().Contains("private-token"));
            await new MatchmakingServerClient(http).PublishConnectionAsync("match", "claim", connection);
            Assert.AreEqual("/api/matchmaking/server/matches/match/connection", http.path);
            Assert.AreEqual("claim", (string)http.body["claimToken"]);
            Assert.IsFalse(http.body.ToString().Contains("private-token"));
        }

        [Test]
        public async Task DrawAlwaysSerializesExplicitNullAndNeverClientRatings()
        {
            var http = new RecordingHttp(); var client = new MatchmakingService(http);
            var previous = JsonConvert.DefaultSettings;
            try
            {
                JsonConvert.DefaultSettings = () => new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore };
                await client.SubmitResultAsync("match", "stable-result", null);
                Assert.AreEqual(JTokenType.Null, http.body["winningTeam"].Type);
                CollectionAssert.AreEquivalent(new[] { "resultId", "winningTeam" }, http.body.Properties().Select(p => p.Name));
            }
            finally { JsonConvert.DefaultSettings = previous; }
            Assert.Throws<ArgumentOutOfRangeException>(() => client.SubmitResultAsync("match", "result", 2));
        }

        [Test]
        public async Task DedicatedClaimsOmitAbsentFiltersAndRenewWithFencingToken()
        {
            var http = new RecordingHttp(); var client = new MatchmakingServerClient(http);
            await client.ClaimAsync("worker");
            Assert.AreEqual("/api/matchmaking/server/claims", http.path);
            CollectionAssert.AreEquivalent(new[] { "workerId" }, http.body.Properties().Select(p => p.Name));
            await client.ClaimAsync("worker", "ranked", "eu");
            Assert.AreEqual("ranked", (string)http.body["queueId"]);
            Assert.AreEqual("eu", (string)http.body["region"]);
            await client.RenewClaimAsync("match", "worker", "fence");
            Assert.AreEqual("match", (string)http.body["matchId"]);
            Assert.AreEqual("fence", (string)http.body["claimToken"]);
        }

        [Test]
        public async Task AdmissionAndResultsUseDifferentPlayerAndServerAuthorities()
        {
            var http = new RecordingHttp(); var player = new MatchmakingService(http); var server = new MatchmakingServerClient(http);
            await player.VerifyAdmissionAsync("match", "steam:1", "proof");
            Assert.AreEqual("/api/matchmaking/matches/match/verify", http.path);
            await server.VerifyAdmissionAsync("match", "steam:1", "proof");
            Assert.AreEqual("/api/matchmaking/server/matches/match/verify", http.path);
            Assert.AreEqual("steam:1", (string)http.body["playerId"]);
            Assert.AreEqual("proof", (string)http.body["connectionToken"]);
            await server.SubmitResultAsync("match", "result", 1);
            Assert.AreEqual("/api/matchmaking/server/matches/match/result", http.path);
            await server.CancelMatchAsync("match", MatchmakingCancellationReason.ServerUnavailable);
            Assert.AreEqual("/api/matchmaking/server/matches/match/cancel", http.path);
            Assert.AreEqual("server_unavailable", (string)http.body["reason"]);
        }

        [Test]
        public async Task IdentifiersCannotInjectAPathOrQuery()
        {
            var http = new RecordingHttp(); var client = new MatchmakingService(http);
            await client.GetTicketAsync("ticket/../../server?x=1");
            Assert.AreEqual("/api/matchmaking/tickets/ticket%2F..%2F..%2Fserver%3Fx%3D1", http.path);
            await client.GetRatingAsync("queue&other=1");
            Assert.AreEqual("/api/matchmaking/ratings?queueId=queue%26other%3D1", http.path);
        }

        [Test]
        public void MatchResponsesPreserveNestedTeamsNullableConnectionAndPrivateAdmission()
        {
            const string json = "{\"id\":\"m\",\"state\":\"allocating\",\"teams\":[[{\"playerId\":\"a\",\"partyId\":\"p\",\"accepted\":true}],[{\"playerId\":\"b\",\"accepted\":false}]],\"connection\":null,\"deadline\":\"2026-11-01T12:00:00Z\"}";
            var match = JsonConvert.DeserializeObject<MatchmakingMatchData>(json);
            Assert.IsNull(match.connection);
            Assert.AreEqual("a", match.teams[0][0].playerId); Assert.IsTrue(match.teams[0][0].accepted);
            Assert.AreEqual("b", match.teams[1][0].playerId); Assert.IsFalse(match.teams[1][0].accepted);
            Assert.AreEqual("2026-11-01T12:00:00Z", match.deadline);
            Assert.IsNull(JsonConvert.DeserializeObject<MatchmakingClaim>("{\"match\":null}").match);
            Assert.IsNull(JsonConvert.DeserializeObject<MatchmakingAdmission>("{\"valid\":false}").team);
            Assert.IsNull(JsonConvert.DeserializeObject<MatchmakingResult>("{\"winningTeam\":null}").winningTeam);
        }

        [TestCase("pk_pub_client")]
        [TestCase("pk_user")]
        [TestCase("")]
        [TestCase(null)]
        public void TrustedClientRejectsNonSecretConfiguration(string key)
        {
            Assert.Throws<ArgumentException>(() => new MatchmakingServerClient("https://example.test", key));
        }

        [Test]
        public void CapturedContextRetainsOriginalIdentityAfterLoginAndScopeChange()
        {
            var session = "first-session"; var scope = "first-scope"; var apiKey = "first-key"; var compatibility = "first-build";
            var original = new ServiceHttp(() => "https://example.test", () => apiKey, () => scope, () => compatibility, () => session, () => null);
            original.timeoutSeconds = 9;
            var captured = original.CaptureContext();
            session = "second-session"; scope = "second-scope"; apiKey = "second-key"; compatibility = "second-build";
            using var request = UnityWebRequest.Get("https://example.test");
            typeof(ServiceHttp).GetMethod("SetHeaders", BindingFlags.Instance | BindingFlags.NonPublic).Invoke(captured, new object[] { request });
            Assert.AreEqual("Bearer first-key", request.GetRequestHeader("Authorization"));
            Assert.AreEqual("first-session", request.GetRequestHeader("X-Player-Session"));
            Assert.AreEqual("first-scope", request.GetRequestHeader("X-Purr-Scope"));
            Assert.AreEqual("first-build", request.GetRequestHeader("X-Purr-Lobby-Compatibility"));
            Assert.AreEqual(9, captured.timeoutSeconds);
        }

        [Test]
        public void CancellationBeforeSendDoesNotBuildUrlOrTouchUnityNetworking()
        {
            var http = new ServiceHttp(() => throw new Exception("Must not build a URL"), () => null, () => null, () => null, () => null, () => null);
            using var stop = new CancellationTokenSource(); stop.Cancel();
            Assert.CatchAsync<OperationCanceledException>(() => http.GetAsync<object>("/test", stop.Token));
            Assert.Throws<ArgumentOutOfRangeException>(() => http.timeoutSeconds = 0);
            Assert.Throws<ArgumentOutOfRangeException>(() => http.timeoutSeconds = 301);
        }

        static HttpResult<MatchmakingMatchData> ParseResponse(int status, bool succeeded, string body)
        {
            var method = typeof(ServiceHttp).GetMethod("ParseResponseBody", BindingFlags.Static | BindingFlags.NonPublic)
                .MakeGenericMethod(typeof(MatchmakingMatchData));
            return (HttpResult<MatchmakingMatchData>)method.Invoke(null, new object[] { status, succeeded, body, null });
        }

        [Test]
        public void MalformedSuccessDoesNotExposeValuesFromParserExceptions()
        {
            var response = ParseResponse(200, true, "{\"connection\":{\"serverPort\":\"private-admission-token\"}}");
            Assert.IsFalse(response.success);
            Assert.AreEqual("The service returned an invalid response.", response.error);
            Assert.IsNull(response.data);
        }

        [TestCase("{\"connectionToken\":\"private-admission-token\"}")]
        [TestCase("{\"error\":{\"connectionToken\":\"private-admission-token\"}}")]
        [TestCase("<html>private-admission-token</html>")]
        [TestCase("null")]
        [TestCase(null)]
        public void MalformedErrorsDoNotExposeRawResponseBodies(string body)
        {
            var response = ParseResponse(503, false, body);
            Assert.IsFalse(response.success);
            Assert.AreEqual("Service request failed (HTTP 503).", response.error);
            Assert.AreEqual(503, response.statusCode);
        }

        [Test]
        public void StructuredErrorKeepsOnlyTheServiceMessage()
        {
            var response = ParseResponse(409, false, "{\"error\":\"The match is not ready\",\"connectionToken\":\"private-admission-token\"}");
            Assert.AreEqual("The match is not ready", response.error);
            Assert.IsFalse(response.success);
        }

        [TestCase("17", 17)]
        [TestCase("999999", 86400)]
        [TestCase("invalid", 0)]
        [TestCase(null, 0)]
        public void RetryAfterIsBoundedAndMalformedValuesAreIgnored(string header, int expected)
        {
            var parsed = typeof(ServiceHttp).GetMethod("ParseRetryAfter", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, new object[] { header });
            Assert.AreEqual(expected, parsed);
        }
    }
}
