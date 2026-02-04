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

// Configured HttpClient with an increased timeout for deep AI analysis
builder.Services.AddHttpClient("", client => {
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddLogging();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS").AllowAnyHeader()));

// Simple in-memory log buffer for the Command Center UI
var logs = new ConcurrentQueue<string>();

// Register services for Background Worker
builder.Services.AddSingleton(logs);
builder.Services.AddHostedService<SeasonBackgroundWorker>();

var app = builder.Build();
app.UseCors("AllowAll");

// --- Top Level Statements / App Config ---

// Configuration Helper
string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val;
    if (key == "API_KEY") {
        val = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrEmpty(val)) return val;
    }
    try {
        return app.Configuration[key] ?? "";
    } catch {
        return "";
    }
}

void AddLog(string message, string level = "INFO") {
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    var logEntry = $"[{timestamp}] [{level}] {message}";
    logs.Enqueue(logEntry);
    while (logs.Count > 200) logs.TryDequeue(out _);
    Console.WriteLine(logEntry); 
}

// Helper to get CET timestamp consistently
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

// Helper to summarize activities for AI context
string SummarizeActivitiesForAI(List<JsonElement> activities) {
    return string.Join("\n", activities.Select(a => {
        var type = a.TryGetProperty("type", out var t) ? t.GetString() : "Unknown";
        var date = a.TryGetProperty("start_date", out var d) ? d.GetString() : "Unknown";
        var dist = a.TryGetProperty("distance", out var distP) ? distP.GetDouble() / 1000 : 0;
        var speed = a.TryGetProperty("average_speed", out var s) ? s.GetDouble() : 0;
        var pace = speed > 0 ? (16.6667 / speed) : 0;
        var hr = a.TryGetProperty("average_heartrate", out var h) ? h.GetDouble().ToString() : "N/A";
        return $"- {date}: {type}, {dist:F2}km, Pace: {pace:F2}m/k, HR: {hr}";
    }));
}

// Security Middleware
app.Use(async (context, next) => {
    var path = context.Request.Path.Value?.ToLower() ?? "";
    if (path == "/" || path == "/health" || path == "/webhook")
    {
        await next();
        return;
    }

    var secret = GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";
    if (!context.Request.Headers.TryGetValue("X-StravAI-Secret", out var providedSecret) || providedSecret != secret)
    {
        context.Response.StatusCode = 401;
        await context.Response.WriteAsync("Unauthorized.");
        return;
    }

    await next();
});

// --- Routes ---

app.MapGet("/", () => "StravAI Engine v1.2.0-multi-planner is Online.");

app.MapGet("/health", () => Results.Ok(new { 
    status = "healthy", 
    engine = "StravAI_Core_v1.2.0_Multi_Planner",
    config = new {
        gemini_ready = !string.IsNullOrEmpty(GetEnv("API_KEY")),
        strava_ready = !string.IsNullOrEmpty(GetEnv("STRAVA_REFRESH_TOKEN"))
    }
}));

app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

app.MapPost("/sync", (int? hours, IHttpClientFactory clientFactory) => {
    var label = hours.HasValue ? $"{hours}H Scan" : "Deep Scan";
    AddLog($"AUTH_ACTION: {label} triggered.");
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            var accessToken = await GetStravaAccessToken(client, GetEnv);
            if (string.IsNullOrEmpty(accessToken)) return;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            string url = "https://www.strava.com/api/v3/athlete/activities?per_page=30";
            if (hours.HasValue) {
                var afterTimestamp = DateTimeOffset.UtcNow.AddHours(-hours.Value).ToUnixTimeSeconds();
                url += $"&after={afterTimestamp}";
            }
            var activities = await client.GetFromJsonAsync<List<JsonElement>>(url);
            foreach (var act in (activities ?? new())) {
                if (act.TryGetProperty("id", out var idProp) && act.TryGetProperty("type", out var typeProp) && typeProp.GetString() == "Run") {
                    await ProcessActivityAsync(idProp.GetInt64(), clientFactory, GetEnv, AddLog, GetCetTimestamp, SummarizeActivitiesForAI);
                }
            }
            AddLog($"SYNC_FINISH: Scanning queue processed.");
        } catch (Exception ex) { AddLog($"SYNC_ERR: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

app.MapPost("/sync/season", (IHttpClientFactory clientFactory) => {
    AddLog("AUTH_ACTION: Manual Season Strategy Re-calculation triggered.");
    _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(clientFactory, GetEnv, logs, GetCetTimestamp));
    return Results.Accepted();
});

app.MapPost("/sync/custom-race", ([FromBody] CustomRaceRequest req, IHttpClientFactory clientFactory) => {
    AddLog($"AUTH_ACTION: Custom Race Analysis triggered for '{req.Name}'.");
    _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(clientFactory, GetEnv, logs, GetCetTimestamp, req));
    return Results.Accepted();
});

app.MapPost("/sync/{id}", (long id, IHttpClientFactory clientFactory) => {
    AddLog($"AUTH_ACTION: Individual scan for {id}.");
    _ = Task.Run(() => ProcessActivityAsync(id, clientFactory, GetEnv, AddLog, GetCetTimestamp, SummarizeActivitiesForAI));
    return Results.Accepted();
});

app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, [FromQuery(Name = "hub.challenge")] string challenge, [FromQuery(Name = "hub.verify_token")] string token) => 
{
    if (mode == "subscribe" && token == (GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN")) return Results.Ok(new { hub_challenge = challenge });
    return Results.BadRequest();
});

app.MapPost("/webhook", ([FromBody] StravaWebhookEvent @event, IHttpClientFactory clientFactory) => 
{
    if (@event.ObjectType == "activity" && (@event.AspectType == "create" || @event.AspectType == "update")) {
        _ = Task.Run(() => ProcessActivityAsync(@event.ObjectId, clientFactory, GetEnv, AddLog, GetCetTimestamp, SummarizeActivitiesForAI));
    }
    return Results.Ok();
});

app.Run();

// --- Local Functions ---

async Task<string?> GetStravaAccessToken(HttpClient client, Func<string, string> envGetter) {
    try {
        var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", envGetter("STRAVA_CLIENT_ID")),
            new KeyValuePair<string, string>("client_secret", envGetter("STRAVA_CLIENT_SECRET")),
            new KeyValuePair<string, string>("refresh_token", envGetter("STRAVA_REFRESH_TOKEN")),
            new KeyValuePair<string, string>("grant_type", "refresh_token")
        }));
        if (!authRes.IsSuccessStatusCode) return null;
        var data = await authRes.Content.ReadFromJsonAsync<JsonElement>();
        return data.TryGetProperty("access_token", out var tokenProp) ? tokenProp.GetString() : null;
    } catch { return null; }
}

async Task ProcessActivityAsync(long activityId, IHttpClientFactory clientFactory, Func<string, string> envGetter, Action<string, string> logger, Func<string> timeGetter, Func<List<JsonElement>, string> summarizer) {
    var tid = Guid.NewGuid().ToString().Substring(0, 5);
    try {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client, envGetter);
        if (token == null) return;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{activityId}");
        
        var desc = act.TryGetProperty("description", out var d) ? d.GetString() : "";
        bool NeedsAnalysis(string? s) => string.IsNullOrEmpty(s) || !(s.Contains("stravai report") || s.Contains("[stravai-processed]"));
        if (!NeedsAnalysis(desc)) return;

        logger($"[{tid}] START: Analyzing {activityId} (using Flash-Preview)...", "INFO");
        
        var histRaw = await client.GetFromJsonAsync<List<JsonElement>>("https://www.strava.com/api/v3/athlete/activities?per_page=12");
        var histSummary = summarizer(histRaw ?? new());
        
        client.DefaultRequestHeaders.Authorization = null;

        var prompt = $"ROLE: Master Performance Running Coach. GOAL: {envGetter("GOAL_RACE_TYPE")} on {envGetter("GOAL_RACE_DATE")}.\n" +
                     $"TASK: Analyze this workout: {act.GetRawText()}.\n" +
                     $"HISTORY SUMMARY:\n{histSummary}\n\n" +
                     "INSTRUCTION: Return Markdown with Summary, Race Readiness %, T-Minus, Next Week Focus, and Next Training Step. Be analytical and encouraging.";

        var apiKey = envGetter("API_KEY");
        var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new { 
            contents = new[] { new { parts = new[] { new { text = prompt } } } } 
        });
        
        if (!geminiRes.IsSuccessStatusCode) {
             var err = await geminiRes.Content.ReadAsStringAsync();
             logger($"[{tid}] AI API ERROR {geminiRes.StatusCode}: {err}", "ERROR");
             return;
        }

        var aiRes = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
        if (aiRes.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0) {
            var content = candidates[0].GetProperty("content");
            var parts = content.GetProperty("parts");
            var aiText = parts[0].GetProperty("text").GetString();
            
            var finalDesc = (desc?.Split("################################")[0].Trim() ?? "") + $"\n\n################################\nStravAI Report\n---\n{aiText}\n\nAnalysis: {timeGetter()} CET\n*[StravAI-Processed]*\n################################";
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { description = finalDesc });
            logger($"[{tid}] SUCCESS: Activity {activityId} updated.", "SUCCESS");
        }
    } catch (Exception ex) { logger($"[{tid}] ERROR: {ex.Message}", "ERROR"); }
}

// --- Type Definitions ---

public record CustomRaceRequest(string Name, string Distance, string Date, string TargetTime, string? InfoUrl);

public static class SeasonStrategyEngine {
    public static async Task ProcessSeasonAnalysisAsync(IHttpClientFactory clientFactory, Func<string, string> envGetter, ConcurrentQueue<string> logs, Func<string> timeGetter, CustomRaceRequest? customRace = null) {
        var tid = customRace != null ? "CUSTOM" : "SEASON";
        void LocalLog(string m, string l = "INFO") {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var entry = $"[{timestamp}] [{l}] [{tid}] {m}";
            logs.Enqueue(entry);
            Console.WriteLine(entry);
        }

        try {
            var raceName = customRace?.Name ?? envGetter("GOAL_RACE_TYPE");
            var raceDate = customRace?.Date ?? envGetter("GOAL_RACE_DATE");
            var raceTime = customRace?.TargetTime ?? envGetter("GOAL_RACE_TIME");
            var raceDist = customRace?.Distance ?? "Unknown";

            LocalLog($"[STEP 1/5] Initiating Career History Analysis (Target: 1000 Activities)...");
            using var client = clientFactory.CreateClient();
            
            var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
                new KeyValuePair<string, string>("client_id", envGetter("STRAVA_CLIENT_ID")),
                new KeyValuePair<string, string>("client_secret", envGetter("STRAVA_CLIENT_SECRET")),
                new KeyValuePair<string, string>("refresh_token", envGetter("STRAVA_REFRESH_TOKEN")),
                new KeyValuePair<string, string>("grant_type", "refresh_token")
            }));
            if (!authRes.IsSuccessStatusCode) { LocalLog("CRITICAL: Authentication failed.", "ERROR"); return; }
            var authData = await authRes.Content.ReadFromJsonAsync<JsonElement>();
            var token = authData.GetProperty("access_token").GetString();
            
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            LocalLog("[STEP 2/5] Compiling physiological data points from past activities...");
            var allActivities = new List<JsonElement>();
            for (int page = 1; page <= 5; page++) {
                var pageRes = await client.GetFromJsonAsync<List<JsonElement>>($"($"https://www.strava.com/api/v3/athlete/activities?per_page=200&page={page}"));
                if (pageRes == null || pageRes.Count == 0) break;
                allActivities.AddRange(pageRes);
                if (allActivities.Count >= 1000) break;
            }

            var historySummary = string.Join("\n", allActivities.Take(1000).Select(a => {
                var type = a.TryGetProperty("type", out var t) ? t.GetString() : "Unknown";
                var date = a.TryGetProperty("start_date", out var d) ? d.GetString() : "Unknown";
                var dist = a.TryGetProperty("distance", out var distP) ? distP.GetDouble() / 1000 : 0;
                var speed = a.TryGetProperty("average_speed", out var s) ? s.GetDouble() : 0;
                var pace = speed > 0 ? (16.6667 / speed) : 0;
                return $"{date}: {type}, {dist:F2}km, Pace: {pace:F2}m/k";
            }));

            LocalLog($"[STEP 3/5] Engaging AI Reasoner ({ (customRace != null ? "Multi-Planner Mode" : "Standard Mode") })...");
            
            bool useSearch = !string.IsNullOrEmpty(customRace?.InfoUrl);
            var modelName = useSearch ? "gemini-3-pro-image-preview" : "gemini-3-flash-preview";
            
            var prompt = $@"ROLE: Elite Ultra-Running Performance Strategist.
ATHLETE TARGET: {raceName} ({raceDist}) on {raceDate} (Target Time: {raceTime}).
{ (useSearch ? $"RACE DESCRIPTION URL: {customRace.InfoUrl}" : "") }

TASK: Perform a mathematical feasibility assessment and season plan using the history below (1000 items).

ATHLETE HISTORY:
{historySummary}

REQUIREMENTS:
1. GOAL REALISM & FEASIBILITY ASSESSMENT: 
   - Calculate current Functional Threshold Pace (FTP) from history.
   - Compare with required pace for {raceTime}.
   - Provide a probability score (%) and suggest Silver/Bronze alternative goals if needed.

2. EXECUTIVE SUMMARY: Fitness trends based on full history.
3. RACE PACE STRATEGY (3 TIERS): Optimistic, Realistic, Pessimistic.
4. NUTRITION & REFRESHMENT PLAN: Carbs/hour and specific intake frequency.
5. LOGISTICS & GEAR STRATEGY: Grounding advice based on terrain found at the URL (if provided).
6. REMAINING SEASON FOCUS & NEXT 7 DAYS ACTION PLAN.

INSTRUCTION: Output strictly Markdown.";

            client.DefaultRequestHeaders.Authorization = null;
            var apiKey = envGetter("API_KEY");
            
            // Correct REST API Structure for Tools (at root level)
            var payload = new {
                contents = new[] {
                    new {
                        parts = new[] {
                            new { text = prompt }
                        }
                    }
                },
                tools = useSearch ? new[] { new { googleSearch = new { } } } : null
            };

            var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={apiKey}", payload);
            
            if (!geminiRes.IsSuccessStatusCode) {
                var errContent = await geminiRes.Content.ReadAsStringAsync();
                LocalLog($"AI API FAILED: {errContent}", "ERROR");
                return;
            }

            var aiRes = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
            var aiText = aiRes.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

            LocalLog("[STEP 4/5] Syncing intelligence with Strava Cloud Storage...");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            string storageNamePrefix = customRace != null ? $"[StravAI] RACE_PLAN: {customRace.Name}" : "[StravAI] SEASON_PLAN_ULTRA_COACH";
            long? storageId = null;
            
            var startOfYear = new DateTime(DateTime.UtcNow.Year, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            var janActivities = await client.GetFromJsonAsync<List<JsonElement>>($"https://www.strava.com/api/v3/athlete/activities?before={new DateTimeOffset(startOfYear.AddDays(2)).ToUnixTimeSeconds()}&after={new DateTimeOffset(startOfYear.AddDays(-2)).ToUnixTimeSeconds()}");
            
            foreach (var act in (janActivities ?? new())) {
                if (act.TryGetProperty("name", out var n) && n.GetString()?.Contains(storageNamePrefix) == true) {
                    storageId = act.GetProperty("id").GetInt64();
                    break;
                }
            }

            if (!storageId.HasValue) {
                var createRes = await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new {
                    name = $"{storageNamePrefix} ({DateTime.UtcNow.Year})",
                    type = "Workout",
                    start_date_local = startOfYear.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    elapsed_time = 1,
                    @private = true
                });
                if (createRes.IsSuccessStatusCode) {
                    var newAct = await createRes.Content.ReadFromJsonAsync<JsonElement>();
                    storageId = newAct.GetProperty("id").GetInt64();
                }
            }

            if (storageId.HasValue) {
                var finalReport = $"# {raceName.ToUpper()} - STRATEGY & FEASIBILITY\nAnalysis: {timeGetter()} CET\n\n{aiText}\n\n*[StravAI-Processed]*";
                await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{storageId.Value}", new { description = finalReport });
                LocalLog($"[STEP 5/5] SUCCESS: Comprehensive strategy deployed (ID: {storageId.Value}).", "SUCCESS");
            }
        } catch (Exception ex) { LocalLog($"CRITICAL ENGINE ERROR: {ex.Message}", "ERROR"); }
    }
}

public class SeasonBackgroundWorker : BackgroundService {
    private readonly IHttpClientFactory _clientFactory;
    private readonly ConcurrentQueue<string> _logs;
    private readonly IConfiguration _config;
    private DateTime? _lastRunDate;

    public SeasonBackgroundWorker(IHttpClientFactory clientFactory, ConcurrentQueue<string> logs, IConfiguration config) {
        _clientFactory = clientFactory;
        _logs = logs;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        while (!stoppingToken.IsCancellationRequested) {
            var now = DateTime.UtcNow;
            if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour == 2 && (!_lastRunDate.HasValue || _lastRunDate.Value.Date != now.Date)) {
                _lastRunDate = now;
                _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(_clientFactory, k => Environment.GetEnvironmentVariable(k) ?? _config[k] ?? "", _logs, () => DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm:ss")));
            }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}

public record StravaWebhookEvent([property: JsonPropertyName("object_type")] string ObjectType, [property: JsonPropertyName("object_id")] long ObjectId, [property: JsonPropertyName("aspect_type")] string AspectType);
