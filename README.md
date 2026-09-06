# Israeli Author Studio

[![CI](https://github.com/shachar-roth/BookWriter/actions/workflows/ci.yml/badge.svg)](https://github.com/shachar-roth/BookWriter/actions/workflows/ci.yml)

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

## On-demand manuscript research

Each conversation turn starts with a compact chapter overview, the active scene ID, up to 2000 selected
characters and a short project-memory excerpt. Full scene text is not automatically sent to the model.
Read tools can list every chapter and scene, search the whole manuscript, read any scene with pagination,
inspect metadata and canonical character/location/timeline indexes, and retrieve the remaining project memory.

Whole-book requests can use `read_manuscript` to move through all scenes in chapter order. The assistant keeps
ephemeral cumulative research notes while older complete tool exchanges leave the active context. Retrieval
coverage is counted separately from search hits; the assistant must disclose incomplete coverage instead of
calling a sample exhaustive. Scene text and metadata are a saved snapshot at the start of the turn. Proposals
retain that snapshot's hashes so edits made during research invalidate stale suggestions.

Reads are bounded to 12000 text characters per page, at most 160 manuscript tool invocations and 128 model
round trips per turn. Recent tool exchanges have a bounded context window; notes are limited to 12000 characters.
The chat displays reading/search progress and can be canceled. Broad reviews can cost substantially more than
targeted questions because they require reading more text and making more model calls. Retrieval quality still
depends on the configured model; literal search is not semantic search, and inferred indexes may be incomplete.

## Git history tool

The writing assistant has a read-only `read_project_git` function tool. Ask about earlier versions of a scene,
deleted text, recent saved changes, or local Git status. The assistant is instructed to use this tool only for
explicit history/recovery questions and their follow-ups, not ordinary writing or fictional timeline questions.
The configured chat provider/model must support OpenAI-compatible function calling and streaming tool calls.

The tool can list commits, show manuscript diffs, and read a scene's Markdown at a saved commit, including
deleted scenes. Git results are paginated and bounded, with up to eight Git tool invocations per turn.
Requested history excerpts are sent to the configured LLM provider as context; no separate GitHub
connection is used. Normal chat does not preload Git history.

Access is limited to the current project's own repository and manuscript/index/scene-metadata files. The tool
cannot run arbitrary commands, access credentials or arbitrary paths, fetch, push, commit, checkout, or revert.
Restoring text still uses the existing manuscript proposal workflow. Git only contains committed versions;
unsaved browser drafts and `.history` recovery backups are not included. Working diffs cover tracked files saved
to disk, not untracked file contents or unsaved browser edits.

## Releases

GitHub Actions tests every change. Pushing a semantic version tag such as `v1.1.0` builds the self-contained Apple Silicon and Intel macOS packages and publishes them under [GitHub Releases](https://github.com/shachar-roth/BookWriter/releases).

The macOS packages currently use ad-hoc signatures and are not notarized. See [the macOS installation notes](Packaging/macos/README.md) before installing them.

Installed macOS builds check the repository's latest GitHub Release at startup and every six hours. A newer package for the Mac's architecture is downloaded in the background and verified against `SHA256SUMS.txt`. The user can then restart and install it with one click. Manuscripts and settings remain under `~/Library/Application Support/IsraeliAuthorStudio`; if the updated local server does not start successfully, the external update helper restores and relaunches the previous app bundle.

## Project layout

- `Scenes/` contains one Markdown file per scene.
- `Metadata/Scenes/` contains inferred and manually locked scene metadata.
- `Indexes/` contains chapter, entity, and chronology indexes.
- `Assistant/project-memory.md` contains compressed durable assistant memory, not transcripts.
- `.studio/project.json` identifies the project schema.

Automatic snapshots run after ten minutes without project changes. They analyze at most ten stale scenes per batch, commit locally, rebase onto a configured upstream, and push. Local editing continues if the model or remote is unavailable.
