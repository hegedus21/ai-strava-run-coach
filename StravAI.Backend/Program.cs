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

var app = builder.Build();
app.UseCors("AllowAll");

// Simple in-memory log buffer for the Command Center UI
var logs = new ConcurrentQueue<string>();
void AddLog(string message, string level = "INFO") {
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
    var logEntry = $"[{timestamp}] [{level}] {message}";
    logs.Enqueue(logEntry);
    while (logs.Count > 200) logs.TryDequeue(out _);
    Console.WriteLine(logEntry); 
}

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

app.MapGet("/", () => "StravAI Engine v1.3.5 (Smart Logic) is Online.");

app.MapGet("/health", () => Results.Ok(new { 
    status = "healthy", 
    engine = "StravAI_Core_v1.3.5",
    config = new {
        gemini_ready = !string.IsNullOrEmpty(GetEnv("API_KEY")),
        strava_ready = !string.IsNullOrEmpty(GetEnv("STRAVA_REFRESH_TOKEN"))
    }
}));

app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

// Unified skip logic
bool NeedsAnalysis(string? description) {
    if (string.IsNullOrWhiteSpace(description)) return true;
    
    // Skip if it contains our markers, OR the processed tag
    bool hasReport = description.Contains("StravAI Report");
    bool hasBorder = description.Contains("#");
    bool hasProcessedTag = description.Contains("[StravAI-Processed]");
    
    // Exception: If it's a placeholder, we WANT to analyze it properly
    if (description.Contains("Activity will be analysed later")) return true;

    return !((hasReport && hasBorder) || hasProcessedTag);
}

// Sync Handlers
app.MapPost("/sync", (int? hours, IHttpClientFactory clientFactory) => {
    var label = hours.HasValue ? $"{hours}H Pulse" : "Deep Batch Sync";
    AddLog($"AUTH_ACTION: {label} initiated.");
    
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            var accessToken = await GetStravaAccessToken(client);
            if (string.IsNullOrEmpty(accessToken)) return;

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            string url = "https://www.strava.com/api/v3/athlete/activities?per_page=30";
            if (hours.HasValue) {
                var afterTimestamp = DateTimeOffset.UtcNow.AddHours(-hours.Value).ToUnixTimeSeconds();
                url += $"&after={afterTimestamp}";
            }

            var activities = await client.GetFromJsonAsync<List<JsonElement>>(url);
            int processed = 0;
            foreach (var act in (activities ?? new())) {
                if (act.TryGetProperty("id", out var idProp) && act.GetProperty("type").GetString() == "Run") {
                    var desc = act.TryGetProperty("description", out var d) ? d.GetString() : "";
                    if (NeedsAnalysis(desc)) {
                        await ProcessActivityAsync(idProp.GetInt64(), clientFactory);
                        processed++;
                    }
                }
            }
            AddLog($"SYNC_COMPLETE: {label} finished. {processed} analyzed.");
        } catch (Exception ex) { AddLog($"SYNC_ERR: {ex.Message}", "ERROR"); }
    });
    return Results.Accepted();
});

app.MapPost("/sync/{id}", (long id, IHttpClientFactory clientFactory) => {
    AddLog($"AUTH_ACTION: Re-analyzing activity {id}.");
    _ = Task.Run(() => ProcessActivityAsync(id, clientFactory));
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
        _ = Task.Run(() => ProcessActivityAsync(@event.ObjectId, clientFactory));
    }
    return Results.Ok();
});

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

async Task ProcessActivityAsync(long activityId, IHttpClientFactory clientFactory) {
    var tid = Guid.NewGuid().ToString().Substring(0, 5);
    try {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client);
        if (token == null) return;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var act = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{activityId}");
        
        if (act.GetProperty("type").GetString() != "Run") return;

        var desc = act.TryGetProperty("description", out var d) ? d.GetString() : "";
        if (!NeedsAnalysis(desc)) {
            AddLog($"[{tid}] SKIP: Activity {activityId} already contains analysis.");
            return;
        }

        // Get history to provide context for Gemini
        var hist = await (await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=10")).Content.ReadAsStringAsync();
        client.DefaultRequestHeaders.Authorization = null;

        var prompt = $"Analyze Strava run for {GetEnv("GOAL_RACE_TYPE")} on {GetEnv("GOAL_RACE_DATE")}. " +
                     $"Activity Data: {act.GetRawText()}. History context: {hist}. " +
                     "Look for existing StravAI reports in the history context to provide a continuous coaching experience. " +
                     "Output a professional, encouraging coach report (text only).";

        var apiKey = GetEnv("API_KEY");
        var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", new { 
            contents = new[] { new { parts = new[] { new { text = prompt } } } } 
        });
        
        var aiRes = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
        var aiText = aiRes.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

        var timestamp = GetCetTimestamp();
        var finalDesc = $"################################\nStravAI Report\n---\n{aiText}\n\nAnalysis created at: {timestamp} CET\n*[StravAI-Processed]*\n################################";

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { description = finalDesc });
        AddLog($"[{tid}] SUCCESS: Activity {activityId} updated.");
    } catch (Exception ex) { AddLog($"[{tid}] EXCEPTION: {ex.Message}", "ERROR"); }
}

app.Run();

public record StravaWebhookEvent([property: JsonPropertyName("object_type")] string ObjectType, [property: JsonPropertyName("object_id")] long ObjectId, [property: JsonPropertyName("aspect_type")] string AspectType);
