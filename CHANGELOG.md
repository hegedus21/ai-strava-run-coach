# Changelog

All notable changes to the StravAI Coach project will be documented in this file.

## [1.2.0-multi-planner] - 2025-03-24
### "The Multi-Race Intelligence Update"

This version introduces the ability to plan and analyze specific races beyond the main season goal.

#### Added
- **Custom Race Deployment**: New UI form allowing users to input specific race details (Name, Distance, Date, Target Time, URL).
- **Google Search Grounding**: The AI now browses provided race URLs to ingest elevation profiles, terrain types, and weather data for more accurate strategy generation.
- **Goal Realism Engine**: Integrated feasibility analysis that compares historical performance (up to 1000 activities) against target race metrics to provide a success probability.
- **Dynamic Strava Storage**: Automatically creates/updates dedicated private Strava activities for each unique custom race.

#### Changed
- **AI Model Selection**: Upgraded the custom analysis engine to `gemini-3-pro-image-preview` when search tools are required.
- **Prompt Architecture**: Re-engineered to include a "Feasibility & Goal Calibration" section.

## [1.1.0-ultra-coach] - 2025-03-24
### "The Deep Performance Update" (Stable Version 2)
- Added Ultra Season Strategy Engine.
- Deep 1000-activity history context.
- Scheduled Sunday analysis.
