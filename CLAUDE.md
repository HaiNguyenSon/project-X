# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A fully-offline personal RAG (retrieval-augmented generation) chatbot. It indexes documents
(PDF/DOCX/TXT/MD) from a local folder and answers questions about them with source citations.
All processing — text extraction, embeddings, vector storage, and LLM inference — stays on the
machine. Remote access is intended only via a private Tailscale network. See `ClaudePlan.md` for
the original design and locked decisions, `README.md` for setup, and `HARDENING.md` for the
auth/remote-access model.

## Commands

```powershell
# Build / run (run from repo root or the project dir)
dotnet build
dotnet run --project PersonChatBot          # serves http://localhost:5088

# Tests (xUnit) — all run WITHOUT Ollama (a fake embedder + real sqlite-vec are used)
dotnet test
dotnet test --filter "FullyQualifiedName~ChunkerTests"            # one class
dotnet test --filter "FullyQualifiedName~SqliteVecStoreTests.Upsert_then_search_ranks_nearest_chunk_first"  # one test

# Generate a hashed app password to put in Auth:PasswordHash
dotnet run --project PersonChatBot -- hash-password "your passphrase"

# What CI runs (.github/workflows/ci.yml): build + test in Release, then fail on vulnerable deps
dotnet list package --vulnerable --include-transitive
```

### Runtime prerequisites (only needed to actually run/use the app, not to build or test)
Ollama must be installed and running locally with two models pulled:
`ollama pull llama3.2:3b` and `ollama pull nomic-embed-text`. The app talks to it at
`http://localhost:11434`.

## Architecture (the data flow is the big picture)

Single ASP.NET Core 8 project (`PersonChatBot/`) hosting a Blazor Interactive Server UI + the
RAG pipeline in one process. Two flows meet at the SQLite vector store:

**Ingestion** (`FolderWatcherService` → `IndexingService`):
`ITextExtractor` (Pdf/Docx/PlainText, dispatched by `TextExtractionService`) → `Chunker` →
`EmbeddingService.EmbedDocumentsAsync` → `IVectorStore.UpsertFileAsync`.
`FolderWatcherService` is a `BackgroundService` that does a full re-index on startup, then
watches the folder (debounced). It retries only *transient* errors (file locked / still being
copied — see `IsTransient`); corrupt/format errors are logged once, not retried. Its startup and
watcher setup are wrapped so a bad `DocumentsFolder` can't crash the host (and `HostOptions`
sets `BackgroundServiceExceptionBehavior.Ignore` as a backstop).
`IndexingService` is idempotent: it skips files whose content hash is unchanged, enforces the
`MaxFileSizeMb` (default 100) and `MaxIndexedFiles` (default 100) limits, and returns an
`IndexOutcome` (Indexed / Unchanged / NoExtractableText / TooLarge / LimitReached / Unsupported).

**Query** (`Chat.razor` → `ChatService`):
`RetrievalService` embeds the question (`EmbeddingService.EmbedQueryAsync`) → `IVectorStore.SearchAsync`
(top-K, filtered by `MinRelevanceScore`) → `ChatService` builds a grounded prompt and streams the
answer from Ollama via Semantic Kernel's `IChatCompletionService`, with citations.

### Things that will bite you if you don't know them

- **Vector store is hand-rolled on `Microsoft.Data.Sqlite` + the `sqlite-vec` native extension,
  NOT Semantic Kernel's `Connectors.Sqlite`.** SK's connector is binary-incompatible with the
  `Microsoft.Extensions.VectorData` version SK 1.77 pulls in. `SqliteVecStore` loads `vec0.dll`
  (from `runtimes/<rid>/native/`) and runs raw parameterized SQL against a `vec0` virtual table.
  Keep all SQL parameterized; the only safe interpolations are config-controlled (db path, the
  integer dimension in the DDL).
- **`EmbeddingService` applies nomic-embed-text task prefixes** (`search_document:` for chunks,
  `search_query:` for queries) — they materially affect retrieval. Changing prefixes or the
  embedding model changes the vector space, so `rag.db` must be deleted and re-indexed.
- **`IVectorStore` is the seam.** Everything goes through it so the backing store can be swapped;
  don't reach around it.
- **Semantic Kernel Ollama APIs are experimental** — calls are wrapped in `#pragma warning disable
  SKEXP0070`. Expect that when touching `Program.cs` DI or `ChatService`.
- The single `SqliteConnection` is serialized with a `SemaphoreSlim`; this is intentional for the
  small (<100 doc) scale, not an oversight.

### Configuration & security (`Program.cs`, `RagOptions`, `AuthOptions`)
- All tunables live under the `Rag` section of `appsettings.json`, bound to `RagOptions`.
- **Auth fails safe:** the app refuses to start with no password unless `Auth:AllowAnonymous=true`.
  `appsettings.Development.json` sets that flag so local dev runs open (with a warning).
  `Auth:PasswordHash` (PBKDF2, preferred) takes precedence over plaintext `Auth:Password`.
- The app is meant to bind to `localhost` only and be reached via `tailscale serve`. Forwarded
  headers are only trusted when bound to loopback (`NetworkBinding`); security headers / CSP are
  added by `SecurityHeadersMiddleware`. Login is rate-limited.

## Testing notes
Tests use a deterministic `FakeEmbeddingGenerator` (no Ollama) and a real `SqliteVecStore` on a
temp db, so the actual sqlite-vec SQL is exercised. When testing storage with fabricated vectors,
set `RagOptions.EmbeddingDimensions` to match the vector length you construct (see `TestSupport`).
The main project exposes internals to the test project via `InternalsVisibleTo("PersonChatBot.Tests")`,
so internal helpers (e.g. `FolderWatcherService.IsTransient`) can be unit-tested directly.
