
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Net;

var builder = WebApplication.CreateSlimBuilder(args);

// Configure JSON Source Generation for Native AOT
builder.Services.ConfigureHttpJsonOptions(options => {
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
});

var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseSetting("http_ports", port);

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("GeminiClient");
builder.Services.AddLogging();
builder.Services.AddHealthChecks();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.MapHealthChecks("/health");

// Root Route - Uses concrete record ServiceStatus
app.MapGet("/", () => Results.Ok(new ServiceStatus("StravAI", "online", DateTime.UtcNow)));

app.UseCors("AllowAll");

// Security Middleware
app.Use(async (context, next) => {
    var path = context.Request.Path.ToString().ToLower();
    if (path != "/health" && path != "/" && !path.StartsWith("/webhook")) {
        var secret = Environment.GetEnvironmentVariable("STRAVAI_BACKEND_SECRET") ?? app.Configuration["X-StravAI-Secret"];
        if (!string.IsNullOrEmpty(secret)) {
            if (!context.Request.Headers.TryGetValue("X-StravAI-Secret", out var providedSecret) || providedSecret != secret) {
                SystemState.AddLog($"AUTH_DENIED: Request to {path} rejected.", "WARNING");
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }
    }
    await next();
});

app.MapGet("/logs", () => Results.Ok(SystemState.Logs.ToArray()));

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client, app.Configuration);
        if (token == null) return Results.Unauthorized();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
        var acts = await res.Content.ReadFromJsonAsync<List<JsonElement>>(AppJsonContext.Default.ListJsonElement);
        var cache = acts?.FirstOrDefault(a => a.GetProperty("name").GetString() == "[StravAI] System Cache");
        
        if (!cache.HasValue) {
            SystemState.AddLog("PROFILE: No cache activity found.", "INFO");
            return Results.NotFound(new ErrorResponse("Cache not found. Please run an audit."));
        }

        var detailRes = await client.GetAsync($"https://www.strava.com/api/v3/activities/{cache.Value.GetProperty("id").GetInt64()}");
        var act = await detailRes.Content.ReadFromJsonAsync<JsonElement>(AppJsonContext.Default.JsonElement);
        var desc = act.GetProperty("description").GetString() ?? "";
        
        if (!desc.Contains("---CACHE_START---")) {
            SystemState.AddLog("PROFILE: Found cache activity but no data markers.", "WARNING");
            return Results.NotFound(new ErrorResponse("Cache activity empty."));
        }

        var jsonString = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0].Trim();
        SystemState.AddLog($"PROFILE: Successfully loaded data.", "SUCCESS");
        return Results.Ok(JsonNode.Parse(jsonString));
    } catch (Exception ex) {
        SystemState.AddLog($"PROFILE_ERROR: {ex.Message}", "ERROR");
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/audit", async (IHttpClientFactory clientFactory) => {
    SystemState.AddLog("AUDIT: Starting analysis...");
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(client, app.Configuration);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
            var history = await res.Content.ReadFromJsonAsync<List<JsonElement>>(AppJsonContext.Default.ListJsonElement);
            
            using var aiClient = clientFactory.CreateClient("GeminiClient");
            var key = ResolveApiKey(app.Configuration);
            
            // Define concrete Gemini request
            var geminiRequest = new GeminiRequest(
                new[] { new GeminiContent(new[] { new GeminiPart($"ROLE: Coach. DATA: {JsonSerializer.Serialize(history?.Take(20), AppJsonContext.Default.IEnumerableJsonElement)}. TASK: Return athlete profile JSON.") }) },
                new GeminiConfig("application/json", 0.1f)
            );

            var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={key}", geminiRequest, AppJsonContext.Default.GeminiRequest);
            var result = await aiRes.Content.ReadFromJsonAsync<JsonElement>(AppJsonContext.Default.JsonElement);
            var profileJson = SafeExtractText(result);
            
            var cache = history?.FirstOrDefault(a => a.GetProperty("name").GetString() == "[StravAI] System Cache");
            var timestamp = DateTime.UtcNow.ToString("O");
            var desc = $"---CACHE_START---\n{profileJson}\n---CACHE_END---\nUpdated: {timestamp}";
            
            if (cache.HasValue) {
                await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cache.Value.GetProperty("id").GetInt64()}", new ActivityUpdate(desc), AppJsonContext.Default.ActivityUpdate);
            } else {
                await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new ActivityCreate("[StravAI] System Cache", "Run", timestamp, 1, desc, true), AppJsonContext.Default.ActivityCreate);
            }
            
            SystemState.AddLog("AUDIT: Cache updated.", "SUCCESS");
        } catch (Exception ex) {
            SystemState.AddLog($"AUDIT_ERROR: {ex.Message}", "ERROR");
        }
    });
    return Results.Accepted();
});

app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, [FromQuery(Name = "hub.challenge")] string challenge, [FromQuery(Name = "hub.verify_token")] string verifyToken) => {
    var secret = GetEnv("STRAVA_VERIFY_TOKEN", app.Configuration) ?? "STRAVAI_SECURE_TOKEN";
    return (mode == "subscribe" && verifyToken == secret) ? Results.Ok(new WebhookChallengeResponse(challenge)) : Results.Unauthorized();
});

app.MapPost("/webhook", async (HttpContext context) => {
    var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
    var eventData = JsonSerializer.Deserialize<JsonElement>(body, AppJsonContext.Default.JsonElement);
    if (eventData.TryGetProperty("object_type", out var objType) && objType.GetString() == "activity") {
        SystemState.AddLog($"WEBHOOK: Event for {eventData.GetProperty("object_id")}");
    }
    return Results.Ok();
});

app.Run();

// --- HELPERS ---

string SafeExtractText(JsonElement root) {
    try {
        var text = root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
        if (text.Contains("```")) {
            var s = text.IndexOf("{");
            var e = text.LastIndexOf("}");
            if (s >= 0 && e > s) text = text.Substring(s, e - s + 1);
        }
        return text.Trim();
    } catch { return "{}"; }
}

async Task<string?> GetStravaAccessToken(HttpClient client, IConfiguration config) {
    var form = new Dictionary<string, string> {
        ["client_id"] = GetEnv("STRAVA_CLIENT_ID", config),
        ["client_secret"] = GetEnv("STRAVA_CLIENT_SECRET", config),
        ["refresh_token"] = GetEnv("STRAVA_REFRESH_TOKEN", config),
        ["grant_type"] = "refresh_token"
    };
    var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(form));
    var data = await res.Content.ReadFromJsonAsync<JsonElement>(AppJsonContext.Default.JsonElement);
    return data.TryGetProperty("access_token", out var t) ? t.GetString() : null;
}

string ResolveApiKey(IConfiguration config) => GetEnv("API_KEY", config) ?? GetEnv("GEMINI_API_KEY", config) ?? "";
string GetEnv(string key, IConfiguration config) => Environment.GetEnvironmentVariable(key) ?? config[key] ?? "";

public static class SystemState {
    public static ConcurrentQueue<string> Logs { get; } = new();
    public static void AddLog(string m, string l = "INFO") {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] [{l}] {m}";
        Logs.Enqueue(entry);
        while (Logs.Count > 100) Logs.TryDequeue(out _);
        Console.WriteLine(entry);
    }
}

// --- CONCRETE TYPES FOR AOT SERIALIZATION ---

public record ServiceStatus(string Service, string Status, DateTime Timestamp);
public record ErrorResponse(string Error);
public record WebhookChallengeResponse(string hub_challenge);
public record ActivityUpdate(string Description);
public record ActivityCreate(string Name, string Type, string Start_date_local, int Elapsed_time, string Description, bool Private);

public record GeminiPart(string Text);
public record GeminiContent(GeminiPart[] Parts);
public record GeminiConfig(string ResponseMimeType, float Temperature);
public record GeminiRequest(GeminiContent[] Contents, GeminiConfig GenerationConfig);

// Source Generation Context for AOT
[JsonSerializable(typeof(ServiceStatus))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(WebhookChallengeResponse))]
[JsonSerializable(typeof(ActivityUpdate))]
[JsonSerializable(typeof(ActivityCreate))]
[JsonSerializable(typeof(GeminiRequest))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(IEnumerable<JsonElement>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(string[]))]
internal partial class AppJsonContext : JsonSerializerContext { }
