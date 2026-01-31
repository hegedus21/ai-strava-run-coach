# StravAI Coach 🏃‍♂️🤖

> **Turning a canceled subscription into a custom-built, elite AI performance engine.**

StravAI is a professional-grade, headless coaching service that bridges the gap between raw Strava data and actionable athletic insights. It uses **Google Gemini 3** to act as a Master Coach, analyzing your activities via webhooks and providing detailed prescriptions directly in your Strava activity descriptions.

![StravAI Header](https://raw.githubusercontent.com/google/material-design-icons/master/png/action/directions_run/black/48dp/1x/outline_directions_run_black_48dp.png)

## 🌟 Key Features

*   **Dual AI Workflow:** Developed using AI-assisted coding to move from concept to cloud-deployed production in hours rather than weeks.
*   **Context-Aware Coaching:** Unlike basic trackers, StravAI reads your last 12 activities *and* its own previous coaching reports to ensure narrative continuity and progressive loading.
*   **Zero-Cost Architecture:** Engineered to run 100% on free tiers (Koyeb, GitHub Actions, Gemini API).
*   **Headless & Real-time:** 
    *   **Webhooks:** Processes activities the moment they are uploaded.
    *   **Scheduled Sync:** Nightly "deep scans" via GitHub Actions to ensure no workout is missed.
*   **Interactive Feedback:** Talk to your coach! Write notes like *"Legs felt heavy"* in your Strava description, and the AI will adjust its next analysis accordingly.
*   **Command Center UI:** A dedicated React-based dashboard for manual overrides, system health monitoring, and real-time log streaming.

---

## 📸 Coach in Action

Below are examples of how StravAI transforms a standard run log into a professional coaching session:

| Analysis Overview | Detailed Prescription | Strava Integration |
| :---: | :---: | :---: |
| ![Analysis](screenshot1.jpg) | ![Prescription](screenshot2.jpg) | ![Strava View](screenshot3.jpg) |

---

## 🛠 Technical Architecture

*   **Backend:** .NET 9 Minimal API (High-performance, containerized).
*   **Intelligence:** Google Gemini 1.5/3 (Multimodal reasoning).
*   **Infrastructure:** Koyeb (Service Hosting) & GitHub Actions (Automation).
*   **Frontend:** React + Tailwind CSS (Management Console).

---

## 🚀 Setup Instructions

### 1. Requirements
* **Gemini API Key:** Obtain from [Google AI Studio](https://aistudio.google.com/app/apikey).
* **Strava API Application:** Create at [Strava Settings](https://www.strava.com/settings/api).

### 2. The "Forever" Refresh Token
To allow the headless engine to write reports while you sleep:
1. Authorize your app: `https://www.strava.com/oauth/authorize?client_id=[YOUR_ID]&response_type=code&redirect_uri=http://localhost&approval_prompt=force&scope=read,activity:read_all,activity:write`
2. Exchange the resulting `code` for a permanent `refresh_token`:
   ```bash
   curl -X POST https://www.strava.com/oauth/token \
     -F client_id=[ID] -F client_secret=[SECRET] \
     -F code=[CODE] -F grant_type=authorization_code
   ```

### 3. Deployment (Koyeb / Docker)
Deploy using the provided `Dockerfile`. 
**Required Environment Variables:**
| Variable | Description |
| :--- | :--- |
| `API_KEY` | Gemini API Key |
| `STRAVA_CLIENT_ID` | Strava App ID |
| `STRAVA_CLIENT_SECRET` | Strava App Secret |
| `STRAVA_REFRESH_TOKEN` | Your permanent refresh token |
| `STRAVA_VERIFY_TOKEN` | A secret "password" for webhook security |
| `GOAL_RACE_TYPE` | e.g., "100km Ultramaraton" |
| `GOAL_RACE_DATE` | YYYY-MM-DD |

### 4. Webhook Registration
Point Strava to your live backend:
```bash
curl -X POST https://www.strava.com/api/v3/push_subscriptions \
  -F client_id=[YOUR_ID] \
  -F client_secret=[YOUR_SECRET] \
  -F callback_url=https://your-app.koyeb.app/webhook \
  -F verify_token=[YOUR_STRAVA_VERIFY_TOKEN]
```

---

## 💡 Developer Note
This project was a study in **AI-Native Development**. By leveraging AI for boilerplate generation, logic implementation, and complex API debugging (Strava/Gemini), I was able to focus entirely on architecture and user experience. 

*The time saved by studying documentation instead of writing syntax allowed this project to reach "Stable" status in less than a day.*

---
*Disclaimer: This service is intended for private use. Usage within free tiers is subject to API rate limits.*
