# Changelog

All notable changes to the StravAI Coach project will be documented in this file.

## [1.3.0-ultra-stable] - 2026-02-05
### "The Stable Command Center Update"

This version focuses on UI reliability, input integrity, and professional presentation.

#### Added
- **UI Validation Layer**: Implemented pre-flight checks for all mission-critical inputs. The command terminal now blocks invalid data transmission to the cloud engine.
- **Visual Error Feedback**: Added pulsing red state alerts and high-visibility error labels for missing required fields (Race Name, Date, Goal Time).

#### Fixed
- **Input Layout**: Corrected placeholder clipping for Date fields on desktop viewports.
- **Label Clarity**: Renamed operational buttons and section headers to use professional coaching terminology (e.g., "Analyse Main Race", "Analyse Recent Activities").

## [1.2.0-multi-planner] - 2026-02-03
### "The Multi-Race Intelligence Update"

This version introduces the ability to plan and analyze specific races beyond the main season goal.

#### Added
- **Custom Race Deployment**: New UI form allowing users to input specific race details (Name, Distance, Date, Target Time, URL).
- **Google Search Grounding**: The AI now browses provided race URLs to ingest elevation profiles, terrain types, and weather data for more accurate strategy generation.
- **Goal Realism Engine**: Integrated feasibility analysis that compares historical performance (up to 1000 activities) against target race metrics to provide a success probability.
- **Dynamic Strava Storage**: Automatically creates/updates dedicated private Strava activities for each unique custom race.

## [1.1.0-ultra-coach] - 2026-01-29
### "The Deep Performance Update"
- Added Ultra Season Strategy Engine.
- Deep 1000-activity history context.
- Scheduled Sunday analysis.
