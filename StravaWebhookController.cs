using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;

// This is a Minimal API implementation for the StravAI backend service.
// You can run this in a .NET 8.0+ project.

var builder = WebApplication.CreateBuilder(args);
var app = builder.Build();

// 1. Webhook Validation (GET)
// Strava sends a GET request to verify your endpoint when you create a subscription.
app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, 
                        [FromQuery(Name = "hub.challenge")] string challenge, 
                        [FromQuery(Name = "hub.verify_token")] string token) => 
{
    // Verification token should match what you set when creating the subscription
    const string VERIFY_TOKEN = "STRAVAI_SECURE_TOKEN";
    
    if (mode == "subscribe" && token == VERIFY_TOKEN) {
        Console.WriteLine("WEBHOOK_VERIFIED");
        return Results.Ok(new { hub_challenge = challenge });
    }
    
    return Results.BadRequest();
});

// 2. Event Handling (POST)
// Strava pushes new activities here in real-time.
app.MapPost("/webhook", async ([FromBody] StravaWebhookEvent @event, ILogger<Program> logger) => 
{
    logger.LogInformation("Received event: {ObjectType} {AspectType} for ID {ObjectId}", 
        @event.ObjectType, @event.AspectType, @event.ObjectId);

    // Filter for new activity uploads
    if (@event.ObjectType == "activity" && @event.AspectType == "create") {
        // Here you would trigger the Gemini Analysis logic (similar to sync.ts)
        // 1. Fetch activity details from Strava API
        // 2. Fetch history for baseline
        // 3. Send to Gemini for coaching
        // 4. Update Strava description
        logger.LogInformation("Processing new activity {Id} via Gemini Coach...", @event.ObjectId);
    }

    return Results.Ok();
});

app.Run();

public record StravaWebhookEvent(
    [property: JsonPropertyName("object_type")] string ObjectType,
    [property: JsonPropertyName("object_id")] long ObjectId,
    [property: JsonPropertyName("aspect_type")] string AspectType,
    [property: JsonPropertyName("owner_id")] long OwnerId,
    [property: JsonPropertyName("subscription_id")] long SubscriptionId,
    [property: JsonPropertyName("event_time")] long EventTime,
    [property: JsonPropertyName("updates")] Dictionary<string, string> Updates
);