
# StravAI Coach 🏃‍♂️🤖

> **Elite performance coaching powered by Gemini 3. From raw GPS data to professional prescriptions.**

StravAI is an autonomous coaching engine designed for athletes who demand professional-grade analysis. It processes your Strava activities via real-time webhooks, analyzes them against your training history, and writes detailed coaching reports directly into your activity descriptions.

---

## 🚀 Milestone: v1.4.0 ZERO TRUST (Latest Release)
This version represents the **Security Hardening Milestone**.
*   **Release Date:** February 6, 2026
*   **Key Achievement:** Implemented dual-token separation and global authorization middleware.

---

## 🔐 Security Configuration (REQUIRED)

To protect your service, you must set these environment variables on your host (e.g., Koyeb):

1.  **`BACKEND_SECRET`**: Your master password for the Web Command Center. This protects your logs and manual sync buttons.
2.  **`STRAVA_VERIFY_TOKEN`**: The unique string you provide to Strava during Webhook Subscription setup. This allows your service to perform the "Handshake" securely.
3.  **`GEMINI_API_KEY`**: Your Google AI key for analysis.

*Note: If `BACKEND_SECRET` is missing, the system will fallback to the verify token, but separation is highly recommended.*

---

## 🛠 Features

*   **Real-Time Webhooks**: Strava pings your service the second you finish a run. Handshake handled via specialized GET challenge logic.
*   **Zero Trust Middleware**: All administrative endpoints (/logs, /sync) are locked behind a mandatory `X-StravAI-Secret` header check.
*   **UI Auto-Lock**: If the backend detects an invalid token (401), the React UI immediately wipes local sessions and forces a re-login.
*   **1000-Activity Context**: Deep scan logic pulls and summarizes your entire season history for the Coach.

---

## 🛠 Technical Stack

*   **Logic Engine:** C# / .NET 9 (Headless Minimal API).
*   **AI Model:** Google Gemini 3 (Complex Reasoning & Flash Modes).
*   **Infrastructure:** Koyeb (Service Hosting) & GitHub Actions (Scheduled Sync).
*   **Console:** React + Tailwind CSS (Command Center for manual triggers).

---
*Disclaimer: This is a personal research project. Usage must comply with Strava and Google API Terms of Service.*
