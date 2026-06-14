# Local Document Chatbot — Project Plan

A personal RAG (retrieval-augmented generation) chatbot that reads PDFs and documents
from a local folder and answers questions about their content. **All document
processing stays offline on the machine** (text extraction, embeddings, vector
storage, and LLM inference). The chat UI is reachable remotely via a private VPN.

---

## Locked Decisions

| Area | Choice |
|------|--------|
| **Inference** | Ollama, GPU-accelerated (CUDA), local only |
| **Chat model** | Start with a 3B model (Llama 3.2 3B / Qwen2.5 3B); keep a 7B for quality A/B |
| **Embeddings** | `nomic-embed-text` via Ollama |
| **Backend** | ASP.NET Core + Semantic Kernel (Ollama connector) |
| **Vector store** | SQLite + `sqlite-vec` (single file) |
| **Extraction** | PdfPig (PDF), DocumentFormat.OpenXml (.docx), plain reader (.txt/.md) |
| **Frontend** | Blazor chat UI (hosted with the API) |
| **Ingestion** | `FileSystemWatcher`, auto re-index on changes, debounced + hash dedup |
| **Remote access** | Tailscale (private mesh VPN) |
| **Scale** | Small (<100 docs) |

### Hardware note
Dell Precision 3571 — i7-12800H (14C/20T), 64 GB RAM, **NVIDIA T600 (4 GB VRAM)**.
The 4 GB VRAM is the binding constraint. A 3–4B quantized model fits on the GPU and
stays snappy. A 7–8B model works with CPU offload (slower but usable at this scale).
Embeddings are tiny and run effortlessly. Model is a one-line config swap.

---

## Privacy boundary

- **Never leaves the machine:** documents, extracted text, chunks, embeddings, the model.
- **Leaves the machine:** only encrypted chat traffic between your phone and the app,
  over your private Tailscale network.
- App binds to `localhost`; Tailscale is the only external entry path.

---

## Phase 0 — Setup

- [ ] Install Ollama; confirm CUDA/GPU is being used (`ollama ps` shows GPU).
- [ ] Pull a chat model (`llama3.2:3b` or `qwen2.5:3b`) and `nomic-embed-text`.
- [ ] Scaffold ASP.NET Core solution (hosted Blazor: API + Blazor in one).
- [ ] Add NuGet packages: Semantic Kernel + Ollama connector, `sqlite-vec`,
      PdfPig, DocumentFormat.OpenXml.
- [ ] Add a config file for: folder path, model names, chunk size/overlap, top-K.

## Phase 1 — Ingestion pipeline

- [ ] Text extraction per type: PdfPig (PDF), OpenXml (.docx), plain reader (.txt/.md).
- [ ] Chunking: ~500–1000 tokens with overlap; keep source filename + page/offset.
- [ ] Embedding: send each chunk to local Ollama embed model.
- [ ] Storage: write vectors + metadata (filename, page, chunk text, hash) into SQLite.
- [ ] Idempotent upsert keyed by file + content hash.

## Phase 2 — Folder watching

- [ ] `FileSystemWatcher` on the target folder (recursive).
- [ ] On add/change: extract → chunk → embed → upsert.
- [ ] On delete: remove that file's chunks.
- [ ] Debounce events (files fire multiple events); skip unchanged files via hash.
- [ ] One-time full index on startup to catch changes made while the app was off.

## Phase 3 — Retrieval + chat

- [ ] On a question: embed the query → similarity search in SQLite → top-K chunks.
- [ ] Build a grounded prompt (retrieved context + question + guardrail instructions).
- [ ] Send to local LLM via Semantic Kernel.
- [ ] Return the answer **with source citations** (filename + page).
- [ ] Handle "no relevant context found" gracefully (don't hallucinate).

## Phase 4 — Chat UI (Blazor)

- [ ] Chat thread with streaming responses.
- [ ] Render cited sources under each answer.
- [ ] Status panel: indexed doc count, last re-index time, manual "reindex now" button.

## Phase 5 — Remote access + hardening

- [ ] Add app-level auth (single password or ASP.NET Identity) before exposing.
- [ ] Bind the app to `localhost`.
- [ ] Install Tailscale on the laptop and the phone; join the same tailnet.
- [ ] Access the app via the laptop's Tailscale address from the phone.
- [ ] (Optional) Tailscale MagicDNS for a friendly hostname.

## Phase 6 — Nice-to-haves (later)

- [ ] Conversation history persistence.
- [ ] Per-folder or per-tag filtering of sources.
- [ ] Heading-aware / semantic chunking for better retrieval.
- [ ] Easy model switching via config; A/B 3B vs 7B for quality.
- [ ] Re-ranking retrieved chunks before sending to the LLM.

---

## Suggested build order

Phase 0 → 1 → 3 first (get end-to-end Q&A working in a console or minimal endpoint
before the UI). Then 2 (watching), then 4 (UI), then 5 (remote). This gets you a
working answer-from-documents loop as early as possible, then layers convenience on top.
