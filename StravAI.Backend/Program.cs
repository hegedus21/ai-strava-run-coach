
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json.Nodes;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("GeminiClient");
builder.Services.AddLogging();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

// CRITICAL: Override platform defaults (like http://*) with http://0.0.0.0
app.Urls.Clear();
app.Urls.Add($"http://0.0.0.0:{port}");

app.UseCors("AllowAll");

// --- GLOBAL DIAGNOSTIC MIDDLEWARE ---
app.Use(async (context, next) => {
    try {
        await next();
    } catch (KeyNotFoundException ex) {
        SystemState.AddLog($"JSON_MAP_ERROR: A required key was missing in the data dictionary.", "ERROR");
        SystemState.AddLog($"DETAILS: {ex.Message}", "ERROR");
        context.Response.StatusCode = 500;
        await context.Response.WriteAsJsonAsync(new { error = "Data Mapping Error", details = ex.Message });
    } catch (Exception ex) {
        SystemState.AddLog($"UNHANDLED_EXCEPTION: {ex.GetType().Name} - {ex.Message}", "ERROR");
        throw;
    }
});

// --- STARTUP DIAGNOSTICS ---
SystemState.AddLog($"SYSTEM_BOOT: Binding to http://0.0.0.0:{port}");
string[] criticalKeys = { "STRAVA_CLIENT_ID", "STRAVA_CLIENT_SECRET", "STRAVA_REFRESH_TOKEN", "API_KEY", "GEMINI_API_KEY" };
foreach(var key in criticalKeys) {
    var val = GetEnv(key, app);
    if (string.IsNullOrEmpty(val)) {
        SystemState.AddLog($"CONFIG_CHECK: {key} is MISSING", "WARNING");
    } else {
        var masked = val.Length > 8 ? $"{val[..4]}...{val[^4..]}" : "****";
        SystemState.AddLog($"CONFIG_CHECK: {key} is LOADED ({masked})");
    }
}

// --- HEALTH HANDLERS (PLATFORM SURVIVAL) ---
app.MapGet("/", () => {
    return Results.Ok(new { service = "StravAI", status = "online", uptime = DateTime.UtcNow });
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", timestamp = DateTime.UtcNow }));
app.MapGet("/logs", () => Results.Ok(SystemState.Logs.ToArray()));

// --- WEBHOOK MANAGEMENT ---

app.MapGet("/webhook/status", async (IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var res = await client.GetAsync($"https://www.strava.com/api/v3/push_subscriptions?client_id={GetEnv("STRAVA_CLIENT_ID", app)}&client_secret={GetEnv("STRAVA_CLIENT_SECRET", app)}");
        if (!res.IsSuccessStatusCode) return Results.StatusCode((int)res.StatusCode);
        return Results.Ok(await res.Content.ReadFromJsonAsync<JsonElement>());
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/webhook/register", async (IHttpClientFactory clientFactory, [FromQuery] string callbackUrl) => {
    try {
        using var client = clientFactory.CreateClient();
        var payload = new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID", app)),
            new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET", app)),
            new KeyValuePair<string, string>("callback_url", callbackUrl),
            new KeyValuePair<string, string>("verify_token", GetEnv("STRAVA_VERIFY_TOKEN", app) ?? "STRAVAI_SECURE_TOKEN")
        });
        var res = await client.PostAsync("https://www.strava.com/api/v3/push_subscriptions", payload);
        var content = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return Results.BadRequest(new { error = content });
        SystemState.AddLog("WEBHOOK_REG: Success.", "SUCCESS");
        return Results.Ok(JsonSerializer.Deserialize<JsonElement>(content));
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, [FromQuery(Name = "hub.challenge")] string challenge, [FromQuery(Name = "hub.verify_token")] string verifyToken) => {
    var secret = GetEnv("STRAVA_VERIFY_TOKEN", app) ?? "STRAVAI_SECURE_TOKEN";
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
                var token = await GetStravaAccessToken(stravaClient, app);
                if (token == null) return;
                stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                var actRes = await stravaClient.GetAsync($"https://www.strava.com/api/v3/activities/{activityId}");
                var activity = await actRes.Content.ReadFromJsonAsync<JsonElement>();
                var historyRes = await stravaClient.GetAsync($"https://www.strava.com/api/v3/athlete/activities?per_page=20");
                var history = await historyRes.Content.ReadFromJsonAsync<List<JsonElement>>() ?? new();
                using var aiClient = clientFactory.CreateClient("GeminiClient");
                var report = await RunCoachAnalysis(aiClient, activity, history, app);
                await stravaClient.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { description = report });
                SystemState.AddLog($"WEBHOOK_SUCCESS: Activity {activityId} processed.", "SUCCESS");
            } catch (Exception ex) { SystemState.AddLog($"WEBHOOK_ERROR: {ex.Message}", "ERROR"); }
        });
    }
    return Results.Ok();
});

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    using var client = clientFactory.CreateClient();
    var token = await GetStravaAccessToken(client, app);
    if (token == null) return Results.Unauthorized();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
    var acts = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
    var cache = acts?.FirstOrDefault(a => a.GetProperty("name").GetString() == "[StravAI] System Cache");
    if (cache.ValueKind == JsonValueKind.Undefined) return Results.NotFound();
    var detail = await client.GetAsync($"https://www.strava.com/api/v3/activities/{cache.GetProperty("id").GetInt64()}");
    var act = await detail.Content.ReadFromJsonAsync<JsonElement>();
    var desc = act.GetProperty("description").GetString() ?? "";
    var json = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0];
    return Results.Ok(JsonNode.Parse(json));
});

app.MapPost("/audit", async (IHttpClientFactory clientFactory) => {
    SystemState.AddLog("AUDIT: Started full history re-analysis.");
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(client, app);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var history = await (await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100")).Content.ReadFromJsonAsync<List<JsonElement>>();
            using var aiClient = clientFactory.CreateClient("GeminiClient");
            var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={ResolveApiKey(app)}", new { contents = new[] { new { parts = new[] { new { text = $"ROLE: Elite Coach. Return raw JSON AthleteProfile. DATA: {JsonSerializer.Serialize(history?.Take(50))}" } } } } });
            var root = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
            var text = SafeExtractText(root);
            int s = text.IndexOf("{"); int e = text.LastIndexOf("}");
            var profile = text.Substring(s, e - s + 1);
            var finalDesc = $"[StravAI System Cache]\n---CACHE_START---\n{profile}\n---CACHE_END---\nUpdated: {DateTime.UtcNow}";
            var cache = history?.FirstOrDefault(a => a.GetProperty("name").GetString() == "[StravAI] System Cache");
            if (cache?.ValueKind == JsonValueKind.Object) await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cache.Value.GetProperty("id").GetInt64()}", new { description = finalDesc });
            else await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name = "[StravAI] System Cache", type = "Run", start_date_local = DateTime.UtcNow.ToString("O"), elapsed_time = 1, description = finalDesc, @private = true });
            SystemState.AddLog("AUDIT: Success.", "SUCCESS");
        } catch (Exception ex) { SystemState.AddLog($"AUDIT_FAIL: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

app.Run();

// --- HELPERS ---

async Task<string> RunCoachAnalysis(HttpClient aiClient, JsonElement activity, List<JsonElement> history, WebApplication app) {
    var key = ResolveApiKey(app);
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
    throw new KeyNotFoundException($"[{context}] Could not find '{name}'. Available keys at this level are: [{keys}]. Raw data: {element}");
}

async Task<string?> GetStravaAccessToken(HttpClient client, WebApplication app) {
    var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID", app)),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET", app)),
        new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN", app)),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    var data = await res.Content.ReadFromJsonAsync<JsonElement>();
    return data.TryGetProperty("access_token", out var t) ? t.GetString() : null;
}

string ResolveApiKey(WebApplication app) {
    var k = GetEnv("API_KEY", app);
    return string.IsNullOrEmpty(k) ? GetEnv("GEMINI_API_KEY", app) : k;
}

string GetEnv(string key, WebApplication app) {
    var v = Environment.GetEnvironmentVariable(key);
    return string.IsNullOrEmpty(v) ? app.Configuration[key] ?? "" : v;
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
