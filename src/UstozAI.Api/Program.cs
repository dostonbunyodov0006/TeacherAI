using System.Net;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Anthropic;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using UstozAI.Api.Auth;
using UstozAI.Api.Data;
using UstozAI.Api.Endpoints;
using UstozAI.Api.Hubs;
using UstozAI.Api.Services;
using UstozAI.Api.Services.Conversations;
using UstozAI.Api.Services.Llm;
using UstozAI.Api.Services.Mentor;
using UstozAI.Api.Services.Learning;
using UstozAI.Api.Services.Analytics;
using UstozAI.Api.Services.Media;
using UstozAI.Api.Services.Pipeline;
using UstozAI.Api.Services.Speech;

var builder = WebApplication.CreateBuilder(args);
var cfg = builder.Configuration;

// ---------- Options (all secrets come from env vars / user-secrets, never from committed files) ----------
builder.Services.Configure<JwtOptions>(cfg.GetSection("Jwt"));
builder.Services.Configure<SeedOptions>(cfg.GetSection("Seed"));
builder.Services.Configure<LlmOptions>(cfg.GetSection("Llm"));
builder.Services.Configure<AzureSpeechOptions>(cfg.GetSection("AzureSpeech"));

var jwt = cfg.GetSection("Jwt").Get<JwtOptions>() ?? new();
if (System.Text.Encoding.UTF8.GetByteCount(jwt.SigningKey) < 32)
    throw new InvalidOperationException("Jwt:SigningKey must be at least 32 bytes. Set env var Jwt__SigningKey.");

// ---------- Data ----------
builder.Services.AddDbContext<AppDbContext>(o =>
    o.UseNpgsql(cfg.GetConnectionString("Default")));
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();   // PBKDF2-HMAC-SHA512, 100k iters
builder.Services.AddSingleton<TokenService>();
builder.Services.AddScoped<ConversationService>();
builder.Services.AddScoped<MentorStateService>();
builder.Services.AddScoped<LearningService>();
builder.Services.AddScoped<WeeklyReportService>();
builder.Services.AddScoped<MediaStore>();
builder.Services.AddScoped<SafetyPolicy>();
builder.Services.Configure<MediaOptions>(cfg.GetSection("Media"));
builder.Services.Configure<OpenAiImageOptions>(cfg.GetSection("OpenAIImages"));

// ---------- Auth ----------
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwt.Issuer,
            ValidateAudience = true, ValidAudience = jwt.Audience,
            ValidateIssuerSigningKey = true, IssuerSigningKey = jwt.Key(),
            ValidateLifetime = true, ClockSkew = TimeSpan.FromSeconds(30),
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            NameClaimType = "unique_name",
        };
        // Browsers can't set headers on WebSocket upgrade → SignalR sends the JWT as ?access_token= on /hubs only.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

// Behind nginx on the same server: take the real client IP from X-Forwarded-For — but ONLY when the request comes
// from a known local proxy (loopback by default). A client on the internet can't spoof its IP with that header.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto);

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = 429;
    // Sign-ups: 3 per hour per IP (soxta akkauntlarga qarshi). Local development on this machine is not limited.
    var devLoopbackExempt = builder.Environment.IsDevelopment();
    o.AddPolicy("register", http =>
    {
        var ip = http.Connection.RemoteIpAddress;
        if (devLoopbackExempt && ip is not null && IPAddress.IsLoopback(ip)) return RateLimitPartition.GetNoLimiter("dev-local");
        return RateLimitPartition.GetFixedWindowLimiter(ip?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromHours(1) });
    });
    o.AddPolicy("auth", http => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1) }));
    o.AddPolicy("upload", http => RateLimitPartition.GetFixedWindowLimiter(
        http.User.Identity?.Name ?? http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
});

// ---------- CORS (only needed if the frontend is served from another origin) ----------
var origins = cfg.GetSection("Cors:Origins").Get<string[]>() ?? [];
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

// ---------- Real-time ----------
builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = UstozHub.MaxAudioBytes * 4 / 3 + 64 * 1024; // base64 WAV + envelope
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
    o.MaximumParallelInvocationsPerClient = 1;
});

// ---------- AI providers: real ones when keys exist, offline fallbacks otherwise ----------
var azure = cfg.GetSection("AzureSpeech").Get<AzureSpeechOptions>() ?? new();
builder.Services.AddMemoryCache(o => o.SizeLimit = 200_000);          // TTS cache budget (≈ chars of cached lines)
if (azure.Enabled)
{
    builder.Services.AddHttpClient<ISpeechToText, AzureSpeechToText>(c => c.Timeout = TimeSpan.FromSeconds(30));
    builder.Services.AddSingleton<AzureTextToSpeech>();
    builder.Services.AddSingleton<ITextToSpeech>(sp =>
        new CachedTextToSpeech(sp.GetRequiredService<AzureTextToSpeech>(), sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>()));
}
else
{
    builder.Services.AddSingleton<ISpeechToText, NoopSpeechToText>();
    builder.Services.AddSingleton<ITextToSpeech, TextVisemeSpeech>();
}

var anthropicKey = cfg["ANTHROPIC_API_KEY"] ?? cfg["Anthropic:ApiKey"];
if (!string.IsNullOrWhiteSpace(anthropicKey))
{
    builder.Services.AddSingleton(new AnthropicClient { ApiKey = anthropicKey });
    builder.Services.AddSingleton<ILlmService, ClaudeLlmService>();
}
else builder.Services.AddSingleton<ILlmService, OfflineLlmService>();

// Image editing/generation: OpenAI gpt-image-1 when a key exists, otherwise in-browser canvas variations.
var imageOpts = cfg.GetSection("OpenAIImages").Get<OpenAiImageOptions>() ?? new();
if (imageOpts.Enabled)
    builder.Services.AddHttpClient<IImageGenerator, OpenAiImageGenerator>(c => c.Timeout = TimeSpan.FromSeconds(150));
else builder.Services.AddSingleton<IImageGenerator, ClientFilterImageGenerator>();

builder.Services.AddSingleton<UserTurnGate>();
builder.Services.AddSingleton<ConversationPipeline>();

var app = builder.Build();
app.UseForwardedHeaders();                        // first: everything after sees the real client IP

if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
    app.UseHttpsRedirection();
}
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["Permissions-Policy"] = "microphone=(self), camera=()";
    await next();
});
app.UseDefaultFiles();
app.UseStaticFiles();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapAuth();
app.MapConversations();
app.MapMentor();
app.MapAnalytics();
app.MapMedia();
app.MapNotebooks();
app.MapHub<UstozHub>("/hubs/ustoz");
// Dev aid: the exact JSON schema the LLM is constrained to (contract v2). Docs: docs/realtime-contract.md
if (app.Environment.IsDevelopment())
    app.MapGet("/api/contract/schema", () => Results.Json(MentorJson.Schema()));
app.MapGet("/api/health", () => new
{
    ok = true,
    stt = azure.Enabled ? "azure" : "none",
    tts = azure.Enabled ? "azure" : "browser",
    llm = string.IsNullOrWhiteSpace(anthropicKey) ? "offline" : "claude",
    images = imageOpts.Enabled ? "openai" : "browser-filters",
});

await DbSeeder.MigrateAndSeedAsync(app.Services);
app.Run();
