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
    while (logs.Count > 150) logs.TryDequeue(out _);
    Console.WriteLine(logEntry); // This shows up in Koyeb/Docker logs
}

// Configuration Helper - Updated to check both GEMINI_API_KEY and API_KEY
var config = app.Configuration;
string GetEnv(string key) {
    var val = Environment.GetEnvironmentVariable(key);
    if (!string.IsNullOrEmpty(val)) return val;
    
    // Fallback for Gemini key specifically
    if (key == "API_KEY") {
        val = Environment.GetEnvironmentVariable("GEMINI_API_KEY");
        if (!string.IsNullOrEmpty(val)) return val;
    }
    
    return config[key] ?? "";
}

// Global Request Logger Middleware
app.Use(async (context, next) => {
    AddLog($"INCOMING_REQUEST: {context.Request.Method} {context.Request.Path}{context.Request.QueryString}");
    await next();
    AddLog($"OUTGOING_RESPONSE: {context.Response.StatusCode} for {context.Request.Path}");
});

// Startup Diagnostics
AddLog("==================================================");
AddLog("STRAVAI BACKEND STARTUP INITIATED");
AddLog($"Target Port: {port}");
AddLog("Verifying Core Configuration:");
AddLog($" - GEMINI API: {(string.IsNullOrEmpty(GetEnv("API_KEY")) ? "❌ NOT SET" : "✅ CONFIGURED")}");
AddLog($" - STRAVA ID: {(string.IsNullOrEmpty(GetEnv("STRAVA_CLIENT_ID")) ? "❌ NOT SET" : "✅ CONFIGURED")}");
AddLog($" - STRAVA REFRESH: {(string.IsNullOrEmpty(GetEnv("STRAVA_REFRESH_TOKEN")) ? "❌ NOT SET" : "✅ CONFIGURED")}");
AddLog($" - VERIFY TOKEN: '{GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN"}'");
AddLog("==================================================");

app.MapGet("/", () => "StravAI Cloud Service is Online.");

app.MapGet("/health", () => {
    return Results.Ok(new { 
        status = "healthy", 
        config_valid = !string.IsNullOrEmpty(GetEnv("API_KEY")) && !string.IsNullOrEmpty(GetEnv("STRAVA_CLIENT_ID")),
        config_report = new {
            gemini = !string.IsNullOrEmpty(GetEnv("API_KEY")),
            strava_id = !string.IsNullOrEmpty(GetEnv("STRAVA_CLIENT_ID")),
            strava_secret = !string.IsNullOrEmpty(GetEnv("STRAVA_CLIENT_SECRET")),
            strava_refresh = !string.IsNullOrEmpty(GetEnv("STRAVA_REFRESH_TOKEN"))
        }
    });
});

app.MapGet("/logs", () => Results.Ok(logs.ToArray()));

app.MapPost("/webhook/ping", () => {
    AddLog("DIAGNOSTIC_PING: Received from UI console.");
    return Results.Ok(new { message = "Pong", timestamp = DateTime.UtcNow });
});

// Subscription Listing
app.MapGet("/webhook/subscriptions", async (IHttpClientFactory clientFactory) => {
    AddLog("ACTION: Fetching active subscriptions from Strava API...");
    try {
        using var client = clientFactory.CreateClient();
        string clientId = GetEnv("STRAVA_CLIENT_ID");
        string clientSecret = GetEnv("STRAVA_CLIENT_SECRET");
        var url = $"https://www.strava.com/api/v3/push_subscriptions?client_id={clientId}&client_secret={clientSecret}";
        
        var res = await client.GetAsync(url);
        var content = await res.Content.ReadAsStringAsync();
        
        if (res.IsSuccessStatusCode) {
            AddLog("SUCCESS: Subscriptions retrieved.");
            return Results.Content(content, "application/json");
        } else {
            AddLog($"FAILURE: Strava v3/push_subscriptions returned {res.StatusCode}. Body: {content}", "ERROR");
            return Results.Problem(content);
        }
    } catch (Exception ex) {
        AddLog($"EXCEPTION in getSubscriptions: {ex.Message}", "ERROR");
        return Results.Problem(ex.Message);
    }
});

// Handshake (GET)
app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, 
                        [FromQuery(Name = "hub.challenge")] string challenge, 
                        [FromQuery(Name = "hub.verify_token")] string token) => 
{
    var expected = GetEnv("STRAVA_VERIFY_TOKEN") ?? "STRAVAI_SECURE_TOKEN";
    AddLog($"HANDSHAKE: Mode={mode}, ProvidedToken='{token}', ExpectedToken='{expected}'");
    
    if (mode == "subscribe" && token == expected) {
        AddLog("HANDSHAKE: Validation successful. Returning challenge.");
        return Results.Ok(new { hub_challenge = challenge });
    }
    
    AddLog("HANDSHAKE: Validation failed. Mode or Token mismatch.", "WARNING");
    return Results.BadRequest("Handshake Failed.");
});

// Registration (POST)
app.MapPost("/webhook/register", async ([FromBody] RegisterRequest req, IHttpClientFactory clientFactory) => {
    AddLog($"REGISTRATION_REQUEST: URL={req.CallbackUrl}, Token='{req.VerifyToken}'");
    
    try {
        using var client = clientFactory.CreateClient();
        var formData = new FormUrlEncodedContent(new[] {
            new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
            new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
            new KeyValuePair<string, string>("callback_url", req.CallbackUrl),
            new KeyValuePair<string, string>("verify_token", req.VerifyToken)
        });

        AddLog("REGISTRATION: Sending POST to Strava...");
        var res = await client.PostAsync("https://www.strava.com/api/v3/push_subscriptions", formData);
        var content = await res.Content.ReadAsStringAsync();
        
        if (res.IsSuccessStatusCode) {
            AddLog($"REGISTRATION_SUCCESS: Strava Response: {content}");
            return Results.Ok(JsonSerializer.Deserialize<JsonElement>(content));
        } else {
            AddLog($"REGISTRATION_FAILURE: {res.StatusCode} - {content}", "ERROR");
            return Results.Problem(content);
        }
    } catch (Exception ex) {
        AddLog($"REGISTRATION_EXCEPTION: {ex.Message}", "ERROR");
        return Results.Problem(ex.Message);
    }
});

// Webhook Handler (POST)
app.MapPost("/webhook", ([FromBody] StravaWebhookEvent @event, IHttpClientFactory clientFactory) => 
{
    AddLog($"EVENT_RECEIVED: {@event.ObjectType}.{@event.AspectType} for ID {@event.ObjectId}");
    
    if (@event.ObjectType == "activity" && (@event.AspectType == "create" || @event.AspectType == "update")) {
        AddLog($"PIPELINE: Queueing analysis for activity {@event.ObjectId}...");
        _ = Task.Run(async () => {
            try { await ProcessActivityAsync(@event.ObjectId, clientFactory); }
            catch (Exception ex) { AddLog($"FATAL_PIPELINE_ERROR ({@event.ObjectId}): {ex.Message}", "ERROR"); }
        });
    } else {
        AddLog($"EVENT_IGNORED: Type={@event.ObjectType}, Aspect={@event.AspectType}");
    }
    
    return Results.Ok();
});

async Task ProcessActivityAsync(long activityId, IHttpClientFactory clientFactory) {
    var traceId = Guid.NewGuid().ToString().Substring(0, 8);
    using var client = clientFactory.CreateClient();
    AddLog($"[{traceId}] Starting Activity Analysis Pipeline...");

    // 1. Refresh Auth
    AddLog($"[{traceId}] (1/5) Refreshing Strava OAuth Token...");
    var authForm = new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID")),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET")),
        new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN")),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    });
    var authRes = await client.PostAsync("https://www.strava.com/oauth/token", authForm);
    if (!authRes.IsSuccessStatusCode) {
        AddLog($"[{traceId}] AUTH_FAILURE: {await authRes.Content.ReadAsStringAsync()}", "ERROR");
        return;
    }
    var authData = await authRes.Content.ReadFromJsonAsync<JsonElement>();
    var accessToken = authData.GetProperty("access_token").GetString();
    AddLog($"[{traceId}] Auth Success.");

    // 2. Fetch Activity
    AddLog($"[{traceId}] (2/5) Fetching Activity Data...");
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var activity = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{activityId}");
    var type = activity.GetProperty("type").GetString();
    if (type != "Run") {
        AddLog($"[{traceId}] SKIP: Type '{type}' is not a Run.");
        return;
    }
    AddLog($"[{traceId}] Run found: '{activity.GetProperty("name").GetString()}'");

    // 3. Fetch History
    AddLog($"[{traceId}] (3/5) Fetching Athlete Context...");
    var historyRes = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=15");
    var historyJson = await historyRes.Content.ReadAsStringAsync();

    // 4. Gemini AI
    AddLog($"[{traceId}] (4/5) Consulting Gemini AI (using model gemini-3-flash-preview)...");
    var geminiKey = GetEnv("API_KEY");
    var prompt = $"Analyze this Strava run and provide coaching feedback. Goal: {GetEnv("GOAL_RACE_TYPE")} on {GetEnv("GOAL_RACE_DATE")}. Activity: {activity.GetRawText()}. History: {historyJson}. Return a short summary and tomorrow's workout.";
    var geminiPayload = new { contents = new[] { new { parts = new[] { new { text = prompt } } } } };
    
    var geminiRes = await client.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={geminiKey}", geminiPayload);
    if (!geminiRes.IsSuccessStatusCode) {
        AddLog($"[{traceId}] AI_FAILURE: {await geminiRes.Content.ReadAsStringAsync()}", "ERROR");
        return;
    }
    var geminiData = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
    var coachText = geminiData.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();
    AddLog($"[{traceId}] AI Analysis Complete.");

    // 5. Update Strava
    AddLog($"[{traceId}] (5/5) Updating Strava Activity Description...");
    var updateBody = new { description = $"[StravAI Report]\n\n{coachText}\n\n[StravAI-Processed]" };
    var updateRes = await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", updateBody);
    
    if (updateRes.IsSuccessStatusCode) {
        AddLog($"[{traceId}] COMPLETED SUCCESSFULY.");
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
