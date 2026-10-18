using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using LocalModelManager.Core;
using LocalModelManager.Infrastructure.Network;
using LocalModelManager.Infrastructure.Processes;

namespace LocalModelManager.Infrastructure.LMStudio;

public sealed record ApiModel(string Identifier, string ModelKey, string Path, int ContextLength);
public sealed record HermesConnection(ApiModel Model, string LocalBaseUrl, string? LanBaseUrl,
    string? LanInterface, bool LanVerifiedFromThisPc, string Reply, DateTimeOffset VerifiedAt, string? LanWarning);

public sealed class HermesApiService
{
    public const string LocalBaseUrl = "http://127.0.0.1:1234/v1";
    private readonly string executable;
    private readonly Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run;
    private readonly HttpClient http;
    private readonly Func<bool> isLanBound;
    private readonly Func<LanCandidate?> findLan;
    public HermesApiService(string executable, ProcessRunner runner, HttpClient http) : this(executable,
        runner.RunAsync, http, () => NetworkEndpoints.IsListeningOnAllIPv4(1234), NetworkEndpoints.FindLanAddress) { }
    public HermesApiService(string executable, Func<string, IEnumerable<string>, CancellationToken, Task<ProcessResult>> run,
        HttpClient http, Func<bool> isLanBound, Func<LanCandidate?> findLan)
    {
        this.executable = executable; this.run = run; this.http = http; this.isLanBound = isLanBound; this.findLan = findLan;
    }
    public static HttpClient CreateLocalClient() => new(new HttpClientHandler
        { UseProxy = false, AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(60) };

    public async Task<HermesConnection> VerifyAsync(ModelFile? file, string repository,
        Action<string> log, Action<string, ProcessResult> report, CancellationToken token)
    {
        async Task<ProcessResult> Execute(string label, string[] args)
        {
            token.ThrowIfCancellationRequested();
            var result = await run(executable, args, token); report(label, result);
            token.ThrowIfCancellationRequested();
            if (result.ExitCode != 0) throw new InvalidOperationException($"LMS {label}, exit={result.ExitCode}\n{LmsOutput.CombinedText(result)}");
            return result;
        }
        var status = await Execute("server status", ["server", "status"]);
        var state = LmsOutput.ParseServerState(LmsOutput.CombinedText(status));
        if (state == ServerState.Stopped)
        {
            var help = await Execute("server start --help", ["server", "start", "--help"]);
            if (new[] { "--bind", "--port" }.Any(flag => !LmsOutput.CombinedText(help).Contains(flag, StringComparison.Ordinal)))
                throw new InvalidOperationException("LMS не подтверждает параметры API Hermes.");
            await Execute("server start / Hermes LAN", ["server", "start", "--bind", "0.0.0.0", "--port", "1234"]);
        }
        else if (state != ServerState.Running) throw new InvalidOperationException("Состояние сервера LMS неизвестно. Автоматический перезапуск не выполняется.");
        var before = await Execute("ps --json (API preflight)", ["ps", "--json"]);
        var model = ResolveLoadedModel(before.StandardOutput, file, repository);
        await CheckModels(LocalBaseUrl, model.Identifier, log, token);
        using var request = new HttpRequestMessage(HttpMethod.Post, LocalBaseUrl + "/chat/completions")
        {
            Content = JsonContent.Create(new
            {
                model = model.Identifier, messages = new[] { new { role = "user", content = "Reply with exactly LMM_OK. No explanation." } },
                max_tokens = 128, temperature = 0, stream = false
            })
        };
        var chat = await Send(request, log, token);
        var reply = ParseChatReply(chat, model.Identifier);
        log("Тестовый ответ модели: " + reply);
        var after = await Execute("ps --json (API verification)", ["ps", "--json"]);
        if (ResolveLoadedModel(after.StandardOutput, file, repository) != model)
            throw new InvalidOperationException("Загруженный экземпляр или его контекст изменился во время проверки. Профиль не подготовлен.");
        string? lanUrl = null, lanName = null, lanWarning = null;
        var lanVerified = false;
        try
        {
            if (!isLanBound()) lanWarning = "Сервер не подтверждён на 0.0.0.0:1234. Локальный API проверен, LAN-профиль недоступен; работающий сервер автоматически не перезапускается.";
            else if (findLan() is { } lan)
            {
                lanUrl = $"http://{lan.Address}:1234/v1"; lanName = lan.Name;
                using var lanTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                lanTimeout.CancelAfter(TimeSpan.FromSeconds(10));
                await CheckModels(lanUrl, model.Identifier, log, lanTimeout.Token);
                lanVerified = true;
            }
            else lanWarning = "Активный LAN IPv4 с основным шлюзом не найден. VPN/виртуальные интерфейсы исключены.";
        }
        catch (OperationCanceledException) when (!token.IsCancellationRequested)
        {
            lanWarning = "Истекло время проверки LAN. Локальный API подтверждён; LAN-профиль недоступен.";
            lanUrl = null; lanName = null; log(lanWarning);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            lanWarning = "LAN-адрес не подтверждён с этого ПК: " + ex.Message;
            lanUrl = null; lanName = null;
            log(lanWarning);
        }
        token.ThrowIfCancellationRequested();
        return new(model, LocalBaseUrl, lanUrl, lanName, lanVerified, reply, DateTimeOffset.UtcNow, lanWarning);
    }

    private async Task<string> Send(HttpRequestMessage request, Action<string> log, CancellationToken token)
    {
        using var response = await http.SendAsync(request, token);
        var body = await response.Content.ReadAsStringAsync(token);
        log($"[http] {request.Method} {request.RequestUri} -> {(int)response.StatusCode}");
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"API HTTP {(int)response.StatusCode}: {body[..Math.Min(body.Length, 2000)]}");
        return body;
    }
    private async Task CheckModels(string baseUrl, string identifier, Action<string> log, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, baseUrl + "/models");
        var body = await Send(request, log, token);
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.GetProperty("data").EnumerateArray().Any(model => model.GetProperty("id").GetString() == identifier))
            throw new InvalidOperationException("API не показывает выбранный загруженный экземпляр. Подмена model ID не выполняется.");
    }
    public static ApiModel ResolveLoadedModel(string json, ModelFile? file, string repository)
    {
        using var document = JsonDocument.Parse(LmsOutput.Clean(json));
        var models = document.RootElement.EnumerateArray().Where(model =>
            model.TryGetProperty("type", out var type) && type.GetString() == "llm" &&
            model.TryGetProperty("deviceIdentifier", out var device) && device.ValueKind == JsonValueKind.Null);
        if (file is not null)
        {
            var expected = repository.Trim() + "/" + file.FileName;
            models = models.Where(model => model.TryGetProperty("path", out var path) &&
                string.Equals(path.GetString()?.Replace('\\', '/'), expected, StringComparison.OrdinalIgnoreCase));
        }
        var matches = models.ToArray();
        if (matches.Length != 1) throw new InvalidOperationException("Нужен один однозначно выбранный загруженный локальный LLM. Загрузите модель или выберите её GGUF и категорию.");
        var selected = matches[0];
        if (!selected.TryGetProperty("contextLength", out var context) || !context.TryGetInt32(out var length) || length < MemoryAdvisor.HermesContext)
            throw new InvalidOperationException("Фактический контекст загруженной модели меньше 65 536 или неизвестен. Профиль Hermes не подготовлен.");
        var identifier = selected.GetProperty("identifier").GetString();
        var key = selected.GetProperty("modelKey").GetString();
        var modelPath = selected.GetProperty("path").GetString();
        if (string.IsNullOrWhiteSpace(identifier) || string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(modelPath))
            throw new InvalidOperationException("LMS не сообщил идентификатор загруженной модели.");
        return new(identifier, key, modelPath, length);
    }
    public static string ParseChatReply(string json, string expectedIdentifier)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (!root.TryGetProperty("model", out var model) || model.GetString() != expectedIdentifier)
            throw new InvalidOperationException("API ответил другой моделью. Проверка не подтверждена.");
        var choices = root.GetProperty("choices");
        if (choices.GetArrayLength() == 0) throw new InvalidOperationException("API вернул пустой choices.");
        var first = choices[0];
        var message = first.GetProperty("message");
        if (message.GetProperty("role").GetString() != "assistant" ||
            !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(content.GetString()) ||
            first.GetProperty("finish_reason").GetString() != "stop")
            throw new InvalidOperationException("API не вернул завершённый текстовый ответ. Только reasoning или обрезанный ответ не считаются успешной проверкой.");
        return content.GetString()!;
    }
    public static string BuildProfile(HermesConnection connection, bool useLan)
    {
        if (connection.Model.ContextLength < MemoryAdvisor.HermesContext) throw new InvalidOperationException("Hermes требует минимум 64K.");
        var url = useLan ? connection.LanVerifiedFromThisPc ? connection.LanBaseUrl : null : connection.LocalBaseUrl;
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("LAN-профиль не подтверждён.");
        // JSON double-quoted scalars are valid YAML; this preserves punctuation without YAML injection.
        var quoteId = JsonSerializer.Serialize(connection.Model.Identifier);
        return $"# Generated by Local Model Manager. Model must remain loaded.\n# Verified: {connection.VerifiedAt:O}\n" +
            (useLan ? "# LAN checked from this PC only; access from another device is unverified.\n" : "") +
            $"model:\n  provider: lmstudio\n  default: {quoteId}\n  base_url: {JsonSerializer.Serialize(url)}\n  context_length: {connection.Model.ContextLength}\n";
    }
}