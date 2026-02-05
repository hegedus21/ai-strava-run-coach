# StravAI Coach 🏃‍♂️🤖

> **Elite performance coaching powered by Gemini 3. From raw GPS data to professional prescriptions.**

StravAI is an autonomous coaching engine designed for athletes who demand professional-grade analysis without the premium price tag. It processes your Strava activities via real-time webhooks, analyzes them against your training history, and writes detailed coaching reports directly into your activity descriptions.

---

## 🚀 Milestone: v1.3.0 ULTRA STABLE (Current Release)
This version represents the **Command & Control Milestone**.
*   **Release Date:** February 2026
*   **Tag:** `v1.3.0-ultra-stable`
*   **Key Achievement:** Implemented a robust Command Center with real-time UI validation and multi-race intelligence.

### New in v1.3.0:
*   **Operational Validation:** Pre-flight data integrity checks prevent invalid deployments to the cloud engine.
*   **Multi-Race Intelligence:** Analyse specific target races with separate goals, logic, and elevation profiles.
*   **1000-Activity Context:** Deep scan logic pulls and summarizes your entire season history for the Coach.
*   **Google Search Grounding:** (v1.2) Automated ingestion of race-specific details from external URLs to refine strategy.

---

## 🛠 Version Control & Rollback
To ensure you can always return to this stable state:
1.  **Tagging:** Use `git tag -a v1.3.0-ultra-stable -m "Stable Release"` in your repository.
2.  **Restoring:** Use `git checkout v1.3.0-ultra-stable` to rollback any future breaking changes.
3.  **History:** Refer to `CHANGELOG.md` for the full technical delta of this version.

---

## 🌟 Key Features

*   **Pro Reasoning Engine:** Uses `gemini-3-pro-preview` for deep strategic planning and `gemini-3-flash-preview` for high-speed automated reports.
*   **Dual-AI Architecture:** A service *built by* AI to be *powered by* AI. Developed using GenAI to compress months of work into a single afternoon.
*   **Deep Narrative Intelligence:** The Coach remembers. It doesn't just look at one run; it analyzes historical trends to detect *Aerobic Decoupling*, *Structural Integrity*, and recovery patterns.
*   **Zero-Cost Operation:** Strategically engineered for the free cloud ecosystem:
    *   **Backend:** .NET 9 Minimal API hosted on **Koyeb**.
    *   **Intelligence:** **Gemini 3** for elite-tier reasoning.
    *   **Automation:** **GitHub Actions** for headless synchronization.

---

## 🛠 Technical Stack

*   **Logic Engine:** C# / .NET 9 (Headless Minimal API).
*   **AI Model:** Google Gemini 3 (Complex Reasoning & Flash Modes).
*   **Infrastructure:** Koyeb (Service Hosting) & GitHub Actions (Scheduled Sync).
*   **Console:** React + Tailwind CSS (Command Center for manual triggers).

---
*Disclaimer: This is a personal research project. Usage must comply with Strava and Google API Terms of Service.*
