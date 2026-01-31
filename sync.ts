
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
    console.log(`Target: ${goals.raceType} | Goal Date: ${goals.raceDate}`);
    
    console.log("Fetching recent activities (Scan depth: 30)...");
    const history = await strava.getRecentActivities(30);
    const runs = history.filter(a => a.type === 'Run');

    if (runs.length === 0) {
      console.log("No running activities found in recent history.");
      return;
    }

    const twentyFourHoursAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);

    // Identify candidates based on date and type (descriptions are missing in list view)
    const candidates = runs.filter(a => {
      const activityDate = new Date(a.start_date);
      return activityDate > twentyFourHoursAgo;
    });

    if (candidates.length === 0) {
      console.log("No new runs found in the last 24 hours.");
      return;
    }

    console.log(`Found ${candidates.length} recent run(s). Verifying analysis status...`);

    for (const summaryActivity of candidates) {
      try {
        // Fetch full activity to get the description
        const activity = await strava.getActivity(summaryActivity.id);
        
        if (!GeminiCoachService.needsAnalysis(activity.description)) {
          console.log(`  [SKIPPING] "${activity.name}" - Already contains a StravAI report.`);
          continue;
        }

        const timestamp = new Date(activity.start_date).toLocaleString();
        console.log(`\n[ANALYZING] "${activity.name}" (${timestamp})`);
        
        const contextHistory = runs.filter(a => a.id !== activity.id);
        
        console.log("  -> Generating AI Coaching Insights...");
        const analysis = await coach.analyzeActivity(activity, contextHistory, goals);
        const formattedReport = coach.formatDescription(analysis);

        // Keep existing user text, remove previous StravAI blocks
        const cleanDesc = (activity.description || "")
          .split("################################")[0]
          .trim();
          
        const newDescription = cleanDesc ? `${cleanDesc}\n\n${formattedReport}` : formattedReport;

        await strava.updateActivity(activity.id, { description: newDescription });
        console.log(`  ✅ Success: Activity ${activity.id} updated.`);
        
      } catch (innerError: any) {
        if (innerError instanceof QuotaExhaustedError) {
          console.error("  ❌ Quota Exhausted.");
          // Capacity warning logic is handled inside analyzeActivity error catching if necessary
          (process as any).exit(0);
        }
        console.error(`  ❌ Error processing activity: ${innerError.message}`);
      }
    }

    console.log(`\n--- Batch Sync Cycle Finished ---`);
  } catch (error: any) {
    console.error("CRITICAL ENGINE ERROR:", error.message);
    (process as any).exit(1);
  }
}

runSync();
