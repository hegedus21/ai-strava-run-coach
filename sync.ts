
import { StravaService } from './services/stravaService';
import { GeminiCoachService, QuotaExhaustedError, STRAVAI_PLACEHOLDER, STRAVAI_SIGNATURE } from './services/geminiService';
import { GoalSettings } from './types';

/**
 * Headless Sync Script
 * Runs via GitHub Actions to process activities from the last 24 hours.
 */
async function runSync() {
  console.log("--- StravAI Cloud Engine: Scheduled Batch Sync ---");
  
  const goals: GoalSettings = {
    raceType: process.env.GOAL_RACE_TYPE || "Marathon",
    raceDate: process.env.GOAL_RACE_DATE || "2025-12-31",
    goalTime: process.env.GOAL_RACE_TIME || "3:30:00"
  };

  const strava = new StravaService();
  const coach = new GeminiCoachService();

  try {
    console.log("Authenticating with Strava...");
    await strava.refreshAuth();
    
    console.log("Fetching recent activities (Scan depth: 30)...");
    const history = await strava.getRecentActivities(30);
    const runs = history.filter(a => a.type === 'Run');

    if (runs.length === 0) {
      console.log("No running activities found.");
      return;
    }

    const twentyFourHoursAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);
    const candidates = runs.filter(a => new Date(a.start_date) > twentyFourHoursAgo);

    if (candidates.length === 0) {
      console.log("No new runs found.");
      return;
    }

    console.log(`Verifying ${candidates.length} run(s) for analysis...`);

    for (const summaryActivity of candidates) {
      try {
        const activity = await strava.getActivity(summaryActivity.id);
        
        if (!GeminiCoachService.needsAnalysis(activity.description)) {
          // Skip silently to reduce logs unless it's a new analysis
          continue;
        }

        console.log(`[ANALYZING] "${activity.name}"`);
        const analysis = await coach.analyzeActivity(activity, runs.filter(a => a.id !== activity.id), goals);
        const formattedReport = coach.formatDescription(analysis);

        // Keep existing user text, remove previous StravAI blocks
        const cleanDesc = (activity.description || "")
          .split("################################")[0]
          .trim();
          
        const newDescription = cleanDesc ? `${cleanDesc}\n\n${formattedReport}` : formattedReport;

        await strava.updateActivity(activity.id, { description: newDescription });
        console.log(`  ✅ Updated: ${activity.id}`);
        
      } catch (innerError: any) {
        if (innerError instanceof QuotaExhaustedError) {
          console.error("  ❌ Quota Exhausted.");
          process.exit(0);
        }
        console.error(`  ❌ Error: ${innerError.message}`);
      }
    }

    console.log(`--- Sync Cycle Finished ---`);
  } catch (error: any) {
    console.error("CRITICAL ENGINE ERROR:", error.message);
    process.exit(1);
  }
}

runSync();
