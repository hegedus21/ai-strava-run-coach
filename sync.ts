import { StravaService } from './services/stravaService';
import { GeminiCoachService, QuotaExhaustedError, STRAVAI_PLACEHOLDER } from './services/geminiService';
import { GoalSettings, StravaActivity } from './types';

async function runSync() {
  console.log("--- Starting StravAI Daily Batch Sync ---");
  
  const goals: GoalSettings = {
    raceType: process.env.GOAL_RACE_TYPE || "Marathon",
    raceDate: process.env.GOAL_RACE_DATE || "2025-12-31",
    goalTime: process.env.GOAL_RACE_TIME || "3:30:00"
  };

  const strava = new StravaService();
  const coach = new GeminiCoachService();

  try {
    await strava.refreshAuth();
    console.log(`Target Goal: ${goals.raceType} | Date: ${goals.raceDate}`);
    
    console.log("Scanning recent history...");
    const history = await strava.getHistoryForBaseline(30);
    const runs = history.filter(a => a.type === 'Run');

    if (runs.length === 0) {
      console.log("No runs found in recent history.");
      return;
    }

    const twentyFourHoursAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);

    const activitiesToProcess = runs.filter(a => {
      const activityDate = new Date(a.start_date);
      const isRecent = activityDate > twentyFourHoursAgo;
      return isRecent && GeminiCoachService.needsAnalysis(a.description);
    });

    if (activitiesToProcess.length === 0) {
      console.log("No new or pending activities found from the last 24 hours.");
      return;
    }

    console.log(`Found ${activitiesToProcess.length} activity(ies) to process/retry.`);

    for (const activity of activitiesToProcess) {
      console.log(`\nProcessing: "${activity.name}" (${new Date(activity.start_date).toLocaleString()})`);
      const contextHistory = runs.filter(a => a.id !== activity.id);
      
      try {
        console.log("Consulting Coach Gemini...");
        const analysis = await coach.analyzeActivity(activity, contextHistory, goals);
        const formattedReport = coach.formatDescription(analysis);

        const cleanDesc = (activity.description || "").split("################################")[0].trim();
        const newDescription = `${cleanDesc}\n\n${formattedReport}`;

        await strava.updateActivity(activity.id, { description: newDescription });
        console.log(`✅ Success: Activity ${activity.id} updated with real analysis.`);
      } catch (innerError: any) {
        if (innerError instanceof QuotaExhaustedError) {
          console.error("CRITICAL: Gemini Free Tier Quota Exhausted.");
          
          if (!activity.description?.includes(STRAVAI_PLACEHOLDER)) {
            console.log("Adding placeholder to activity description...");
            const placeholderReport = coach.formatPlaceholder();
            const cleanDesc = (activity.description || "").split("################################")[0].trim();
            const newDescription = `${cleanDesc}\n\n${placeholderReport}`;
            await strava.updateActivity(activity.id, { description: newDescription });
            console.log(`Activity ${activity.id} marked with capacity warning.`);
          }
          
          console.log("Aborting current batch sync.");
          (process as any).exit(0);
        }
        console.error(`Failed to analyze activity ${activity.id}: ${innerError.message}`);
      }
    }

    console.log(`\n--- Batch Sync Cycle Complete ---`);
  } catch (error) {
    console.error("Critical Sync Failure:", error);
    (process as any).exit(1);
  }
}

runSync();