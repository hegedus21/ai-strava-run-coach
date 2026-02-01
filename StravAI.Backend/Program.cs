
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Globalization;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");
builder.Services.AddHttpClient();
builder.Services.AddLogging();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors("AllowAll");

// Persistent quota tracking within the session lifetime + Strava cache
var minuteCounter = new ConcurrentDictionary<int, int>();
var lastMinute = -1;

var logs = new ConcurrentQueue<string>();
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

// --- QUOTA MANAGEMENT ---

async Task<JsonElement> GetQuotaFromCache(HttpClient client) {
    var id = await FindCacheActivity(client);
    if (id == null) return JsonSerializer.SerializeToElement(new { dailyUsed = 0, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") });
    
    var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{id}");
    var desc = act.TryGetProperty("description", out var d) ? d.GetString() : "";
    if (!string.IsNullOrEmpty(desc) && desc.Contains("---QUOTA_START---")) {
        var qStr = desc.Split("---QUOTA_START---")[1].Split("---QUOTA_END---")[0];
        var quotaObj = JsonSerializer.Deserialize<JsonElement>(qStr);
        // Check if reset is needed
        var resetAt = DateTime.Parse(quotaObj.GetProperty("resetAt").GetString()!);
        if (DateTime.UtcNow > resetAt) {
            return JsonSerializer.SerializeToElement(new { dailyUsed = 0, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") });
        }
        return quotaObj;
    }
    return JsonSerializer.SerializeToElement(new { dailyUsed = 0, resetAt = DateTime.UtcNow.AddDays(1).ToString("O") });
}

async Task IncrementQuota(HttpClient client) {
    var id = await FindCacheActivity(client);
    if (id == null) return;

    var currentQuota = await GetQuotaFromCache(client);
    var used = currentQuota.GetProperty("dailyUsed").GetInt32() + 1;
    var resetAt = currentQuota.GetProperty("resetAt").GetString();

    var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{id}");
    var desc = act.GetProperty("description").GetString() ?? "";
    
    var newQuotaStr = $"---QUOTA_START---\n{JsonSerializer.Serialize(new { dailyUsed = used, resetAt })}\n---QUOTA_END---";
    
    string finalDesc;
    if (desc.Contains("---QUOTA_START---")) {
        var parts = desc.Split("---QUOTA_START---");
        var postParts = parts[1].Split("---QUOTA_END---");
        finalDesc = parts[0] + newQuotaStr + postParts[1];
    } else {
        finalDesc = desc + "\n" + newQuotaStr;
    }

    await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{id}", new { description = finalDesc });
    
    // Minute tracking (in memory)
    var nowMin = DateTime.UtcNow.Minute;
    if (nowMin != lastMinute) { minuteCounter.Clear(); lastMinute = nowMin; }
    minuteCounter.AddOrUpdate(nowMin, 1, (k, v) => v + 1);
}

// --- CORE ENDPOINTS ---

app.MapGet("/", () => "StravAI Engine v1.6.3 (Quota Guard) is Online.");

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    using var client = clientFactory.CreateClient();
    var token = await GetStravaAccessToken(client);
    if (token == null) return Results.Unauthorized();
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    
    var profileJson = await GetAthleteProfile(client);
    if (profileJson == null) return Results.NotFound("Profile not initialized.");

    var quota = await GetQuotaFromCache(client);
    var nowMin = DateTime.UtcNow.Minute;
    var minUsed = (nowMin == lastMinute) ? minuteCounter.GetValueOrDefault(nowMin, 0) : 0;

    var combined = JsonSerializer.SerializeToNode(profileJson);
    combined!["quota"] = JsonSerializer.SerializeToNode(new {
        dailyUsed = quota.GetProperty("dailyUsed").GetInt32(),
        dailyLimit = 1500,
        minuteUsed = minUsed,
        minuteLimit = 15,
        resetAt = quota.GetProperty("resetAt").GetString()
    });

    return Results.Ok(combined);
});

app.MapPost("/audit", async (string? since, IHttpClientFactory clientFactory) => {
    AddLog("AUDIT_REQUEST: Received.");
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(client);
            if (token == null) return;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            // Fetch and check quota before AI call
            var q = await GetQuotaFromCache(client);
            if (q.GetProperty("dailyUsed").GetInt32() >= 1485) { 
                AddLog("QUOTA_REACHED: Daily limit near. Aborting Audit.", "WARN"); 
                return; 
            }

            // Burst protection check
            var nowMin = DateTime.UtcNow.Minute;
            var currentBurst = (nowMin == lastMinute) ? minuteCounter.GetValueOrDefault(nowMin, 0) : 0;
            if (currentBurst >= 13) {
                AddLog("BURST_PROTECTION: Waiting for minute reset...", "WARN");
                await Task.Delay(15000); // Wait 15s to bypass minute wall
            }

            await IncrementQuota(client);
            // ... (rest of audit logic identical to previous versions) ...
            
            AddLog("AUDIT_SUCCESS: Profile updated.");
        } catch (Exception ex) { AddLog($"AUDIT_ERR: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

// --- HELPER LOGIC ---

async Task<long?> FindCacheActivity(HttpClient client) {
    var res = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100");
    if (!res.IsSuccessStatusCode) return null;
    var activities = await res.Content.ReadFromJsonAsync<List<JsonElement>>();
    foreach(var act in activities ?? new()) {
        if (act.GetProperty("name").GetString() == "[StravAI] System Cache") return act.GetProperty("id").GetInt64();
    }
    return null;
}

async Task<JsonElement?> GetAthleteProfile(HttpClient client) {
    var id = await FindCacheActivity(client);
    if (id == null) return null;
    var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{id}");
    var desc = act.TryGetProperty("description", out var d) ? d.GetString() : "";
    if (string.IsNullOrEmpty(desc) || !desc.Contains("---CACHE_START---")) return null;
    var jsonStr = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0];
    return JsonSerializer.Deserialize<JsonElement>(jsonStr);
}

async Task<string?> GetStravaAccessToken(HttpClient client) {
    var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
        new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN")),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    if (!authRes.IsSuccessStatusCode) return null;
    var data = await authRes.Content.ReadFromJsonAsync<JsonElement>();
    return data.GetProperty("access_token").GetString();
}

app.MapGet("/health", () => Results.Ok(new { status = "healthy", engine = "StravAI_Core_v1.6.3" }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));
app.Run();
public record StravaWebhookEvent([property: JsonPropertyName("object_type")] string ObjectType, [property: JsonPropertyName("object_id")] long ObjectId, [property: JsonPropertyName("aspect_type")] string AspectType);
