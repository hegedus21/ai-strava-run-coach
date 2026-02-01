
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

var logs = new ConcurrentQueue<string>();
long? _cachedSystemActivityId = null;
DateTime _cacheExpiry = DateTime.MinValue;
string? _cachedToken = null;
DateTime _tokenExpiry = DateTime.MinValue;

void AddLog(string message, string level = "INFO") {
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    var logEntry = $"[{timestamp}] [{level}] {message}";
    logs.Enqueue(logEntry);
    while (logs.Count > 100) logs.TryDequeue(out _);
    Console.WriteLine(logEntry); 
}

var config = app.Configuration;
string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val.Trim();
    return config[key]?.Trim() ?? "";
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

// Global Auth Check
app.Use(async (context, next) => {
    var path = context.Request.Path.Value?.ToLower() ?? "";
    if (path == "/" || path == "/health") { await next(); return; }
    var secret = GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";
    if (!context.Request.Headers.TryGetValue("X-StravAI-Secret", out var providedSecret) || providedSecret != secret) {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized.");
        return;
    }
    await next();
});

app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "2.0.0_TMS" }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    using var client = clientFactory.CreateClient();
    var token = await GetStravaAccessToken(client);
    if (token == null) return Results.Unauthorized();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    
    var cacheId = await GetCachedSystemActivityId(client);
    if (cacheId == null) return Results.NotFound("System cache not initialized.");
    
    var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{cacheId}");
    var desc = act.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";
    
    if (!desc.Contains("---CACHE_START---")) return Results.NotFound("Empty cache.");
    
    var jsonStr = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0];
    var profile = JsonNode.Parse(jsonStr);
    
    // Enrich with dynamic quota data
    profile!["lastUpdated"] = GetCetTimestamp();
    return Results.Ok(profile);
});

app.MapPost("/audit", async (string? since, IHttpClientFactory clientFactory) => {
    AddLog("TMS_AUDIT: Commencing full historical mapping.");
    _ = Task.Run(async () => {
        try {
            var apiKey = GetEnv("API_KEY");
            if (string.IsNullOrEmpty(apiKey)) apiKey = GetEnv("GEMINI_API_KEY");
            if (string.IsNullOrEmpty(apiKey)) { AddLog("AUTH_FAIL: Gemini Key missing.", "ERROR"); return; }

            using var stravaClient = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(stravaClient);
            if (token == null) return;
            stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            var allActivities = new List<JsonElement>();
            int page = 1;
            var sinceTs = DateTimeOffset.Parse(since ?? "2020-01-01").ToUnixTimeSeconds();
            
            while(page <= 10) {
                var batch = await stravaClient.GetFromJsonAsync<List<JsonElement>>($"https://www.strava.com/api/v3/athlete/activities?per_page=200&page={page}&after={sinceTs}");
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

            var prompt = $@"ROLE: Pro Endurance Coach. 
TASK: Analyze full history and generate a training plan until {raceDate} for a {raceType}.
RULES:
1. RUN EVERY DAY. No rest days from running.
2. GYM: 2-3 times/week using dumbbells (Specified exercises/sets).
3. CATEGORIES: Run categories must use +/- 2km fuzzy logic. 
   - Backyard Loop: ~6.7km
   - 5k: 3-7km
   - 10k: 8-12km
   - 20k: 18-22km
   - HM: 19-23km
   - Marathon: 40-44km
   - Ultra: >45km
4. PLAN: Generate a daily schedule from today until {raceDate}.
5. OUTPUT: Strict JSON.

DATA: {JsonSerializer.Serialize(cleanData)}";

            using var aiClient = clientFactory.CreateClient("GeminiClient");
            var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new { contents = new[] { new { parts = new[] { new { text = prompt } } } } });
            
            if (!aiRes.IsSuccessStatusCode) { AddLog($"AI_ERR: {aiRes.StatusCode}", "ERROR"); return; }
            
            var aiResJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
            var aiText = aiResJson.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
            
            if (aiText.Contains("```json")) aiText = aiText.Split("```json")[1].Split("```")[0].Trim();
            else if (aiText.Contains("```")) aiText = aiText.Split("```")[1].Split("```")[0].Trim();

            // Save to Cache Activity
            var cacheId = await GetCachedSystemActivityId(stravaClient);
            var finalDesc = $"[StravAI System Cache]\n---CACHE_START---\n{aiText}\n---CACHE_END---\nUpdated: {GetCetTimestamp()}";
            
            if (cacheId == null) {
                await stravaClient.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name="[StravAI] System Cache", type="Run", start_date_local=DateTime.UtcNow.ToString("O"), elapsed_time=1, description=finalDesc, @private=true });
            } else {
                await stravaClient.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cacheId}", new { description = finalDesc });
            }
            
            AddLog("TMS_SYNC: Audit complete. Plan generated.", "SUCCESS");
            _cacheExpiry = DateTime.MinValue; 
        } catch (Exception ex) { AddLog($"AUDIT_FATAL: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

async Task<long?> GetCachedSystemActivityId(HttpClient client) {
    if (_cachedSystemActivityId.HasValue && DateTime.UtcNow < _cacheExpiry) return _cachedSystemActivityId;
    var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
    if (!res.IsSuccessStatusCode) return null;
    var activities = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
    _cacheExpiry = DateTime.UtcNow.AddMinutes(10);
    foreach(var act in activities ?? new()) {
        if (act.GetProperty("name").GetString() == "[StravAI] System Cache") {
            _cachedSystemActivityId = act.GetProperty("id").GetInt64();
            return _cachedSystemActivityId;
        }
    }
    return null;
}

async Task<string?> GetStravaAccessToken(HttpClient client) {
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
}

app.Run();
