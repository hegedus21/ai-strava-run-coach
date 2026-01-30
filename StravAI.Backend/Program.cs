using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);

// Ensure the app listens on the port provided by the platform (default 8080)
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseUrls($"http://*:{port}");

// Add services
builder.Services.AddHttpClient();
builder.Services.AddLogging();

var app = builder.Build();

// Configuration - Use Environment Variables (Best for Cloud Deployment)
var config = app.Configuration;
string GeminiApiKey = config["GEMINI_API_KEY"] ?? config["API_KEY"] ?? "";
string StravaClientId = config["STRAVA_CLIENT_ID"] ?? "";
string StravaClientSecret = config["STRAVA_CLIENT_SECRET"] ?? "";
string StravaRefreshToken = config["STRAVA_REFRESH_TOKEN"] ?? "";

// Goal Settings from Env
string GoalRaceType = config["GOAL_RACE_TYPE"] ?? "Marathon";
string GoalRaceDate = config["GOAL_RACE_DATE"] ?? "2025-12-31";
string GoalRaceTime = config["GOAL_RACE_TIME"] ?? "3:30:00";

const string WEBHOOK_VERIFY_TOKEN = "STRAVAI_SECURE_TOKEN";
const string BORDER = "################################";
const string SIGNATURE = "[StravAI-Processed]";
const string PLACEHOLDER = "Activity will be analysed later as soon as the AI coach has capacity";

// --- ENDPOINTS ---

app.MapGet("/", () => "StravAI Backend is Running. Ready for Webhooks.");

app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, 
                        [FromQuery(Name = "hub.challenge")] string challenge, 
                        [FromQuery(Name = "hub.verify_token")] string token) => 
{
    if (mode == "subscribe" && token == WEBHOOK_VERIFY_TOKEN) {
        Console.WriteLine("✅ Webhook validated by Strava.");
        return Results.Ok(new { hub_challenge = challenge });
    }
    return Results.BadRequest();
});

app.MapPost("/webhook", async ([FromBody] StravaWebhookEvent @event, IHttpClientFactory clientFactory, ILogger<Program> logger) => 
{
    logger.LogInformation("🚀 Event Received: {Type} - ID: {Id}", @event.ObjectType, @event.ObjectId);

    if (string.IsNullOrEmpty(GeminiApiKey)) {
        logger.LogError("GEMINI_API_KEY is not configured.");
        return Results.Ok(); // Still return 200 to Strava
    }

    // Filter for new activity uploads
    if (@event.ObjectType == "activity" && (@event.AspectType == "create" || @event.AspectType == "update")) {
        // Fire and forget processing to return 200 OK to Strava within 2 seconds
        _ = Task.Run(async () => {
            try {
                await ProcessActivityAsync(@event.ObjectId, clientFactory, logger);
            } catch (Exception ex) {
                logger.LogError(ex, "Failed to process activity {Id}", @event.ObjectId);
            }
        });
    }

    return Results.Ok();
});

// --- CORE LOGIC ---

async Task ProcessActivityAsync(long activityId, IHttpClientFactory clientFactory, ILogger logger) {
    var client = clientFactory.CreateClient();
    
    // 1. Get Strava Access Token
    logger.LogInformation("Refreshing Strava token...");
    var authRes = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", StravaClientId),
        new KeyValuePair<string, string>("client_secret", StravaClientSecret),
        new KeyValuePair<string, string>("refresh_token", StravaRefreshToken),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    
    if (!authRes.IsSuccessStatusCode) {
        logger.LogError("Strava Auth Failed: {Status}", authRes.StatusCode);
        return;
    }

    var authData = await authRes.Content.ReadFromJsonAsync<JsonElement>();
    var accessToken = authData.GetProperty("access_token").GetString();

    // 2. Fetch Activity Details
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
    var activity = await client.GetFromJsonAsync<JsonElement>($"https://www.strava.com/api/v3/activities/{activityId}");
    
    if (activity.GetProperty("type").GetString() != "Run") {
        logger.LogInformation("Activity {Id} is not a Run. Skipping.", activityId);
        return;
    }

    // Check if it already has the signature to avoid loops
    var currentDesc = activity.TryGetProperty("description", out var d) ? d.GetString() : "";
    if (currentDesc != null && currentDesc.Contains(SIGNATURE) && !currentDesc.Contains(PLACEHOLDER)) {
        logger.LogInformation("Activity {Id} already processed. Skipping.", activityId);
        return;
    }

    // 3. Fetch History for Context (Last 10 runs)
    var historyRes = await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=10");
    var historyJson = await historyRes.Content.ReadAsStringAsync();

    // 4. Calculate Stats for AI
    DateTime raceDate = DateTime.TryParse(GoalRaceDate, out var rd) ? rd : DateTime.Now.AddMonths(3);
    int daysRemaining = (int)(raceDate - DateTime.Now).TotalDays;

    // 5. Consult Gemini AI
    logger.LogInformation("Consulting Gemini Coach for activity {Id}...", activityId);
    
    var prompt = $@"
        ROLE: Professional Athletic Performance Coach.
        ATHLETE GOAL: {GoalRaceType} on {GoalRaceDate} (Target: {GoalRaceTime}).
        DAYS REMAINING: {daysRemaining} days.

        CURRENT ACTIVITY:
        {activity.GetRawText()}

        PAST 10 ACTIVITIES CONTEXT:
        {historyJson}

        TASK:
        1. Write a 3-sentence summary of this run.
        2. Progress Report: Analyze how far the athlete is in the preparation process to reach the goal ({GoalRaceType} in {GoalRaceTime}).
        3. Next Week Focus: Based on this training and the last month of context, what should be the primary training focus for the next 7 days?
        4. Immediate Next Step: Prescribe exactly one workout for tomorrow.

        FORMAT: Professional, structured with bold headers. Use emojis for engagement.
    ";

    var geminiRequest = new {
        contents = new[] { new { parts = new[] { new { text = prompt } } } }
    };

    try {
        var geminiRes = await client.PostAsJsonAsync(
            $"https://generativelanguage.googleapis.com/v1beta/models/gemini-2.0-flash:generateContent?key={GeminiApiKey}", 
            geminiRequest
        );

        if (!geminiRes.IsSuccessStatusCode) {
            var errorBody = await geminiRes.Content.ReadAsStringAsync();
            if (errorBody.Contains("QUOTA_EXCEEDED") || errorBody.Contains("RESOURCE_EXHAUSTED")) {
                throw new InvalidOperationException("QUOTA_EXCEEDED");
            }
            throw new Exception($"Gemini API Error: {geminiRes.StatusCode} - {errorBody}");
        }
        
        var geminiData = await geminiRes.Content.ReadFromJsonAsync<JsonElement>();
        var coachNotes = geminiData.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString();

        // 6. Update Strava Description
        var baseDesc = currentDesc?.Split(BORDER)[0].Trim() ?? "";
        
        var reportBuilder = new StringBuilder();
        reportBuilder.AppendLine($"{BORDER}");
        reportBuilder.AppendLine("StravAI Performance Report");
        reportBuilder.AppendLine("---");
        reportBuilder.AppendLine($"**T-Minus:** {daysRemaining} days to {GoalRaceType}");
        reportBuilder.AppendLine(coachNotes);
        reportBuilder.AppendLine("");
        reportBuilder.AppendLine($"*{SIGNATURE}*");
        reportBuilder.AppendLine($"{BORDER}");

        var newDesc = $"{baseDesc}\n\n{reportBuilder.ToString()}".Trim();

        var updateRes = await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { description = newDesc });
        if (updateRes.IsSuccessStatusCode) {
            logger.LogInformation("✅ Successfully updated activity {Id}", activityId);
        } else {
             logger.LogError("Failed to update Strava: {Status}", updateRes.StatusCode);
        }
    } 
    catch (Exception ex) when (ex.Message == "QUOTA_EXCEEDED") {
        logger.LogWarning("⚠️ Gemini Quota Exceeded. Applying placeholder to activity {Id}", activityId);
        
        if (currentDesc == null || !currentDesc.Contains(PLACEHOLDER)) {
            var baseDesc = currentDesc?.Split(BORDER)[0].Trim() ?? "";
            var placeholderBlock = $"{BORDER}\nStrava AI analysis\n---\n{PLACEHOLDER}\n\n*{SIGNATURE}*\n{BORDER}";
            var newDesc = $"{baseDesc}\n\n{placeholderBlock}".Trim();
            await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{activityId}", new { description = newDesc });
        }
    }
}

app.Run();

public record StravaWebhookEvent(
    [property: JsonPropertyName("object_type")] string ObjectType,
    [property: JsonPropertyName("object_id")] long ObjectId,
    [property: JsonPropertyName("aspect_type")] string AspectType,
    [property: JsonPropertyName("owner_id")] long OwnerId
);
