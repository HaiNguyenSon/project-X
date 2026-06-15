using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
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
var loopbackOnly = NetworkBinding.IsLoopbackOnly(builder.Configuration);

// Fail safe: never run open by accident. Running without a password requires an
// explicit Auth:AllowAnonymous=true (set in appsettings.Development.json for local dev).
if (!authOptions.Enabled && !authOptions.AllowAnonymous)
{
    throw new InvalidOperationException(
        "No authentication password is configured. Set 'Auth:Password' (preferably via the " +
        "Auth__Password environment variable or user-secrets) before running, or set " +
        "'Auth:AllowAnonymous=true' to explicitly run with NO authentication.");
}

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

    // Behind `tailscale serve` the app is reached over HTTPS but receives plain
    // HTTP on localhost. Honor X-Forwarded-Proto so the request is seen as HTTPS,
    // which makes the auth cookie Secure.
    //
    // Trusting forwarded headers from ANY source is only safe when the app is bound
    // to loopback (the local proxy is the only possible client). If it is bound to a
    // public address those headers can be spoofed, so we keep the default behaviour
    // (only loopback proxies trusted) and warn.
    builder.Services.Configure<ForwardedHeadersOptions>(forwarded =>
    {
        forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedFor;
        if (loopbackOnly)
        {
            forwarded.KnownNetworks.Clear();
            forwarded.KnownProxies.Clear();
        }
    });

    // Throttle login attempts per client IP to slow password guessing.
    builder.Services.AddRateLimiter(rateLimiter =>
    {
        rateLimiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        rateLimiter.AddPolicy(AuthEndpoints.LoginRateLimitPolicy, httpContext =>
            RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(5),
                    QueueLimit = 0,
                }));
    });
}

var app = builder.Build();

if (!authOptions.Enabled)
    app.Logger.LogWarning(
        "Running WITHOUT authentication (Auth:AllowAnonymous is set). Anyone who can reach " +
        "the app over the network can use it. Set Auth:Password before exposing it.");

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
    app.UseForwardedHeaders(); // must run before auth so the scheme is correct
    if (!loopbackOnly)
        app.Logger.LogWarning(
            "App is bound to a non-loopback address; X-Forwarded-* headers are only trusted " +
            "from known proxies. Keep the app bound to localhost behind `tailscale serve`.");
    app.UseRateLimiter();
    app.UseAuthentication();
    app.UseAuthorization();

    if (authOptions.Password.Length < 12)
        app.Logger.LogWarning(
            "Auth password is shorter than 12 characters. Use a long passphrase before " +
            "exposing the app, even over Tailscale.");
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
