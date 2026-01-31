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
    // Ensure we use the correct method name from StravaService.ts
    const history = await strava.getRecentActivities(30);
    const runs = history.filter(a => a.type === 'Run');

    if (runs.length === 0) {
      console.log("No running activities found in recent history.");
      return;
    }

    const twentyFourHoursAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);

    const activitiesToProcess = runs.filter(a => {
      const activityDate = new Date(a.start_date);
      const isRecent = activityDate > twentyFourHoursAgo;
      const needsAnalysis = GeminiCoachService.needsAnalysis(a.description);
      return isRecent && needsAnalysis;
    });

    if (activitiesToProcess.length === 0) {
      console.log("All recent activities are already processed or no new runs found in the last 24h.");
      return;
    }

    console.log(`Found ${activitiesToProcess.length} pending activity(ies). Processing...`);

    for (const activity of activitiesToProcess) {
      const timestamp = new Date(activity.start_date).toLocaleString();
      console.log(`\n[ANALYZING] "${activity.name}" (${timestamp})`);
      
      const contextHistory = runs.filter(a => a.id !== activity.id);
      
      try {
        console.log("  -> Generating AI Coaching Insights...");
        const analysis = await coach.analyzeActivity(activity, contextHistory, goals);
        const formattedReport = coach.formatDescription(analysis);

        // Keep existing user description, append AI report at the bottom
        const cleanDesc = (activity.description || "")
          .split("################################")[0]
          .split("---")[0] // Avoid double borders if previous analysis failed halfway
          .trim();
          
        const newDescription = cleanDesc ? `${cleanDesc}\n\n${formattedReport}` : formattedReport;

        await strava.updateActivity(activity.id, { description: newDescription });
        console.log(`  ✅ Success: Activity ${activity.id} updated.`);
      } catch (innerError: any) {
        if (innerError instanceof QuotaExhaustedError) {
          console.error("  ❌ CRITICAL: Gemini API Quota Exhausted.");
          
          if (!activity.description?.includes(STRAVAI_SIGNATURE)) {
            console.log("  -> Marking activity with capacity warning placeholder...");
            const placeholderReport = coach.formatPlaceholder();
            const cleanDesc = (activity.description || "").split("################################")[0].trim();
            const newDescription = cleanDesc ? `${cleanDesc}\n\n${placeholderReport}` : placeholderReport;
            await strava.updateActivity(activity.id, { description: newDescription });
          }
          
          console.log("Stopping batch execution to prevent further errors.");
          // Cast process to any to access exit() in Node.js environment
          (process as any).exit(0);
        }
        console.error(`  ❌ Error processing ${activity.id}: ${innerError.message}`);
      }
    }

    console.log(`\n--- Batch Sync Cycle Finished Successfully ---`);
  } catch (error: any) {
    console.error("CRITICAL ENGINE FAILURE:", error.message);
    // Cast process to any to access exit() in Node.js environment
    (process as any).exit(1);
  }
}

runSync();
