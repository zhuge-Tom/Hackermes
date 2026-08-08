using Hackermes.Automation.Packet;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Hackermes.PacketTraffic.Tests;

public sealed class LocalHttpAcceptanceTests
{
    [Fact]
    public async Task Real_loopback_http_exchange_can_be_captured_and_parsed()
    {
        await using var server = new LocalHttpServerFixture();
        using var client = new HttpClient();
        using var content = new StringContent("token=secret", Encoding.UTF8, "application/x-www-form-urlencoded");

        using var response = await client.PostAsync(new System.Uri(server.BaseUri, "echo?source=test"), content);
        var responseBody = await response.Content.ReadAsStringAsync();
        var rawRequest = await server.Request.WaitAsync(System.TimeSpan.FromSeconds(5));
        var request = HttpPacketCodec.Parse(rawRequest);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("{\"accepted\":true}", responseBody);
        Assert.Equal("POST", request.Method);
        Assert.Equal("/echo?source=test", request.Target);
        Assert.Equal("token=secret", request.Body);
        Assert.Contains("body:token", HttpPacketAnalyzer.Analyze(request).SensitiveFields);
    }
}
