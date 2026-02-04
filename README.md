
# StravAI Coach 🏃‍♂️🤖

> **Elite performance coaching powered by Gemini 3. From raw GPS data to professional prescriptions.**

StravAI is an autonomous coaching engine designed for athletes who demand professional-grade analysis without the premium price tag. It processes your Strava activities via real-time webhooks, analyzes them against your training history, and writes detailed coaching reports directly into your activity descriptions.

---

## 🚀 Milestone: v1.1.0 ULTRA COACH (Stable)
This version represents the **Deep Performance Milestone**.
*   **Release Date:** March 2025
*   **Tag:** `v1.1.0-ultra-coach`
*   **Key Achievement:** Scaled context window to **1,000 activities** for full-season history analysis.

### New in v1.1.0:
*   **1000-Activity Context:** Deep scan logic now pulls and summarizes your entire season history for the Coach.
*   **Optimized Token Summarization:** Dramatically reduced latency and prevented timeouts by pre-summarizing activity JSON before AI processing.
*   **Ultra Strategy Engine:** Detailed race logistics, including 3-tier pacing (Optimistic/Realistic/Pessimistic), nutrition timing, and aid station gear swaps.
*   **Improved Reliability:** Increased HTTP timeouts to 5 minutes to handle deep reasoning tasks during peak API usage.

---

## 🌟 Key Features

*   **Pro Reasoning Engine:** Switched to `gemini-3-pro-preview` for deep analysis and `gemini-3-flash-preview` for high-speed automated reports.
*   **Dual-AI Architecture:** A service *built by* AI to be *powered by* AI. Developed using GenAI to compress months of work into a single afternoon.
*   **Deep Narrative Intelligence:** The Coach remembers. It doesn't just look at one run; it analyzes historical trends to detect *Aerobic Decoupling*, *Structural Integrity*, and recovery patterns.
*   **Zero-Cost Operation:** Strategically engineered for the free cloud ecosystem:
    *   **Backend:** .NET 9 Minimal API hosted on **Koyeb**.
    *   **Intelligence:** **Gemini 3** (Free Tier) for elite-tier reasoning.
    *   **Automation:** **GitHub Actions** for headless synchronization.
*   **Interactive Feedback Loop:** Add athlete notes (e.g., *"Legs felt heavy"*) to your activity description, and the AI Coach will incorporate your subjective feel into the next prescription.

---

## 🛠 Technical Stack

*   **Logic Engine:** C# / .NET 9 (Headless Minimal API).
*   **AI Model:** Google Gemini 3 (Complex Reasoning & Flash Modes).
*   **Infrastructure:** Koyeb (Service Hosting) & GitHub Actions (Scheduled Sync).
*   **Console:** React + Tailwind CSS (Command Center for manual triggers).

---

## 🚀 Deployment

1.  **Get Keys:** Obtain a [Gemini API Key](https://aistudio.google.com/app/apikey) and [Strava Client ID/Secret](https://www.strava.com/settings/api).
2.  **Environment:** Set your `GOAL_RACE_TYPE` and `GOAL_RACE_DATE` in your hosting environment.
3.  **Webhook:** Register your service URL with Strava's webhook API to enable real-time analysis.

---

## 💡 Engineering Philosophy
This project demonstrates the power of **AI-Native Development**. By utilizing AI as a co-pilot, I bypassed the friction of API documentation and boilerplate, focusing entirely on the **data architecture** and **coaching logic**. The result is a production-grade performance engine maintained for $0/month.

---
*Disclaimer: This is a personal research project. Usage must comply with Strava and Google API Terms of Service.*
