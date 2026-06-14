using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.SemanticKernel;
using PersonChatBot.Auth;
using PersonChatBot.Chat;
using PersonChatBot.Components;
using PersonChatBot.Configuration;
using PersonChatBot.Embeddings;
using PersonChatBot.Ingestion;
using PersonChatBot.Storage;
using PersonChatBot.Watching;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// --- Local RAG configuration ---
builder.Services.Configure<RagOptions>(
    builder.Configuration.GetSection(RagOptions.SectionName));
var ragOptions = builder.Configuration.GetSection(RagOptions.SectionName).Get<RagOptions>()
                 ?? new RagOptions();

// --- Semantic Kernel + local Ollama (chat + embeddings, all on-machine) ---
#pragma warning disable SKEXP0070 // Ollama connector is experimental
var ollamaUri = new Uri(ragOptions.OllamaEndpoint);
builder.Services.AddOllamaChatCompletion(ragOptions.ChatModel, ollamaUri);
builder.Services.AddOllamaEmbeddingGenerator(ragOptions.EmbeddingModel, ollamaUri);
#pragma warning restore SKEXP0070
builder.Services.AddKernel();

// --- RAG: embeddings, storage, ingestion ---
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<IVectorStore, SqliteVecStore>();

builder.Services.AddSingleton<ITextExtractor, PdfTextExtractor>();
builder.Services.AddSingleton<ITextExtractor, DocxTextExtractor>();
builder.Services.AddSingleton<ITextExtractor, PlainTextExtractor>();
builder.Services.AddSingleton<TextExtractionService>();
builder.Services.AddSingleton<Chunker>();
builder.Services.AddSingleton<IndexingService>();

// --- RAG: retrieval + chat ---
builder.Services.AddSingleton<RetrievalService>();
builder.Services.AddSingleton<ChatService>();

// --- RAG: folder watching (startup index + live re-index) ---
builder.Services.AddHostedService<FolderWatcherService>();

// --- Auth (single password). Disabled when no password is configured. ---
builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection(AuthOptions.SectionName));
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>()
                  ?? new AuthOptions();

if (authOptions.Enabled)
{
    builder.Services
        .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
        .AddCookie(options =>
        {
            options.LoginPath = "/login";
            options.ExpireTimeSpan = TimeSpan.FromDays(30);
            options.SlidingExpiration = true;
        });
    builder.Services.AddAuthorization();
    builder.Services.AddCascadingAuthenticationState();
}

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();

app.UseStaticFiles();

if (authOptions.Enabled)
{
    app.UseAuthentication();
    app.UseAuthorization();
}

app.UseAntiforgery();

var components = app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

if (authOptions.Enabled)
{
    components.RequireAuthorization();
    app.MapAuthEndpoints();
}

app.Run();
