using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using NUnit.Framework;

namespace PurrNet.Services.Tests
{
    public class DataSharingClientTests
    {
        sealed class FakeHttp : ServiceHttp
        {
            public string method;
            public string path;
            public object body;
            public CancellationToken token;
            public string response = "{}";
            public FakeHttp() : base(() => "http://localhost", () => "pk_pub_test", () => "test", () => "build", () => null, () => null) { }
            public override Task<HttpResult<T>> SendAsync<T>(string requestMethod, string requestPath, object requestBody, CancellationToken cancellationToken = default)
            {
                method = requestMethod; path = requestPath; body = requestBody; token = cancellationToken;
                return Task.FromResult(new HttpResult<T> { success = true, statusCode = 200, data = JsonConvert.DeserializeObject<T>(response) });
            }
        }

        [Test]
        public async Task AsksTheDataSharingEndpointWithAGetAndForwardsCancellation()
        {
            var http = new FakeHttp { response = "{\"dataSharing\":true,\"record\":true}" };
            using var stop = new CancellationTokenSource();
            var result = await new DataSharingService(http).GetStatusAsync(stop.Token);
            Assert.AreEqual("GET", http.method);
            Assert.AreEqual("/api/services/data-sharing", http.path);
            Assert.IsNull(http.body);
            Assert.AreEqual(stop.Token, http.token);
            Assert.IsTrue(result.success);
            Assert.IsTrue(result.data.dataSharing);
            Assert.IsTrue(result.data.record);
        }

        [Test]
        public async Task AnOptedOutProjectIsToldNotToRecord()
        {
            var http = new FakeHttp { response = "{\"dataSharing\":false,\"record\":false}" };
            var result = await new DataSharingService(http).GetStatusAsync();
            Assert.IsFalse(result.data.dataSharing);
            Assert.IsFalse(result.data.record);
        }

        [Test]
        public void TheWireNamesMatchTheWebsite()
        {
            var parsed = JsonConvert.DeserializeObject<DataSharingStatus>("{\"dataSharing\":true,\"record\":false}");
            Assert.IsTrue(parsed.dataSharing);
            Assert.IsFalse(parsed.record);
            StringAssert.Contains("\"dataSharing\":true", JsonConvert.SerializeObject(parsed));
            StringAssert.Contains("\"record\":false", JsonConvert.SerializeObject(parsed));
        }
    }
}
