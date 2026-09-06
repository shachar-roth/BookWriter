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
        var message = json.RootElement.GetProperty("choices")[0].GetProperty("message");
        var contents = new List<AIContent>();
        if (ReadString(message, "content") is { Length: > 0 } text) contents.Add(new TextContent(text));
        if (message.TryGetProperty("tool_calls", out var toolCalls))
        {
            if (toolCalls.GetArrayLength() > 8) throw new InvalidOperationException("Too many tool calls in one response.");
            foreach (var call in toolCalls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                contents.Add(ParseFunctionCall(ReadString(call, "id"), ReadString(function, "name"), ReadString(function, "arguments")));
            }
        }
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, contents))
        {
            ModelId = ReadString(json.RootElement, "model") ?? _model,
            FinishReason = contents.OfType<FunctionCallContent>().Any() ? ChatFinishReason.ToolCalls : ChatFinishReason.Stop
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
        var pendingCalls = new SortedDictionary<int, PendingFunctionCall>();
        var toolCallsFinished = false;
        while (!reader.EndOfStream)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:", StringComparison.Ordinal)) continue;
            var payload = line[5..].Trim();
            if (payload == "[DONE]") break;
            string? chunk = null;
            try
            {
                using var json = JsonDocument.Parse(payload);
                if (!json.RootElement.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0) continue;
                var choice = choices[0];
                toolCallsFinished |= ReadString(choice, "finish_reason") == "tool_calls";
                if (choice.TryGetProperty("delta", out var delta))
                {
                    chunk = ReadString(delta, "content");
                    if (delta.TryGetProperty("tool_calls", out var calls))
                    {
                        foreach (var call in calls.EnumerateArray())
                        {
                            var index = call.GetProperty("index").GetInt32();
                            if (index is < 0 or > 7) throw new InvalidOperationException("Too many tool calls in one response.");
                            if (!pendingCalls.TryGetValue(index, out var pending)) pendingCalls[index] = pending = new();
                            if (ReadString(call, "id") is { } id) pending.Id = id;
                            if (call.TryGetProperty("function", out var function))
                            {
                                pending.Name.Append(ReadString(function, "name"));
                                pending.Arguments.Append(ReadString(function, "arguments"));
                            }
                            if (pending.Arguments.Length > 32000 || pending.Name.Length > 128 || pending.Id?.Length > 256)
                                throw new InvalidOperationException("Tool call exceeded the allowed size.");
                        }
                    }
                }
            }
            catch (JsonException)
            {
                if (options?.Tools is { Count: > 0 })
                    throw new InvalidOperationException("The provider returned a malformed tool stream. Please try again.");
                // Ignore malformed keep-alive chunks from compatible providers.
            }
            if (!string.IsNullOrEmpty(chunk)) yield return new ChatResponseUpdate(ChatRole.Assistant, chunk) { ModelId = _model };
        }
        if (pendingCalls.Count > 0)
        {
            // Never execute a partial call after a disconnected or truncated provider stream.
            if (!toolCallsFinished) throw new InvalidOperationException("The provider did not finish its tool request. Please try again.");
            yield return new ChatResponseUpdate
            {
                Role = ChatRole.Assistant, ModelId = _model, FinishReason = ChatFinishReason.ToolCalls,
                Contents = pendingCalls.Values.Select(call => (AIContent)ParseFunctionCall(call.Id, call.Name.ToString(), call.Arguments.ToString())).ToList()
            };
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
            ["messages"] = messages.SelectMany(SerializeMessage).ToArray()
        };
        if (options?.Tools is { Count: > 0 })
        {
            payload["tools"] = options.Tools.OfType<AIFunctionDeclaration>().Select(tool => new
            {
                type = "function",
                function = new { name = tool.Name, description = tool.Description, parameters = tool.JsonSchema }
            }).ToArray();
            payload["parallel_tool_calls"] = false;
            payload["tool_choice"] = options.ToolMode switch
            {
                NoneChatToolMode => (object)"none",
                RequiredChatToolMode { RequiredFunctionName: { } name } => new { type = "function", function = new { name } },
                RequiredChatToolMode => "required",
                _ => "auto"
            };
        }
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

    private static IEnumerable<Dictionary<string, object?>> SerializeMessage(ChatMessage message)
    {
        if (message.Role == ChatRole.Tool)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
                yield return new()
                {
                    ["role"] = "tool", ["tool_call_id"] = result.CallId,
                    ["content"] = result.Result is string text ? text : JsonSerializer.Serialize(result.Result)
                };
            yield break;
        }
        var serialized = new Dictionary<string, object?> { ["role"] = RoleName(message.Role), ["content"] = message.Text ?? "" };
        var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
        if (calls.Length > 0)
        {
            serialized["tool_calls"] = calls.Select(call => new
            {
                id = call.CallId, type = "function",
                function = new { name = call.Name, arguments = JsonSerializer.Serialize(call.Arguments) }
            }).ToArray();
        }
        yield return serialized;
    }

    private static FunctionCallContent ParseFunctionCall(string? id, string? name, string? arguments)
    {
        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(name) ||
            id.Length > 256 || name.Length > 128 || arguments is not { Length: <= 32000 })
            throw new InvalidOperationException("The provider returned an invalid tool call.");
        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, object?>>(arguments);
            if (parsed is null) throw new JsonException();
            return new FunctionCallContent(id, name, parsed);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("The provider returned invalid tool arguments. Please try again.");
        }
    }

    private sealed class PendingFunctionCall
    {
        public string? Id { get; set; }
        public StringBuilder Name { get; } = new();
        public StringBuilder Arguments { get; } = new();
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
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
