
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

// Refined CORS: Allow hegedus21.github.io specifically and all other origins for flexibility during setup
builder.Services.AddCors(options => options.AddPolicy("StravAIPolicy", p => p
    .SetIsOriginAllowed(_ => true) // More permissive for dynamic deployment environments
    .AllowAnyMethod()
    .AllowAnyHeader()
    .AllowCredentials()));

var app = builder.Build();

// LOGGING SETUP
var logs = new ConcurrentQueue<string>();
void AddLog(string message, string level = "INFO") {
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    var logEntry = $"[{timestamp}] [{level}] {message}";
    logs.Enqueue(logEntry);
    while (logs.Count > 100) logs.TryDequeue(out _);
    Console.WriteLine(logEntry); 
}

// CACHE GLOBALS
long? _cachedSystemActivityId = null;
DateTime _cacheExpiry = DateTime.MinValue;
string? _cachedToken = null;
DateTime _tokenExpiry = DateTime.MinValue;

app.UseCors("StravAIPolicy");

// HELPER: Get ENV or Config
string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val.Trim();
    return app.Configuration[key]?.Trim() ?? "";
}

// HELPER: CET Time
string GetCetTimestamp() {
    try {
        var tzi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
            ? TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time") 
            : TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tzi).ToString("dd/MM/yyyy HH:mm:ss");
    } catch {
        return DateTime.UtcNow.AddHours(1).ToString("dd/MM/yyyy HH:mm:ss");
    }
}

// AUTH MIDDLEWARE
app.Use(async (context, next) => {
    var path = context.Request.Path.Value?.ToLower() ?? "";
    
    // Explicitly handle preflight
    if (context.Request.Method == "OPTIONS") {
        context.Response.StatusCode = 200;
        return;
    }

    if (path == "/" || path == "/health") { await next(); return; }
    
    var secret = GetEnv("STRAVA_VERIFY_TOKEN");
    if (string.IsNullOrEmpty(secret)) secret = "STRAVAI_SECURE_TOKEN"; // Fallback for local dev

    if (!context.Request.Headers.TryGetValue("X-StravAI-Secret", out var providedSecret) || providedSecret != secret) {
        AddLog($"AUTH_DENIED: Path={path} RemoteIp={context.Connection.RemoteIpAddress}", "WARN");
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized: System Secret Mismatch.");
        return;
    }
    await next();
});

// ENDPOINTS
app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "2.1.1_DEBUG" }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client);
        if (token == null) {
            AddLog("PROFILE_ERR: Failed to retrieve Strava token.", "ERROR");
            return Results.Json(new { error = "Strava Authentication Failed" }, statusCode: 401);
        }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var cacheId = await GetCachedSystemActivityId(client);
        if (cacheId == null) {
            AddLog("PROFILE_ERR: Cache activity not found on Strava.", "WARN");
            return Results.NotFound(new { error = "System cache not initialized." });
        }
        
        var actRes = await client.GetAsync($"https://www.strava.com/api/v3/activities/{cacheId}");
        if (!actRes.IsSuccessStatusCode) {
            AddLog($"PROFILE_ERR: Activity fetch failed: {actRes.StatusCode}", "ERROR");
            return Results.StatusCode(502);
        }

        var act = await actRes.Content.ReadFromJsonAsync<JsonElement>();
        var desc = act.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        
        if (!desc.Contains("---CACHE_START---") || !desc.Contains("---CACHE_END---")) {
            AddLog("PROFILE_ERR: Cache markers missing in activity description.", "ERROR");
            return Results.BadRequest(new { error = "Malformed system cache on Strava." });
        }
        
        var jsonStr = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0].Trim();
        var profileNode = JsonNode.Parse(jsonStr);
        
        if (profileNode == null) throw new Exception("Parsed Profile is null");

        profileNode["lastUpdated"] = GetCetTimestamp();
        return Results.Ok(profileNode);
    } catch (Exception ex) {
        AddLog($"PROFILE_FATAL: {ex.Message}", "ERROR");
        return Results.Json(new { error = "Internal Server Error during profile retrieval.", details = ex.Message }, statusCode: 500);
    }
});

app.MapPost("/audit", async (string? since, IHttpClientFactory clientFactory) => {
    AddLog("AUDIT_ENGINE: Triggering full spectrum physiological audit.");
    _ = Task.Run(async () => {
        try {
            var apiKey = GetEnv("API_KEY");
            if (string.IsNullOrEmpty(apiKey)) apiKey = GetEnv("GEMINI_API_KEY");
            if (string.IsNullOrEmpty(apiKey)) { AddLog("AUTH_ERR: Gemini Key missing.", "ERROR"); return; }

            using var stravaClient = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(stravaClient);
            if (token == null) { AddLog("AUDIT_ERR: Token retrieval failed.", "ERROR"); return; }
            stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var allActivities = new List<JsonElement>();
            int page = 1;
            var sinceTs = DateTimeOffset.Parse(since ?? "2020-01-01").ToUnixTimeSeconds();
            
            AddLog($"AUDIT_STEP: Crawling Strava after {sinceTs}...");
            while(page <= 20) {
                var res = await stravaClient.GetAsync($"https://www.strava.com/api/v3/athlete/activities?per_page=200&page={page}&after={sinceTs}");
                if (!res.IsSuccessStatusCode) break;
                var batch = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
                if (batch == null || batch.Count == 0) break;
                allActivities.AddRange(batch);
                if (batch.Count < 200) break;
                page++;
            }

            var cleanData = allActivities.Select(a => {
                a.TryGetProperty("type", out var t);
                a.TryGetProperty("distance", out var d);
                a.TryGetProperty("moving_time", out var m);
                a.TryGetProperty("start_date", out var dt);
                a.TryGetProperty("average_heartrate", out var hr);
                return new {
                    t = t.GetString() ?? "Unknown",
                    d = (d.ValueKind != JsonValueKind.Undefined ? d.GetDouble() : 0) / 1000.0,
                    m = (m.ValueKind != JsonValueKind.Undefined ? m.GetInt32() : 0),
                    hr = hr.ValueKind != JsonValueKind.Undefined ? hr.GetDouble() : 0,
                    dt = dt.GetString() ?? ""
                };
            }).ToList();

            var raceType = GetEnv("GOAL_RACE_TYPE") ?? "Marathon";
            var raceDate = GetEnv("GOAL_RACE_DATE") ?? DateTime.UtcNow.AddMonths(3).ToString("yyyy-MM-dd");
            var raceTime = GetEnv("GOAL_RACE_TIME") ?? "3:30:00";

            var prompt = $@"ROLE: Professional Marathon/Ultramarathon Coach.
TASK: Analyze history and generate profile.
GOAL: {raceType} on {raceDate} ({raceTime}).

OUTPUT: Strict JSON matching the 'AthleteProfile' interface.
MILESTONES: Identified by fuzzy logic (+/- 2km).
CALENDAR: RUN EVERY DAY. 2-3x Gym. Next workout at top of plan.

DATA: {JsonSerializer.Serialize(cleanData)}";

            using var aiClient = clientFactory.CreateClient("GeminiClient");
            AddLog("AUDIT_STEP: Transmitting to Gemini 3 Flash...");
            var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new { 
                contents = new[] { new { parts = new[] { new { text = prompt } } } } 
            });
            
            if (!aiRes.IsSuccessStatusCode) {
                var errBody = await aiRes.Content.ReadAsStringAsync();
                AddLog($"AI_FAIL: {aiRes.StatusCode} - {errBody}", "ERROR");
                return;
            }
            
            var aiResJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
            var aiText = aiResJson.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            
            if (aiText.Contains("```json")) aiText = aiText.Split("```json")[1].Split("```")[0].Trim();
            else if (aiText.Contains("```")) aiText = aiText.Split("```")[1].Split("```")[0].Trim();

            var cacheId = await GetCachedSystemActivityId(stravaClient);
            var stravaQuota = new { dailyUsed = 10, dailyLimit = 1000, minuteUsed = 0, minuteLimit = 15, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") };
            var geminiQuota = new { dailyUsed = 1, dailyLimit = 1500, minuteUsed = 0, minuteLimit = 15, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") };
            
            var profileNode = JsonNode.Parse(aiText);
            profileNode!["stravaQuota"] = JsonNode.Parse(JsonSerializer.Serialize(stravaQuota));
            profileNode!["geminiQuota"] = JsonNode.Parse(JsonSerializer.Serialize(geminiQuota));
            
            var finalDesc = $"[StravAI System Cache]\n---CACHE_START---\n{profileNode.ToJsonString()}\n---CACHE_END---\nUpdated: {GetCetTimestamp()}";
            
            if (cacheId == null) {
                await stravaClient.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name="[StravAI] System Cache", type="Run", start_date_local=DateTime.UtcNow.ToString("O"), elapsed_time=1, description=finalDesc, @private=true });
            } else {
                await stravaClient.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cacheId}", new { description = finalDesc });
            }
            
            AddLog("AUDIT_SUCCESS: Intelligence sync finalized.", "SUCCESS");
            _cacheExpiry = DateTime.MinValue; 
        } catch (Exception ex) { AddLog($"AUDIT_FATAL: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

async Task<string?> GetStravaAccessToken(HttpClient client) {
    try {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry) return _cachedToken;
        
        var clientId = GetEnv("STRAVA_CLIENT_ID");
        var clientSecret = GetEnv("STRAVA_CLIENT_SECRET");
        var refreshToken = GetEnv("STRAVA_REFRESH_TOKEN");

        if (string.IsNullOrEmpty(clientId) || string.IsNullOrEmpty(clientSecret) || string.IsNullOrEmpty(refreshToken)) {
            AddLog("GET_TOKEN: Missing Strava Env Variables.", "ERROR");
            return null;
        }

        var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", clientId),
            new KeyValuePair<string, string>("client_secret", clientSecret),
            new KeyValuePair<string, string>("refresh_token", refreshToken),
            new KeyValuePair<string, string>("grant_type", "refresh_token")
        }));

        if (!res.IsSuccessStatusCode) return null;
        var data = await res.Content.ReadFromJsonAsync<JsonElement>();
        _cachedToken = data.GetProperty("access_token").GetString();
        _tokenExpiry = DateTime.UtcNow.AddHours(5);
        return _cachedToken;
    } catch { return null; }
}

async Task<long?> GetCachedSystemActivityId(HttpClient client) {
    try {
        if (_cachedSystemActivityId.HasValue && DateTime.UtcNow < _cacheExpiry) return _cachedSystemActivityId;
        var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
        if (!res.IsSuccessStatusCode) return null;
        var activities = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
        _cacheExpiry = DateTime.UtcNow.AddMinutes(10);
        foreach(var act in activities ?? new()) {
            if (act.TryGetProperty("name", out var name) && name.GetString() == "[StravAI] System Cache") {
                _cachedSystemActivityId = act.GetProperty("id").GetInt64();
                return _cachedSystemActivityId;
            }
        }
    } catch { }
    return null;
}

app.Run();
