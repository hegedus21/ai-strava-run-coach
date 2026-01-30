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
    var timestamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff");
    var logEntry = $"[{timestamp}] [{level}] {message}";
    logs.Enqueue(logEntry);
    while (logs.Count > 200) logs.TryDequeue(out _);
    Console.WriteLine(logEntry); // Visible in Koyeb/Docker logs
}

// Configuration Helper
var config = app.Configuration;
string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val;
    
    // Explicit check for the key names seen in Koyeb
    if (key == "API_KEY") {
        val = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrEmpty(val)) return val;
    }
    
    return config[key] ?? "";
}

// Global Request Logger Middleware - Log EVERY hit
app.Use(async (context, next) => {
    AddLog($"HTTP_{context.Request.Method}: {context.Request.Path}{context.Request.QueryString}");
    await next();
    if (context.Response.StatusCode >= 400) {
        AddLog($"RESPONSE_ERROR: {context.Response.StatusCode} for {context.Request.Path}", "WARNING");
    }
});

// Startup Diagnostics
AddLog("==================================================");
AddLog("STRAVAI BACKEND INITIALIZING...");
AddLog($"Deployment Port: {port}");
AddLog("Configuration Status:");
AddLog($" - Gemini API: {(string.IsNullOrEmpty(GetEnv("API_KEY")) ? "❌ MISSING" : "✅ LOADED")}");
AddLog($" - Strava ID: {(string.IsNullOrEmpty(GetEnv("STRAVA_CLIENT_ID")) ? "❌ MISSING" : "✅ LOADED")}");
AddLog($" - Strava Secret: {(string.IsNullOrEmpty(GetEnv("STRAVA_CLIENT_SECRET")) ? "❌ MISSING" : "✅ LOADED")}");
AddLog($" - Strava Refresh: {(string.IsNullOrEmpty(GetEnv("STRAVA_REFRESH_TOKEN")) ? "❌ MISSING" : "✅ LOADED")}");
AddLog($" - Verify Token: '{GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN"}'");
AddLog("==================================================");

app.MapGet("/", () => {
    AddLog("Root pinged.");
    return "StravAI Backend is fully operational.";
});

app.MapGet("/health", () => {
    AddLog("Health check processed.");
    return Results.Ok(new { 
        status = "healthy", 
        timestamp = DateTime.UtcNow,
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

app.MapPost("/webhook/ping", () => {
    AddLog("UI_SIGNAL: Connection test successful from management console.");
    return Results.Ok(new { message = "Connection verified", time = DateTime.UtcNow });
});

// Subscription Listing Proxy
app.MapGet("/webhook/subscriptions", async (IHttpClientFactory clientFactory) => {
    AddLog("ACTION: Requesting active webhook list from Strava...");
    try {
        using var client = clientFactory.CreateClient();
        string clientId = GetEnv("STRAVA_CLIENT_ID");
        string clientSecret = GetEnv("STRAVA_CLIENT_SECRET");
        var res = await client.GetAsync($"https://www.strava.com/api/v3/push_subscriptions?client_id={clientId}&client_secret={clientSecret}");
        var content = await res.Content.ReadAsStringAsync();
        
        if (res.IsSuccessStatusCode) {
            AddLog("SUCCESS: Webhooks listed successfully.");
            return Results.Content(content, "application/json");
        } else {
            AddLog($"ERROR: Strava returned {res.StatusCode}: {content}", "ERROR");
            return Results.Problem(content);
        }
    } catch (Exception ex) {
        AddLog($"FATAL: Exception in subscription list: {ex.Message}", "ERROR");
        return Results.Problem(ex.Message);
    }
});

// Handshake Validation (GET)
app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, 
                        [FromQuery(Name = "hub.challenge")] string challenge, 
                        [FromQuery(Name = "hub.verify_token")] string token) => 
{
    var expected = GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";
    AddLog($"HANDSHAKE_RECEIVED: Mode={mode}, GivenToken='{token}', ExpectedToken='{expected}'");
    
    if (mode == "subscribe" && token == expected) {
        AddLog("HANDSHAKE_SUCCESS: Returning challenge to Strava.");
        // CRITICAL: Use Dictionary for 'hub.challenge' because dot is not allowed in anonymous type property names
        return Results.Ok(new Dictionary<string, string> { { "hub.challenge", challenge } });
    }
    
    AddLog("HANDSHAKE_FAILED: Token mismatch or invalid mode.", "ERROR");
    return Results.BadRequest("Verification Failed.");
});

// Registration Proxy (POST)
app.MapPost("/webhook/register", async ([FromBody] RegisterRequest req, IHttpClientFactory clientFactory) => {
    AddLog($"REGISTRATION_START: Attempting to register '{req.CallbackUrl}' with token '{req.VerifyToken}'");
    
    try {
        using var client = clientFactory.CreateClient();
        var formData = new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
            new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
            new KeyValuePair<string, string>("callback_url", req.CallbackUrl),
            new KeyValuePair<string, string>("verify_token", req.VerifyToken)
        });

        AddLog("REGISTRATION_STEP: Sending POST to Strava v3/push_subscriptions...");
        var res = await client.PostAsync("https://www.strava.com/api/v3/push_subscriptions", formData);
        var content = await res.Content.ReadAsStringAsync();
        
        if (res.IsSuccessStatusCode) {
            AddLog($"REGISTRATION_SUCCESS: {content}");
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>(content));
        } else {
            AddLog($"REGISTRATION_ERROR: Strava rejected request ({res.StatusCode}). Details: {content}", "ERROR");
            return Results.Problem(content);
        }
    } catch (Exception ex) {
        AddLog($"REGISTRATION_EXCEPTION: {ex.Message}", "ERROR");
        return Results.Problem(ex.Message);
    }
});

// Webhook Event Handler (POST)
app.MapPost("/webhook", ([FromBody] StravaWebhookEvent @event, IHttpClientFactory clientFactory) => 
{
    AddLog($"WEBHOOK_EVENT: {@event.ObjectType}.{@event.AspectType} (ID: {@event.ObjectId}) received.");
    
    if (@event.ObjectType == "activity" && (@event.AspectType == "create" || @event.AspectType == "update")) {
        AddLog($"PIPELINE_TRIGGER: Starting background analysis for activity {@event.ObjectId}");
        _ = Task.Run(async () => {
            try { await ProcessActivityAsync(@event.ObjectId, clientFactory); }
            catch (Exception ex) { AddLog($"FATAL_PIPELINE_ERROR: Activity {@event.ObjectId} failed: {ex.Message}", "ERROR"); }
        });
    } else {
        AddLog($"EVENT_SKIPPED: Not an activity update/creation.");
    }
    
    return Results.Ok();
});

async Task ProcessActivityAsync(long activityId, IHttpClientFactory clientFactory) {
    var traceId = Guid.NewGuid().ToString().Substring(0, 8);
    using var client = clientFactory.CreateClient();
    AddLog($"[{traceId}] >>> Starting Pipeline for Activity {activityId}");

    // 1. Refreshing Strava Auth
    AddLog($"[{traceId}] Step 1/5: Refreshing Strava credentials...");
    var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
        new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN")),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    
    if (!authRes.IsSuccessStatusCode) {
        var errBody = await authRes.Content.ReadAsStringAsync();
        AddLog($"[{traceId}] AUTH_FAILURE: {errBody}", "ERROR");
        return;
    }
    var authData = await authRes.Content.ReadFromJsonAsync<JsonElement>();
    var accessToken = authData.GetProperty("access_token").GetString();
    AddLog($"[{traceId}] Auth successful.");

    // 2. Fetch Activity Data
    AddLog($"[{traceId}] Step 2/5: Fetching run details from Strava...");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var activity = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{activityId}");
    var name = activity.GetProperty("name").GetString();
    var type = activity.GetProperty("type").GetString();
    
    if (type != "Run") {
        AddLog($"[{traceId}] SKIPPED: Activity '{name}' is a '{type}', not a Run.");
        return;
    }
    AddLog($"[{traceId}] Processing Run: '{name}'");

    // 3. Fetch History context
    AddLog($"[{traceId}] Step 3/5: Gathering recent activity history...");
    var historyRes = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=15");
    var historyJson = await historyRes.Content.ReadAsStringAsync();
    AddLog($"[{traceId}] History context retrieved.");

    // 4. Consulting AI
    AddLog($"[{traceId}] Step 4/5: Consulting Coach Gemini AI...");
    var apiKey = GetEnv("API_KEY");
    var goalDate = GetEnv("GOAL_RACE_DATE") ?? "Future Date";
    var goalType = GetEnv("GOAL_RACE_TYPE") ?? "Marathon";
    
    var prompt = $"Analyze this Strava run for an athlete training for a {goalType} on {goalDate}. Activity: {activity.GetRawText()}. History: {historyJson}. Provide a short summary and a specific workout suggestion for tomorrow.";
    var geminiRequest = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
    
    var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={apiKey}", geminiRequest);
    if (!geminiRes.IsSuccessStatusCode) {
        AddLog($"[{traceId}] AI_FAILURE: {await geminiRes.Content.ReadAsStringAsync()}", "ERROR");
        return;
    }
    var geminiData = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
    var aiNotes = geminiData.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
    AddLog($"[{traceId}] AI Analysis Complete.");

    // 5. Update Strava Description
    AddLog($"[{traceId}] Step 5/5: Writing report back to Strava activity...");
    var updateRes = await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { 
        description = $"[StravAI Coach Report]\n\n{aiNotes}\n\n[StravAI-Processed]" 
    });

    if (updateRes.IsSuccessStatusCode) {
        AddLog($"[{traceId}] <<< PIPELINE FINISHED SUCCESSFULLY.");
    } else {
        AddLog($"[{traceId}] UPDATE_FAILURE: {await updateRes.Content.ReadAsStringAsync()}", "ERROR");
    }
}

app.Run();

public record RegisterRequest(string CallbackUrl, string VerifyToken);
public record StravaWebhookEvent(
    [property: JsonPropertyName("object_type")] string ObjectType,
    [property: JsonPropertyName("object_id")] long ObjectId,
    [property: JsonPropertyName("aspect_type")] string AspectType
);
