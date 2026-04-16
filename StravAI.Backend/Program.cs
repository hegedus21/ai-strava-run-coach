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

    var runs = activities
        .Where(a => a.TryGetProperty("type", out var t) &&
                    t.ValueKind == JsonValueKind.String &&
                    t.GetString() == "Run")
        .Select(a => new {
            Date = DateTime.Parse(
                a.GetProperty("start_date").GetString()!,
                null,
                System.Globalization.DateTimeStyles.AdjustToUniversal
            ),
            DistKm = a.GetProperty("distance").GetDouble() / 1000,
            Speed  = a.GetProperty("average_speed").GetDouble(),
            HrAvg  = a.TryGetProperty("average_heartrate", out var hr) && hr.ValueKind == JsonValueKind.Number
                       ? hr.GetDouble() : (double?)null,
            NP = a.TryGetProperty("weighted_average_watts", out var np) && np.ValueKind == JsonValueKind.Number
                       ? np.GetDouble() : (double?)null
        })
        .OrderByDescending(a => a.Date)
        .ToList();

    var sb = new System.Text.StringBuilder();

    sb.AppendLine("## MONTHLY SUMMARY (all-time)");
    foreach (var month in runs.GroupBy(a => a.Date.ToString("yyyy-MM")).OrderByDescending(g => g.Key)) {
        var totalKm  = month.Sum(a => a.DistKm);
        var avgPace = month
                        .Where(a => a.Speed > 0)
                        .Select(a => 16.6667 / a.Speed)
                        .DefaultIfEmpty(0)
                        .Average();
        var avgHr    = month.Where(a => a.HrAvg.HasValue).Select(a => a.HrAvg!.Value).DefaultIfEmpty(0).Average();
        var longRun  = month.Max(a => a.DistKm);
        var npValues = month.Where(a => a.NP.HasValue).Select(a => a.NP!.Value).ToList();
        double? avgNP = npValues.Count > 0 ? npValues.Average() : null;

        var runCount = month.Count();
        sb.AppendLine($"- {month.Key}: {runCount} runs, {totalKm:F1}km, " +
                      $"avg pace {avgPace:F2}m/k, longest {longRun:F1}km" +
                      (avgHr > 0 ? $", avg HR {avgHr:F0}bpm" : "") +
                      (avgNP.HasValue ? $", avg NP {avgNP.Value:F0}W" : ""));
    }

    sb.AppendLine("\n## WEEKLY DETAIL (last 8 weeks)");
    var eightWeeksAgo = DateTime.UtcNow.AddDays(-56);
    foreach (var week in runs
        .Where(a => a.Date >= eightWeeksAgo)
        .GroupBy(a => a.Date.AddDays(-(int)a.Date.DayOfWeek + (int)DayOfWeek.Monday).ToString("yyyy-MM-dd"))
        .OrderByDescending(g => g.Key)) {
        var totalKm  = week.Sum(a => a.DistKm);
        var avgPace = week
                        .Where(a => a.Speed > 0)
                        .Select(a => 16.6667 / a.Speed)
                        .DefaultIfEmpty(0)
                        .Average();
        var longRun  = week.Max(a => a.DistKm);
        var npValues = week.Where(a => a.NP.HasValue).Select(a => a.NP!.Value).ToList();
        double? avgNP = npValues.Count > 0 ? npValues.Average() : null;
        sb.AppendLine($"- Week of {week.Key}: {week.Count()} runs, {totalKm:F1}km, " +
              $"avg pace {avgPace:F2}m/k, longest {longRun:F1}km" +
              (avgNP.HasValue ? $", avg NP {avgNP.Value:F0}W" : ""));
    }

    sb.AppendLine("\n## RECENT INDIVIDUAL RUNS (last 30 days)");
    foreach (var a in runs.Where(r => r.Date >= DateTime.UtcNow.AddDays(-30))) {
        var pace = a.Speed > 0 ? 16.6667 / a.Speed : 0;
        sb.AppendLine($"- {a.Date:yyyy-MM-dd}: {a.DistKm:F2}km @ {pace:F2}m/k" +
                      (a.HrAvg.HasValue ? $", HR {a.HrAvg:F0}bpm" : ""));
    }

    sb.AppendLine("\n## PERSONAL BESTS / NOTABLE RUNS");
    foreach (var a in runs.OrderByDescending(a => a.DistKm).Take(5)) {
        var pace = a.Speed > 0 ? 16.6667 / a.Speed : 0;
        sb.AppendLine($"- LONGEST: {a.Date:yyyy-MM-dd}: {a.DistKm:F2}km @ {pace:F2}m/k");
    }
    foreach (var a in runs.Where(a => a.DistKm >= 5).OrderBy(a => a.Speed > 0 ? 16.6667 / a.Speed : 999).Take(5)) {
        var pace = a.Speed > 0 ? 16.6667 / a.Speed : 0;
        sb.AppendLine($"- FASTEST: {a.Date:yyyy-MM-dd}: {a.DistKm:F2}km @ {pace:F2}m/k");
    }

    sb.AppendLine("\n## PERFORMANCE METRICS (DERIVED)");

    var allNP = runs.Where(a => a.NP.HasValue).Select(a => a.NP!.Value).ToList();
    double? avgNPAll = allNP.Count > 0 ? allNP.Average() : null;

    var allHr = runs.Where(a => a.HrAvg.HasValue).Select(a => a.HrAvg!.Value).ToList();
    double? avgHrAll = allHr.Count > 0 ? allHr.Average() : null;

    if (avgNPAll.HasValue)
        sb.AppendLine($"- Avg Normalized Power (all-time): {avgNPAll.Value:F0}W");

    if (avgHrAll.HasValue)
        sb.AppendLine($"- Avg Heart Rate (all-time): {avgHrAll.Value:F0} bpm");

    var totalLoad = runs.Sum(a => a.DistKm * (a.NP ?? 200));
    sb.AppendLine($"- Estimated Training Load: {totalLoad:F0}");

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

app.MapPost("/sync/season", ([FromBody] SeasonRequest? req, IHttpClientFactory clientFactory) => {
    AddLog("AUTH_ACTION: Deep Season Strategy Update triggered.");
    AddLog($"DEBUG: Questions received = '{req?.Questions ?? "NULL"}'");
    _ = Task.Run(() => SeasonStrategyEngine.ProcessSeasonAnalysisAsync(clientFactory, GetEnv, logs, GetCetTimestamp, CompactSummarize, null, req?.Questions));
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

    public static async Task<List<JsonElement>> GetAllActivitiesAsync(HttpClient client, int maxCount = 800)
    {
        var all = new List<JsonElement>();
        int page = 1;
        const int perPage = 200;

        while (all.Count < maxCount)
        {
            var batch = await client.GetFromJsonAsync<List<JsonElement>>(
                $"https://www.strava.com/api/v3/athlete/activities?per_page={perPage}&page={page}");

            if (batch == null || batch.Count == 0) break;

            all.AddRange(batch);
            if (batch.Count < perPage) break;
            page++;
        }
         return all.Take(maxCount).ToList();
    }

    public static async Task ProcessSeasonAnalysisAsync(IHttpClientFactory clientFactory, Func<string, string> envGetter, ConcurrentQueue<string> logs, Func<string> timeGetter, Func<List<JsonElement>, string> summarizer, CustomRaceRequest? customRace = null, string? athleteQuestions = null) {
        void L(string m, string lvl = "INFO") => logs.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] [{lvl}] {m}");

        try {
            using var client = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(client, envGetter);
            if (token == null) { L("Auth Failed", "ERROR"); return; }
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);

            L("Gathering Multi-Month Performance Trends...");
            var historyData = await GetAllActivitiesAsync(client, maxCount: 800);
            var historySummary = summarizer(historyData ?? new());

            client.DefaultRequestHeaders.Authorization = null;

            var questionsSection = !string.IsNullOrWhiteSpace(athleteQuestions) ? $"\n\nATHLETE QUESTIONS: The athlete has asked the following specific questions. You MUST answer EACH question below one by one, using the actual activity data provided. Do NOT invent or substitute different questions. Answer only what is asked:\n{athleteQuestions}" : "";

            var prompt = $@"
{(!string.IsNullOrWhiteSpace(athleteQuestions) ? 
$@"⚠️ PRIORITY TASK: The athlete has asked the following questions. You MUST answer ALL of them in section 6, one by one, numbered, using ONLY the actual activity data below. This is mandatory.
ATHLETE QUESTIONS:
{athleteQuestions}
---" : "")}

ATHLETE GOAL: {(customRace?.Name ?? envGetter("GOAL_RACE_TYPE"))} on {(customRace?.Date ?? envGetter("GOAL_RACE_DATE"))} (Target Time: {(customRace?.TargetTime ?? envGetter("GOAL_RACE_TIME"))}).

HISTORY CONTEXT (FULL SEASON SCAN):
{historySummary}

RACE SPECIFICS:
{(customRace?.RaceDetails ?? "N/A")}

TASK: Deep Season Strategy. Include:
1. EXECUTIVE SUMMARY
2. FEASIBILITY (FTP/Aerobic Base + PROBABILITY %)
3. 3-TIER PACE STRATEGY (Optimistic/Realistic/Pessimistic)
4. NUTRITION & LOGISTICS
5. ACTION PLAN (Next 7 Days)
{(!string.IsNullOrWhiteSpace(athleteQuestions) ? 
@"6. ATHLETE Q&A — Answer each question listed at the TOP of this prompt, numbered, one by one. Do NOT skip, invent, or substitute any question. Use only the data provided." : "")}

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
public record SeasonRequest(string? Questions);
