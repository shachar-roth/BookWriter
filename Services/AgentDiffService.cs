using DiffPlex.Chunkers;
using DiffPlex.DiffBuilder;
using DiffPlex.DiffBuilder.Model;

namespace IsraeliAuthorStudio.Services;

public static class AgentDiffService
{
    public static IReadOnlyList<AgentDiffSegment> Build(string before, string after)
    {
        return InlineDiffBuilder
            .Diff(before ?? "", after ?? "", ignoreWhiteSpace: false, ignoreCase: false, WordChunker.Instance)
            .Lines
            .Select(piece => new AgentDiffSegment(piece.Text, piece.Type switch
            {
                ChangeType.Inserted => AgentDiffKind.Inserted,
                ChangeType.Deleted => AgentDiffKind.Deleted,
                _ => AgentDiffKind.Unchanged
            }))
            .ToList();
    }
}

public sealed record AgentDiffSegment(string Text, AgentDiffKind Kind);

public enum AgentDiffKind
{
    Unchanged,
    Inserted,
    Deleted
}
