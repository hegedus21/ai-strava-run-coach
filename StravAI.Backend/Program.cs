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
string GeminiApiKey = Environment.GetEnvironmentVariable("API_KEY") ?? config["GEMINI_API_KEY"] ?? "";
string StravaClientId = Environment.GetEnvironmentVariable("STRAVA_CLIENT_ID") ?? config["STRAVA_CLIENT_ID"] ?? "";
string StravaClientSecret = Environment.GetEnvironmentVariable("STRAVA_CLIENT_SECRET") ?? config["STRAVA_CLIENT_SECRET"] ?? "";
string StravaRefreshToken = Environment.GetEnvironmentVariable("STRAVA_REFRESH_TOKEN") ?? config["STRAVA_REFRESH_TOKEN"] ?? "";
string WebhookVerifyToken = Environment.GetEnvironmentVariable("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";

app.MapGet("/", () => "StravAI Backend is active.");

// Health & Diagnostics Endpoints
app.MapGet("/health", () => Results.Ok(new { status = "healthy", time = DateTime.UtcNow }));
app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

// Securely list subscriptions via backend proxy to avoid CORS
app.MapGet("/webhook/subscriptions", async (IHttpClientFactory clientFactory) => {
    using var client = clientFactory.CreateClient();
    var res = await client.GetAsync($"https://www.strava.com/api/v3/push_subscriptions?client_id={StravaClientId}&client_secret={StravaClientSecret}");
    var content = await res.Content.ReadAsStringAsync();
    
    if (res.IsSuccessStatusCode) {
        return Results.Content(content, "application/json");
    }
    return Results.Problem(content);
});

// Webhook Validation (GET) - Strava calls this to verify your server
app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, 
                        [FromQuery(Name = "hub.challenge")] string challenge, 
                        [FromQuery(Name = "hub.verify_token")] string token) => 
{
    AddLog($"Validation Handshake Received: Mode={mode}, Token={token}");
    if (mode == "subscribe" && token == WebhookVerifyToken) {
        AddLog("SUCCESS: Webhook Handshake Complete.");
        return Results.Ok(new { hub_challenge = challenge });
    }
    AddLog($"FAILURE: Token mismatch. Expected {WebhookVerifyToken}, got {token}");
    return Results.BadRequest();
});

// Proxy Registration (POST) - UI calls this to trigger the Strava link
app.MapPost("/webhook/register", async ([FromBody] RegisterRequest req, IHttpClientFactory clientFactory) => {
    AddLog($"Registration Request: Callback={req.CallbackUrl}, Token={req.VerifyToken}");
    
    if (req.VerifyToken != WebhookVerifyToken) {
        return Results.BadRequest(new { message = "Verify token does not match backend configuration." });
    }

    using var client = clientFactory.CreateClient();
    var formData = new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", StravaClientId),
        new KeyValuePair<string, string>("client_secret", StravaClientSecret),
        new KeyValuePair<string, string>("callback_url", req.CallbackUrl),
        new KeyValuePair<string, string>("verify_token", req.VerifyToken)
    });

    var res = await client.PostAsync("https://www.strava.com/api/v3/push_subscriptions", formData);
    var content = await res.Content.ReadAsStringAsync();
    
    if (res.IsSuccessStatusCode) {
        AddLog("SUCCESS: Registered subscription with Strava.");
        return Results.Ok(JsonSerializer.Deserialize<JsonElement>(content));
    } else {
        AddLog($"FAILURE: Strava Registration Error: {content}");
        return Results.Problem(content);
    }
});

// Event Handling (POST) - Strava calls this when you finish a run
app.MapPost("/webhook", ([FromBody] StravaWebhookEvent @event, IHttpClientFactory clientFactory, ILogger<Program> logger) => 
{
    AddLog($"Webhook Event: {@event.ObjectType} {@event.AspectType} (ID: {@event.ObjectId})");
    
    if (@event.ObjectType == "activity" && (@event.AspectType == "create" || @event.AspectType == "update")) {
        _ = Task.Run(async () => {
            try {
                await ProcessActivityAsync(@event.ObjectId, clientFactory, logger);
            } catch (Exception ex) {
                AddLog($"ERROR processing activity {@event.ObjectId}: {ex.Message}");
            }
        });
    }
    return Results.Ok();
});

async Task ProcessActivityAsync(long activityId, IHttpClientFactory clientFactory, ILogger logger) {
    using var client = clientFactory.CreateClient();
    AddLog($"Starting background analysis for activity {activityId}...");
    
    // 1. Refresh Token
    var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", StravaClientId),
        new KeyValuePair<string, string>("client_secret", StravaClientSecret),
        new KeyValuePair<string, string>("refresh_token", StravaRefreshToken),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    
    if (!authRes.IsSuccessStatusCode) {
        AddLog("ERROR: Failed to refresh Strava token. Check ClientId/Secret/RefreshToken.");
        return;
    }
    var authData = await authRes.Content.ReadFromJsonAsync<JsonElement>();
    var accessToken = authData.GetProperty("access_token").GetString();

    // 2. Fetch Activity
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var activity = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{activityId}");
    
    if (activity.GetProperty("type").GetString() != "Run") {
        AddLog($"Skipped: Activity {activityId} is '{activity.GetProperty("type").GetString()}', not 'Run'.");
        return;
    }

    // 3. History for context
    var historyRes = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=15");
    var historyJson = await historyRes.Content.ReadAsStringAsync();

    // 4. Prompt Gemini
    AddLog($"Consulting Gemini AI for activity {activityId}...");
    var prompt = $"Analyze this Strava run and provide a coaching update. Activity: {activity.GetRawText()} Recent History: {historyJson}. Return a professional 3-sentence summary followed by a workout suggestion for tomorrow.";
    
    var geminiRequest = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
    var geminiRes = await client.PostAsJsonAsync(
        $"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={GeminiApiKey}", 
        geminiRequest
    );

    if (!geminiRes.IsSuccessStatusCode) {
        AddLog($"GEMINI ERROR: {geminiRes.StatusCode}");
        return;
    }
    
    var geminiData = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
    var coachNotes = geminiData.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

    // 5. Update Strava
    await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { 
        description = $"[StravAI Performance Report]\n\n{coachNotes}\n\n[StravAI-Processed]" 
    });
    AddLog($"SUCCESS: Activity {activityId} description updated.");
}

app.Run();

public record RegisterRequest(string CallbackUrl, string VerifyToken);
public record StravaWebhookEvent(
    [property: JsonPropertyName("object_type")] string ObjectType,
    [property: JsonPropertyName("object_id")] long ObjectId,
    [property: JsonPropertyName("aspect_type")] string AspectType
);
