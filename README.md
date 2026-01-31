# StravAI Coach 🏃‍♂️🤖

> **Elite performance coaching powered by Gemini 3. From raw GPS data to professional prescriptions.**

StravAI is an autonomous coaching engine designed for athletes who demand professional-grade analysis without the premium price tag. It processes your Strava activities via real-time webhooks, analyzes them against your training history, and writes detailed coaching reports directly into your activity descriptions.

---

## 🌟 Key Features

*   **Dual-AI Architecture:** A service *built by* AI to be *powered by* AI. Developed using GenAI to compress months of work into a single afternoon.
*   **Deep Narrative Intelligence:** The Coach remembers. It doesn't just look at one run; it analyzes historical trends to detect *Aerobic Decoupling*, *Structural Integrity*, and recovery patterns.
*   **Zero-Cost Operation:** Strategically engineered for the free cloud ecosystem:
    *   **Backend:** .NET 9 Minimal API hosted on **Koyeb**.
    *   **Intelligence:** **Gemini 3 Flash** for elite-tier reasoning.
    *   **Automation:** **GitHub Actions** for headless synchronization.
*   **Interactive Feedback Loop:** Add athlete notes (e.g., *"Legs felt heavy"*) to your activity description, and the AI Coach will incorporate your subjective feel into the next prescription.
*   **Goal-Oriented Periodization:** Tracks **Race Readiness (%)** and **T-Minus** countdowns for your specific goal race.

---

## 📸 Coach in Action

StravAI transforms a standard GPS upload into a professional training log.

| **Analysis & Readiness** | **Progression & Prescription** | **Strava Feed View** |
| :---: | :---: | :---: |
| ![Coach Summary](screenshot1.jpg) | ![Next Steps](screenshot2.jpg) | ![Strava Integration](screenshot3.jpg) |

> *Reports include: Master Coach Summary, Race Readiness Score, Next Week Physiological Focus, and precise Training Prescriptions.*

---

## 🛠 Technical Stack

*   **Logic Engine:** C# / .NET 9 (Headless Minimal API).
*   **AI Model:** Google Gemini 1.5/3 (Utilizing long context windows for historical analysis).
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
