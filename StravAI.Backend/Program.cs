
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
builder.Services.AddLogging();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors("AllowAll");

// --- CACHE & QUOTA GLOBALS ---
var minuteCounter = new ConcurrentDictionary<int, int>();
var lastMinute = -1;
var logs = new ConcurrentQueue<string>();

List<JsonElement>? _activitiesCache = null;
long? _cachedSystemActivityId = null;
DateTime _cacheExpiry = DateTime.MinValue;
string? _cachedToken = null;
DateTime _tokenExpiry = DateTime.MinValue;

void AddLog(string message, string level = "INFO") {
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    var logEntry = $"[{timestamp}] [{level}] {message}";
    logs.Enqueue(logEntry);
    while (logs.Count > 200) logs.TryDequeue(out _);
    Console.WriteLine(logEntry); 
}

var config = app.Configuration;
string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val;
    return config[key] ?? "";
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
    if (path == "/" || path == "/health" || path == "/webhook") { await next(); return; }
    var secret = GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";
    if (!context.Request.Headers.TryGetValue("X-StravAI-Secret", out var providedSecret) || providedSecret != secret) {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized.");
        return;
    }
    await next();
});

// --- CACHE & QUOTA HELPERS ---

async Task<long?> GetCachedSystemActivityId(HttpClient client) {
    if (_cachedSystemActivityId.HasValue && DateTime.UtcNow < _cacheExpiry) return _cachedSystemActivityId;
    
    var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
    if (!res.IsSuccessStatusCode) return _cachedSystemActivityId;

    var activities = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
    _cacheExpiry = DateTime.UtcNow.AddMinutes(10);
    
    foreach(var act in activities ?? new()) {
        if (act.TryGetProperty("name", out var name) && name.GetString() == "[StravAI] System Cache") {
            _cachedSystemActivityId = act.GetProperty("id").GetInt64();
            return _cachedSystemActivityId;
        }
    }
    return null;
}

// --- CORE ENDPOINTS ---

app.MapGet("/health", () => Results.Ok(new { status = "healthy", engine = "StravAI_v1.7.3_Hardened" }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    using var client = clientFactory.CreateClient();
    var token = await GetStravaAccessToken(client);
    if (token == null) return Results.Unauthorized();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    
    var id = await GetCachedSystemActivityId(client);
    if (id == null) return Results.NotFound("System cache activity not found.");
    
    var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{id}");
    var desc = act.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
    
    if (!desc.Contains("---CACHE_START---")) return Results.NotFound("Cache content missing.");
    
    var jsonStr = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0];
    var profile = JsonNode.Parse(jsonStr);
    
    JsonNode? quotaNode = null;
    if (desc.Contains("---QUOTA_START---")) {
        var qStr = desc.Split("---QUOTA_START---")[1].Split("---QUOTA_END---")[0];
        try { quotaNode = JsonNode.Parse(qStr); } catch { }
    }
    
    if (quotaNode == null) {
        quotaNode = JsonNode.Parse(JsonSerializer.Serialize(new { dailyUsed = 0, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") }));
    }
    
    var nowMin = DateTime.UtcNow.Minute;
    profile!["quota"] = JsonNode.Parse(JsonSerializer.Serialize(new {
        dailyUsed = quotaNode?["dailyUsed"]?.GetValue<int>() ?? 0,
        dailyLimit = 1500,
        minuteUsed = (nowMin == lastMinute) ? minuteCounter.GetValueOrDefault(nowMin, 0) : 0,
        minuteLimit = 15,
        resetAt = quotaNode?["resetAt"]?.GetValue<string>() ?? DateTime.UtcNow.AddDays(1).ToString("O")
    }));
    
    profile!["lastUpdated"] = GetCetTimestamp();
    return Results.Ok(profile);
});

app.MapPost("/audit", async (string? since, IHttpClientFactory clientFactory) => {
    AddLog("AUDIT_INIT: Hardening dataset...");
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(client);
            if (token == null) return;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var allActivities = new List<JsonElement>();
            int page = 1;
            int maxPages = 8;
            var sinceTs = DateTimeOffset.Parse(since ?? "2024-01-01").ToUnixTimeSeconds();
            
            AddLog($"CRAWL: Searching for records post-timestamp {sinceTs}.");

            while(page <= maxPages) {
                var batch = await client.GetFromJsonAsync<List<JsonElement>>($"https://www.strava.com/api/v3/athlete/activities?per_page=200&page={page}&after={sinceTs}");
                if (batch == null || batch.Count == 0) break;
                allActivities.AddRange(batch);
                AddLog($"SYNC: Downloaded {batch.Count} records. Total: {allActivities.Count}.");
                if (batch.Count < 200) break;
                page++;
            }

            if (allActivities.Count == 0) {
                AddLog("AUDIT_STOP: No relevant records found.", "WARN");
                return;
            }

            // DEFENSIVE MAPPING: Avoid KeyNotFoundException by checking every property
            AddLog("PRE_PROCESS: Mapping physiological markers...");
            var cleanData = allActivities.Select(a => {
                a.TryGetProperty("type", out var t);
                a.TryGetProperty("distance", out var d);
                a.TryGetProperty("start_date", out var dt);
                a.TryGetProperty("average_heartrate", out var hr);
                return new {
                    t = t.ValueKind != JsonValueKind.Undefined ? t.GetString() : "Unknown",
                    d = d.ValueKind != JsonValueKind.Undefined ? d.GetDouble() : 0,
                    hr = hr.ValueKind != JsonValueKind.Undefined ? hr.GetDouble() : 0,
                    dt = dt.ValueKind != JsonValueKind.Undefined ? dt.GetString() : "1970-01-01"
                };
            }).ToList();

            AddLog($"INTELLIGENCE: Submitting {cleanData.Count} markers to Gemini...");
            var prompt = "Perform physiological aggregate audit. Return strict JSON. DATA: " + JsonSerializer.Serialize(cleanData);
            var apiKey = GetEnv("GEMINI_API_KEY");
            
            var cacheId = await GetCachedSystemActivityId(client);
            var cacheActJson = (cacheId != null ? (await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{cacheId}")) : (JsonElement?)null);
            var cacheDesc = cacheActJson?.TryGetProperty("description", out var cd) == true ? cd.GetString() ?? "" : "";
            
            int currentUsed = 0;
            string resetAtStr = DateTime.UtcNow.AddDays(1).ToString("O");
            if (cacheDesc.Contains("---QUOTA_START---")) {
                try {
                    var qStr = cacheDesc.Split("---QUOTA_START---")[1].Split("---QUOTA_END---")[0];
                    var qObj = JsonSerializer.Deserialize<JsonElement>(qStr);
                    if (qObj.TryGetProperty("dailyUsed", out var u)) currentUsed = u.GetInt32();
                    if (qObj.TryGetProperty("resetAt", out var r)) resetAtStr = r.GetString() ?? resetAtStr;
                } catch { }
            }

            AddLog("AI_INFERENCE: Awaiting analysis...");
            var aiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new { contents = new[] { new { parts = new[] { new { text = prompt } } } } });
            
            if (!aiRes.IsSuccessStatusCode) {
                AddLog($"AI_ERR: HTTP {aiRes.StatusCode}", "ERROR");
                return;
            }

            var aiResJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
            string aiText = "";

            // DEFENSIVE AI PARSING: Search parts for text content
            if (aiResJson.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0) {
                var content = candidates[0].GetProperty("content");
                if (content.TryGetProperty("parts", out var parts)) {
                    foreach (var part in parts.EnumerateArray()) {
                        if (part.TryGetProperty("text", out var textProp)) {
                            aiText = textProp.GetString() ?? "";
                            break;
                        }
                    }
                }
            }

            if (string.IsNullOrWhiteSpace(aiText)) {
                AddLog("AI_ERR: Empty text part in response.", "ERROR");
                return;
            }

            if (aiText.Contains("```json")) aiText = aiText.Split("```json")[1].Split("```")[0].Trim();
            else if (aiText.Contains("```")) aiText = aiText.Split("```")[1].Split("```")[0].Trim();

            currentUsed++;
            var quotaStr = $"---QUOTA_START---\n{JsonSerializer.Serialize(new { dailyUsed = currentUsed, resetAt = resetAtStr })}\n---QUOTA_END---";
            var finalDesc = $"[StravAI System Cache]\n---CACHE_START---\n{aiText}\n---CACHE_END---\n{quotaStr}\nUpdated: {GetCetTimestamp()}";

            if (cacheId == null) {
                await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name="[StravAI] System Cache", type="Run", start_date_local=DateTime.UtcNow.ToString("O"), elapsed_time=1, description=finalDesc, @private=true });
            } else {
                await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cacheId}", new { description = finalDesc });
            }
            
            AddLog("AUDIT_SUCCESS: Dataset Refreshed.");
            _cacheExpiry = DateTime.MinValue; 
        } catch (Exception ex) { 
            AddLog($"AUDIT_FATAL: {ex.Message} (Stack: {ex.StackTrace?.Split('\n')[0]})", "ERROR"); 
        }
    });
    return Results.Accepted();
});

async Task<string?> GetStravaAccessToken(HttpClient client) {
    if (_cachedToken != null && DateTime.UtcNow < _tokenExpiry) return _cachedToken;
    var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
        new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN")),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    if (authRes.IsSuccessStatusCode) {
        var data = await authRes.Content.ReadFromJsonAsync<JsonElement>();
        _cachedToken = data.GetProperty("access_token").GetString();
        _tokenExpiry = DateTime.UtcNow.AddHours(5); 
        return _cachedToken;
    }
    return null;
}

app.Run();
