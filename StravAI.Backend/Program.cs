using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;

var builder = WebApplication.CreateBuilder(args);

// Port configuration
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");

builder.Services.AddHttpClient("", client => {
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddLogging();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS").AllowAnyHeader()));

var logs = new ConcurrentQueue<string>();
builder.Services.AddSingleton(logs);
builder.Services.AddHostedService<SeasonBackgroundWorker>();

var app = builder.Build();
app.UseCors("AllowAll");

// --- Helper Functions ---

string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val;
    if (key == "API_KEY") {
        val = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrEmpty(val)) return val;
    }
    try { return app.Configuration[key] ?? ""; } catch { return ""; }
}

void AddLog(string message, string level = "INFO") {
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    var logEntry = $"[{timestamp}] [{level}] {message}";
    logs.Enqueue(logEntry);
    while (logs.Count > 200) logs.TryDequeue(out _);
    Console.WriteLine(logEntry); 
}

string GetCetTimestamp() {
    try {
        var tzi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
            ? TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time") 
            : TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tzi).ToString("dd/MM/yyyy HH:mm:ss") + " CET";
    } catch { return DateTime.UtcNow.AddHours(1).ToString("dd/MM/yyyy HH:mm:ss") + " CET"; }
}

string CompactSummarize(List<JsonElement> activities) {
    if (activities == null || activities.Count == 0) return "No history found.";
    var grouped = activities
        .Where(a => a.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String && t.GetString() == "Run")
        .GroupBy(a => DateTime.Parse(a.GetProperty("start_date").GetString()!).ToString("yyyy-MM"))
        .OrderByDescending(g => g.Key)
        .Take(6);

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("MONTHLY PERFORMANCE SUMMARY:");
    foreach (var month in grouped) {
        var totalKm = month.Sum(a => a.GetProperty("distance").GetDouble()) / 1000;
        var avgSpeed = month.Average(a => a.GetProperty("average_speed").GetDouble());
        var avgPace = avgSpeed > 0 ? (16.6667 / avgSpeed) : 0;
        sb.AppendLine($"- {month.Key}: {totalKm:F1}km, Avg Pace: {avgPace:F2}m/k");
    }

    sb.AppendLine("\nRECENT EFFORTS:");
    foreach (var a in activities.Take(15)) {
        var date = a.GetProperty("start_date").GetString();
        var dist = a.GetProperty("distance").GetDouble() / 1000;
        var speed = a.GetProperty("average_speed").GetDouble();
        var pace = speed > 0 ? (16.6667 / speed) : 0;
        sb.AppendLine($"- {date}: {dist:F2}km @ {pace:F2}m/k");
    }
    return sb.ToString();
}

// --- Security Middleware (For UI Commands) ---
app.Use(async (context, next) => {
    var path = context.Request.Path.Value ?? "";
    
    // PUBLIC EXEMPTIONS
    if (path == "/" || path == "/health" || path.StartsWith("/webhook")) {
        await next();
        return;
    }

    // Command routes (/logs, /sync, etc) require BACKEND_SECRET
    var expectedSecret = GetEnv("BACKEND_SECRET");
    if (string.IsNullOrEmpty(expectedSecret)) expectedSecret = GetEnv("STRAVA_VERIFY_TOKEN"); // Fallback

    if (string.IsNullOrEmpty(expectedSecret)) {
        context.Response.StatusCode = 500;
        await context.Response.WriteAsync("SERVER_ERROR: Auth credentials not configured on host.");
        return;
    }

    if (!context.Request.Headers.TryGetValue("X-StravAI-Secret", out var receivedSecret) || receivedSecret != expectedSecret) {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("UNAUTHORIZED: Invalid access token.");
        return;
    }

    await next();
});

// --- Routes ---

app.MapGet("/", () => "StravAI Engine v1.3.0_ULTRA_STABLE is Online.");
app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "1.3.0_ULTRA_STABLE" }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

app.MapPost("/sync", (IHttpClientFactory clientFactory) => {
    AddLog("AUTH_ACTION: Starting Intelligent Batch Sync...");
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            var token = await SeasonStrategyEngine.GetStravaAccessToken(client, GetEnv);
            if (token == null) return;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var history = await client.GetFromJsonAsync<List<JsonElement>>("https://www.strava.com/api/v3/athlete/activities?per_page=20");
            foreach (var act in history ?? new()) {
                if (act.TryGetProperty("id", out var id)) {
                    await ProcessActivityAsync(id.GetInt64(), clientFactory, GetEnv, AddLog, GetCetTimestamp, false);
                }
            }
            AddLog("Batch Sync Cycle Completed.", "SUCCESS");
        } catch (Exception ex) { AddLog($"Batch Sync Error: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

app.MapPost("/sync/{id}", (long id, IHttpClientFactory clientFactory) => {
    AddLog($"AUTH_ACTION: Manual Override for Activity {id}.");
    _ = Task.Run(() => ProcessActivityAsync(id, clientFactory, GetEnv, AddLog, GetCetTimestamp, true));
    return Results.Accepted();
});

app.MapPost("/sync/custom-race", ([FromBody] CustomRaceRequest req, IHttpClientFactory clientFactory) => {
    AddLog($"AUTH_ACTION: Custom Race Analysis requested for '{req.Name}'.");
    _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(clientFactory, GetEnv, logs, GetCetTimestamp, CompactSummarize, req));
    return Results.Accepted();
});

app.MapPost("/sync/season", (IHttpClientFactory clientFactory) => {
    AddLog("AUTH_ACTION: Deep Season Strategy Update triggered.");
    _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(clientFactory, GetEnv, logs, GetCetTimestamp, CompactSummarize));
    return Results.Accepted();
});

// --- WEBHOOK HANDLERS ---

// Strava Handshake (Challenge)
app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, [FromQuery(Name = "hub.challenge")] string challenge, [FromQuery(Name = "hub.verify_token")] string verifyToken) => {
    var expectedToken = GetEnv("STRAVA_VERIFY_TOKEN");
    if (string.IsNullOrEmpty(expectedToken)) expectedToken = GetEnv("BACKEND_SECRET"); // Fallback

    if (mode == "subscribe" && verifyToken == expectedToken) {
        AddLog("WEBHOOK_INIT: Strava Verification Handshake Successful.");
        return Results.Ok(new { hub_challenge = challenge });
    }
    
    AddLog($"WEBHOOK_INIT_FAILED: Token Mismatch (Received: {verifyToken}).", "ERROR");
    return Results.Unauthorized();
});

// Strava Event Intake
app.MapPost("/webhook", ([FromBody] StravaWebhookEvent @event, IHttpClientFactory clientFactory) => {
    if (@event.ObjectType == "activity") {
        AddLog($"WEBHOOK_EVENT: New Activity detected (ID: {@event.ObjectId}). Queueing for analysis.");
        _ = Task.Run(() => ProcessActivityAsync(@event.ObjectId, clientFactory, GetEnv, AddLog, GetCetTimestamp));
    }
    return Results.Ok();
});

app.Run();

// --- Core Logic ---

async Task ProcessActivityAsync(long id, IHttpClientFactory clientFactory, Func<string, string> envGetter, Action<string, string> logger, Func<string> timeGetter, bool force = false) {
    try {
        using var client = clientFactory.CreateClient();
        var token = await SeasonStrategyEngine.GetStravaAccessToken(client, envGetter);
        if (token == null) return;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{id}");
        
        if (!force) {
            string description = act.TryGetProperty("description", out var descProp) && descProp.ValueKind == JsonValueKind.String 
                ? descProp.GetString() ?? "" 
                : "";
            
            if (description.Contains("StravAI Report") || description.Contains("[StravAI-Processed]") || description.Contains("StravAI-Processed")) {
                 logger($"Activity {id} already analyzed. Skipping to save tokens.", "INFO");
                 return;
            }
        }

        var history = await client.GetFromJsonAsync<List<JsonElement>>("https://www.strava.com/api/v3/athlete/activities?per_page=10");
        var histSummary = "";
        foreach (var h in history ?? new()) {
            var date = h.GetProperty("start_date").GetString();
            var dist = h.GetProperty("distance").GetDouble() / 1000;
            var speed = h.GetProperty("average_speed").GetDouble();
            var pace = speed > 0 ? (16.6667 / speed) : 0;
            histSummary += $"- {date}: {dist:F1}km @ {pace:F2}m/k\n";
        }

        client.DefaultRequestHeaders.Authorization = null; 
        var prompt = $"ROLE: Master Performance Running Coach. GOAL: {envGetter("GOAL_RACE_TYPE")} on {envGetter("GOAL_RACE_DATE")}.\n" +
                     $"TASK: Analyze this workout: {act.GetRawText()}.\n" +
                     $"HISTORY SUMMARY:\n{histSummary}\n\n" +
                     "INSTRUCTION: Return Markdown with Summary, Race Readiness %, T-Minus, Next Week Focus, and Next Training Step. Be analytical and encouraging.";

        var apiKey = envGetter("API_KEY");
        var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new { contents = new[] { new { parts = new[] { new { text = prompt } } } } });
        
        if (geminiRes.IsSuccessStatusCode) {
            var aiRes = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
            var aiText = aiRes.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            var desc = (act.TryGetProperty("description", out var d) ? d.GetString() : "") + $"\n\n--- StravAI Report ---\n{aiText}\n\nProcessed: {timeGetter()}";
            
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{id}", new { description = desc });
            logger($"Activity {id} analysis successfully appended.", "SUCCESS");
        } else {
            var err = await geminiRes.Content.ReadAsStringAsync();
            logger($"Gemini Error ({geminiRes.StatusCode}): {err}", "ERROR");
        }
    } catch (Exception ex) { logger($"Logic Error for {id}: {ex.Message}", "ERROR"); }
}

public record CustomRaceRequest(string Name, string Distance, string Date, string TargetTime, string? RaceDetails);

public static class SeasonStrategyEngine {
    public static async Task<string?> GetStravaAccessToken(HttpClient client, Func<string, string> envGetter) {
        try {
            var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
                new KeyValuePair<string, string>("client_id", envGetter("STRAVA_CLIENT_ID")),
                new KeyValuePair<string, string>("client_secret", envGetter("STRAVA_CLIENT_SECRET")),
                new KeyValuePair<string, string>("refresh_token", envGetter("STRAVA_REFRESH_TOKEN")),
                new KeyValuePair<string, string>("grant_type", "refresh_token")
            }));
            if (!res.IsSuccessStatusCode) return null;
            var data = await res.Content.ReadFromJsonAsync<JsonElement>();
            return data.GetProperty("access_token").GetString();
        } catch { return null; }
    }

    public static async Task ProcessSeasonAnalysisAsync(IHttpClientFactory clientFactory, Func<string, string> envGetter, ConcurrentQueue<string> logs, Func<string> timeGetter, Func<List<JsonElement>, string> summarizer, CustomRaceRequest? customRace = null) {
        void L(string m, string lvl = "INFO") => logs.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] [{lvl}] {m}");

        try {
            using var client = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(client, envGetter);
            if (token == null) { L("Auth Failed", "ERROR"); return; }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            L("Gathering Multi-Month Performance Trends...");
            var historyData = await client.GetFromJsonAsync<List<JsonElement>>("https://www.strava.com/api/v3/athlete/activities?per_page=200");
            var historySummary = summarizer(historyData ?? new());

            client.DefaultRequestHeaders.Authorization = null;

            var prompt = $@"ATHLETE GOAL: {(customRace?.Name ?? envGetter("GOAL_RACE_TYPE"))} on {(customRace?.Date ?? envGetter("GOAL_RACE_DATE"))} (Target Time: {(customRace?.TargetTime ?? envGetter("GOAL_RACE_TIME"))}).
HISTORY CONTEXT (FULL SEASON SCAN):
{historySummary}

RACE SPECIFICS:
{ (customRace?.RaceDetails ?? "N/A") }

TASK: Deep Season Strategy. Include:
1. EXECUTIVE SUMMARY
2. FEASIBILITY (FTP/Aerobic Base + PROBABILITY %)
3. 3-TIER PACE STRATEGY (Optimistic/Realistic/Pessimistic)
4. NUTRITION & LOGISTICS
5. ACTION PLAN (Next 7 Days)

INSTRUCTION: Professional coaching tone. Markdown. Footer processed stamp: {timeGetter()}";

            var apiKey = envGetter("API_KEY");
            var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new { contents = new[] { new { parts = new[] { new { text = prompt } } } } });
            
            if (geminiRes.IsSuccessStatusCode) {
                var aiRes = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
                var aiText = aiRes.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                string finalDesc = $"{aiText}\n\nProcessed: {timeGetter()}";

                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                string targetTitle = $"[StravAI] STRATEGY: {(customRace?.Name ?? "Full Season Plan")}";
                
                var recent = await client.GetFromJsonAsync<List<JsonElement>>("https://www.strava.com/api/v3/athlete/activities?per_page=50");
                JsonElement? existing = null;
                foreach (var a in recent ?? new()) {
                    if (a.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String && nameProp.GetString() == targetTitle) {
                        existing = a;
                        break;
                    }
                }

                if (existing.HasValue && existing.Value.TryGetProperty("id", out var eid)) {
                    L($"Updating Existing Strategy activity: {eid.GetInt64()}");
                    await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{eid.GetInt64()}", new { description = finalDesc, @private = true });
                    L($"{targetTitle} successfully updated.", "SUCCESS"); 
                } else {
                    L("No matching strategy activity found. Creating new entry.");
                    await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new {
                        name = targetTitle,
                        type = "Workout",
                        start_date_local = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                        elapsed_time = 1,
                        description = finalDesc,
                        @private = true
                    });
                    L($"{targetTitle} deployed as new Private Activity.", "SUCCESS");
                }
            } else {
                var err = await geminiRes.Content.ReadAsStringAsync();
                L($"AI API Error: {err}", "ERROR");
            }
        } catch (Exception ex) { L($"Season Analysis Crash: {ex.Message}", "ERROR"); }
    }
}

public class SeasonBackgroundWorker : BackgroundService {
    private readonly IHttpClientFactory _cf;
    private readonly ConcurrentQueue<string> _l;
    private readonly IConfiguration _cfg;
    public SeasonBackgroundWorker(IHttpClientFactory cf, ConcurrentQueue<string> l, IConfiguration cfg) { _cf = cf; _l = l; _cfg = cfg; }
    protected override async Task ExecuteAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            var now = DateTime.UtcNow;
            if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour == 2) {
                await SeasonStrategyEngine.ProcessSeasonAnalysisAsync(_cf, k => Environment.GetEnvironmentVariable(k) ?? _cfg[k] ?? "", _l, () => DateTime.UtcNow.ToString("G"), (list) => "Automated Sunday Update");
            }
            await Task.Delay(TimeSpan.FromHours(1), ct);
        }
    }
}

public record StravaWebhookEvent([property: JsonPropertyName("object_id")] long ObjectId, [property: JsonPropertyName("object_type")] string ObjectType);
