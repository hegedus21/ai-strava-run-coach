
import { StravaService } from './services/stravaService';
import { GeminiCoachService, QuotaExhaustedError, STRAVAI_PLACEHOLDER, STRAVAI_SIGNATURE } from './services/geminiService';
import { GoalSettings, AthleteProfile, QuotaStatus } from './types';

/**
 * Headless Sync Script
 * Runs via GitHub Actions to process activities and update the Athlete Profile.
 */
async function runSync() {
  console.log("--- StravAI Cloud Engine: Quota-Aware Batch Sync ---");
  
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
    
    // --- STEP 1: Fetch the Athlete Profile & Quota ---
    console.log("Locating System Cache...");
    const recent = await strava.getRecentActivities(50);
    const cacheActivitySummary = recent.find(a => a.name === "[StravAI] System Cache");
    
    let cacheActivity: any = null;
    let quota: QuotaStatus = { dailyUsed: 0, dailyLimit: 1500, minuteUsed: 0, minuteLimit: 15, resetAt: new Date(Date.now() + 86400000).toISOString() };

    if (cacheActivitySummary) {
      cacheActivity = await strava.getActivity(cacheActivitySummary.id);
      const desc = cacheActivity.description || "";
      
      // Parse Quota
      if (desc.includes("---QUOTA_START---")) {
        const qStr = desc.split("---QUOTA_START---")[1].split("---QUOTA_END---")[0];
        quota = JSON.parse(qStr);
        // Reset check
        if (new Date() > new Date(quota.resetAt)) {
          quota.dailyUsed = 0;
          quota.resetAt = new Date(Date.now() + 86400000).toISOString();
        }
      }
    }

    if (quota.dailyUsed >= 1495) {
      console.warn("⚠️ API Quota nearly exhausted. Stopping headless sync to preserve manual audit capacity.");
      return;
    }

    console.log("Fetching recent activities for analysis...");
    const runs = recent.filter(a => a.type === 'Run');
    const twentyFourHoursAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);
    const candidates = runs.filter(a => new Date(a.start_date) > twentyFourHoursAgo);

    if (candidates.length === 0) {
      console.log("No new runs found in the last 24 hours.");
      return;
    }

    for (const summaryActivity of candidates) {
      try {
        const activity = await strava.getActivity(summaryActivity.id);
        
        if (!GeminiCoachService.needsAnalysis(activity.description)) {
          console.log(`  [SKIPPING] "${activity.name}" - Already processed.`);
          continue;
        }

        if (quota.dailyUsed >= 1500) break;

        console.log(`\n[ANALYZING] "${activity.name}" (Used: ${quota.dailyUsed}/${quota.dailyLimit})`);
        
        const analysis = await coach.analyzeActivity(activity, runs.filter(r => r.id !== activity.id), goals);
        const formattedReport = coach.formatDescription(analysis);

        const cleanDesc = (activity.description || "").split("################################")[0].trim();
        const newDescription = cleanDesc ? `${cleanDesc}\n\n${formattedReport}` : formattedReport;

        await strava.updateActivity(activity.id, { description: newDescription });
        console.log(`  ✅ Success: Activity updated.`);
        
        // Update Quota
        quota.dailyUsed++;
        
      } catch (innerError: any) {
        if (innerError instanceof QuotaExhaustedError) {
          console.error("  ❌ API Quota Exhausted.");
          break;
        }
        console.error(`  ❌ Error processing activity: ${innerError.message}`);
      }
    }

    // --- STEP 2: Save Updated Quota to Cache ---
    if (cacheActivity) {
      const desc = cacheActivity.description || "";
      const newQuotaStr = `---QUOTA_START---\n${JSON.stringify(quota)}\n---QUOTA_END---`;
      let finalDesc = desc;
      if (desc.includes("---QUOTA_START---")) {
        const parts = desc.split("---QUOTA_START---");
        const postParts = parts[1].split("---QUOTA_END---");
        finalDesc = parts[0] + newQuotaStr + postParts[1];
      } else {
        finalDesc += "\n" + newQuotaStr;
      }
      await strava.updateActivity(cacheActivity.id, { description: finalDesc });
      console.log(`\n📊 System Quota Updated: ${quota.dailyUsed} calls used today.`);
    }

    console.log(`\n--- Batch Sync Cycle Finished ---`);
  } catch (error: any) {
    console.error("CRITICAL ENGINE ERROR:", error.message);
    (process as any).exit(1);
  }
}

runSync();
