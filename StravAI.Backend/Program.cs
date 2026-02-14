using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

var builder = WebApplication.CreateBuilder(args);

// Port configuration
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

builder.Services.AddHttpClient("", client => {
    client.Timeout = TimeSpan.FromMinutes(5);
});
builder.Services.AddLogging();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().WithMethods("GET", "POST", "PUT", "DELETE", "OPTIONS").AllowAnyHeader()));

var logs = new ConcurrentQueue<string>();
builder.Services.AddSingleton(logs);
builder.Services.AddSingleton<RaceTrackerManager>();
builder.Services.AddHostedService<SeasonBackgroundWorker>();
builder.Services.AddHostedService<RacePollingWorker>();

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
    } catch { return DateTime.UtcNow.AddHours(1).ToString("dd/yyyy HH:mm:ss") + " CET"; }
}

// --- Security Middleware ---
app.Use(async (context, next) => {
    var path = context.Request.Path.Value ?? "";
    if (path == "/" || path == "/health" || path.StartsWith("/webhook", StringComparison.OrdinalIgnoreCase) || path.StartsWith("/race/test-parse", StringComparison.OrdinalIgnoreCase)) {
        await next();
        return;
    }
    var expectedSecret = GetEnv("BACKEND_SECRET");
    if (string.IsNullOrEmpty(expectedSecret)) expectedSecret = GetEnv("STRAVA_VERIFY_TOKEN");
    
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

app.MapGet("/", () => "StravAI Engine v1.4.9_STABLE is Online.");
app.MapGet("/health", () => Results.Ok(new { status = "healthy", version = "1.4.9" }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

app.MapGet("/race/test-parse", () => Results.Ok(new { status = "Ready", methods = "POST", hint = "Send JSON via POST" }));

app.MapPost("/race/test-parse", async ([FromBody] RaceTestRequest req, IHttpClientFactory clientFactory) => {
    AddLog($"DIAG_ACTION: Scraper Request for {req.Url}");
    var debugLogs = new List<string>();
    try {
        if (string.IsNullOrEmpty(req.Url)) return Results.BadRequest("URL is required.");
        
        using var client = clientFactory.CreateClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36");
        
        debugLogs.Add($"FETCHING: {req.Url}");
        var response = await client.GetAsync(req.Url);
        
        if (!response.IsSuccessStatusCode) {
            debugLogs.Add($"NETWORK_ERROR: HTTP {(int)response.StatusCode} {response.StatusCode}");
            return Results.Ok(new { success = false, count = 0, checkpoints = new List<RaceCheckpoint>(), debugLogs });
        }

        var html = await response.Content.ReadAsStringAsync();
        debugLogs.Add($"SUCCESS: Content Received ({html.Length} bytes)");
        
        var (checkpoints, scraperLogs) = RaceScraper.ParseCheckpoints(html);
        debugLogs.AddRange(scraperLogs);
        
        return Results.Ok(new { success = true, count = checkpoints.Count, checkpoints, debugLogs });
    } catch (Exception ex) {
        debugLogs.Add($"EXCEPTION: {ex.Message}");
        return Results.Ok(new { success = false, count = 0, checkpoints = new List<RaceCheckpoint>(), debugLogs });
    }
});

app.MapPost("/race/start", ([FromBody] LiveRaceConfig config, RaceTrackerManager tracker) => {
    tracker.Start(config);
    return Results.Ok(new { status = "Tracker Engaged" });
});

app.MapPost("/race/stop", (RaceTrackerManager tracker) => {
    tracker.Stop();
    return Results.Ok(new { status = "Tracker Disengaged" });
});

app.MapGet("/race/status", (RaceTrackerManager tracker) => Results.Ok(tracker.GetStatus()));

// Fallback for debugging path issues
app.MapFallback((HttpContext context) => {
    return Results.NotFound(new { error = "Route Not Found", path = context.Request.Path.Value });
});

app.Run();

// --- Logic Implementation ---

public static class RaceScraper {
    public static (List<RaceCheckpoint> Checkpoints, List<string> Logs) ParseCheckpoints(string html) {
        var results = new List<RaceCheckpoint>();
        var logs = new List<string>();
        
        var tables = Regex.Matches(html, @"<table[^>]*>(.*?)<\/table>", RegexOptions.Singleline);
        logs.Add($"Found {tables.Count} table(s) in HTML.");

        foreach (Match table in tables) {
            var tableHtml = table.Groups[1].Value;
            
            // Standard RunTiming Layout detection
            int nameIdx = -1, kmIdx = -1, timeIdx = -1, paceIdx = -1;
            
            var headerMatch = Regex.Match(tableHtml, @"<thead[^>]*>(.*?)<\/thead>", RegexOptions.Singleline);
            string headerContent = headerMatch.Success ? headerMatch.Groups[1].Value : tableHtml;
            
            var headerCells = Regex.Matches(headerContent, @"<(th|td)[^>]*>(.*?)<\/\1>", RegexOptions.Singleline);
            if (headerCells.Count > 0) {
                for (int i = 0; i < headerCells.Count; i++) {
                    var hText = Clean(headerCells[i].Groups[2].Value).ToLower();
                    if (hText.Contains("mérőpont")) nameIdx = i;
                    else if (hText.Contains("táv") || hText.Contains("km")) {
                        if (kmIdx == -1) kmIdx = i;
                    }
                    else if (hText.Contains("versenyidő")) timeIdx = i;
                    else if (timeIdx == -1 && hText.Contains("idő")) timeIdx = i;
                    else if (hText.Contains("tempó") || hText.Contains("pace")) paceIdx = i;
                }
            }

            // Fallback to common UB indices if detection was incomplete
            if (nameIdx == -1) nameIdx = 1;
            if (kmIdx == -1) kmIdx = 2;
            if (timeIdx == -1) timeIdx = 4;
            if (paceIdx == -1) paceIdx = 5;

            logs.Add($"HEURISTIC: Mapping -> Name:{nameIdx}, Km:{kmIdx}, Time:{timeIdx}, Pace:{paceIdx}");

            var rows = Regex.Matches(tableHtml, @"<tr[^>]*>(.*?)<\/tr>", RegexOptions.Singleline);
            foreach (Match row in rows) {
                var cells = Regex.Matches(row.Groups[1].Value, @"<td[^>]*>(.*?)<\/td>", RegexOptions.Singleline);
                int maxRequired = Math.Max(nameIdx, Math.Max(kmIdx, Math.Max(timeIdx, paceIdx)));
                
                if (cells.Count > maxRequired) {
                    try {
                        var name = Clean(cells[nameIdx].Groups[1].Value);
                        var kmStr = Clean(cells[kmIdx].Groups[1].Value).Replace(",", ".");
                        var time = Clean(cells[timeIdx].Groups[1].Value);
                        var pace = Clean(cells[paceIdx].Groups[1].Value);
                        
                        // Valid RunTiming row has a numeric distance and a time with colon
                        if (double.TryParse(kmStr, out double km) && km >= 0 && !string.IsNullOrEmpty(time) && time.Contains(":")) {
                            results.Add(new RaceCheckpoint(name, km, time, pace));
                        }
                    } catch { }
                }
            }

            if (results.Count > 0) break;
        }
        
        logs.Add($"SCRAPER_FINAL: Identified {results.Count} checkpoints.");
        return (results.OrderBy(c => c.DistanceKm).ToList(), logs);
    }

    private static string Clean(string html) => Regex.Replace(html, "<.*?>", "").Trim();
}

public class RaceTrackerManager {
    private LiveRaceConfig? _config;
    private RaceCheckpoint? _lastCheckpoint;
    private int _checkpointsFound = 0;
    private string _lastUpdate = "NEVER";
    private string? _latestAdvice;
    private bool _isActive = false;

    public void Start(LiveRaceConfig config) { _config = config; _isActive = true; _lastUpdate = DateTime.UtcNow.ToString("O"); }
    public void Stop() { _isActive = false; }
    public LiveRaceStatus GetStatus() => new LiveRaceStatus(_isActive, _lastCheckpoint, _checkpointsFound, _lastUpdate, _latestAdvice);
    public LiveRaceConfig? GetConfig() => _config;
    public void UpdateCheckpoint(RaceCheckpoint cp, string? advice) {
        _lastCheckpoint = cp;
        _checkpointsFound++;
        _lastUpdate = DateTime.UtcNow.ToString("O");
        _latestAdvice = advice;
    }
}

public class RacePollingWorker : BackgroundService {
    private readonly RaceTrackerManager _tracker;
    private readonly IHttpClientFactory _cf;
    private readonly ConcurrentQueue<string> _logs;
    private readonly IConfiguration _cfg;

    public RacePollingWorker(RaceTrackerManager tracker, IHttpClientFactory cf, ConcurrentQueue<string> logs, IConfiguration cfg) {
        _tracker = tracker; _cf = cf; _logs = logs; _cfg = cfg;
    }

    protected override async Task ExecuteAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) {
            var status = _tracker.GetStatus();
            if (status.IsActive) {
                var config = _tracker.GetConfig();
                if (config != null) await PollRaceAsync(config);
            }
            await Task.Delay(TimeSpan.FromMinutes(2), ct);
        }
    }

    private async Task PollRaceAsync(LiveRaceConfig config) {
        try {
            using var client = _cf.CreateClient();
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/119.0.0.0 Safari/537.36");
            
            var response = await client.GetAsync(config.TimingUrl);
            if (!response.IsSuccessStatusCode) return;

            var html = await response.Content.ReadAsStringAsync();
            var (checkpoints, _) = RaceScraper.ParseCheckpoints(html);
            
            if (checkpoints.Count > 0) {
                var latest = checkpoints.Last();
                var currentStatus = _tracker.GetStatus();
                
                if (currentStatus.LastCheckpoint == null || currentStatus.LastCheckpoint.Name != latest.Name) {
                    _logs.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] [RACE] New Checkpoint Detected: {latest.Name} ({latest.DistanceKm}km)");
                    var advice = await GetAIAdvice(latest, config);
                    await SendTelegramMessage(config, latest, advice);
                    _tracker.UpdateCheckpoint(latest, advice);
                }
            }
        } catch (Exception ex) {
            _logs.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] [ERROR] Race Polling Failed: {ex.Message}");
        }
    }

    private async Task<string> GetAIAdvice(RaceCheckpoint cp, LiveRaceConfig config) {
        try {
            using var client = _cf.CreateClient();
            var apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? _cfg["API_KEY"];
            var prompt = $@"ROLE: Elite Ultra Running Coach.
EVENT: {config.RaceName}.
TELEMETRY: {cp.Name} @ {cp.DistanceKm}km, Time: {cp.Time}, Pace: {cp.Pace}.
Provide 2 sentences of tactical advice for the runner.";

            var payload = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
            var res = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", payload);
            if (res.IsSuccessStatusCode) {
                var json = await res.Content.ReadFromJsonAsync<JsonElement>();
                return json.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "...";
            }
        } catch { }
        return "Steady progress. Stay fueled and hydrate.";
    }

    private async Task SendTelegramMessage(LiveRaceConfig config, RaceCheckpoint cp, string advice) {
        try {
            using var client = _cf.CreateClient();
            var text = $"🏃‍♂️ *Checkpoint: {cp.Name}*\n📍 {cp.DistanceKm}km\n⏱ {cp.Time}\n⚡️ {cp.Pace}\n\n🤖 *Coach Advice:*\n{advice}";
            var url = $"https://api.telegram.org/bot{config.TelegramBotToken}/sendMessage";
            await client.PostAsJsonAsync(url, new { chat_id = config.TelegramChatId, text = text, parse_mode = "Markdown" });
        } catch { }
    }
}

public class SeasonBackgroundWorker : BackgroundService {
    private readonly IHttpClientFactory _cf;
    private readonly ConcurrentQueue<string> _l;
    private readonly IConfiguration _cfg;
    public SeasonBackgroundWorker(IHttpClientFactory cf, ConcurrentQueue<string> l, IConfiguration cfg) { _cf = cf; _l = l; _cfg = cfg; }
    protected override async Task ExecuteAsync(CancellationToken ct) {
        while (!ct.IsCancellationRequested) await Task.Delay(TimeSpan.FromHours(1), ct);
    }
}

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
            var historyData = await client.GetFromJsonAsync<List<JsonElement>>("https://www.strava.com/api/v3/athlete/activities?per_page=200");
            var historySummary = summarizer(historyData ?? new());
            client.DefaultRequestHeaders.Authorization = null;
            var prompt = $@"GOAL: {(customRace?.Name ?? "Main Race")} on {(customRace?.Date ?? "2025-12-31")}.
HISTORY: {historySummary}
Return coaching strategy.";
            var apiKey = envGetter("API_KEY");
            var payload = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
            var res = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", payload);
            if (res.IsSuccessStatusCode) {
                var aiRes = await res.Content.ReadFromJsonAsync<JsonElement>();
                var aiText = aiRes.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
                if (!string.IsNullOrEmpty(aiText)) {
                    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
                    await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name = $"[StravAI] Strategy", type = "Workout", description = aiText, @private = true });
                    L("Strategy published.", "SUCCESS");
                }
            } else { L($"API Error: {res.StatusCode}", "ERROR"); }
        } catch (Exception ex) { L($"Crash: {ex.Message}", "ERROR"); }
    }
}

public record RaceTestRequest([property: JsonPropertyName("url")] string Url);
public record CustomRaceRequest(string Name, string Distance, string Date, string TargetTime, string? RaceDetails);
public record RaceCheckpoint(string Name, double DistanceKm, string Time, string Pace);
public record LiveRaceConfig(string TimingUrl, string TelegramBotToken, string TelegramChatId, string TargetPace, string RaceName, double TotalDistance);
public record LiveRaceStatus(bool IsActive, RaceCheckpoint? LastCheckpoint, int CheckpointsFound, string LastUpdate, string? LatestAdvice);
public record StravaWebhookEvent([property: JsonPropertyName("object_id")] long ObjectId, [property: JsonPropertyName("object_type")] string ObjectType);
