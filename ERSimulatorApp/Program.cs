using ERSimulatorApp.Services;
using Newtonsoft.Json.Serialization;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

// Forwarded headers will be configured inline in the middleware pipeline

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
var ragTimeout = builder.Configuration.GetValue<int?>("RAG:TimeoutSeconds") ?? 120; // Longer timeout for remote RAG service

// Register base RAG service with HttpClient
builder.Services.AddHttpClient<RAGService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(ragTimeout);
});

// Explicitly register RAGService as a service for DI
builder.Services.AddTransient<RAGService>();

// Register Character Gateway service
builder.Services.AddHttpClient<ICharacterGateway, CharacterGatewayService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(openAITimeout);
});

// Register the combined service with personality layer
builder.Services.AddTransient<ILLMService, RAGWithPersonalityService>();

builder.Services.AddSingleton<ChatLogService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<ChatLogService>>();
    return new ChatLogService(logger);
});
builder.Services.AddSingleton<ICustomGPTService>(sp =>
{
    var logger = sp.GetRequiredService<ILogger<CustomGPTService>>();
    return new CustomGPTService(logger);
});

builder.Services.AddHttpClient<QuizService>();

// Register HeyGen Streaming service (for real-time avatar streaming)
var heyGenTimeout = builder.Configuration.GetValue<int?>("HeyGen:TimeoutSeconds") ?? 120;
builder.Services.AddHttpClient<IHeyGenStreamingService, HeyGenStreamingService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(heyGenTimeout);
});

// Register HeyGen Video Proxy service (for asynchronous video generation)
builder.Services.AddHttpClient<IHeyGenVideoProxyService, HeyGenVideoProxyService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(heyGenTimeout);
});

// Register Whisper ASR service
var whisperTimeout = builder.Configuration.GetValue<int?>("Whisper:TimeoutSeconds") ?? 60;
builder.Services.AddHttpClient<IWhisperService, WhisperService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(whisperTimeout);
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

// Use forwarded headers middleware (must be early in pipeline)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto
});

// Custom middleware to handle X-Forwarded-Prefix header and set path base
// This must be done BEFORE UseStaticFiles() and other middleware
app.Use(async (ctx, next) =>
{
    // Read X-Forwarded-Prefix header from reverse proxy
    var prefixHeader = ctx.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault();
    if (!string.IsNullOrEmpty(prefixHeader))
    {
        // Normalize the prefix (ensure it starts with / and doesn't end with /)
        var normalizedPrefix = prefixHeader.Trim();
        if (!normalizedPrefix.StartsWith("/"))
        {
            normalizedPrefix = "/" + normalizedPrefix;
        }
        if (normalizedPrefix.EndsWith("/") && normalizedPrefix.Length > 1)
        {
            normalizedPrefix = normalizedPrefix.TrimEnd('/');
        }
        
        // Set PathBase on the request context
        // This ensures static files, URL generation, and routing all use the correct base path
        ctx.Request.PathBase = new PathString(normalizedPrefix);
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

app.UseHttpsRedirection();

// Configure static files to respect PathBase set by middleware
app.UseStaticFiles(new StaticFileOptions
{
    // Static files will automatically use Request.PathBase set by our middleware
    // This ensures files are served from the correct path (e.g., /er-simulator/css/site.css)
});

app.UseRouting();

// Enable CORS
app.UseCors("AllowAll");

app.UseAuthorization();

app.MapRazorPages();
app.MapControllers();

app.Run();
