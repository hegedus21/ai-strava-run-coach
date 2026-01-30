using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Port configuration
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");

builder.Services.AddHttpClient();
builder.Services.AddLogging();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();
app.UseCors("AllowAll");

// Simple in-memory log buffer for diagnostics UI
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

// Global Request Logger Middleware
app.Use(async (context, next) => {
    if (context.Request.Path != "/logs" && context.Request.Path != "/health") {
        AddLog($"HTTP_{context.Request.Method}: {context.Request.Path}");
    }
    await next();
});

app.MapGet("/", () => "StravAI Backend is fully operational.");
app.MapGet("/health", () => Results.Ok(new { status = "healthy", time = DateTime.UtcNow }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

// Manual Sync Trigger
app.MapPost("/sync", (IHttpClientFactory clientFactory) => {
    AddLog("ACTION: Manual Global Sync triggered from console.");
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            AddLog("SYNC_STEP: Fetching last 30 activities...");
            
            var accessToken = await GetStravaAccessToken(client);
            if (string.IsNullOrEmpty(accessToken)) return;

            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            var activities = await client.GetFromJsonAsync<List<JsonElement>>("https://www.strava.com/api/v3/athlete/activities?per_page=30");
            
            int processedCount = 0;
            foreach (var act in activities ?? new()) {
                var id = act.GetProperty("id").GetInt64();
                var type = act.GetProperty("type").GetString();
                var desc = act.TryGetProperty("description", out var d) ? d.GetString() : "";

                if (type == "Run" && (string.IsNullOrEmpty(desc) || !desc.Contains("[StravAI-Processed]"))) {
                    AddLog($"SYNC_HIT: Found pending run '{act.GetProperty("name").GetString()}'. Processing...");
                    await ProcessActivityAsync(id, clientFactory);
                    processedCount++;
                }
            }
            AddLog($"SYNC_COMPLETE: Processed {processedCount} activities.");
        } catch (Exception ex) {
            AddLog($"SYNC_ERROR: {ex.Message}", "ERROR");
        }
    });
    return Results.Accepted();
});

app.MapGet("/webhook/subscriptions", async (IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        string clientId = GetEnv("STRAVA_CLIENT_ID");
        string clientSecret = GetEnv("STRAVA_CLIENT_SECRET");
        var res = await client.GetAsync($"https://www.strava.com/api/v3/push_subscriptions?client_id={clientId}&client_secret={clientSecret}");
        return Results.Content(await res.Content.ReadAsStringAsync(), "application/json");
    } catch (Exception ex) {
        return Results.Problem(ex.Message);
    }
});

app.MapDelete("/webhook/subscriptions/{id}", async (int id, IHttpClientFactory clientFactory) => {
    AddLog($"ACTION: Deleting webhook subscription {id}...");
    try {
        using var client = clientFactory.CreateClient();
        string clientId = GetEnv("STRAVA_CLIENT_ID");
        string clientSecret = GetEnv("STRAVA_CLIENT_SECRET");
        var res = await client.DeleteAsync($"https://www.strava.com/api/v3/push_subscriptions/{id}?client_id={clientId}&client_secret={clientSecret}");
        if (res.IsSuccessStatusCode) {
            AddLog($"SUCCESS: Webhook {id} removed.");
            return Results.NoContent();
        }
        return Results.Problem(await res.Content.ReadAsStringAsync());
    } catch (Exception ex) {
        return Results.Problem(ex.Message);
    }
});

app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, [FromQuery(Name = "hub.challenge")] string challenge, [FromQuery(Name = "hub.verify_token")] string token) => 
{
    var expected = GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";
    if (mode == "subscribe" && token == expected) {
        AddLog("HANDSHAKE_SUCCESS: Handshake verified.");
        return Results.Ok(new Dictionary<string, string> { { "hub.challenge", challenge } });
    }
    return Results.BadRequest("Verification Failed.");
});

app.MapPost("/webhook/register", async ([FromBody] RegisterRequest req, IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var formData = new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
            new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
            new KeyValuePair<string, string>("callback_url", req.CallbackUrl),
            new KeyValuePair<string, string>("verify_token", req.VerifyToken)
        });
        var res = await client.PostAsync("https://www.strava.com/api/v3/push_subscriptions", formData);
        return Results.Content(await res.Content.ReadAsStringAsync(), "application/json");
    } catch (Exception ex) {
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/webhook", ([FromBody] StravaWebhookEvent @event, IHttpClientFactory clientFactory) => 
{
    if (@event.ObjectType == "activity" && (@event.AspectType == "create" || @event.AspectType == "update")) {
        AddLog($"WEBHOOK_EVENT: Activity {@event.ObjectId} updated. Starting pipeline...");
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
    var traceId = Guid.NewGuid().ToString().Substring(0, 5);
    try {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client);
        if (token == null) { AddLog($"[{traceId}] Auth Failure.", "ERROR"); return; }

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var activity = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{activityId}");
        
        if (activity.GetProperty("type").GetString() != "Run") return;

        var historyRes = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=10");
        var historyJson = await historyRes.Content.ReadAsStringAsync();

        var prompt = $"Analyze this Strava run for a runner training for {GetEnv("GOAL_RACE_TYPE")} on {GetEnv("GOAL_RACE_DATE")}. Activity: {activity.GetRawText()}. History: {historyJson}. Provide a professional coaching summary and a training tip for tomorrow.";
        var geminiRequest = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
        
        var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={GetEnv("API_KEY")}", geminiRequest);
        var geminiData = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
        var aiNotes = geminiData.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

        var cetTime = DateTime.UtcNow.AddHours(1);
        var timestampStr = cetTime.ToString("yyyy-MM-dd HH:mm:ss") + " CET";

        await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { 
            description = aiNotes + $"\n\nAnalyzed at: {timestampStr}\n[StravAI-Processed]" 
        });
        AddLog($"[{traceId}] SUCCESS: Activity {activityId} updated.");
    } catch (Exception ex) {
        AddLog($"[{traceId}] ERROR: {ex.Message}", "ERROR");
    }
}

app.Run();

public record RegisterRequest(string CallbackUrl, string VerifyToken);
public record StravaWebhookEvent(
    [property: JsonPropertyName("object_type")] string ObjectType,
    [property: JsonPropertyName("object_id")] long ObjectId,
    [property: JsonPropertyName("aspect_type")] string AspectType
);
