
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
var portEnv = Environment.GetEnvironmentVariable("PORT") ?? "8080";
if (!int.TryParse(portEnv, out var port)) port = 8080;

// FORCED IPv4 BINDING: Most cloud platforms require 0.0.0.0 for health checks to pass.
// Using ConfigureKestrel is the most authoritative way to bypass environment variable overrides.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Listen(IPAddress.Any, port);
});

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("GeminiClient");
builder.Services.AddLogging();
builder.Services.AddHealthChecks();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// Enable standard health check endpoint
app.MapHealthChecks("/health");

app.UseCors("AllowAll");

// --- GLOBAL DIAGNOSTIC MIDDLEWARE ---
app.Use(async (context, next) => {
    // Log incoming requests to prove the app is reachable
    if (context.Request.Path == "/" || context.Request.Path == "/health") {
        SystemState.AddLog($"HEALTH_PROBE: {context.Request.Method} {context.Request.Path} from {context.Connection.RemoteIpAddress}");
    }
    
    try {
        await next();
    } catch (KeyNotFoundException ex) {
        SystemState.AddLog($"JSON_MAP_ERROR: Required data key missing.", "ERROR");
        SystemState.AddLog($"DETAILS: {ex.Message}", "ERROR");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Mapping Error", details = ex.Message });
    } catch (Exception ex) {
        SystemState.AddLog($"CRITICAL_FAULT: {ex.GetType().Name} - {ex.Message}", "ERROR");
        throw;
    }
});

// --- STARTUP LOGS ---
SystemState.AddLog($"SYSTEM_BOOT: Listening on http://0.0.0.0:{port}");
string[] criticalKeys = { "STRAVA_CLIENT_ID", "STRAVA_CLIENT_SECRET", "STRAVA_REFRESH_TOKEN", "API_KEY", "GEMINI_API_KEY" };
foreach(var key in criticalKeys) {
    var val = GetEnv(key, app.Configuration);
    if (string.IsNullOrEmpty(val)) {
        SystemState.AddLog($"CONFIG_CHECK: {key} is MISSING", "WARNING");
    } else {
        var masked = val.Length > 8 ? $"{val[..4]}...{val[^4..]}" : "****";
        SystemState.AddLog($"CONFIG_CHECK: {key} is LOADED ({masked})");
    }
}

// --- ROOT HANDLER ---
app.MapGet("/", () => Results.Ok(new { service = "StravAI", status = "online", timestamp = DateTime.UtcNow }));
app.MapGet("/logs", () => Results.Ok(SystemState.Logs.ToArray()));

// --- WEBHOOK MANAGEMENT ---

app.MapGet("/webhook/status", async (IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var res = await client.GetAsync($"https://www.strava.com/api/v3/push_subscriptions?client_id={GetEnv("STRAVA_CLIENT_ID", app.Configuration)}&client_secret={GetEnv("STRAVA_CLIENT_SECRET", app.Configuration)}");
        if (!res.IsSuccessStatusCode) return Results.StatusCode((int)res.StatusCode);
        return Results.Ok(await res.Content.ReadFromJsonAsync<JsonElement>());
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/webhook/register", async (IHttpClientFactory clientFactory, [FromQuery] string callbackUrl) => {
    try {
        using var client = clientFactory.CreateClient();
        var payload = new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID", app.Configuration)),
            new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET", app.Configuration)),
            new KeyValuePair<string, string>("callback_url", callbackUrl),
            new KeyValuePair<string, string>("verify_token", GetEnv("STRAVA_VERIFY_TOKEN", app.Configuration) ?? "STRAVAI_SECURE_TOKEN")
        });
        var res = await client.PostAsync("https://www.strava.com/api/v3/push_subscriptions", payload);
        var content = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return Results.BadRequest(new { error = content });
        SystemState.AddLog("WEBHOOK_REG: Success.", "SUCCESS");
        return Results.Ok(JsonSerializer.Deserialize<JsonElement>(content));
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, [FromQuery(Name = "hub.challenge")] string challenge, [FromQuery(Name = "hub.verify_token")] string verifyToken) => {
    var secret = GetEnv("STRAVA_VERIFY_TOKEN", app.Configuration) ?? "STRAVAI_SECURE_TOKEN";
    if (mode == "subscribe" && verifyToken == secret) return Results.Ok(new { hub_challenge = challenge });
    return Results.Unauthorized();
});

app.MapPost("/webhook", async (HttpContext context, IHttpClientFactory clientFactory) => {
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var eventData = JsonSerializer.Deserialize<JsonElement>(body);
    if (eventData.TryGetProperty("object_type", out var objType) && objType.GetString() == "activity" && eventData.TryGetProperty("aspect_type", out var aspect) && aspect.GetString() == "create") {
        var activityId = eventData.GetProperty("object_id").GetInt64();
        SystemState.AddLog($"WEBHOOK_EVENT: Activity {activityId} detected.");
        _ = Task.Run(async () => {
            try {
                using var stravaClient = clientFactory.CreateClient();
                var token = await GetStravaAccessToken(stravaClient, app.Configuration);
                if (token == null) return;
                stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var actRes = await stravaClient.GetAsync($"https://www.strava.com/api/v3/activities/{activityId}");
                var activity = await actRes.Content.ReadFromJsonAsync<JsonElement>();
                var historyRes = await stravaClient.GetAsync($"https://www.strava.com/api/v3/athlete/activities?per_page=20");
                var history = await historyRes.Content.ReadFromJsonAsync<List<JsonElement>>() ?? new();
                using var aiClient = clientFactory.CreateClient("GeminiClient");
                var report = await RunCoachAnalysis(aiClient, activity, history, app.Configuration);
                await stravaClient.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { description = report });
                SystemState.AddLog($"WEBHOOK_SUCCESS: Activity {activityId} processed.", "SUCCESS");
            } catch (Exception ex) { SystemState.AddLog($"WEBHOOK_ERROR: {ex.Message}", "ERROR"); }
        });
    }
    return Results.Ok();
});

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    using var client = clientFactory.CreateClient();
    var token = await GetStravaAccessToken(client, app.Configuration);
    if (token == null) return Results.Unauthorized();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
    var acts = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
    
    var cache = acts?.FirstOrDefault(a => a.GetProperty("name").GetString() == "[StravAI] System Cache");
    if (!cache.HasValue || cache.Value.ValueKind == JsonValueKind.Undefined) return Results.NotFound();
    
    var detail = await client.GetAsync($"https://www.strava.com/api/v3/activities/{cache.Value.GetProperty("id").GetInt64()}");
    var act = await detail.Content.ReadFromJsonAsync<JsonElement>();
    var desc = act.GetProperty("description").GetString() ?? "";
    var json = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0];
    return Results.Ok(JsonNode.Parse(json));
});

app.MapPost("/audit", async (IHttpClientFactory clientFactory) => {
    SystemState.AddLog("AUDIT: Manual re-analysis requested.");
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(client, app.Configuration);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var history = await (await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100")).Content.ReadFromJsonAsync<List<JsonElement>>();
            using var aiClient = clientFactory.CreateClient("GeminiClient");
            var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={ResolveApiKey(app.Configuration)}", new { contents = new[] { new { parts = new[] { new { text = $"ROLE: Elite Coach. Return raw JSON AthleteProfile. DATA: {JsonSerializer.Serialize(history?.Take(50))}" } } } } });
            var root = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
            var text = SafeExtractText(root);
            int s = text.IndexOf("{"); int e = text.LastIndexOf("}");
            var profile = text.Substring(s, e - s + 1);
            var finalDesc = $"[StravAI System Cache]\n---CACHE_START---\n{profile}\n---CACHE_END---\nUpdated: {DateTime.UtcNow}";
            
            var cache = history?.FirstOrDefault(a => a.GetProperty("name").GetString() == "[StravAI] System Cache");
            if (cache.HasValue && cache.Value.ValueKind == JsonValueKind.Object) 
                await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cache.Value.GetProperty("id").GetInt64()}", new { description = finalDesc });
            else 
                await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name = "[StravAI] System Cache", type = "Run", start_date_local = DateTime.UtcNow.ToString("O"), elapsed_time = 1, description = finalDesc, @private = true });
            
            SystemState.AddLog("AUDIT: Success.", "SUCCESS");
        } catch (Exception ex) { SystemState.AddLog($"AUDIT_FAIL: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

app.Run();

// --- HELPERS ---

async Task<string> RunCoachAnalysis(HttpClient aiClient, JsonElement activity, List<JsonElement> history, IConfiguration config) {
    var key = ResolveApiKey(config);
    var prompt = $"Analyze activity vs history. Activity: {JsonSerializer.Serialize(activity)}. History: {JsonSerializer.Serialize(history.Take(10))}";
    var res = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={key}", new { contents = new[] { new { parts = new[] { new { text = prompt } } } } });
    var root = await res.Content.ReadFromJsonAsync<JsonElement>();
    return SafeExtractText(root);
}

string SafeExtractText(JsonElement root) {
    if (root.TryGetProperty("error", out var error)) throw new Exception($"AI API Error: {error.GetProperty("message").GetString()}");
    var candidates = GetRequiredProperty(root, "candidates", "AI_ROOT");
    var parts = GetRequiredProperty(candidates[0].GetProperty("content"), "parts", "AI_CONTENT");
    return GetRequiredProperty(parts[0], "text", "AI_PART").GetString() ?? "";
}

JsonElement GetRequiredProperty(JsonElement element, string name, string context) {
    if (element.TryGetProperty(name, out var prop)) return prop;
    var keys = string.Join(", ", element.ValueKind == JsonValueKind.Object ? element.EnumerateObject().Select(p => p.Name) : new[] { "Not an Object" });
    throw new KeyNotFoundException($"[{context}] Could not find key '{name}'. Found keys: [{keys}]. Raw: {element}");
}

async Task<string?> GetStravaAccessToken(HttpClient client, IConfiguration config) {
    var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID", config)),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET", config)),
        new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN", config)),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    var data = await res.Content.ReadFromJsonAsync<JsonElement>();
    return data.TryGetProperty("access_token", out var t) ? t.GetString() : null;
}

string ResolveApiKey(IConfiguration config) {
    var k = GetEnv("API_KEY", config);
    return string.IsNullOrEmpty(k) ? GetEnv("GEMINI_API_KEY", config) : k;
}

string GetEnv(string key, IConfiguration config) {
    var v = Environment.GetEnvironmentVariable(key);
    return string.IsNullOrEmpty(v) ? config[key] ?? "" : v;
}

public static class SystemState {
    public static ConcurrentQueue<string> Logs { get; } = new();
    public static void AddLog(string m, string l = "INFO") {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] [{l}] {m}";
        Logs.Enqueue(entry);
        while (Logs.Count > 100) Logs.TryDequeue(out _);
        Console.WriteLine(entry);
    }
}
