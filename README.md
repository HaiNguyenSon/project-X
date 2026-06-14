# PersonChatBot — Local Document Chatbot (RAG)

A personal, **fully offline** retrieval-augmented chatbot. Point it at a folder of
documents (PDF, DOCX, TXT, MD); it indexes them locally and answers questions
about their content with **source citations**. Document text, embeddings, the
vector store, and the LLM all stay on the machine. Remote access is via a private
Tailscale network only.

See [`ClaudePlan.md`](ClaudePlan.md) for the full design and [`HARDENING.md`](HARDENING.md)
for remote access & security.

## Stack

| Area | Choice |
|------|--------|
| Inference | [Ollama](https://ollama.com) (local, GPU-accelerated) |
| Chat model | `llama3.2:3b` (config-swappable) |
| Embeddings | `nomic-embed-text` (768-dim) |
| Backend | ASP.NET Core + Semantic Kernel (Ollama connector) |
| Vector store | SQLite + [`sqlite-vec`](https://github.com/asg017/sqlite-vec) (single file) |
| Extraction | PdfPig (PDF), OpenXml (DOCX), plain reader (TXT/MD) |
| Frontend | Blazor (Interactive Server), hosted with the API |
| Ingestion | `FileSystemWatcher`, debounced, hash-deduped |

## Prerequisites

1. **.NET 8 SDK**
2. **Ollama** — install from <https://ollama.com/download>, then pull the models:
   ```powershell
   ollama pull llama3.2:3b
   ollama pull nomic-embed-text
   ollama ps          # confirm GPU is used
   ```

## Run

```powershell
cd PersonChatBot
dotnet run
```

Then open the app, drop documents into the watched folder (default
`PersonChatBot/Documents/`, configurable), and ask questions. The folder is
indexed on startup and watched live; use **Reindex now** in the status panel to
force a rescan.

## Configuration

All settings live under the `Rag` section of `appsettings.json`:

| Key | Meaning |
|-----|---------|
| `DocumentsFolder` | Folder to index (recursively) |
| `OllamaEndpoint` | Ollama base URL |
| `ChatModel` / `EmbeddingModel` | Model names served by Ollama |
| `EmbeddingDimensions` | Must match the embedding model (768 for nomic-embed-text) |
| `ChunkSizeTokens` / `ChunkOverlapTokens` | Chunking (token estimate) |
| `TopK` / `MinRelevanceScore` | Retrieval breadth and relevance floor |
| `Temperature` | Chat sampling temperature |
| `WatchDebounceMs` | Quiet period before re-indexing a changed file |

Swapping the chat model is a one-line change to `Rag:ChatModel` (e.g. a 7B model
for an A/B quality test) — no embedding re-index needed, since the embedding model
is unchanged.

## Architecture

```
Configuration/  RagOptions, AuthOptions
Ingestion/      ITextExtractor (+ PDF/DOCX/plain), Chunker, IndexingService
Embeddings/     EmbeddingService (Ollama)
Storage/        IVectorStore + SqliteVecStore (sqlite-vec)
Chat/           RetrievalService, ChatService (grounded prompt, streaming, citations)
Watching/       FolderWatcherService (startup index + live re-index)
Auth/           single-password login (cookie)
Components/     Blazor UI (Chat page + status panel)
```

> **Note on the vector store:** Semantic Kernel's `Connectors.Sqlite` (preview) is
> pinned to an older `Microsoft.Extensions.VectorData` than SK 1.77 pulls in, which
> is binary-incompatible. We therefore use `Microsoft.Data.Sqlite` + `sqlite-vec`
> directly, behind the `IVectorStore` interface, so the backing store can be
> swapped without touching the rest of the app.
