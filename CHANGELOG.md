# Changelog

All notable changes to the StravAI Coach project will be documented in this file.

## [1.1.0-ultra-coach] - 2025-03-24
### "The Deep Performance Update" (Stable Version 2)

This version focuses on long-term physiological modeling and race-day readiness.

#### Added
- **Ultra Season Strategy Engine**: A new backend module that generates a comprehensive race strategy including nutrition, gear, and 3-tier pacing plans.
- **Deep History Context**: Expanded the AI context window to analyze up to **1,000 activities**, providing the coach with a "career-level" view of the athlete's fitness.
- **Scheduled Analysis**: Implemented `SeasonBackgroundWorker` to automatically recalculate race strategies every Sunday at 02:00 UTC.
- **Manual Recalculation**: Added a dedicated "Recalculate Strategy" button in the Command Center UI.

#### Changed
- **Optimization**: Switched from sending raw activity JSON to **Pre-Summarized History Strings**. This reduces token usage by ~80% and prevents "Context Overflow" and API timeouts.
- **Timeout Management**: Increased internal HttpClient timeouts to **5 minutes** to support complex "Pro" reasoning tasks.
- **UI Branding**: Updated Command Center to display `v1.1.0_ULTRA_COACH` status.

#### Fixed
- **.NET Build Error**: Resolved namespace collision in `Program.cs` involving `System.Net.Http.Options`.
- **Latency Issues**: Optimized history fetching loops to handle large activity counts without blocking the main event loop.

---
*Note: This version is considered stable and suitable for production deployments on Koyeb.*
