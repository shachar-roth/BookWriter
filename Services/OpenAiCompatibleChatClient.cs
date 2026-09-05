using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IsraeliAuthorStudio.Services;

public sealed class OpenAiCompatibleChatClient : IChatClient
{
    private readonly HttpClient _httpClient;
    private readonly Uri _endpoint;
    private readonly string _model;
    private readonly string? _reasoningEffort;
    private readonly ChatClientMetadata _metadata;

    public OpenAiCompatibleChatClient(
        Uri baseUri,
        string model,
        string apiKey,
        string? reasoningEffort = null,
        HttpMessageHandler? messageHandler = null)
    {
        _model = model;
        _reasoningEffort = string.IsNullOrWhiteSpace(reasoningEffort) ? null : reasoningEffort.Trim();
        _endpoint = new Uri($"{baseUri.ToString().TrimEnd('/')}/chat/completions", UriKind.Absolute);
        _httpClient = messageHandler is null ? new HttpClient() : new HttpClient(messageHandler);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        _metadata = new ChatClientMetadata("openai-compatible", baseUri, model);
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, stream: false);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException(ReadProviderError(body, response.StatusCode.ToString()));
        using var json = JsonDocument.Parse(body);
        var content = json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, content))
        {
            ModelId = ReadString(json.RootElement, "model") ?? _model
        };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(messages, options, stream: true);
        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException(ReadProviderError(error, response.StatusCode.ToString()));
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]") yield break;
            string? chunk = null;
            try
            {
                using var json = JsonDocument.Parse(payload);
                var choice = json.RootElement.GetProperty("choices")[0];
                if (choice.TryGetProperty("delta", out var delta) && delta.TryGetProperty("content", out var content))
                {
                    chunk = content.GetString();
                }
            }
            catch (JsonException)
            {
                // Ignore malformed keep-alive chunks from compatible providers.
            }
            if (!string.IsNullOrEmpty(chunk)) yield return new ChatResponseUpdate(ChatRole.Assistant, chunk) { ModelId = _model };
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
    {
        ArgumentNullException.ThrowIfNull(serviceType);
        if (serviceKey is not null) return null;
        if (serviceType.IsInstanceOfType(this)) return this;
        if (serviceType == typeof(ChatClientMetadata)) return _metadata;
        return null;
    }

    public void Dispose() => _httpClient.Dispose();

    private HttpRequestMessage CreateRequest(IEnumerable<ChatMessage> messages, ChatOptions? options, bool stream)
    {
        var payload = new Dictionary<string, object?>
        {
            ["model"] = string.IsNullOrWhiteSpace(options?.ModelId) ? _model : options.ModelId,
            ["stream"] = stream,
            ["messages"] = messages.Select(message => new
            {
                role = RoleName(message.Role),
                content = message.Text ?? ""
            }).ToArray()
        };
        if (options?.Temperature is { } temperature) payload["temperature"] = temperature;
        if (_reasoningEffort is not null) payload["reasoning_effort"] = _reasoningEffort;

        return new HttpRequestMessage(HttpMethod.Post, _endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
    }

    private static string RoleName(ChatRole role)
    {
        if (role == ChatRole.System) return "system";
        if (role == ChatRole.Assistant) return "assistant";
        if (role == ChatRole.Tool) return "tool";
        return "user";
    }

    private static string ReadProviderError(string body, string fallback)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var message))
                return message.GetString() ?? fallback;
        }
        catch (JsonException)
        {
        }
        return string.IsNullOrWhiteSpace(body) ? fallback : body;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) ? value.GetString() : null;
}
