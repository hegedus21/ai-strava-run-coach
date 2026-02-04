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
        return TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, tzi).ToString("dd/MM/yyyy HH:mm:ss");
    } catch { return DateTime.UtcNow.AddHours(1).ToString("dd/MM/yyyy HH:mm:ss"); }
}

string CompactSummarize(List<JsonElement> activities) {
    if (activities == null || activities.Count == 0) return "No history found.";
    
    // Grouping to provide a dense overview for the Coach
    var grouped = activities
        .Where(a => a.TryGetProperty("type", out var t) && t.GetString() == "Run")
        .GroupBy(a => DateTime.Parse(a.GetProperty("start_date").GetString()!).ToString("yyyy-MM"))
        .OrderByDescending(g => g.Key)
        .Take(6);

    var sb = new System.Text.StringBuilder();
    sb.AppendLine("MONTHLY TRAINING VOLUME (LAST 6 MONTHS):");
    foreach (var month in grouped) {
        var totalKm = month.Sum(a => a.GetProperty("distance").GetDouble()) / 1000;
        var avgSpeed = month.Average(a => a.GetProperty("average_speed").GetDouble());
        var avgPace = avgSpeed > 0 ? (16.6667 / avgSpeed) : 0;
        sb.AppendLine($"- {month.Key}: {totalKm:F1}km, Avg Pace: {avgPace:F2}m/k");
    }

    sb.AppendLine("\nMOST RECENT 15 RUNS (SPECIFICS):");
    foreach (var a in activities.Take(15)) {
        var date = a.GetProperty("start_date").GetString();
        var dist = a.GetProperty("distance").GetDouble() / 1000;
        var speed = a.GetProperty("average_speed").GetDouble();
        var pace = speed > 0 ? (16.6667 / speed) : 0;
        var hr = a.TryGetProperty("average_heartrate", out var h) ? h.GetDouble().ToString() : "N/A";
        sb.AppendLine($"- {date}: {dist:F2}km @ {pace:F2}m/k, HR: {hr}");
    }

    return sb.ToString();
}

// --- Routes ---

app.MapGet("/", () => "StravAI Engine v1.2.2 is Online.");
app.MapGet("/health", () => Results.Ok(new { status = "healthy", engine = "StravAI_Core_v1.2.2" }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

app.MapPost("/sync/custom-race", ([FromBody] CustomRaceRequest req, IHttpClientFactory clientFactory) => {
    AddLog($"AUTH_ACTION: Custom Race Analysis triggered for '{req.Name}'.");
    _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(clientFactory, GetEnv, logs, GetCetTimestamp, CompactSummarize, req));
    return Results.Accepted();
});

app.MapPost("/sync/season", (IHttpClientFactory clientFactory) => {
    AddLog("AUTH_ACTION: Season Strategy Update triggered.");
    _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(clientFactory, GetEnv, logs, GetCetTimestamp, CompactSummarize));
    return Results.Accepted();
});

app.MapPost("/webhook", ([FromBody] StravaWebhookEvent @event, IHttpClientFactory clientFactory) => {
    if (@event.ObjectType == "activity") {
        _ = Task.Run(() => ProcessActivityAsync(@event.ObjectId, clientFactory, GetEnv, AddLog, GetCetTimestamp));
    }
    return Results.Ok();
});

app.Run();

// --- Core Logic ---

async Task ProcessActivityAsync(long id, IHttpClientFactory clientFactory, Func<string, string> envGetter, Action<string, string> logger, Func<string> timeGetter) {
    try {
        using var client = clientFactory.CreateClient();
        var token = await SeasonStrategyEngine.GetStravaAccessToken(client, envGetter);
        if (token == null) return;
        
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{id}");
        
        // Fetch context for the prompt
        var history = await client.GetFromJsonAsync<List<JsonElement>>("https://www.strava.com/api/v3/athlete/activities?per_page=10");
        var histSummary = "";
        foreach (var h in history ?? new()) {
            var date = h.GetProperty("start_date").GetString();
            var dist = h.GetProperty("distance").GetDouble() / 1000;
            var speed = h.GetProperty("average_speed").GetDouble();
            var pace = speed > 0 ? (16.6667 / speed) : 0;
            histSummary += $"- {date}: {dist:F1}km @ {pace:F2}m/k\n";
        }

        // CRITICAL: Clear Strava auth before calling Gemini to avoid 401
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
            
            // Re-apply Strava auth to update the activity
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{id}", new { description = desc });
            logger($"Activity {id} updated successfully.", "SUCCESS");
        } else {
            var err = await geminiRes.Content.ReadAsStringAsync();
            logger($"AI Analysis Failed: {err}", "ERROR");
        }
    } catch (Exception ex) { logger($"Analysis Error: {ex.Message}", "ERROR"); }
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

            L("Compiling Full History Context...");
            var activities = await client.GetFromJsonAsync<List<JsonElement>>("https://www.strava.com/api/v3/athlete/activities?per_page=200");
            var historySummary = summarizer(activities ?? new());

            // CRITICAL: Clear Strava auth before calling Gemini to avoid 401
            client.DefaultRequestHeaders.Authorization = null;

            var prompt = $@"ATHLETE GOAL: {(customRace?.Name ?? envGetter("GOAL_RACE_TYPE"))} on {(customRace?.Date ?? envGetter("GOAL_RACE_DATE"))} (Target Time: {(customRace?.TargetTime ?? envGetter("GOAL_RACE_TIME"))}).
HISTORY CONTEXT (Up to 1000 Activities - FULL HISTORY):
{historySummary}

RACE SPECIFICS PROVIDED BY ATHLETE (LOOPS, NUTRITION, TERRAIN):
{ (customRace?.RaceDetails ?? "N/A") }

TASK: Perform a deep physiological and logistical analysis to generate a SEASON STRATEGY.

REQUIRED SECTIONS IN OUTPUT (Markdown):

1. EXECUTIVE SUMMARY:
   A high-level overview of fitness trends based on the full activity history provided.

2. FEASIBILITY & GOAL CALIBRATION (NEW):
   - Calculate Functional Threshold Pace (FTP) and Aerobic Base from history.
   - Compare to the required pace for the {customRace?.TargetTime ?? "target time"}.
   - Provide a FEASIBILITY PROBABILITY (%).
   - If goal is too aggressive, suggest a 'Silver Goal' (Progressive) and a 'Bronze Goal' (Safe Finish).

3. RACE PACE STRATEGY (3 TIERS):
   - OPTIMISTIC: Target Pace, Cadence, and HR for perfect conditions.
   - REALISTIC: Expected metrics based on typical performance data.
   - PESSIMISTIC: Minimum thresholds to maintain to clear race cutoffs.

4. NUTRITION & REFRESHMENT PLAN:
   Detailed hydration/nutrition strategy based on the loop length and available refreshments ({ (customRace?.RaceDetails ?? "N/A") }). Provide a timed intake schedule.

5. LOGISTICS & GEAR STRATEGY:
   - WHAT TO CARRY: Required/recommended gear.
   - AID STATION SWAPS: Timing for gear/shoe swaps based on race duration.

6. REMAINING SEASON FOCUS:
   Key training themes (Vertical, Heat, Recovery, or Speed).

7. NEXT 7 DAYS - ACTION PLAN:
   A concrete daily training plan for the immediate next 7 days.

INSTRUCTION: Provide the response in a professional coaching tone. Use Markdown.";

            var apiKey = envGetter("API_KEY");
            var payload = new {
                contents = new[] { new { parts = new[] { new { text = prompt } } } }
            };

            var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", payload);
            
            if (geminiRes.StatusCode == System.Net.HttpStatusCode.TooManyRequests) {
                L("QUOTA EXHAUSTED: Free Tier limit reached. Please wait 60s.", "ERROR");
                return;
            }

            if (!geminiRes.IsSuccessStatusCode) {
                var err = await geminiRes.Content.ReadAsStringAsync();
                L($"AI API ERROR: {err}", "ERROR");
                return;
            }

            var aiRes = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
            var aiText = aiRes.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

            L("Finalizing Ultra Strategy Document...");
            
            // Re-apply Strava auth to save the report
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new {
                name = $"[StravAI] STRATEGY: {customRace?.Name ?? "Full Season Plan"}",
                type = "Workout",
                start_date_local = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
                elapsed_time = 1,
                description = aiText,
                @private = true
            });
            L("SUCCESS: Season Strategy Deployed.", "SUCCESS");

        } catch (Exception ex) { L($"CRITICAL: {ex.Message}", "ERROR"); }
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

public record StravaWebhookEvent(
    [property: JsonPropertyName("object_type")] string ObjectType, 
    [property: JsonPropertyName("object_id")] long ObjectId, 
    [property: JsonPropertyName("aspect_type")] string AspectType
);
