
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

// LOGGING SYSTEM
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

string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val.Trim();
    return app.Configuration[key]?.Trim() ?? "";
}

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

app.Use(async (context, next) => {
    var path = context.Request.Path.Value?.ToLower() ?? "";
    if (context.Request.Method == "OPTIONS" || path == "/" || path == "/health") { await next(); return; }
    var secret = GetEnv("STRAVA_VERIFY_TOKEN");
    if (string.IsNullOrEmpty(secret)) secret = "STRAVAI_SECURE_TOKEN"; 
    if (!context.Request.Headers.TryGetValue("X-StravAI-Secret", out var providedSecret) || providedSecret != secret) {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized.");
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "2.1.4_DEBUG" }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client);
        if (token == null) return Results.Json(new { error = "Auth Failed" }, statusCode: 401);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var cacheId = await GetCachedSystemActivityId(client);
        if (cacheId == null) {
            AddLog("PROFILE_ERR: System Cache activity ID not found in Strava feed.", "WARN");
            return Results.NotFound(new { error = "Cache not found." });
        }
        
        var actRes = await client.GetAsync($"https://www.strava.com/api/v3/activities/{cacheId}");
        if (!actRes.IsSuccessStatusCode) {
            AddLog($"PROFILE_ERR: Failed to fetch activity {cacheId}. Status: {actRes.StatusCode}", "ERROR");
            return Results.StatusCode(502);
        }

        var act = await actRes.Content.ReadFromJsonAsync<JsonElement>();
        var desc = act.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
        
        if (!desc.Contains("---CACHE_START---") || !desc.Contains("---CACHE_END---")) {
            AddLog($"PROFILE_ERR: Cache markers missing. Desc Length: {desc.Length}", "ERROR");
            return Results.BadRequest(new { error = "Malformed cache markers." });
        }
        
        var jsonStr = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0].Trim();
        AddLog($"PROFILE_DEBUG: Parsing JSON (Length: {jsonStr.Length}). Start: {jsonStr.Substring(0, Math.Min(20, jsonStr.Length))}...");

        var node = JsonNode.Parse(jsonStr);
        if (node is JsonObject profileObj) {
            profileObj["lastUpdated"] = GetCetTimestamp();
            return Results.Ok(profileObj);
        }
        
        var nodeType = node?.GetType().Name ?? "null";
        AddLog($"PROFILE_ERR: Node is {nodeType}, expected JsonObject. Raw: {jsonStr}", "ERROR");
        return Results.Json(new { error = "Cache structure error", type = nodeType }, statusCode: 500);
    } catch (Exception ex) {
        AddLog($"PROFILE_FATAL: {ex.Message} @ {ex.StackTrace?.Split('\n')[0]}", "ERROR");
        return Results.Json(new { error = "Internal Error", details = ex.Message }, statusCode: 500);
    }
});

app.MapPost("/audit", async (string? since, IHttpClientFactory clientFactory) => {
    AddLog("AUDIT_ENGINE: Triggering full spectrum physiological audit.");
    _ = Task.Run(async () => {
        try {
            var apiKey = GetEnv("API_KEY") ?? GetEnv("GEMINI_API_KEY");
            if (string.IsNullOrEmpty(apiKey)) { AddLog("AUTH_ERR: Gemini Key missing.", "ERROR"); return; }

            using var stravaClient = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(stravaClient);
            if (token == null) return;
            stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var allActivities = new List<JsonElement>();
            int page = 1;
            var sinceTs = DateTimeOffset.Parse(since ?? "2020-01-01").ToUnixTimeSeconds();
            
            AddLog($"AUDIT_STEP: Crawling activities since {sinceTs}...");
            while(page <= 25) {
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
                    hr = (hr.ValueKind != JsonValueKind.Undefined ? hr.GetDouble() : 0),
                    dt = dt.GetString() ?? ""
                };
            }).ToList();

            var raceType = GetEnv("GOAL_RACE_TYPE") ?? "Marathon";
            var raceDate = GetEnv("GOAL_RACE_DATE") ?? DateTime.UtcNow.AddMonths(3).ToString("yyyy-MM-dd");
            var raceTime = GetEnv("GOAL_RACE_TIME") ?? "3:30:00";

            var prompt = $@"ROLE: Elite Marathon Coach. Return a JSON AthleteProfile.
GOAL: {raceType} on {raceDate} (Target: {raceTime}).
DATA: {JsonSerializer.Serialize(cleanData.Take(500))}";

            using var aiClient = clientFactory.CreateClient("GeminiClient");
            AddLog("AUDIT_STEP: Querying Gemini 3 Flash...");
            var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new { 
                contents = new[] { new { parts = new[] { new { text = prompt } } } } 
            });
            
            if (!aiRes.IsSuccessStatusCode) {
                AddLog($"AI_FAIL: {aiRes.StatusCode}", "ERROR");
                return;
            }
            
            var aiResJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
            var aiText = aiResJson.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            
            // Refined JSON Extraction
            int jsonStart = aiText.IndexOf("{");
            int jsonEnd = aiText.LastIndexOf("}");
            if (jsonStart != -1 && jsonEnd != -1 && jsonEnd > jsonStart) {
                aiText = aiText.Substring(jsonStart, jsonEnd - jsonStart + 1);
            }

            try {
                var node = JsonNode.Parse(aiText);
                if (node is JsonObject profileObj) {
                    var stravaQuota = new { dailyUsed = 25, dailyLimit = 1000, minuteUsed = 0, minuteLimit = 15, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") };
                    var geminiQuota = new { dailyUsed = 1, dailyLimit = 1500, minuteUsed = 0, minuteLimit = 15, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") };
                    
                    profileObj["stravaQuota"] = JsonNode.Parse(JsonSerializer.Serialize(stravaQuota));
                    profileObj["geminiQuota"] = JsonNode.Parse(JsonSerializer.Serialize(geminiQuota));
                    
                    var jsonToSave = profileObj.ToJsonString();
                    var finalDesc = $"[StravAI System Cache]\n---CACHE_START---\n{jsonToSave}\n---CACHE_END---\nUpdated: {GetCetTimestamp()}";
                    
                    AddLog($"AUDIT_DEBUG: Saving JSON (Length: {jsonToSave.Length}) to Strava.");
                    
                    var cacheId = await GetCachedSystemActivityId(stravaClient);
                    if (cacheId == null) {
                        await stravaClient.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name="[StravAI] System Cache", type="Run", start_date_local=DateTime.UtcNow.ToString("O"), elapsed_time=1, description=finalDesc, @private=true });
                    } else {
                        var updateRes = await stravaClient.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cacheId}", new { description = finalDesc });
                        if (!updateRes.IsSuccessStatusCode) AddLog($"AUDIT_ERR: Strava Save Failed: {updateRes.StatusCode}", "ERROR");
                    }
                    AddLog("AUDIT_SUCCESS: Sync complete.", "SUCCESS");
                } else {
                    AddLog($"AUDIT_FAIL: AI returned {node?.GetType().Name} instead of Object.", "ERROR");
                }
            } catch (Exception parseEx) {
                AddLog($"AUDIT_FAIL: JSON Parse Error: {parseEx.Message}. Raw: {aiText.Substring(0, Math.Min(50, aiText.Length))}", "ERROR");
            }
            _cacheExpiry = DateTime.MinValue; 
        } catch (Exception ex) { AddLog($"AUDIT_FATAL: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

async Task<string?> GetStravaAccessToken(HttpClient client) {
    try {
        if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry) return _cachedToken;
        var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
            new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
            new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN")),
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
