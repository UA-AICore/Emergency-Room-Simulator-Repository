using ERSimulatorApp.Services;
using Newtonsoft.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

var builder = WebApplication.CreateBuilder(args);

// Prefer appsettings.json over appsettings.Development.json so credentials and RAG in appsettings.json win
builder.Configuration.AddJsonFile("appsettings.json", optional: true, reloadOnChange: false);

// Add forwarded headers middleware to handle reverse proxy scenarios
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                              ForwardedHeaders.XForwardedProto;
    // Honor path base from reverse proxy
    options.ForwardedPrefixHeaderName = "X-Forwarded-Prefix";
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

// Add services to the container.
builder.Services.AddRazorPages();

// Add API controllers with camelCase JSON serialization
builder.Services.AddControllers()
    .AddNewtonsoftJson(options =>
    {
        options.SerializerSettings.ContractResolver = new CamelCasePropertyNamesContractResolver();
        options.SerializerSettings.NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore;
    });

// Register our custom services
var llmTimeout = builder.Configuration.GetValue<int?>("Ollama:TimeoutSeconds") ?? 60;
var openAITimeout = builder.Configuration.GetValue<int?>("OpenAI:TimeoutSeconds") ?? 60;
// Python RAG (embedding + Chroma + Ollama) can take 2+ min; always use 300s so config/env cannot shorten it
const int ragTimeoutSeconds = 300;

// Register base RAG service with HttpClient (300s timeout for Python RAG; do NOT add AddTransient<RAGService> or the typed client is overridden and timeout reverts to default 100s)
builder.Services.AddHttpClient<RAGService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(ragTimeoutSeconds);
});

// Register Character Gateway service
builder.Services.AddHttpClient<ICharacterGateway, CharacterGatewayService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(openAITimeout);
});

// Register the combined service with personality layer
builder.Services.AddTransient<ILLMService, RAGWithPersonalityService>();

builder.Services.AddSingleton<ChatLogService>();
builder.Services.AddSingleton<ICustomGPTService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<CustomGPTService>>();
    return new CustomGPTService(logger);
});

builder.Services.AddHttpClient<QuizService>();

// Avatar streaming: LiveAvatar LITE when enabled + configured, else HeyGen, else stub
var useLiveAvatar = builder.Configuration.GetValue<bool>("UseLiveAvatar");
var liveAvatarKey = (builder.Configuration["LiveAvatar:ApiKey"] ?? Environment.GetEnvironmentVariable("LIVEAVATAR_API_KEY") ?? "").Trim();
var liveAvatarId = (builder.Configuration["LiveAvatar:AvatarId"] ?? "").Trim();
var liveAvatarReady = useLiveAvatar
    && !string.IsNullOrWhiteSpace(liveAvatarKey)
    && !liveAvatarKey.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)
    && !liveAvatarKey.StartsWith("PASTE_", StringComparison.OrdinalIgnoreCase)
    && Guid.TryParse(liveAvatarId, out _);

var heyGenApiKey = (builder.Configuration["HeyGen:ApiKey"] ?? "").Trim();
var heyGenAvatarId = (builder.Configuration["HeyGen:AvatarId"] ?? "").Trim();
bool isHeyGenPlaceholder = string.IsNullOrWhiteSpace(heyGenApiKey) || string.IsNullOrWhiteSpace(heyGenAvatarId)
    || heyGenApiKey.StartsWith("YOUR_", StringComparison.OrdinalIgnoreCase)
    || heyGenApiKey.StartsWith("PASTE_", StringComparison.OrdinalIgnoreCase)
    || heyGenAvatarId.StartsWith("PASTE_", StringComparison.OrdinalIgnoreCase)
    || heyGenAvatarId.StartsWith("your-heygen", StringComparison.OrdinalIgnoreCase);

var heyGenTimeout = builder.Configuration.GetValue<int?>("HeyGen:TimeoutSeconds") ?? 120;
var liveAvatarTimeout = builder.Configuration.GetValue<int?>("LiveAvatar:TimeoutSeconds") ?? heyGenTimeout;

if (liveAvatarReady)
{
    builder.Services.AddHttpClient<IHeyGenStreamingService, LiveAvatarStreamingService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(liveAvatarTimeout);
    });
}
else if (!isHeyGenPlaceholder)
{
    builder.Services.AddHttpClient<IHeyGenStreamingService, HeyGenStreamingService>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(heyGenTimeout);
    });
}
else
{
    builder.Services.AddSingleton<IHeyGenStreamingService, HeyGenStreamingServiceStub>();
}

// Register HeyGen Video Proxy service (for asynchronous video generation)
builder.Services.AddHttpClient<IHeyGenVideoProxyService, HeyGenVideoProxyService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(heyGenTimeout);
});

// ElevenLabs speech-to-text for Dr. Dexter avatar voice input
var elevenLabsTimeout = builder.Configuration.GetValue<int?>("ElevenLabs:TimeoutSeconds") ?? 60;
builder.Services.AddHttpClient<IElevenLabsSpeechToTextService, ElevenLabsSpeechToTextService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(elevenLabsTimeout);
});

// Add CORS for development
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowAll", policy =>
    {
        policy.AllowAnyOrigin()
              .AllowAnyMethod()
              .AllowAnyHeader();
    });
});

var app = builder.Build();

var startupLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
if (liveAvatarReady)
{
    var laMask = liveAvatarId.Length <= 12 ? liveAvatarId : liveAvatarId.Substring(0, 12) + "...";
    startupLogger.LogInformation("Avatar streaming: LiveAvatar LITE, AvatarId: {AvatarId}", laMask);
}
else
{
    if (useLiveAvatar && !liveAvatarReady)
        startupLogger.LogWarning("UseLiveAvatar is true but LiveAvatar:ApiKey / LiveAvatar:AvatarId (UUID) invalid or missing; using HeyGen or stub.");
    var avatarMask = string.IsNullOrEmpty(heyGenAvatarId) ? "(empty)" : (heyGenAvatarId.Length <= 12 ? heyGenAvatarId : heyGenAvatarId.Substring(0, 12) + "...");
    startupLogger.LogInformation("Avatar streaming: {Mode}, HeyGen AvatarId: {AvatarId}",
        isHeyGenPlaceholder ? "stub (not configured)" : "HeyGen", avatarMask);
}

// Use forwarded headers middleware (must be early in pipeline)
app.UseForwardedHeaders();

// Configure path base from X-Forwarded-Prefix header or environment variable
// This middleware extracts the path base and sets it for static files and routing
app.Use(async (context, next) =>
{
    // First, try to get path base from X-Forwarded-Prefix header (from reverse proxy)
    var forwardedPrefix = context.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault();
    
    // If not in header, try environment variable
    if (string.IsNullOrEmpty(forwardedPrefix))
    {
        forwardedPrefix = builder.Configuration["ASPNETCORE_PATHBASE"];
    }
    
    // Set the path base if we have one
    if (!string.IsNullOrEmpty(forwardedPrefix))
    {
        // Ensure path base starts with / and doesn't end with /
        var pathBase = forwardedPrefix.TrimEnd('/');
        if (!pathBase.StartsWith('/'))
        {
            pathBase = "/" + pathBase;
        }
        
        // Set the path base for this request (this affects static files, routing, and Razor views)
        context.Request.PathBase = new PathString(pathBase);
    }
    
    await next();
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// Only redirect HTTP→HTTPS when we're actually listening on HTTPS (ServerHttps profile). Avoids "Failed to determine the https port" when running HTTP-only (Server profile).
var urls = builder.Configuration["ASPNETCORE_URLS"] ?? "";
if (urls.Contains("https:", StringComparison.OrdinalIgnoreCase))
{
    var kestrelCertPath = builder.Configuration["Kestrel:Certificates:Default:Path"];
    if (!string.IsNullOrWhiteSpace(kestrelCertPath))
    {
        var path = kestrelCertPath.Trim();
        if (!Path.IsPathRooted(path))
            path = Path.Combine(builder.Environment.ContentRootPath, path);
        if (File.Exists(path))
            app.UseHttpsRedirection();
    }
}
app.UseStaticFiles();

app.UseRouting();

// Enable CORS
app.UseCors("AllowAll");

app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();

app.Run();
