# StravAI Coach 🏃‍♂️🤖

> **Elite performance coaching powered by Gemini 3. From raw GPS data to professional prescriptions.**

StravAI is an autonomous coaching engine designed for athletes who demand professional-grade analysis without the premium price tag. It processes your Strava activities via real-time webhooks, analyzes them against your training history, and writes detailed coaching reports directly into your activity descriptions.

---

## 🚀 Milestone: v1.3.0 ULTRA STABLE (Current Release)
This version represents the **Command & Control Milestone**.
*   **Release Date:** February 5, 2026
*   **Key Achievement:** Implemented a robust Command Center with real-time UI validation and a "Zero Trust" security layer.

---

## 🔐 Security Configuration (CRITICAL)

To protect your service, you must set these environment variables on your host (e.g., Koyeb):

1.  **`STRAVA_VERIFY_TOKEN`**: This is the secret handshake for Strava. When you register your webhook at `strava.com/settings/api`, use this exact value in the "Verify Token" field.
2.  **`BACKEND_SECRET`**: This is your password for the Web Command Center. When you visit your UI, you will be prompted for this "Verify Token" to see logs and trigger manual syncs.
3.  **`GEMINI_API_KEY`**: Your Google AI key.

---

## 🛠 Features

*   **Real-Time Webhooks**: Strava pings your service the second you finish a run. The AI analyzes it and updates the description within seconds.
*   **Command Terminal**: A React-based UI to manually trigger deep analysis, sync specific activity IDs, or plan custom races.
*   **1000-Activity Context**: Deep scan logic pulls and summarizes your entire season history for the Coach.
*   **Google Search Grounding**: Automated ingestion of race-specific details from external URLs to refine strategy.

---

## 🛠 Technical Stack

*   **Logic Engine:** C# / .NET 9 (Headless Minimal API).
*   **AI Model:** Google Gemini 3 (Complex Reasoning & Flash Modes).
*   **Infrastructure:** Koyeb (Service Hosting) & GitHub Actions (Scheduled Sync).
*   **Console:** React + Tailwind CSS (Command Center for manual triggers).

---
*Disclaimer: This is a personal research project. Usage must comply with Strava and Google API Terms of Service.*
