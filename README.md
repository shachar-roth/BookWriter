# Israeli Author Studio

Local, scene-based writing studio for Hebrew book authors.

## Stack

- .NET 8
- ASP.NET Core Blazor Web App
- Server interactivity
- Hebrew/RTL-ready browser UI
- Markdown scene files with chapter, character, location, and timeline indexes
- Provider-neutral LLM assistant through `Microsoft.Extensions.AI`
- Git history and optional synchronization for every story project

## Run locally

```powershell
dotnet run
```

Git must be installed. LLM and remote Git credentials are configured in the application and are never stored inside story repositories.

## Project layout

- `Scenes/` contains one Markdown file per scene.
- `Metadata/Scenes/` contains inferred and manually locked scene metadata.
- `Indexes/` contains chapter, entity, and chronology indexes.
- `Assistant/project-memory.md` contains compressed durable assistant memory, not transcripts.
- `.studio/project.json` identifies the project schema.

Automatic snapshots run after ten minutes without project changes. They analyze at most ten stale scenes per batch, commit locally, rebase onto a configured upstream, and push. Local editing continues if the model or remote is unavailable.
