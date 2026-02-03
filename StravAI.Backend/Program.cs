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
// Fixed: Use an empty string for the default client name to avoid build errors
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

app.MapGet("/", () => "StravAI Engine v1.0.1-pro-gold is Online.");

app.MapGet("/health", () => Results.Ok(new { 
    status = "healthy", 
    engine = "StravAI_Core_v1.0.1_Pro_Gold",
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
        
        // Optimize: Summarize history instead of sending raw JSON to avoid timeouts and reduce token usage
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
        } else {
            logger($"[{tid}] AI ERROR: No candidates returned. This might be a safety filter or quota block.", "ERROR");
        }
    } catch (Exception ex) { logger($"[{tid}] ERROR: {ex.Message}", "ERROR"); }
}

// --- Type Definitions ---

public static class SeasonStrategyEngine {
    public static async Task ProcessSeasonAnalysisAsync(IHttpClientFactory clientFactory, Func<string, string> envGetter, ConcurrentQueue<string> logs, Func<string> timeGetter) {
        var tid = "SEASON";
        void LocalLog(string m, string l = "INFO") {
            var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
            var entry = $"[{timestamp}] [{l}] [{tid}] {m}";
            logs.Enqueue(entry);
            Console.WriteLine(entry);
        }

        try {
            LocalLog("[STEP 1/5] Initiating Deep History Analysis (Target: 500 Activities)...");
            using var client = clientFactory.CreateClient();
            
            LocalLog("Authenticating with Strava API...");
            var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
                new KeyValuePair<string, string>("client_id", envGetter("STRAVA_CLIENT_ID")),
                new KeyValuePair<string, string>("client_secret", envGetter("STRAVA_CLIENT_SECRET")),
                new KeyValuePair<string, string>("refresh_token", envGetter("STRAVA_REFRESH_TOKEN")),
                new KeyValuePair<string, string>("grant_type", "refresh_token")
            }));
            if (!authRes.IsSuccessStatusCode) { LocalLog("CRITICAL: Authentication failed. Verify credentials in Environment.", "ERROR"); return; }
            var authData = await authRes.Content.ReadFromJsonAsync<JsonElement>();
            if (!authData.TryGetProperty("access_token", out var tokenProp)) { LocalLog("CRITICAL: Access token missing in response.", "ERROR"); return; }
            var token = tokenProp.GetString();
            
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            LocalLog("[STEP 2/5] Starting multi-page data fetch...");
            var allActivities = new List<JsonElement>();
            for (int page = 1; page <= 3; page++) {
                LocalLog($"Fetching page {page} (Depth: {allActivities.Count}/500)...");
                var pageRes = await client.GetFromJsonAsync<List<JsonElement>>($"https://www.strava.com/api/v3/athlete/activities?per_page=200&page={page}");
                if (pageRes == null || pageRes.Count == 0) break;
                allActivities.AddRange(pageRes);
                if (allActivities.Count >= 500) break;
            }

            LocalLog($"[STEP 3/5] Compressing {allActivities.Count} data points for AI context...");
            var historySummary = string.Join("\n", allActivities.Take(500).Select(a => {
                var type = a.TryGetProperty("type", out var t) ? t.GetString() : "Unknown";
                var date = a.TryGetProperty("start_date", out var d) ? d.GetString() : "Unknown";
                var dist = a.TryGetProperty("distance", out var distP) ? distP.GetDouble() / 1000 : 0;
                var speed = a.TryGetProperty("average_speed", out var s) ? s.GetDouble() : 0;
                var pace = speed > 0 ? (16.6667 / speed) : 0;
                var hr = a.TryGetProperty("average_heartrate", out var h) ? h.GetDouble().ToString() : "N/A";
                return $"{date}: {type}, {dist:F2}km, Pace: {pace:F2}m/k, HR: {hr}";
            }));

            LocalLog($"[STEP 4/5] Engaging Gemini 3 Flash (High-Speed Reasoning Engine)...");
            var prompt = $@"ROLE: Elite Ultra-Running Strategy Consultant.
ATHLETE GOAL: {envGetter("GOAL_RACE_TYPE")} on {envGetter("GOAL_RACE_DATE")} (Target Time: {envGetter("GOAL_RACE_TIME")}).
HISTORY CONTEXT (Up to 500 Activities):
{historySummary}

TASK: Perform a deep physiological and logistical analysis to generate a SEASON STRATEGY.

REQUIRED SECTIONS IN OUTPUT (Markdown):

1. EXECUTIVE SUMMARY:
   A high-level overview of current fitness based on 500-activity history trends.

2. RACE PACE STRATEGY (3 TIERS):
   - OPTIMISTIC (Everything is perfect): Realistic Target Pace (min/km), Target SPM (Cadence), Target Heart Rate (bpm).
   - REALISTIC (Based on current data): Recommended Pace, SPM, and HR.
   - PESSIMISTIC (Weak/Sick conditions): Minimum Pace, SPM, and HR required to finish under cutoff.

3. NUTRITION & REFRESHMENT PLAN:
   Detailed plan on what to eat/drink, specific quantities (e.g., carbs/hour), and exact frequency (e.g., every 30-45 mins).

4. LOGISTICS & GEAR STRATEGY:
   - WHAT TO CARRY: Mandatory and recommended gear.
   - AID STATION SWAPS: Specific points/times to grab headlamp, powerbank, change shoes, or change clothes based on race progression.

5. REMAINING SEASON FOCUS:
   Training themes and focuses for the remaining weeks (e.g., vertical gain, heat acclimation, volume).

6. NEXT 7 DAYS - ACTION PLAN:
   A concrete training plan for the immediate next 7 days, specifying type, distance/duration, and purpose for each session.

INSTRUCTION: Provide the response in a professional, coaching tone. Use Markdown headers and lists.";

            client.DefaultRequestHeaders.Authorization = null;
            var apiKey = envGetter("API_KEY");
            if (string.IsNullOrEmpty(apiKey)) { LocalLog("CRITICAL: GEMINI_API_KEY is missing.", "ERROR"); return; }

            var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new { 
                contents = new[] { new { parts = new[] { new { text = prompt } } } } 
            });
            
            if (!geminiRes.IsSuccessStatusCode) {
                var errContent = await geminiRes.Content.ReadAsStringAsync();
                LocalLog($"AI API FAILED ({geminiRes.StatusCode}): {errContent}", "ERROR");
                return;
            }

            var aiRes = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
            if (aiRes.TryGetProperty("candidates", out var candidates) && candidates.GetArrayLength() > 0) {
                var candidate = candidates[0];
                if (candidate.TryGetProperty("content", out var contentProp) && contentProp.TryGetProperty("parts", out var partsProp) && partsProp.GetArrayLength() > 0) {
                    var aiText = partsProp[0].GetProperty("text").GetString();
                    LocalLog("AI Analysis complete. Generation successful.");

                    LocalLog("[STEP 5/5] Synchronizing strategy with Strava Storage Activity...");
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    var startOfYear = new DateTime(DateTime.UtcNow.Year, 1, 1, 10, 0, 0, DateTimeKind.Utc);
                    long? storageId = null;
                    
                    var janActivities = await client.GetFromJsonAsync<List<JsonElement>>($"https://www.strava.com/api/v3/athlete/activities?before={new DateTimeOffset(startOfYear.AddDays(2)).ToUnixTimeSeconds()}&after={new DateTimeOffset(startOfYear.AddDays(-2)).ToUnixTimeSeconds()}");
                    
                    if (janActivities != null) {
                        foreach (var act in janActivities) {
                            if (act.ValueKind != JsonValueKind.Object) continue;
                            if (act.TryGetProperty("name", out var nProp) && nProp.GetString()?.Contains("[StravAI] SEASON_PLAN") == true) {
                                if (act.TryGetProperty("id", out var idProp)) {
                                    storageId = idProp.GetInt64();
                                    LocalLog($"Found existing strategy storage: {storageId}");
                                    break;
                                }
                            }
                        }
                    }

                    if (!storageId.HasValue) {
                        LocalLog("Storage activity missing. Creating new Jan 1st placeholder...");
                        var createRes = await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new {
                            name = $"[StravAI] SEASON_PLAN_PRO_GOLD ({DateTime.UtcNow.Year})",
                            type = "Workout",
                            start_date_local = startOfYear.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                            elapsed_time = 1,
                            @private = true
                        });
                        if (createRes.IsSuccessStatusCode) {
                            var newAct = await createRes.Content.ReadFromJsonAsync<JsonElement>();
                            if (newAct.TryGetProperty("id", out var idProp)) storageId = idProp.GetInt64();
                        } else {
                            LocalLog($"Failed to create storage activity: {createRes.StatusCode}", "ERROR");
                        }
                    }

                    if (storageId.HasValue) {
                        var finalReport = $"# SEASON STRATEGY\nUpdated: {timeGetter()} CET\n\n{aiText}\n\n*[StravAI-Processed]*";
                        var updateRes = await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{storageId.Value}", new { description = finalReport });
                        if (updateRes.IsSuccessStatusCode) LocalLog($"SUCCESS: Season strategy updated (ID: {storageId.Value}).", "SUCCESS");
                        else LocalLog($"Failed to update Strava description: {updateRes.StatusCode}", "ERROR");
                    } else {
                        LocalLog("ERROR: Could not resolve storage activity ID.", "ERROR");
                    }
                } else {
                    LocalLog("AI ERROR: Response parts missing. Likely a safety block.", "ERROR");
                }
            } else {
                LocalLog("AI ERROR: No candidates returned in JSON response.", "ERROR");
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
                _logs.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] [INFO] [SCHEDULER] Triggering scheduled Sunday Season Analysis...");
                _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(_clientFactory, k => Environment.GetEnvironmentVariable(k) ?? _config[k] ?? "", _logs, () => DateTime.UtcNow.ToString("dd/MM/yyyy HH:mm:ss")));
            }
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}

public record StravaWebhookEvent([property: JsonPropertyName("object_type")] string ObjectType, [property: JsonPropertyName("object_id")] long ObjectId, [property: JsonPropertyName("aspect_type")] string AspectType);
