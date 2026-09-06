using System.Text.Json;
using Microsoft.Extensions.AI;

namespace IsraeliAuthorStudio.Services;

// Keep completed tool exchanges intact, but drop older exchanges once their findings are in research notes.
public sealed class ResearchContextChatClient(IChatClient inner, AssistantResearchSession research, int initialMessageCount)
    : DelegatingChatClient(inner)
{
    public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, CancellationToken cancellationToken = default) =>
        base.GetStreamingResponseAsync(BoundContext(messages), options, cancellationToken);

    public IReadOnlyList<ChatMessage> BoundContext(IEnumerable<ChatMessage> messages)
    {
        research.EnsureCurrentProject();
        var all = messages.ToList();
        var initial = all.Take(initialMessageCount).ToList();
        var groups = new List<List<ChatMessage>>();
        foreach (var message in all.Skip(initialMessageCount))
        {
            if (groups.Count == 0 || message.Role != ChatRole.Tool) groups.Add([]);
            groups[^1].Add(message);
        }
        while (groups.Count > 4 || (groups.Count > 1 && Size(groups.SelectMany(group => group)) > 48000)) groups.RemoveAt(0);
        if (Size(groups.SelectMany(group => group)) > 64000)
            throw new InvalidOperationException("The assistant requested too much content in one step. Please retry with a narrower request.");
        initial.Insert(1, new ChatMessage(ChatRole.System,
            "Older tool exchanges may have been removed to keep context bounded. Preserve cumulative findings using keep_research_notes, " +
            "and reread cited scenes if needed. Do not assume removed details.\n" + research.CoverageContext));
        return initial.Concat(groups.SelectMany(group => group)).ToList();
    }

    private static int Size(IEnumerable<ChatMessage> messages) => messages.Sum(message =>
        JsonSerializer.Serialize(message.Contents, AssistantResearchSession.JsonOptions).Length);
}
