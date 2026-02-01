
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

// Use 0.0.0.0 for better container compatibility (Koyeb/Render/Docker)
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("GeminiClient");
builder.Services.AddLogging();

builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p
    .AllowAnyOrigin()
    .AllowAnyMethod()
    .AllowAnyHeader()));

var app = builder.Build();
app.UseCors("AllowAll");

// --- STARTUP DIAGNOSTICS ---
SystemState.AddLog($"SYSTEM_BOOT: Target Port: {port}");
SystemState.AddLog("SYSTEM_BOOT: Verifying environment configuration...");

string[] criticalKeys = { "STRAVA_CLIENT_ID", "STRAVA_CLIENT_SECRET", "STRAVA_REFRESH_TOKEN", "API_KEY", "GEMINI_API_KEY" };
foreach(var key in criticalKeys) {
    var source = "Environment";
    var val = Environment.GetEnvironmentVariable(key);
    if (string.IsNullOrEmpty(val)) {
        val = app.Configuration[key];
        source = "appsettings.json";
    }

    if (string.IsNullOrEmpty(val)) {
        SystemState.AddLog($"CONFIG_CHECK: {key} is MISSING (Checked Env and Config)", "WARNING");
    } else {
        var masked = val.Length > 8 ? $"{val[..4]}...{val[^4..]}" : "****";
        SystemState.AddLog($"CONFIG_CHECK: {key} is LOADED from {source} ({masked})");
    }
}

// --- ROOT & HEALTH HANDLERS ---
// Many platforms kill the app if '/' doesn't return 200
app.MapGet("/", () => Results.Ok(new { 
    service = "StravAI Backend", 
    status = "running", 
    timestamp = DateTime.UtcNow,
    diagnostics = "/health"
}));

app.MapGet("/health", () => Results.Ok(new { 
    status = "healthy", 
    version = "2.3.5_STABILITY_PATCH",
    os = RuntimeInformation.OSDescription,
    arch = RuntimeInformation.OSArchitecture.ToString()
}));

app.MapGet("/logs", () => Results.Ok(SystemState.Logs.ToArray()));

// --- WEBHOOK MANAGEMENT ---

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
        SystemState.AddLog("WEBHOOK_REG: Subscription successfully linked.", "SUCCESS");
        return Results.Ok(JsonSerializer.Deserialize<JsonElement>(content));
    } catch (Exception ex) { return Results.Problem(ex.Message); }
});

// --- WEBHOOK RECEIVER ---

app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, [FromQuery(Name = "hub.challenge")] string challenge, [FromQuery(Name = "hub.verify_token")] string verifyToken) => {
    var secret = GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";
    if (mode == "subscribe" && verifyToken == secret) {
        SystemState.AddLog("WEBHOOK_HANDSHAKE: Handshake complete.");
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
        SystemState.AddLog($"WEBHOOK_EVENT: New activity {activityId}. Initiating analysis.");

        _ = Task.Run(async () => {
            try {
                using var stravaClient = clientFactory.CreateClient();
                var token = await GetStravaAccessToken(stravaClient);
                if (token == null) return;
                stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

                var actRes = await stravaClient.GetAsync($"https://www.strava.com/api/v3/activities/{activityId}");
                if (!actRes.IsSuccessStatusCode) return;
                var activity = await actRes.Content.ReadFromJsonAsync<JsonElement>();

                var thirtyDaysAgo = DateTimeOffset.UtcNow.AddDays(-30).ToUnixTimeSeconds();
                var historyRes = await stravaClient.GetAsync($"https://www.strava.com/api/v3/athlete/activities?per_page=100&after={thirtyDaysAgo}");
                var history = await historyRes.Content.ReadFromJsonAsync<List<JsonElement>>() ?? new();

                using var aiClient = clientFactory.CreateClient("GeminiClient");
                var report = await RunCoachSingleActivityAnalysis(aiClient, activity, history);
                
                await stravaClient.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { description = report });
                SystemState.AddLog($"WEBHOOK_SUCCESS: Report for {activityId} synced.", "SUCCESS");
            } catch (Exception ex) {
                SystemState.AddLog($"WEBHOOK_FATAL: {ex.Message}", "ERROR");
            }
        });
    }
    return Results.Ok();
});

// --- PROFILE & AUDIT ---

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
    SystemState.AddLog("AUDIT_ENGINE: Triggering manual full history re-analysis.");
    _ = Task.Run(async () => {
        try {
            using var stravaClient = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(stravaClient);
            if (token == null) {
                SystemState.AddLog("AUDIT_ERR: Failed to obtain Strava Access Token.", "ERROR");
                return;
            }
            stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var historyRes = await stravaClient.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
            if (!historyRes.IsSuccessStatusCode) {
                SystemState.AddLog($"AUDIT_ERR: Strava history fetch failed. Code: {historyRes.StatusCode}", "ERROR");
                return;
            }

            var history = await historyRes.Content.ReadFromJsonAsync<List<JsonElement>>() ?? new();
            SystemState.AddLog($"AUDIT_INFO: Retrieved {history.Count} activities for processing.");

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
                SystemState.AddLog("AUDIT_SUCCESS: Intelligence cache updated.", "SUCCESS");
            } else {
                SystemState.AddLog("AUDIT_ERR: AI response could not be parsed as a JSON Object.", "ERROR");
            }
        } catch (Exception ex) { SystemState.AddLog($"AUDIT_FATAL: {ex.GetType().Name} - {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

// Start the app
app.Lifetime.ApplicationStopping.Register(() => {
    SystemState.AddLog("SYSTEM_SHUTDOWN: Termination signal received.");
});

app.Run();

// --- LOGIC HELPERS ---

async Task<string> RunCoachSingleActivityAnalysis(HttpClient aiClient, JsonElement activity, List<JsonElement> history) {
    var apiKey = ResolveApiKey();
    if (string.IsNullOrEmpty(apiKey)) return "[StravAI Error] AI Key missing.";

    var prompt = $@"ROLE: Elite Coach. Analyze NEW activity vs 30-day trends. 
DATA: {JsonSerializer.Serialize(activity)} 
HISTORY: {JsonSerializer.Serialize(history.Take(20))}";

    var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new {
        contents = new[] { new { parts = new[] { new { text = prompt } } } }
    });

    if (!aiRes.IsSuccessStatusCode) {
        var errBody = await aiRes.Content.ReadAsStringAsync();
        SystemState.AddLog($"AI_CLIENT_ERR: {aiRes.StatusCode} - {errBody}", "ERROR");
        return "[StravAI Error] AI unavailable.";
    }

    var resJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
    return SafeExtractText(resJson);
}

async Task<string> RunCoachFullProfileAnalysis(HttpClient aiClient, List<JsonElement> history) {
    var apiKey = ResolveApiKey();
    if (string.IsNullOrEmpty(apiKey)) return "{}";

    var prompt = $@"ROLE: Elite Coach. Return raw JSON AthleteProfile. DATA: {JsonSerializer.Serialize(history.Take(80))}";
    
    var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new {
        contents = new[] { new { parts = new[] { new { text = prompt } } } }
    });

    if (!aiRes.IsSuccessStatusCode) {
        var errBody = await aiRes.Content.ReadAsStringAsync();
        SystemState.AddLog($"AI_CLIENT_ERR: {aiRes.StatusCode} - {errBody}", "ERROR");
        return "{}";
    }

    var resJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
    var text = SafeExtractText(resJson);
    
    int s = text.IndexOf("{"); int e = text.LastIndexOf("}");
    return (s != -1 && e != -1) ? text.Substring(s, e - s + 1) : "{}";
}

string ResolveApiKey() {
    var key = GetEnv("API_KEY");
    if (string.IsNullOrEmpty(key)) key = GetEnv("GEMINI_API_KEY");
    
    if (string.IsNullOrEmpty(key)) {
        SystemState.AddLog("AUTH_ERR: No API Key found in environment or config.", "ERROR");
        return "";
    }
    
    if (key == "YOUR_GOOGLE_API_KEY") {
        SystemState.AddLog("AUTH_ERR: Using placeholder API key. Please update your environment variables.", "ERROR");
        return "";
    }
    
    return key;
}

string SafeExtractText(JsonElement root) {
    try {
        if (root.TryGetProperty("error", out var errorObj)) {
            var msg = errorObj.TryGetProperty("message", out var m) ? m.GetString() : "Unknown Google API error";
            SystemState.AddLog($"AI_API_ERROR: {msg}", "ERROR");
            return "";
        }

        var candidates = GetRequiredProperty(root, "candidates", "AI.Response");
        if (candidates.ValueKind != JsonValueKind.Array || candidates.GetArrayLength() == 0) {
            SystemState.AddLog("AI_PARSE: 'candidates' is empty.", "ERROR");
            return "";
        }
        
        var first = candidates[0];
        var content = GetRequiredProperty(first, "content", "AI.Candidate");
        var parts = GetRequiredProperty(content, "parts", "AI.Content");
        
        if (parts.ValueKind != JsonValueKind.Array || parts.GetArrayLength() == 0) {
            SystemState.AddLog("AI_PARSE: 'parts' is empty.", "ERROR");
            return "";
        }
        
        var textProp = GetRequiredProperty(parts[0], "text", "AI.Part");
        return textProp.GetString() ?? "";
    } catch (Exception ex) {
        SystemState.AddLog($"PARSE_FATAL: {ex.Message}", "ERROR");
        return "";
    }
}

JsonElement GetRequiredProperty(JsonElement element, string name, string context) {
    if (element.TryGetProperty(name, out var prop)) return prop;
    
    var keys = new List<string>();
    if (element.ValueKind == JsonValueKind.Object) {
        foreach (var p in element.EnumerateObject()) keys.Add(p.Name);
    }
    
    var available = string.Join(", ", keys);
    var raw = element.ToString();
    var snippet = raw.Length > 200 ? raw[..200] + "..." : raw;
    
    // This solves your question: "What was it looking for? what does the dictionary contain?"
    throw new KeyNotFoundException($"[{context}] FAILED to find key '{name}'. Available keys here: [{available}]. Data snippet: {snippet}");
}

async Task<string?> GetStravaAccessToken(HttpClient client) {
    try {
        if (SystemState.CachedToken != null && DateTime.UtcNow < SystemState.TokenExpiry) return SystemState.CachedToken;
        
        var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
            new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
            new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN")),
            new KeyValuePair<string, string>("grant_type", "refresh_token")
        }));
        
        var content = await res.Content.ReadAsStringAsync();
        if (!res.IsSuccessStatusCode) {
            SystemState.AddLog($"STRAVA_AUTH_FAIL: {res.StatusCode} - {content}", "ERROR");
            return null;
        }

        var data = JsonSerializer.Deserialize<JsonElement>(content);
        if (data.TryGetProperty("access_token", out var tokenProp)) {
            SystemState.CachedToken = tokenProp.GetString();
            SystemState.TokenExpiry = DateTime.UtcNow.AddHours(5);
            return SystemState.CachedToken;
        } else {
            SystemState.AddLog($"STRAVA_AUTH: Missing 'access_token' in response. Response: {content}", "ERROR");
            return null;
        }
    } catch (Exception ex) {
        SystemState.AddLog($"AUTH_EXCEPTION: {ex.Message}", "ERROR");
        return null;
    }
}

async Task<long?> GetCachedSystemActivityId(HttpClient client) {
    try {
        if (SystemState.CachedSystemActivityId.HasValue && DateTime.UtcNow < SystemState.CacheExpiry) return SystemState.CachedSystemActivityId;
        var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
        if (!res.IsSuccessStatusCode) return null;
        var acts = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
        SystemState.CacheExpiry = DateTime.UtcNow.AddMinutes(5);
        foreach (var act in acts ?? []) {
            if (act.TryGetProperty("name", out var n) && n.GetString() == "[StravAI] System Cache") {
                if (act.TryGetProperty("id", out var idProp)) {
                    SystemState.CachedSystemActivityId = idProp.GetInt64();
                    return SystemState.CachedSystemActivityId;
                }
            }
        }
    } catch (Exception ex) {
        SystemState.AddLog($"CACHE_LOOKUP_ERR: {ex.Message}", "ERROR");
    }
    return null;
}

string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val.Trim();
    val = app.Configuration[key];
    if (!string.IsNullOrEmpty(val)) return val.Trim();
    return "";
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
