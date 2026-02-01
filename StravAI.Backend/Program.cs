
using Microsoft.AspNetCore.Mvc;
using System.Text.Json.Serialization;
using System.Net.Http.Headers;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Runtime.InteropServices;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Net;

var builder = WebApplication.CreateBuilder(args);
var port = Environment.GetEnvironmentVariable("PORT") ?? "8080";
builder.WebHost.UseSetting("http_ports", port);

builder.Services.AddHttpClient();
builder.Services.AddHttpClient("GeminiClient");
builder.Services.AddLogging();
builder.Services.AddHealthChecks();
builder.Services.AddCors(options => options.AddPolicy("AllowAll", p => p.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader()));

var app = builder.Build();

app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new { service = "StravAI", status = "online", timestamp = DateTime.UtcNow }));

app.UseCors("AllowAll");

// Security Middleware
app.Use(async (context, next) => {
    var path = context.Request.Path.ToString().ToLower();
    if (path != "/health" && path != "/" && !path.StartsWith("/webhook")) {
        var secret = Environment.GetEnvironmentVariable("STRAVAI_BACKEND_SECRET") ?? app.Configuration["X-StravAI-Secret"];
        if (!string.IsNullOrEmpty(secret)) {
            if (!context.Request.Headers.TryGetValue("X-StravAI-Secret", out var providedSecret) || providedSecret != secret) {
                SystemState.AddLog($"AUTH_DENIED: Request to {path} rejected.", "WARNING");
                context.Response.StatusCode = 401;
                await context.Response.WriteAsync("Unauthorized");
                return;
            }
        }
    }
    await next();
});

app.MapGet("/logs", () => Results.Ok(SystemState.Logs.ToArray()));

app.MapGet("/profile", async (IHttpClientFactory clientFactory) => {
    try {
        using var client = clientFactory.CreateClient();
        var token = await GetStravaAccessToken(client, app.Configuration);
        if (token == null) return Results.Unauthorized();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        
        var acts = await (await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100")).Content.ReadFromJsonAsync<List<JsonElement>>();
        var cache = acts?.FirstOrDefault(a => a.GetProperty("name").GetString() == "[StravAI] System Cache");
        
        if (!cache.HasValue) {
            SystemState.AddLog("PROFILE: No cache activity found.", "INFO");
            return Results.NotFound(new { error = "Cache not found. Please run an audit." });
        }

        var detailRes = await client.GetAsync($"https://www.strava.com/api/v3/activities/{cache.Value.GetProperty("id").GetInt64()}");
        var act = await detailRes.Content.ReadFromJsonAsync<JsonElement>();
        var desc = act.GetProperty("description").GetString() ?? "";
        
        if (!desc.Contains("---CACHE_START---")) {
            SystemState.AddLog("PROFILE: Found cache activity but no data markers.", "WARNING");
            return Results.NotFound(new { error = "Cache activity empty." });
        }

        var jsonString = desc.Split("---CACHE_START---")[1].Split("---CACHE_END---")[0].Trim();
        SystemState.AddLog($"PROFILE: Successfully loaded {jsonString.Length} bytes of athlete data.", "SUCCESS");
        return Results.Ok(JsonNode.Parse(jsonString));
    } catch (Exception ex) {
        SystemState.AddLog($"PROFILE_ERROR: {ex.Message}", "ERROR");
        return Results.Problem(ex.Message);
    }
});

app.MapPost("/audit", async (IHttpClientFactory clientFactory) => {
    SystemState.AddLog("AUDIT: Starting deep history analysis...");
    _ = Task.Run(async () => {
        try {
            using var client = clientFactory.CreateClient();
            var token = await GetStravaAccessToken(client, app.Configuration);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            var history = await (await client.GetAsync("https://www.strava.com/api/v3/athlete/activities?per_page=100")).Content.ReadFromJsonAsync<List<JsonElement>>();
            
            using var aiClient = clientFactory.CreateClient("GeminiClient");
            var key = ResolveApiKey(app.Configuration);
            
            var milestoneSchema = new {
                type = "OBJECT",
                properties = new {
                    count = new { type = "INTEGER" },
                    pb = new { type = "STRING" }
                },
                required = new[] { "count", "pb" }
            };

            var schema = new {
                type = "OBJECT",
                properties = new {
                    lastUpdated = new { type = "STRING" },
                    summary = new { type = "STRING" },
                    coachNotes = new { type = "STRING" },
                    milestones = new { 
                        type = "OBJECT", 
                        properties = new {
                            fiveK = milestoneSchema,
                            tenK = milestoneSchema,
                            halfMarathon = milestoneSchema,
                            marathon = milestoneSchema,
                            ultra = milestoneSchema
                        },
                        required = new[] { "fiveK", "tenK", "halfMarathon" }
                    },
                    trainingPlan = new { 
                        type = "ARRAY", 
                        items = new { 
                            type = "OBJECT", 
                            properties = new {
                                date = new { type = "STRING" },
                                type = new { type = "STRING", @enum = new[] { "Easy", "Tempo", "Interval", "Long Run", "Gym", "Rest" } },
                                title = new { type = "STRING" },
                                description = new { type = "STRING" }
                            },
                            required = new[] { "date", "type", "title", "description" }
                        } 
                    }
                },
                required = new[] { "summary", "milestones", "trainingPlan" }
            };

            var payload = new {
                contents = new[] { new { parts = new[] { new { text = $"ROLE: Professional Running Coach. DATA: {JsonSerializer.Serialize(history?.Take(50))}. TASK: Create athlete profile JSON with summary, milestones (PBs), and a 7-day plan." } } } },
                generationConfig = new { responseMimeType = "application/json", responseSchema = schema, temperature = 0.1 }
            };

            var aiRes = await aiClient.PostAsJsonAsync($"https://generativelanguage.googleapis.com/v1beta/models/gemini-3-flash-preview:generateContent?key={key}", payload);
            var result = await aiRes.Content.ReadFromJsonAsync<JsonElement>();
            var profileJson = SafeExtractText(result);
            
            var cache = history?.FirstOrDefault(a => a.GetProperty("name").GetString() == "[StravAI] System Cache");
            var timestamp = DateTime.UtcNow.ToString("O");
            var desc = $"[StravAI System Cache]\nDO NOT DELETE\n---CACHE_START---\n{profileJson}\n---CACHE_END---\nUpdated: {timestamp}";
            
            if (cache.HasValue) await client.PutAsJsonAsync($"https://www.strava.com/api/v3/activities/{cache.Value.GetProperty("id").GetInt64()}", new { description = desc });
            else await client.PostAsJsonAsync("https://www.strava.com/api/v3/activities", new { name = "[StravAI] System Cache", type = "Run", start_date_local = timestamp, elapsed_time = 1, description = desc, @private = true });
            
            SystemState.AddLog("AUDIT: Success. Profile rebuilt and cached.", "SUCCESS");
        } catch (Exception ex) {
            SystemState.AddLog($"AUDIT_ERROR: {ex.Message}", "ERROR");
        }
    });
    return Results.Accepted();
});

app.MapGet("/webhook", ([FromQuery(Name = "hub.mode")] string mode, [FromQuery(Name = "hub.challenge")] string challenge, [FromQuery(Name = "hub.verify_token")] string verifyToken) => {
    var secret = GetEnv("STRAVA_VERIFY_TOKEN", app.Configuration) ?? "STRAVAI_SECURE_TOKEN";
    return (mode == "subscribe" && verifyToken == secret) ? Results.Ok(new { hub_challenge = challenge }) : Results.Unauthorized();
});

app.MapPost("/webhook", async (HttpContext context, IHttpClientFactory clientFactory) => {
    var body = await new StreamReader(context.Request.Body).ReadToEndAsync();
    var eventData = JsonSerializer.Deserialize<JsonElement>(body);
    if (eventData.TryGetProperty("object_type", out var objType) && objType.GetString() == "activity") {
        SystemState.AddLog($"WEBHOOK: {eventData.GetProperty("aspect_type").GetString()} event for {eventData.GetProperty("object_id")}");
    }
    return Results.Ok();
});

app.Run();

// --- HELPERS ---

string SafeExtractText(JsonElement root) {
    if (root.TryGetProperty("error", out var error)) throw new Exception(error.GetProperty("message").GetString());
    var text = root.GetProperty("candidates")[0].GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "";
    
    // Robust Markdown cleaning
    if (text.Contains("```")) {
        var lines = text.Split('\n');
        var jsonLines = lines.SkipWhile(l => !l.Trim().StartsWith("```"))
                             .Skip(1)
                             .TakeWhile(l => !l.Trim().StartsWith("```"));
        text = string.Join("\n", jsonLines);
        if (string.IsNullOrWhiteSpace(text)) {
            // Fallback for cases where splits are weird
            var s = text.IndexOf("{");
            var e = text.LastIndexOf("}");
            if (s >= 0 && e > s) text = text.Substring(s, e - s + 1);
        }
    }
    return text.Trim();
}

async Task<string?> GetStravaAccessToken(HttpClient client, IConfiguration config) {
    var res = await client.PostAsync("https://www.strava.com/oauth/token", new FormUrlEncodedContent(new[] {
        new KeyValuePair<string, string>("client_id", GetEnv("STRAVA_CLIENT_ID", config)),
        new KeyValuePair<string, string>("client_secret", GetEnv("STRAVA_CLIENT_SECRET", config)),
        new KeyValuePair<string, string>("refresh_token", GetEnv("STRAVA_REFRESH_TOKEN", config)),
        new KeyValuePair<string, string>("grant_type", "refresh_token")
    }));
    var data = await res.Content.ReadFromJsonAsync<JsonElement>();
    return data.TryGetProperty("access_token", out var t) ? t.GetString() : null;
}

string ResolveApiKey(IConfiguration config) => GetEnv("API_KEY", config) ?? GetEnv("GEMINI_API_KEY", config) ?? "";
string GetEnv(string key, IConfiguration config) => Environment.GetEnvironmentVariable(key) ?? config[key] ?? "";

public static class SystemState {
    public static ConcurrentQueue<string> Logs { get; } = new();
    public static void AddLog(string m, string l = "INFO") {
        var entry = $"[{DateTime.UtcNow:HH:mm:ss}] [{l}] {m}";
        Logs.Enqueue(entry);
        while (Logs.Count > 100) Logs.TryDequeue(out _);
        Console.WriteLine(entry);
    }
}
