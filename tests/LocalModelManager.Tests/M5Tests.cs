using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.Json;
using LocalModelManager.Core;
using LocalModelManager.Infrastructure.LMStudio;
using LocalModelManager.Infrastructure.Network;
using Xunit;

namespace LocalModelManager.Tests;

public sealed class M5Tests
{
    private const string Ps = "[{\"type\":\"llm\",\"modelKey\":\"model-key\",\"path\":\"local/model/model.gguf\",\"deviceIdentifier\":null,\"identifier\":\"instance-id\",\"contextLength\":65536}]";
    private const string Models = "{\"data\":[{\"id\":\"instance-id\"}]}";
    private const string Chat = "{\"model\":\"instance-id\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"LMM_OK\",\"reasoning_content\":\"not part of displayed reply\"},\"finish_reason\":\"stop\"}]}";
    private readonly List<string[]> cliCalls = [];
    private readonly List<string> urls = [];
    private bool stopped;
    private string? changedPs;
    private int psCalls;
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>? respond;
    private static LanCandidate Candidate(string name = "Wi-Fi", string description = "Intel Wireless", string ip = "192.168.1.20",
        NetworkInterfaceType type = NetworkInterfaceType.Wireless80211, bool up = true, bool gateway = true, int index = 12) =>
        new(name, description, type, up, gateway, IPAddress.Parse(ip), index);
    private Task<ProcessResult> Run(string executable, IEnumerable<string> arguments, CancellationToken token)
    {
        var args = arguments.ToArray(); cliCalls.Add(args);
        if (args.SequenceEqual(new[] { "server", "status" })) return Task.FromResult(new ProcessResult(0, "", stopped ? "The server is not running." : "The server is running on port 1234."));
        if (args.Contains("--help")) return Task.FromResult(new ProcessResult(0, "--bind --port", ""));
        if (args[0] == "server") { stopped = false; return Task.FromResult(new ProcessResult(0, "", "Server started")); }
        psCalls++;
        return Task.FromResult(new ProcessResult(0, psCalls > 1 && changedPs is not null ? changedPs : Ps, ""));
    }
    private static HttpResponseMessage Response(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request, cancellationToken);
    }
    private async Task<HermesConnection> Verify(bool bound = true, LanCandidate? lan = null, CancellationToken token = default)
    {
        using var client = new HttpClient(new Handler(async (request, ct) =>
        {
            urls.Add(request.RequestUri!.ToString());
            if (request.Method == HttpMethod.Post)
            {
                using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                Assert.Equal("instance-id", body.RootElement.GetProperty("model").GetString());
                Assert.False(body.RootElement.GetProperty("stream").GetBoolean());
                Assert.Equal(128, body.RootElement.GetProperty("max_tokens").GetInt32());
            }
            return respond is not null ? await respond(request, ct) : Response(request.Method == HttpMethod.Post ? Chat : Models);
        }));
        var service = new HermesApiService("lms", Run, client, () => bound, () => lan ?? Candidate());
        return await service.VerifyAsync(null, "", _ => {}, (_, _) => {}, token);
    }
    [Fact]
    public async Task LanTimeoutDoesNotEraseVerifiedLocalConnection()
    {
        respond = (request, _) => request.RequestUri!.Host != "127.0.0.1" ?
            Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true)) : Task.FromResult(Response(request.Method == HttpMethod.Post ? Chat : Models));
        var connection = await Verify();
        Assert.False(connection.LanVerifiedFromThisPc);
        Assert.Contains("Локальный API подтверждён", connection.LanWarning);
        Assert.Contains("127.0.0.1", HermesApiService.BuildProfile(connection, false));
    }
    [Fact]
    public async Task VerifiesActualInstanceChatAndLanBeforeGeneratingProfiles()
    {
        var connection = await Verify();
        Assert.Equal("LMM_OK", connection.Reply);
        Assert.Equal("http://127.0.0.1:1234/v1", connection.LocalBaseUrl);
        Assert.Equal("http://192.168.1.20:1234/v1", connection.LanBaseUrl);
        Assert.True(connection.LanVerifiedFromThisPc);
        var local = HermesApiService.BuildProfile(connection, false);
        var lan = HermesApiService.BuildProfile(connection, true);
        Assert.Contains("provider: lmstudio", local);
        Assert.Contains("default: \"instance-id\"", local);
        Assert.Contains("context_length: 65536", local);
        Assert.Contains("192.168.1.20", lan);
        Assert.DoesNotContain("api_key", local);
        Assert.DoesNotContain("reasoning", connection.Reply);
        Assert.Equal(3, urls.Count);
        Assert.DoesNotContain(cliCalls, args => args[0] is "load" or "unload" || args.Contains("stop"));
    }
    [Fact]
    public async Task StoppedServerStartsAtHermesBindAndPortWithoutCors()
    {
        stopped = true;
        await Verify();
        Assert.Contains(cliCalls, args => args.SequenceEqual(new[] { "server", "start", "--bind", "0.0.0.0", "--port", "1234" }));
        Assert.DoesNotContain(cliCalls, args => args.Contains("--cors"));
    }
    [Fact]
    public async Task LocalOnlyListenerDoesNotPublishLanProfileOrRestartServer()
    {
        var connection = await Verify(bound: false);
        Assert.False(connection.LanVerifiedFromThisPc);
        Assert.Null(connection.LanBaseUrl);
        Assert.Throws<InvalidOperationException>(() => HermesApiService.BuildProfile(connection, true));
        Assert.DoesNotContain(cliCalls, args => args.Contains("start") || args.Contains("stop"));
    }
    [Fact]
    public async Task FailedLanCheckPreservesVerifiedLocalProfile()
    {
        respond = (request, _) => Task.FromResult(request.RequestUri!.Host != "127.0.0.1" ? Response("unreachable", HttpStatusCode.ServiceUnavailable) : Response(request.Method == HttpMethod.Post ? Chat : Models));
        var connection = await Verify();
        Assert.False(connection.LanVerifiedFromThisPc);
        Assert.NotNull(connection.LanWarning);
        Assert.Contains("127.0.0.1", HermesApiService.BuildProfile(connection, false));
    }
    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task HttpErrorsCannotProduceProfile(HttpStatusCode code)
    {
        respond = (_, _) => Task.FromResult(Response("API rejected request", code));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Verify());
        Assert.Contains(((int)code).ToString(), error.Message);
        Assert.Single(urls);
    }
    [Fact]
    public async Task MissingExactInstanceDoesNotTryDifferentModel()
    {
        respond = (_, _) => Task.FromResult(Response(Models.Replace("instance-id", "other-model")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Verify());
        Assert.Single(urls);
    }
    [Fact]
    public async Task InstanceChangeDuringCheckDoesNotProduceProfile()
    {
        changedPs = Ps.Replace("instance-id", "replacement-id");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Verify());
        Assert.Equal(2, urls.Count);
    }
    [Fact]
    public async Task CancellationDoesNotPublishProfile()
    {
        using var source = new CancellationTokenSource();
        respond = (_, token) => { source.Cancel(); return Task.FromCanceled<HttpResponseMessage>(token); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Verify(token: source.Token));
    }
    [Theory]
    [InlineData("\"content\":\"LMM_OK\"", "\"content\":null")]
    [InlineData("\"content\":\"LMM_OK\"", "\"content\":\"\"")]
    [InlineData("\"finish_reason\":\"stop\"", "\"finish_reason\":\"length\"")]
    [InlineData("\"model\":\"instance-id\"", "\"model\":\"other\"")]
    public void RejectsEmptyReasoningOnlyTruncatedOrWrongModelReplies(string from, string to) =>
        Assert.Throws<InvalidOperationException>(() => HermesApiService.ParseChatReply(Chat.Replace(from, to), "instance-id"));
    [Fact]
    public void RejectsShortUnknownRemoteOrAmbiguousLoadedContext()
    {
        Assert.Throws<InvalidOperationException>(() => HermesApiService.ResolveLoadedModel(Ps.Replace("65536", "32768"), null, ""));
        Assert.Throws<InvalidOperationException>(() => HermesApiService.ResolveLoadedModel(Ps.Replace("65536", "null"), null, ""));
        Assert.Throws<InvalidOperationException>(() => HermesApiService.ResolveLoadedModel(Ps.Replace("\"deviceIdentifier\":null", "\"deviceIdentifier\":\"remote\""), null, ""));
        Assert.Throws<InvalidOperationException>(() => HermesApiService.ResolveLoadedModel(Ps.TrimEnd(']') + "," + Ps.Trim('[', ']') + "]", null, ""));
    }
    [Fact]
    public void SelectedFileRestrictsLoadedModelAndYAMLQuotesIdentifier()
    {
        Assert.Throws<InvalidOperationException>(() => HermesApiService.ResolveLoadedModel(Ps, new ModelFile("not-read.gguf", "different.gguf", 4), "local/model"));
        var model = HermesApiService.ResolveLoadedModel(Ps, null, "") with { Identifier = "id\nmodel: hacked" };
        var profile = HermesApiService.BuildProfile(new(model, HermesApiService.LocalBaseUrl, null, null, false, "ok", DateTimeOffset.UtcNow, null), false);
        Assert.Contains("default: \"id\\nmodel: hacked\"", profile);
    }
    [Theory]
    [InlineData("tun0", "sing-tun Tunnel", "172.19.0.1", NetworkInterfaceType.Ethernet)]
    [InlineData("WARP", "Cloudflare WARP", "10.0.0.1", NetworkInterfaceType.Ethernet)]
    [InlineData("Wi-Fi", "Intel Wireless", "169.254.1.2", NetworkInterfaceType.Wireless80211)]
    [InlineData("Loopback", "Loopback", "127.0.0.1", NetworkInterfaceType.Loopback)]
    [InlineData("Ethernet", "Physical", "192.168.1.2", NetworkInterfaceType.Tunnel)]
    public void LanSelectionExcludesVirtualAndInvalidAddresses(string name, string description, string ip, NetworkInterfaceType type) =>
        Assert.Null(NetworkEndpoints.SelectLanAddress([Candidate(name, description, ip, type)]));
    [Fact]
    public void LanSelectionRequiresUpAndGatewayAndChoosesPhysicalInterface()
    {
        var physical = Candidate();
        var result = NetworkEndpoints.SelectLanAddress([
            Candidate("tun0", "sing-tun Tunnel", "172.19.0.1", NetworkInterfaceType.Ethernet, index: 1),
            Candidate("Down", "Physical", "192.168.2.1", up: false),
            Candidate("No gateway", "Physical", "192.168.3.1", gateway: false), physical]);
        Assert.Equal(physical, result);
    }
}