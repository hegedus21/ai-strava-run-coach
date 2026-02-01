
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

// ENDPOINTS
app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "2.1.7_DEBUG_FIX" }));
app.MapGet("/logs", () => Results.Ok(SystemState.Logs.ToArray()));

app.MapGet("/profile", async (IHttpClientFactory clientFactory) =>
{
    try
    {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client);
        if (token == null) return Results.StatusCode(401);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var cacheId = await GetCachedSystemActivityId(client);
        if (cacheId == null) return Results.NotFound(new { error = "No Cache Activity Found" });

        var actRes = await client.GetAsync($"https://www.strava.com/api/v3/activities/{cacheId}");
        if (!actRes.IsSuccessStatusCode) return Results.StatusCode(502);

        var act = await actRes.Content.ReadFromJsonAsync<JsonElement>();
        var desc = act.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";

        if (!desc.Contains("---CACHE_START---") || !desc.Contains("---CACHE_END---"))
        {
            SystemState.AddLog($"PROFILE_ERR: Markers missing. Desc size: {desc.Length}", "ERROR");
            return Results.BadRequest(new { error = "Malformed markers" });
        }

        var jsonStr = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0].Trim();
        jsonStr = jsonStr.Trim('\uFEFF', '\u200B'); // Remove BOM and zero-width spaces

        SystemState.AddLog($"PROFILE_DEBUG: Raw JSON Start: {jsonStr.Substring(0, Math.Min(50, jsonStr.Length))}");

        try
        {
            var node = JsonNode.Parse(jsonStr);
            if (node == null) 
            {
                SystemState.AddLog("PROFILE_ERR: JsonNode.Parse returned null.", "ERROR");
                return Results.Json(new { error = "Null Node" }, statusCode: 500);
            }

            // Diagnostic: Log what we actually got
            var kind = node.GetValueKind();
            SystemState.AddLog($"PROFILE_DEBUG: Node Type: {node.GetType().Name}, ValueKind: {kind}");

            if (node is JsonObject profileObj)
            {
                // Logic check: If the AI nested everything under a key like "athlete_status"
                if (profileObj.Count == 1 && (profileObj.ContainsKey("athlete_status") || profileObj.ContainsKey("profile")))
                {
                    SystemState.AddLog("PROFILE_DEBUG: Detected nested root object. Flattening...");
                    var firstKey = profileObj.First().Key;
                    if (profileObj[firstKey] is JsonObject nested)
                    {
                        profileObj = nested;
                    }
                }

                profileObj["lastUpdated"] = GetCetTimestamp();
                return Results.Ok(profileObj);
            }

            SystemState.AddLog($"PROFILE_ERR: Expected JsonObject but got {kind}. Content: {jsonStr.Substring(0, Math.Min(200, jsonStr.Length))}", "ERROR");
            return Results.Json(new { error = "Invalid Cache Structure", kind = kind.ToString() }, statusCode: 500);
        }
        catch (JsonException jex)
        {
            SystemState.AddLog($"PROFILE_ERR: JSON Syntax: {jex.Message}. Offset: {jex.BytePositionInLine}", "ERROR");
            return Results.Json(new { error = "JSON Parse Failed", details = jex.Message }, statusCode: 500);
        }
    }
    catch (Exception ex)
    {
        SystemState.AddLog($"PROFILE_FATAL: {ex.GetType().Name}: {ex.Message}", "ERROR");
        return Results.Json(new { error = "Internal Error", type = ex.GetType().Name }, statusCode: 500);
    }
});

app.MapPost("/audit", async (string? since, IHttpClientFactory clientFactory) =>
{
    SystemState.AddLog("AUDIT_ENGINE: Starting deep physiological audit sequence.");
    _ = Task.Run(async () =>
    {
        try
        {
            var apiKey = GetEnv("GEMINI_API_KEY");
            if (string.IsNullOrEmpty(apiKey))
            {
                SystemState.AddLog("AUTH_ERR: No Gemini API Key provided in environment.", "ERROR");
                return;
            }

            using var stravaClient = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(stravaClient);
            if (token == null) return;
            stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var allActivities = new List<JsonElement>();
            int page = 1;
            var sinceTs = DateTimeOffset.Parse(since ?? "2020-01-01").ToUnixTimeSeconds();

            SystemState.AddLog("AUDIT_STEP: Syncing activities from Strava Cloud...");
            while (page <= 25)
            {
                var res = await stravaClient.GetAsync($"https://www.strava.com/api/v3/athlete/activities?per_page=200&page={page}&after={sinceTs}");
                if (!res.IsSuccessStatusCode) break;
                var batch = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
                if (batch == null || batch.Count == 0) break;
                allActivities.AddRange(batch);
                if (batch.Count < 200) break;
                page++;
            }

            var cleanData = allActivities.Select(a =>
            {
                a.TryGetProperty("type", out var t);
                a.TryGetProperty("distance", out var d);
                a.TryGetProperty("moving_time", out var m);
                a.TryGetProperty("start_date", out var dt);
                a.TryGetProperty("average_heartrate", out var hr);
                return new
                {
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

            var prompt = $@"ROLE: Elite Marathon Coach. Analyze the training data and return a JSON AthleteProfile. 
GOAL: {raceType} on {raceDate} (Target: {raceTime}).
RULES: Return ONLY the JSON object. Do not nest it inside other keys.
DATA: {JsonSerializer.Serialize(cleanData.Take(500))}";

            using var aiClient = clientFactory.CreateClient("GeminiClient");
            var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } }
            });

            if (!aiRes.IsSuccessStatusCode)
            {
                SystemState.AddLog($"AI_ERR: API responded with status {aiRes.StatusCode}", "ERROR");
                return;
            }

            var aiResJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
            var aiText = aiResJson.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

            // Precise JSON Extraction
            int jsonStart = aiText.IndexOf("{");
            int jsonEnd = aiText.LastIndexOf("}");
            if (jsonStart != -1 && jsonEnd != -1) aiText = aiText.Substring(jsonStart, jsonEnd - jsonStart + 1);

            var node = JsonNode.Parse(aiText);
            if (node is JsonObject profileObj)
            {
                // Quota Simulation
                var stravaQuota = new { dailyUsed = 35, dailyLimit = 1000, minuteUsed = 0, minuteLimit = 15, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") };
                var geminiQuota = new { dailyUsed = 1, dailyLimit = 1500, minuteUsed = 0, minuteLimit = 15, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") };

                profileObj["stravaQuota"] = JsonNode.Parse(JsonSerializer.Serialize(stravaQuota));
                profileObj["geminiQuota"] = JsonNode.Parse(JsonSerializer.Serialize(geminiQuota));

                var finalDesc = $"[StravAI System Cache]\n---CACHE_START---\n{profileObj.ToJsonString()}\n---CACHE_END---\nUpdated: {GetCetTimestamp()}";

                var cacheId = await GetCachedSystemActivityId(stravaClient);
                if (cacheId == null)
                {
                    await stravaClient.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name = "[StravAI] System Cache", type = "Run", start_date_local = DateTime.UtcNow.ToString("O"), elapsed_time = 1, description = finalDesc, @private = true });
                }
                else
                {
                    await stravaClient.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cacheId}", new { description = finalDesc });
                }
                SystemState.AddLog("AUDIT_SUCCESS: Intelligence cache updated successfully.", "SUCCESS");
            }
            else
            {
                SystemState.AddLog($"AUDIT_ERR: AI output type was {node?.GetValueKind()}, not Object.", "ERROR");
            }
            SystemState.CacheExpiry = DateTime.MinValue; // Invalidate cache to force reload
        }
        catch (Exception ex)
        {
            SystemState.AddLog($"AUDIT_FATAL: {ex.Message}", "ERROR");
        }
    });
    return Results.Accepted();
});

app.Run();

// HELPERS (Static Methods)
async Task<string?> GetStravaAccessToken(HttpClient client)
{
    try
    {
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
    catch { return null; }
}

async Task<long?> GetCachedSystemActivityId(HttpClient client)
{
    try
    {
        if (SystemState.CachedSystemActivityId.HasValue && DateTime.UtcNow < SystemState.CacheExpiry) return SystemState.CachedSystemActivityId;
        var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
        if (!res.IsSuccessStatusCode) return null;
        var activities = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
        SystemState.CacheExpiry = DateTime.UtcNow.AddMinutes(10);
        foreach (var act in activities ?? new())
        {
            if (act.TryGetProperty("name", out var name) && name.GetString() == "[StravAI] System Cache")
            {
                SystemState.CachedSystemActivityId = act.GetProperty("id").GetInt64();
                return SystemState.CachedSystemActivityId;
            }
        }
    }
    catch { }
    return null;
}

string GetEnv(string key)
{
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val.Trim();
    return app.Configuration[key]?.Trim() ?? "";
}

string GetCetTimestamp()
{
    try
    {
        var tzi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time")
            : TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tzi).ToString("dd/MM/yyyy HH:mm:ss");
    }
    catch
    {
        return DateTime.UtcNow.AddHours(1).ToString("dd/MM/yyyy HH:mm:ss");
    }
}

// TYPES & STATE
public static class SystemState
{
    public static long? CachedSystemActivityId { get; set; }
    public static DateTime CacheExpiry { get; set; } = DateTime.MinValue;
    public static string? CachedToken { get; set; }
    public static DateTime TokenExpiry { get; set; } = DateTime.MinValue;
    public static ConcurrentQueue<string> Logs { get; } = new();

    public static void AddLog(string message, string level = "INFO")
    {
        var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        var logEntry = $"[{timestamp}] [{level}] {message}";
        Logs.Enqueue(logEntry);
        while (Logs.Count > 100) Logs.TryDequeue(out _);
        Console.WriteLine(logEntry);
    }
}
