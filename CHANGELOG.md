
# Changelog

All notable changes to the StravAI Coach project will be documented in this file.

## [1.4.0-zero-trust] - 2026-02-06
### "The Zero Trust Security Update"

This version elevates the service from a functional utility to a secure production-grade engine.

#### Added
- **Global Auth Middleware**: Implemented a mandatory header check (`X-StravAI-Secret`) for all sensitive API routes.
- **Webhook Handshake (GET)**: Dedicated handler for the Strava Subscription challenge using `STRAVA_VERIFY_TOKEN`.
- **UI Auto-Invalidation**: The Command Center now detects 401 Unauthorized errors and immediately triggers a system lockout to protect the gateway.

#### Changed
- **Secret Separation**: Decoupled the UI password from the Webhook verify token for improved security posture.
- **Path Exemptions**: Specifically allow-listed `/health` and `/webhook` to ensure system uptime and integration reliability while keeping administrative routes dark.

## [1.3.0-ultra-stable] - 2026-02-05
### "The Stable Command Center Update"
- UI Validation Layer for all inputs.
- Visual error feedback and pulsing red state alerts.
- Corrected input layout for Date fields.

## [1.2.0-multi-planner] - 2026-02-03
### "The Multi-Race Intelligence Update"
- Custom Race Deployment with Gemini Search Grounding.
- Goal Realism Engine using 1000-activity history scans.

## [1.1.0-ultra-coach] - 2026-01-29
### "The Deep Performance Update"
- Added Ultra Season Strategy Engine.
- Scheduled Sunday analysis.
