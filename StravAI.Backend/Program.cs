
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

builder.Services.AddHttpClient();
builder.Services.AddLogging();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

// Simple in-memory log buffer for the Command Center UI
var logs = new ConcurrentQueue<string>();
void AddLog(string message, string level = "INFO") {
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    var logEntry = $"[{timestamp}] [{level}] {message}";
    logs.Enqueue(logEntry);
    while (logs.Count > 200) logs.TryDequeue(out _);
    Console.WriteLine(logEntry); 
}

// Register services for Background Worker
builder.Services.AddSingleton(logs);
builder.Services.AddHostedService<SeasonBackgroundWorker>();

var app = builder.Build();
app.UseCors("AllowAll");

// Configuration Helper
var config = app.Configuration;
string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val;
    if (key == "API_KEY") {
        val = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrEmpty(val)) return val;
    }
    return config[key] ?? "";
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

// Unified skip logic
bool NeedsAnalysis(string? description) {
    if (description == null) return true;
    string lowerDesc = description.ToLower().Trim();
    if (string.IsNullOrEmpty(lowerDesc)) return true;
    return !(lowerDesc.Contains("stravai report") || lowerDesc.Contains("[stravai-processed]") || lowerDesc.Contains("*[stravai-processed]*"));
}

// Standard Sync Handlers
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
                if (act.TryGetProperty("id", out var idProp) && act.GetProperty("type").GetString() == "Run") {
                    await ProcessActivityAsync(idProp.GetInt64(), clientFactory, GetEnv, AddLog, GetCetTimestamp, NeedsAnalysis);
                }
            }
            AddLog($"SYNC_FINISH: Scanning queue processed.");
        } catch (Exception ex) { AddLog($"SYNC_ERR: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

// Season Strategy Endpoint (Manual Trigger)
app.MapPost("/sync/season", (IHttpClientFactory clientFactory) => {
    AddLog("AUTH_ACTION: Manual Season Strategy Re-calculation triggered.");
    _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(clientFactory, GetEnv, logs, GetCetTimestamp));
    return Results.Accepted();
});

app.MapPost("/sync/{id}", (long id, IHttpClientFactory clientFactory) => {
    AddLog($"AUTH_ACTION: Individual scan for {id}.");
    _ = Task.Run(() => ProcessActivityAsync(id, clientFactory, GetEnv, AddLog, GetCetTimestamp, NeedsAnalysis));
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
        _ = Task.Run(() => ProcessActivityAsync(@event.ObjectId, clientFactory, GetEnv, AddLog, GetCetTimestamp, NeedsAnalysis));
    }
    return Results.Ok();
});

// Logic pulled into methods for reuse
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
        return data.GetProperty("access_token").GetString();
    } catch { return null; }
}

async Task ProcessActivityAsync(long activityId, IHttpClientFactory clientFactory, Func<string, string> envGetter, Action<string, string> logger, Func<string> timeGetter, Func<string?, bool> skipCheck) {
    var tid = Guid.NewGuid().ToString().Substring(0, 5);
    try {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client, envGetter);
        if (token == null) return;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{activityId}");
        if (act.GetProperty("type").GetString() != "Run") return;
        var desc = act.TryGetProperty("description", out var d) ? d.GetString() : "";
        if (!skipCheck(desc)) return;

        logger($"[{tid}] START: Analyzing {activityId} (using PRO engine)...", "INFO");
        var hist = await (await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=12")).Content.ReadAsStringAsync();
        client.DefaultRequestHeaders.Authorization = null;

        var prompt = $"ROLE: Master Performance Running Coach. GOAL: {envGetter("GOAL_RACE_TYPE")} on {envGetter("GOAL_RACE_DATE")}.\n" +
                     $"TASK: Analyze activity: {act.GetRawText()}.\n" +
                     $"HISTORY: {hist}.\n" +
                     "INSTRUCTION: Return Markdown with Summary, Race Readiness %, T-Minus, Next Week Focus, and Next Training Step.";

        var apiKey = envGetter("API_KEY");
        var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-pro-preview:generateContent?key={apiKey}", new { 
            contents = new[] { new { parts = new[] { new { text = prompt } } } } 
        });
        var aiRes = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
        var aiText = aiRes.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

        var finalDesc = (desc?.Split("###")[0].Trim() ?? "") + $"\n\n################################\nStravAI Report\n---\n{aiText}\n\nAnalysis: {timeGetter()} CET\n*[StravAI-Processed]*\n################################";
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { description = finalDesc });
        logger($"[{tid}] SUCCESS: Activity {activityId} updated.", "SUCCESS");
    } catch (Exception ex) { logger($"[{tid}] ERROR: {ex.Message}", "ERROR"); }
}

// Global Static Engine for Season Analysis
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
            var token = await GetStravaAccessTokenStatic(client, envGetter);
            if (token == null) { LocalLog("CRITICAL: Authentication failed. Check STRAVA_REFRESH_TOKEN.", "ERROR"); return; }
            
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            
            LocalLog("[STEP 2/5] Starting multi-page data fetch...");
            var allActivities = new List<JsonElement>();
            for (int page = 1; page <= 3; page++) {
                LocalLog($"Fetching page {page} of athlete history...");
                var pageRes = await client.GetFromJsonAsync<List<JsonElement>>($"https://www.strava.com/api/v3/athlete/activities?per_page=200&page={page}");
                if (pageRes == null || pageRes.Count == 0) {
                    LocalLog($"No more activities found on page {page}. Ending fetch.");
                    break;
                }
                allActivities.AddRange(pageRes);
                LocalLog($"Retrieved {pageRes.Count} items. Total buffer: {allActivities.Count}.");
                if (allActivities.Count >= 500) break;
            }

            LocalLog($"[STEP 3/5] Compressing {allActivities.Count} data points for AI context...");
            var historySummary = string.Join("\n", allActivities.Take(500).Select(a => {
                var type = a.GetProperty("type").GetString();
                var date = a.GetProperty("start_date").GetString();
                var dist = a.TryGetProperty("distance", out var d) ? d.GetDouble() / 1000 : 0;
                var speed = a.TryGetProperty("average_speed", out var s) ? s.GetDouble() : 0;
                var pace = speed > 0 ? (16.6667 / speed) : 0;
                var hr = a.TryGetProperty("average_heartrate", out var h) ? h.GetDouble().ToString() : "N/A";
                return $"{date}: {type}, {dist:F2}km, Pace: {pace:F2}m/k, HR: {hr}";
            }));

            var goalType = envGetter("GOAL_RACE_TYPE");
            var goalDate = envGetter("GOAL_RACE_DATE");
            var goalTime = envGetter("GOAL_RACE_TIME");

            LocalLog($"[STEP 4/5] Engaging Gemini 3 Pro for Season Strategy Architecture...");
            LocalLog($"Prompting AI with {historySummary.Length} chars of training context...");

            var prompt = $"ROLE: Elite Ultra-Running Strategy Consultant & Performance Architect.\n" +
                         $"ATHLETE GOAL: {goalType} on {goalDate} (Target: {goalTime}).\n" +
                         $"500-ACTIVITY TRAINING CONTEXT:\n{historySummary}\n\n" +
                         $"TASK: Synthesize this data into a 100% professional Season Strategy.\n" +
                         "OUTPUT STRUCTURE:\n" +
                         "1. **Performance Trend Analysis**: Volume, intensity distribution, and modality balance.\n" +
                         "2. **The Triple-Plan Race Strategy** (A/Optimistic, B/Realistic, C/Survival).\n" +
                         "3. **Fueling & Hydration Protocol** (Specific timing and electrolytes).\n" +
                         "4. **Logistics & Gear Timeline** (Aid station checklist).\n" +
                         "5. **Micro-Block Focus (Next 7 Days)**: Specific workouts.\n\n" +
                         "FORMAT: Strictly professional Markdown.";

            client.DefaultRequestHeaders.Authorization = null;
            var apiKey = envGetter("API_KEY");
            var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-pro-preview:generateContent?key={apiKey}", new { 
                contents = new[] { new { parts = new[] { new { text = prompt } } } } 
            });
            
            if (!geminiRes.IsSuccessStatusCode) {
                LocalLog($"AI Request failed with status: {geminiRes.StatusCode}", "ERROR");
                return;
            }

            var aiRes = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
            var aiText = aiRes.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
            LocalLog("Architectural reasoning complete. Strategy document generated.");

            LocalLog("[STEP 5/5] Synchronizing strategy with Strava Storage Activity...");
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var startOfYear = new DateTime(DateTime.UtcNow.Year, 1, 1, 10, 0, 0, DateTimeKind.Utc);
            
            long? storageActivityId = null;
            var janActivities = await client.GetFromJsonAsync<List<JsonElement>>($"https://www.strava.com/api/v3/athlete/activities?before={new DateTimeOffset(startOfYear.AddDays(2)).ToUnixTimeSeconds()}&after={new DateTimeOffset(startOfYear.AddDays(-2)).ToUnixTimeSeconds()}");
            
            var existing = janActivities?.FirstOrDefault(a => a.GetProperty("name").GetString()?.Contains("[StravAI] SEASON_PLAN") == true);
            if (existing.HasValue) {
                storageActivityId = existing.Value.GetProperty("id").GetInt64();
                LocalLog($"Found existing storage activity (ID: {storageActivityId}). Overwriting...");
            } else {
                LocalLog("No storage activity found for Jan 1st. Creating a new one...");
                var createRes = await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new {
                    name = $"[StravAI] SEASON_PLAN_PRO_GOLD ({DateTime.UtcNow.Year})",
                    type = "Workout",
                    start_date_local = startOfYear.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                    elapsed_time = 1,
                    description = "StravAI Season Strategy Storage",
                    @private = true
                });
                if (createRes.IsSuccessStatusCode) {
                    var newAct = await createRes.Content.ReadFromJsonAsync<JsonElement>();
                    storageActivityId = newAct.GetProperty("id").GetInt64();
                    LocalLog($"Storage activity created (ID: {storageActivityId}).");
                }
            }

            if (storageActivityId.HasValue) {
                var finalReport = $"# SEASON STRATEGY: {goalType}\n" +
                                  $"History Context: {allActivities.Count} activities processed.\n" +
                                  $"Updated: {timeGetter()} CET\n\n" +
                                  $"{aiText}\n\n" +
                                  $"*[StravAI-Processed]*";
                var updateRes = await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{storageActivityId.Value}", new { description = finalReport });
                if (updateRes.IsSuccessStatusCode)
                    LocalLog($"SUCCESS: Season strategy updated successfully.", "SUCCESS");
                else
                    LocalLog($"Update failed with status: {updateRes.StatusCode}", "ERROR");
            }
        } catch (Exception ex) { LocalLog($"CRITICAL ENGINE ERROR: {ex.Message}", "ERROR"); }
    }

    private static async Task<string?> GetStravaAccessTokenStatic(HttpClient client, Func<string, string> envGetter) {
        try {
            var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
                new KeyValuePair<string, string>("client_id", envGetter("STRAVA_CLIENT_ID")),
                new KeyValuePair<string, string>("client_secret", envGetter("STRAVA_CLIENT_SECRET")),
                new KeyValuePair<string, string>("refresh_token", envGetter("STRAVA_REFRESH_TOKEN")),
                new KeyValuePair<string, string>("grant_type", "refresh_token")
            }));
            if (!authRes.IsSuccessStatusCode) return null;
            var data = await authRes.Content.ReadFromJsonAsync<JsonElement>();
            return data.GetProperty("access_token").GetString();
        } catch { return null; }
    }
}

// Background Worker for Sundays (02:00 UTC)
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
        Console.WriteLine("[SeasonBackgroundWorker] Service Started. Waiting for Sunday 02:00 UTC schedule...");
        
        while (!stoppingToken.IsCancellationRequested) {
            var now = DateTime.UtcNow;
            
            // Check if it's Sunday, 2 AM UTC, and hasn't run today yet
            if (now.DayOfWeek == DayOfWeek.Sunday && now.Hour == 2 && (!_lastRunDate.HasValue || _lastRunDate.Value.Date != now.Date)) {
                _lastRunDate = now;
                
                string GetEnv(string key) {
                    var val = Environment.GetEnvironmentVariable(key);
                    if (!string.IsNullOrEmpty(val)) return val;
                    if (key == "API_KEY") return Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? _config[key] ?? "";
                    return _config[key] ?? "";
                }

                string GetCetTimestamp() {
                    try {
                        var tzi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) 
                            ? TimeZoneInfo.FindSystemTimeZoneById("Central European Standard Time") 
                            : TimeZoneInfo.FindSystemTimeZoneById("Europe/Berlin");
                        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tzi).ToString("dd/MM/yyyy HH:mm:ss");
                    } catch { return DateTime.UtcNow.AddHours(1).ToString("dd/MM/yyyy HH:mm:ss"); }
                }

                try {
                    _logs.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] [INFO] [SCHEDULER] Triggering scheduled Sunday Season Analysis...");
                    _ = Task.Run(async () => {
                        await SeasonStrategyEngine.ProcessSeasonAnalysisAsync(_clientFactory, GetEnv, _logs, GetCetTimestamp);
                    }, stoppingToken);
                } catch (Exception ex) {
                    Console.WriteLine($"[SeasonBackgroundWorker] Error triggering scan: {ex.Message}");
                }
            }
            
            // Check every 15 minutes
            await Task.Delay(TimeSpan.FromMinutes(15), stoppingToken);
        }
    }
}

app.Run();

public record StravaWebhookEvent([property: JsonPropertyName("object_type")] string ObjectType, [property: JsonPropertyName("object_id")] long ObjectId, [property: JsonPropertyName("aspect_type")] string AspectType);
