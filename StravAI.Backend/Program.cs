
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json.Nodes;

// ENCAPSULATED STATE TO PREVENT SCOPING ISSUES (Fixes CS0103)
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

app.Use(async (context, next) =>
{
    var path = context.Request.Path.Value?.ToLower() ?? "";
    if (context.Request.Method == "OPTIONS" || path == "/" || path == "/health") { await next(); return; }
    var secret = GetEnv("STRAVA_VERIFY_TOKEN");
    if (string.IsNullOrEmpty(secret)) secret = "STRAVAI_SECURE_TOKEN";
    if (!context.Request.Headers.TryGetValue("X-StravAI-Secret", out var providedSecret) || providedSecret != secret)
    {
        context.Response.StatusCode = 401;
        return;
    }
    await next();
});

// ENDPOINTS
app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "2.1.6_STABLE" }));
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
        if (cacheId == null) return Results.NotFound(new { error = "No Cache Activity" });

        var actRes = await client.GetAsync($"https://www.strava.com/api/v3/activities/{cacheId}");
        if (!actRes.IsSuccessStatusCode) return Results.StatusCode(502);

        var act = await actRes.Content.ReadFromJsonAsync<JsonElement>();
        var desc = act.TryGetProperty("description", out var dProp) ? dProp.GetString() ?? "" : "";

        if (!desc.Contains("---CACHE_START---") || !desc.Contains("---CACHE_END---"))
        {
            SystemState.AddLog("PROFILE_ERR: Cache markers corrupted or missing.", "ERROR");
            return Results.BadRequest(new { error = "Malformed markers" });
        }

        var jsonStr = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0].Trim();
        jsonStr = jsonStr.Trim('\uFEFF', '\u200B');

        try
        {
            var node = JsonNode.Parse(jsonStr);
            if (node is JsonObject profileObj)
            {
                profileObj["lastUpdated"] = GetCetTimestamp();
                return Results.Ok(profileObj);
            }

            SystemState.AddLog($"PROFILE_ERR: Parsed node is {node?.GetValueKind()}, expected Object. Raw: {jsonStr.Substring(0, Math.Min(100, jsonStr.Length))}", "ERROR");
            return Results.Json(new { error = "Invalid Cache Type", kind = node?.GetValueKind().ToString() }, statusCode: 500);
        }
        catch (JsonException jex)
        {
            SystemState.AddLog($"PROFILE_ERR: JSON Syntax Error: {jex.Message}", "ERROR");
            return Results.Json(new { error = "JSON Parse Failed", details = jex.Message }, statusCode: 500);
        }
    }
    catch (Exception ex)
    {
        SystemState.AddLog($"PROFILE_FATAL: {ex.Message}", "ERROR");
        return Results.Json(new { error = "Internal Error" }, statusCode: 500);
    }
});

app.MapPost("/audit", async (string? since, IHttpClientFactory clientFactory) =>
{
    SystemState.AddLog("AUDIT_ENGINE: Triggering full spectrum physiological audit.");
    _ = Task.Run(async () =>
    {
        try
        {
            var apiKey = GetEnv("API_KEY");
            if (string.IsNullOrEmpty(apiKey)) apiKey = GetEnv("GEMINI_API_KEY");
            
            if (string.IsNullOrEmpty(apiKey))
            {
                SystemState.AddLog("AUTH_ERR: API_KEY/GEMINI_API_KEY is missing in environment.", "ERROR");
                return;
            }

            using var stravaClient = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(stravaClient);
            if (token == null) return;
            stravaClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            var allActivities = new List<JsonElement>();
            int page = 1;
            var sinceTs = DateTimeOffset.Parse(since ?? "2020-01-01").ToUnixTimeSeconds();

            SystemState.AddLog("AUDIT_STEP: Crawling training history...");
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

            var prompt = $@"ROLE: Elite Performance Running Coach.
TASK: Analyze the training history and return a JSON AthleteProfile. 
GOAL: {raceType} on {raceDate} (Target: {raceTime}).
REQUIRED_JSON_STRUCTURE:
{{
  ""summary"": ""...string..."",
  ""coachNotes"": ""...string..."",
  ""milestones"": {{
    ""backyardLoop"": {{ ""count"": 0, ""pb"": ""00:00"" }},
    ""fiveK"": {{ ""count"": 0, ""pb"": ""00:00"" }},
    ""tenK"": {{ ""count"": 0, ""pb"": ""00:00"" }},
    ""twentyK"": {{ ""count"": 0, ""pb"": ""00:00"" }},
    ""halfMarathon"": {{ ""count"": 0, ""pb"": ""00:00"" }},
    ""marathon"": {{ ""count"": 0, ""pb"": ""00:00"" }},
    ""ultra"": {{ ""count"": 0, ""pb"": ""00:00"" }},
    ""other"": {{ ""count"": 0, ""pb"": ""00:00"" }}
  }},
  ""triathlon"": {{ ""sprint"": 0, ""olympic"": 0, ""halfIronman"": 0, ""ironman"": 0 }},
  ""periodic"": {{
    ""week"": {{ ""distanceKm"": 0.0 }},
    ""month"": {{ ""distanceKm"": 0.0 }}
  }},
  ""trainingPlan"": [
    {{ ""date"": ""YYYY-MM-DD"", ""type"": ""Easy"", ""title"": ""..."", ""description"": ""..."" }}
  ]
}}
DATA_TO_ANALYZE: {JsonSerializer.Serialize(cleanData.Take(500))}";

            using var aiClient = clientFactory.CreateClient("GeminiClient");
            var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new
            {
                contents = new[] { new { parts = new[] { new { text = prompt } } } }
            });

            if (!aiRes.IsSuccessStatusCode)
            {
                SystemState.AddLog($"AI_ERR: Status {aiRes.StatusCode}", "ERROR");
                return;
            }

            var aiResJson = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
            var aiText = aiResJson.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";

            int jsonStart = aiText.IndexOf("{");
            int jsonEnd = aiText.LastIndexOf("}");
            if (jsonStart != -1 && jsonEnd != -1) aiText = aiText.Substring(jsonStart, jsonEnd - jsonStart + 1);

            var node = JsonNode.Parse(aiText);
            if (node is JsonObject profileObj)
            {
                var stravaQuota = new { dailyUsed = 30, dailyLimit = 1000, minuteUsed = 0, minuteLimit = 15, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") };
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
                SystemState.AddLog("AUDIT_SUCCESS: System Intelligence updated and cached.", "SUCCESS");
            }
            else
            {
                SystemState.AddLog($"AUDIT_FAIL: AI Kind {node?.GetValueKind()}, not Object.", "ERROR");
            }
            SystemState.CacheExpiry = DateTime.MinValue;
        }
        catch (Exception ex)
        {
            SystemState.AddLog($"AUDIT_FATAL: {ex.Message}", "ERROR");
        }
    });
    return Results.Accepted();
});

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

app.Run();
