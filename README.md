# StravAI Coach 🏃‍♂️🤖

Automated performance analysis using Strava Webhooks and Gemini AI.

## 🛠 Setup Instructions

### 1. API Keys
* **Gemini:** Get at [AI Studio](https://aistudio.google.com/app/apikey).
* **Strava:** Create at [Strava Settings](https://www.strava.com/settings/api).

### 2. The Permanent Refresh Token (The "Forever" Key)
To run headless, you need a refresh token with `activity:write` permissions.
1. Visit: `https://www.strava.com/oauth/authorize?client_id=[YOUR_ID]&response_type=code&redirect_uri=http://localhost&approval_prompt=force&scope=read,activity:read_all,activity:write`
2. Authorize, then copy the `code=` from the resulting URL.
3. Exchange code for token:
   ```bash
   curl -X POST https://www.strava.com/oauth/token \
     -F client_id=[ID] -F client_secret=[SECRET] \
     -F code=[CODE] -F grant_type=authorization_code
   ```
4. Use the `refresh_token` from the JSON response in your environment variables.

### 3. Deployment
Deploy using the included `Dockerfile`.
**Required Environment Variables:**
* `GEMINI_API_KEY`
* `STRAVA_CLIENT_ID`
* `STRAVA_CLIENT_SECRET`
* `STRAVA_REFRESH_TOKEN`
* `GOAL_RACE_TYPE`
* `GOAL_RACE_DATE` (YYYY-MM-DD)

### 4. Register Webhook
After your backend is live (e.g., `https://my-app.koyeb.app`):
```bash
curl -X POST https://www.strava.com/api/v3/push_subscriptions \
  -F client_id=[ID] \
  -F client_secret=[SECRET] \
  -F callback_url=https://my-app.koyeb.app/webhook \
  -F verify_token=STRAVAI_SECURE_TOKEN
```

## 📁 Structure
* `/StravAI.Backend`: .NET 9 Minimal API service.
* `Dockerfile`: Build container for deployment.
* `index.html / index.tsx`: Management console for manual sync and monitoring.