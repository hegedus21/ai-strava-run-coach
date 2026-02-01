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
builder.WebHost.UseUrls($"http://*:{port}");

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("GeminiClient");
builder.Services.AddLogging();

builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader()));

var app = builder.Build();
app.UseCors("AllowAll");

// --- WEBHOOK MANAGEMENT ENDPOINTS ---

app.MapGet("/webhook/status", async (IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var clientId = GetEnv("STRAVA_CLIENT_ID");
        var clientSecret = GetEnv("STRAVA_CLIENT_SECRET");
        var res = await client.GetAsync($"https://www.strava.com/api/v3/push_subscriptions?client_id={clientId}&client_secret={clientSecret}");
        if (!res.IsSuccessStatusCode) return Results.StatusCode((int)res.StatusCode);
        var subs = await res.Content.ReadFromJsonAsync<JsonElement>();
        return Results.Ok(subs);
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapPost("/webhook/register", async (IHttpClientFactory clientFactory, [FromQuery] string callbackUrl) => {
    try {
        using var client = clientFactory.CreateClient();
        var clientId = GetEnv("STRAVA_CLIENT_ID");
        var clientSecret = GetEnv("STRAVA_CLIENT_SECRET");
        var verifyToken = GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";
        
        var payload = new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("callback_url", callbackUrl),
            new KeyValuePair<string, string>("verify_token", verifyToken)
        });

        var res = await client.PostAsync("https://www.strava.com/api/v3/push_subscriptions", payload);
        var content = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) return Results.BadRequest(new { error = content });
        SystemState.AddLog("WEBHOOK_REG: Subscription successful.", "SUCCESS");
        return Results.Ok(JsonSerializer.Deserialize<JsonElement>(content));
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

app.MapDelete("/webhook/unregister/{id}", async (int id, IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var clientId = GetEnv("STRAVA_CLIENT_ID");
        var clientSecret = GetEnv("STRAVA_CLIENT_SECRET");
        var res = await client.DeleteAsync($"https://www.strava.com/api/v3/push_subscriptions/{id}?client_id={clientId}&client_secret={clientSecret}");
        return res.IsSuccessStatusCode ? Results.Ok() : Results.BadRequest();
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

// --- CORE WEBHOOK RECEIVER ---

app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, [FromQuery(Name = "hub.challenge")] string challenge, [FromQuery(Name = "hub.verify_token")] string verifyToken) => {
    var secret = GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";
    if (mode == "subscribe" && verifyToken == secret) {
        SystemState.AddLog("WEBHOOK_VERIFY: Handshake successful.");
        return Results.Ok(new { hub_challenge = challenge });
    }
    return Results.Unauthorized();
});

app.MapPost("/webhook", async (HttpContext context, IHttpClientFactory clientFactory) => {
    using var reader = new StreamReader(context.Request.Body);
    var body = await reader.ReadToEndAsync();
    var eventData = JsonSerializer.Deserialize<JsonElement>(body);
    
    if (eventData.TryGetProperty("object_type", out var objType) && objType.GetString() == "activity" && 
        eventData.TryGetProperty("aspect_type", out var aspect) && aspect.GetString() == "create") {
        
        var activityId = eventData.GetProperty("object_id").GetInt64();
        SystemState.AddLog($"WEBHOOK_EVENT: New activity {activityId}. Triggering AI analysis.");

        _ = Task.Run(async () => {
            try {
                using var stravaClient = clientFactory.CreateClient();
                var token = await GetStravaAccessToken(stravaClient);
                if (token == null) return;
                stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                // 1. Fetch deep activity details
                var actRes = await stravaClient.GetAsync($"https://www.strava.com/api/v3/activities/{activityId}");
                if (!actRes.IsSuccessStatusCode) return;
                var activity = await actRes.Content.ReadFromJsonAsync<JsonElement>();

                // 2. Fetch context: History from last 30 days
                var thirtyDaysAgo = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
                var historyRes = await stravaClient.GetAsync($"https://www.strava.com/api/v3/athlete/activities?per_page=100&after={thirtyDaysAgo}");
                var history = await historyRes.Content.ReadFromJsonAsync<List<JsonElement>>() ?? new();

                // 3. AI Analysis
                using var aiClient = clientFactory.CreateClient("GeminiClient");
                var report = await RunCoachSingleActivityAnalysis(aiClient, activity, history);
                
                // 4. Update activity description on Strava
                await stravaClient.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { description = report });
                SystemState.AddLog($"WEBHOOK_SUCCESS: Activity {activityId} report posted.", "SUCCESS");
            } catch (Exception ex) {
                SystemState.AddLog($"WEBHOOK_FATAL: {ex.Message}", "ERROR");
            }
        });
    }
    return Results.Ok();
});

// --- SERVICE ENDPOINTS ---

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "2.3.0_WEBHOOK_PRO" }));
app.MapGet("/logs", () => Results.Ok(SystemState.Logs.ToArray()));

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client);
        if (token == null) return Results.StatusCode(401);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var cacheId = await GetCachedSystemActivityId(client);
        if (cacheId == null) return Results.NotFound();

        var actRes = await client.GetAsync($"https://www.strava.com/api/v3/activities/{cacheId}");
        var act = await actRes.Content.ReadFromJsonAsync<JsonElement>();
        var desc = act.TryGetProperty("description", out var d) ? d.GetString() ?? "" : "";
        
        if (!desc.Contains("---CACHE_START---")) return Results.BadRequest();
        var json = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0].Trim();
        var node = JsonNode.Parse(json);
        if (node is JsonObject obj) return Results.Ok(obj);
        return Results.StatusCode(500);
    } catch { return Results.StatusCode(500); }
});

app.MapPost("/audit", async (IHttpClientFactory clientFactory) => {
    SystemState.AddLog("AUDIT_ENGINE: Starting manual full history re-analysis.");
    _ = Task.Run(async () => {
        try {
            using var stravaClient = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(stravaClient);
            if (token == null) return;
            stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var historyRes = await stravaClient.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
            var history = await historyRes.Content.ReadFromJsonAsync<List<JsonElement>>() ?? new();

            using var aiClient = clientFactory.CreateClient("GeminiClient");
            var aiJson = await RunCoachFullProfileAnalysis(aiClient, history);
            
            var node = JsonNode.Parse(aiJson);
            if (node is JsonObject profileObj) {
                var finalDesc = $"[StravAI System Cache]\n---CACHE_START---\n{profileObj.ToJsonString()}\n---CACHE_END---\nUpdated: {GetCetTimestamp()}";
                var cacheId = await GetCachedSystemActivityId(stravaClient);
                if (cacheId == null)
                    await stravaClient.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name = "[StravAI] System Cache", type = "Run", start_date_local = DateTime.UtcNow.ToString("O"), elapsed_time = 1, description = finalDesc, @private = true });
                else
                    await stravaClient.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cacheId}", new { description = finalDesc });
                SystemState.AddLog("AUDIT_SUCCESS: Central intelligence updated.", "SUCCESS");
            }
        } catch (Exception ex) { SystemState.AddLog($"AUDIT_ERR: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

app.Run();

// --- LOGIC HELPERS & TYPES (Static Methods at Bottom) ---

async Task<string> RunCoachSingleActivityAnalysis(HttpClient aiClient, JsonElement activity, List<JsonElement> history) {
    var apiKey = GetEnv("API_KEY") ?? GetEnv("GEMINI_API_KEY");
    var raceType = GetEnv("GOAL_RACE_TYPE") ?? "Marathon";
    var raceDate = GetEnv("GOAL_RACE_DATE") ?? "TBD";
    
    var prompt = $@"ROLE: Elite Marathon Coach.
TASK: Analyze the NEW activity provided below in the context of the athlete's last 30 days of history.
GOAL: Training for {raceType} on {raceDate}.
OUTPUT: Return a professional, analytical coaching report for the activity description. 
FORMAT: 
[StravAI Report]
Summary: (Master summary of the effort)
Physiological Impact: (Impact on training load/recovery)
Coach Advice: (What to do differently or next)

NEW ACTIVITY: {JsonSerializer.Serialize(activity)}
HISTORY (Last 30 Days): {JsonSerializer.Serialize(history.Take(50))}";

    var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new {
        contents = new[] { new { parts = new[] { new { text = prompt } } } }
    });

    if (!aiRes.IsSuccessStatusCode) return "[StravAI Error] Coach unavailable.";
    var resJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
    return resJson.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
}

async Task<string> RunCoachFullProfileAnalysis(HttpClient aiClient, List<JsonElement> history) {
    var apiKey = GetEnv("API_KEY") ?? GetEnv("GEMINI_API_KEY");
    var prompt = $@"ROLE: Elite Coach. Return a JSON AthleteProfile. 
DATA: {JsonSerializer.Serialize(history.Take(100))}";
    var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new {
        contents = new[] { new { parts = new[] { new { text = prompt } } } }
    });
    var resJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
    var text = resJson.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
    int s = text.IndexOf("{"); int e = text.LastIndexOf("}");
    return (s != -1 && e != -1) ? text.Substring(s, e - s + 1) : "{}";
}

async Task<string?> GetStravaAccessToken(HttpClient client) {
    if (SystemState.CachedToken != null && DateTime.UtcNow < SystemState.TokenExpiry) return SystemState.CachedToken;
    var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
        new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN")),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    if (!res.IsSuccessStatusCode) return null;
    var data = await res.Content.ReadFromJsonAsync<JsonElement>();
    SystemState.CachedToken = data.GetProperty("access_token").GetString();
    SystemState.TokenExpiry = DateTime.UtcNow.AddHours(5);
    return SystemState.CachedToken;
}

async Task<long?> GetCachedSystemActivityId(HttpClient client) {
    if (SystemState.CachedSystemActivityId.HasValue && DateTime.UtcNow < SystemState.CacheExpiry) return SystemState.CachedSystemActivityId;
    var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
    if (!res.IsSuccessStatusCode) return null;
    var acts = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
    SystemState.CacheExpiry = DateTime.UtcNow.AddMinutes(5);
    foreach (var act in acts ?? new()) {
        if (act.TryGetProperty("name", out var n) && n.GetString() == "[StravAI] System Cache") {
            SystemState.CachedSystemActivityId = act.GetProperty("id").GetInt64();
            return SystemState.CachedSystemActivityId;
        }
    }
    return null;
}

string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    return !string.IsNullOrEmpty(val) ? val : app.Configuration[key] ?? "";
}

string GetCetTimestamp() {
    try {
        var tzi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time") : TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tzi).ToString("dd/MM/yyyy HH:mm:ss");
    } catch { return DateTime.UtcNow.AddHours(1).ToString("dd/MM/yyyy HH:mm:ss"); }
}

public static class SystemState {
    public static long? CachedSystemActivityId { get; set; }
    public static DateTime CacheExpiry { get; set; } = DateTime.MinValue;
    public static string? CachedToken { get; set; }
    public static DateTime TokenExpiry { get; set; } = DateTime.MinValue;
    public static ConcurrentQueue<string> Logs { get; } = new();
    public static void AddLog(string m, string l = "INFO") {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] [{l}] {m}";
        Logs.Enqueue(entry);
        while (Logs.Count > 100) Logs.TryDequeue(out _);
        Console.WriteLine(entry);
    }
}
