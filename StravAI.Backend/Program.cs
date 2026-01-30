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

// Simple in-memory log buffer for diagnostics
var logs = new ConcurrentQueue<string>();
void AddLog(string message) {
    logs.Enqueue($"[{DateTime.UtcNow:HH:mm:ss}] {message}");
    while (logs.Count > 50) logs.TryDequeue(out _);
    Console.WriteLine(message);
}

// Configuration
var config = app.Configuration;
string GetEnv(string key) => Environment.GetEnvironmentVariable(key) ?? config[key] ?? "";

app.MapGet("/", () => "StravAI Backend is active.");

// Health & Config Check
app.MapGet("/health", () => {
    return Results.Ok(new { 
        status = "healthy", 
        time = DateTime.UtcNow,
        config = new {
            gemini_api_key = !string.IsNullOrEmpty(GetEnv("API_KEY")),
            strava_client_id = !string.IsNullOrEmpty(GetEnv("STRAVA_CLIENT_ID")),
            strava_client_secret = !string.IsNullOrEmpty(GetEnv("STRAVA_CLIENT_SECRET")),
            strava_refresh_token = !string.IsNullOrEmpty(GetEnv("STRAVA_REFRESH_TOKEN")),
            strava_verify_token = !string.IsNullOrEmpty(GetEnv("STRAVA_VERIFY_TOKEN"))
        }
    });
});

app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

// Test Ping
app.MapPost("/webhook/ping", () => {
    AddLog("UI_PING_RECEIVED: Connectivity verified.");
    return Results.Ok(new { message = "Pong" });
});

// Securely list subscriptions via backend proxy
app.MapGet("/webhook/subscriptions", async (IHttpClientFactory clientFactory) => {
    using var client = clientFactory.CreateClient();
    string clientId = GetEnv("STRAVA_CLIENT_ID");
    string clientSecret = GetEnv("STRAVA_CLIENT_SECRET");
    var res = await client.GetAsync($"https://www.strava.com/api/v3/push_subscriptions?client_id={clientId}&client_secret={clientSecret}");
    var content = await res.Content.ReadAsStringAsync();
    return res.IsSuccessStatusCode ? Results.Content(content, "application/json") : Results.Problem(content);
});

// Webhook Validation (GET)
app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, 
                        [FromQuery(Name = "hub.challenge")] string challenge, 
                        [FromQuery(Name = "hub.verify_token")] string token) => 
{
    AddLog($"Validation Handshake: Mode={mode}, Token={token}");
    if (mode == "subscribe" && token == GetEnv("STRAVA_VERIFY_TOKEN")) {
        AddLog("SUCCESS: Handshake Complete.");
        return Results.Ok(new { hub_challenge = challenge });
    }
    return Results.BadRequest();
});

// Proxy Registration (POST)
app.MapPost("/webhook/register", async ([FromBody] RegisterRequest req, IHttpClientFactory clientFactory) => {
    if (req.VerifyToken != GetEnv("STRAVA_VERIFY_TOKEN")) {
        return Results.BadRequest(new { message = "Verify token mismatch." });
    }

    using var client = clientFactory.CreateClient();
    var formData = new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
        new KeyValuePair<string, string>("callback_url", req.CallbackUrl),
        new KeyValuePair<string, string>("verify_token", req.VerifyToken)
    });

    var res = await client.PostAsync("https://www.strava.com/api/v3/push_subscriptions", formData);
    var content = await res.Content.ReadAsStringAsync();
    return res.IsSuccessStatusCode ? Results.Ok(JsonSerializer.Deserialize<JsonElement>(content)) : Results.Problem(content);
});

// Event Handling (POST)
app.MapPost("/webhook", ([FromBody] StravaWebhookEvent @event, IHttpClientFactory clientFactory, ILogger<Program> logger) => 
{
    AddLog($"Webhook Event: {@event.ObjectType} {@event.AspectType} (ID: {@event.ObjectId})");
    if (@event.ObjectType == "activity" && (@event.AspectType == "create" || @event.AspectType == "update")) {
        _ = Task.Run(async () => {
            try { await ProcessActivityAsync(@event.ObjectId, clientFactory, logger); }
            catch (Exception ex) { AddLog($"ERROR activity {@event.ObjectId}: {ex.Message}"); }
        });
    }
    return Results.Ok();
});

async Task ProcessActivityAsync(long activityId, IHttpClientFactory clientFactory, ILogger logger) {
    using var client = clientFactory.CreateClient();
    AddLog($"Analyzing activity {activityId}...");
    
    var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
        new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN")),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    
    if (!authRes.IsSuccessStatusCode) return;
    var authData = await authRes.Content.ReadFromJsonAsync<JsonElement>();
    var accessToken = authData.GetProperty("access_token").GetString();

    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var activity = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{activityId}");
    
    if (activity.GetProperty("type").GetString() != "Run") return;

    var historyRes = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=15");
    var historyJson = await historyRes.Content.ReadAsStringAsync();

    var prompt = $"Analyze this Strava run and provide a coaching update. Activity: {activity.GetRawText()} Recent History: {historyJson}. Return a professional 3-sentence summary followed by a workout suggestion for tomorrow.";
    
    var geminiRequest = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
    var geminiRes = await client.PostAsJsonAsync(
        $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={GetEnv("API_KEY")}", 
        geminiRequest
    );

    if (!geminiRes.IsSuccessStatusCode) return;
    var geminiData = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
    var coachNotes = geminiData.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

    await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { 
        description = $"[StravAI Performance Report]\n\n{coachNotes}\n\n[StravAI-Processed]" 
    });
    AddLog($"SUCCESS: Activity {activityId} updated.");
}

app.Run();

public record RegisterRequest(string CallbackUrl, string VerifyToken);
public record StravaWebhookEvent(
    [property: JsonPropertyName("object_type")] string ObjectType,
    [property: JsonPropertyName("object_id")] long ObjectId,
    [property: JsonPropertyName("aspect_type")] string AspectType
);
