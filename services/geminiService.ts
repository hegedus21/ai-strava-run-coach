import { GoogleGenAI, Type } from "@google/genai";
import { StravaActivity, AIAnalysis, GoalSettings } from "../types";

export class QuotaExhaustedError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "QuotaExhaustedError";
  }
}

export const STRAVAI_SIGNATURE = "[StravAI-Processed]";
export const STRAVAI_PLACEHOLDER = "Activity will be analysed later as soon as the AI coach has capacity";
const BORDER = "################################";

export class GeminiCoachService {
  constructor() {}

  private async sleep(ms: number) {
    return new Promise(resolve => setTimeout(resolve, ms));
  }

  /**
   * Checks if an activity needs analysis based on its description.
   * Skips if description contains "StravAI Report", "###", or "[StravAI-Processed]".
   */
  static needsAnalysis(description: string | undefined): boolean {
    if (!description || description.trim() === "") return true;
    
    const hasReportTitle = description.includes("StravAI Report");
    const hasBorder = description.includes("###");
    const hasSignature = description.includes(STRAVAI_SIGNATURE);
    const hasPlaceholder = description.includes(STRAVAI_PLACEHOLDER);

    // If it has our markers but it's just a placeholder, we might want to re-analyze it
    if (hasPlaceholder) return true;

    // Skip if it contains our report indicators
    return !(hasReportTitle || hasBorder || hasSignature);
  }

  async analyzeActivity(
    activity: StravaActivity, 
    history: StravaActivity[], 
    goals: GoalSettings
  ): Promise<AIAnalysis> {
    const maxRetries = 5;
    let attempt = 0;

    // Use previous analyses in the context history to provide better continuity
    const historySummary = (list: StravaActivity[]) => list
      .map(h => {
        const stats = `${h.type} (${new Date(h.start_date).toLocaleDateString()}): ${(h.distance/1000).toFixed(2)}km, Pace: ${((h.moving_time/60)/(h.distance/1000)).toFixed(2)} min/km, HR: ${h.average_heartrate || '?'}`;
        // If the description has a previous analysis, we could try to extract a snippet, 
        // but for now, we provide the raw metadata which is most reliable.
        return `- ${stats}`;
      })
      .join("\n");

    const now = new Date();
    const raceDate = new Date(goals.raceDate);
    const diffTime = raceDate.getTime() - now.getTime();
    const daysRemaining = Math.max(0, Math.ceil(diffTime / (1000 * 60 * 60 * 24)));

    const thirtyDaysAgo = new Date();
    thirtyDaysAgo.setDate(thirtyDaysAgo.getDate() - 30);
    
    const recent30Days = history.filter(h => new Date(h.start_date) > thirtyDaysAgo);
    const deepBaseline = history.slice(0, 50);

    const prompt = `
      ROLE: Professional Athletic Performance Coach.
      ATHLETE GOAL: ${goals.raceType} on ${goals.raceDate} (Target: ${goals.goalTime}).
      DAYS REMAINING UNTIL RACE: ${daysRemaining} days.
      
      ANALYSIS TARGET (Current Activity):
      - Name: ${activity.name}
      - Distance: ${(activity.distance / 1000).toFixed(2)} km
      - Moving Time: ${(activity.moving_time / 60).toFixed(1)} mins
      - Avg HR: ${activity.average_heartrate ?? 'N/A'} bpm
      
      CONTEXT A: RECENT TRENDS (Last 30 Days)
      ${historySummary(recent30Days)}

      CONTEXT B: DEEP BASELINE (Up to 1 Year / 50 Activities)
      ${historySummary(deepBaseline)}

      TASK:
      1. Classify the workout type.
      2. Provide a summary analysis (2-3 sentences).
      3. Assess current training trends vs goal. Use the history provided to understand if the athlete is improving or needs more rest.
      4. Calculate Progress: Estimate goal readiness percentage (0-100%).
      5. Identify Next Week Focus: Based on recent volume/intensity, what should be the primary focus for the upcoming 7 days?
      6. Prescribe the immediate next workout.

      OUTPUT: JSON only.
    `;

    while (attempt < maxRetries) {
      try {
        const ai = new GoogleGenAI({ apiKey: process.env.API_KEY });
        const response = await ai.models.generateContent({
          model: "gemini-3-flash-preview",
          contents: prompt,
          config: {
            responseMimeType: "application/json",
            responseSchema: {
              type: Type.OBJECT,
              properties: {
                summary: { type: Type.STRING },
                activityClassification: { type: Type.STRING, enum: ['Easy', 'Tempo', 'Long Run', 'Intervals', 'Threshold', 'Other'] },
                effectivenessScore: { type: Type.NUMBER },
                pros: { type: Type.ARRAY, items: { type: Type.STRING } },
                cons: { type: Type.ARRAY, items: { type: Type.STRING } },
                trendImpact: { type: Type.STRING },
                goalProgressPercentage: { type: Type.NUMBER },
                nextWeekFocus: { type: Type.STRING },
                nextTrainingSuggestion: {
                  type: Type.OBJECT,
                  properties: {
                    type: { type: Type.STRING },
                    distance: { type: Type.STRING },
                    duration: { type: Type.STRING },
                    description: { type: Type.STRING },
                    targetMetrics: { type: Type.STRING }
                  },
                  required: ["type", "distance", "duration", "description", "targetMetrics"]
                }
              },
              required: ["summary", "activityClassification", "effectivenessScore", "pros", "cons", "trendImpact", "goalProgressPercentage", "nextWeekFocus", "nextTrainingSuggestion"]
            }
          }
        });

        const text = response.text;
        if (!text) throw new Error("Empty response from Gemini");
        const parsed = JSON.parse(text);
        return { ...parsed, daysRemaining };
      } catch (err: any) {
        attempt++;
        const errStr = err.message?.toLowerCase() || "";
        if (errStr.includes("quota exceeded") || errStr.includes("resource_exhausted")) {
          throw new QuotaExhaustedError("Daily API Quota Exceeded.");
        }
        if (attempt < maxRetries) {
          await this.sleep(Math.pow(2, attempt) * 1000);
          continue;
        }
        throw err;
      }
    }
    throw new Error("Maximum retries reached for Gemini API.");
  }

  private getCETTimestamp(): string {
    // CET is UTC+1 (or UTC+2 in summer, but for simplicity we use a stable UTC+1 shift or Intl)
    return new Intl.DateTimeFormat('en-GB', {
      timeZone: 'Europe/Berlin',
      year: 'numeric',
      month: '2-digit',
      day: '2-digit',
      hour: '2-digit',
      minute: '2-digit',
      second: '2-digit',
      hour12: false
    }).format(new Date()) + " CET";
  }

  formatPlaceholder(): string {
    return `
${BORDER}
StravAI Report
---
${STRAVAI_PLACEHOLDER}

Analysis created at: ${this.getCETTimestamp()}
*${STRAVAI_SIGNATURE}*
${BORDER}
    `.trim();
  }

  formatDescription(analysis: AIAnalysis): string {
    return `
${BORDER}
StravAI Performance Report
---
**Coach's Summary:**
[${analysis.activityClassification}] ${analysis.summary}

**Race Readiness:** ${analysis.goalProgressPercentage}% | **T-Minus:** ${analysis.daysRemaining} days
**Next Week Focus:** ${analysis.nextWeekFocus}

**Training Prescription:**
- **Workout:** ${analysis.nextTrainingSuggestion.type} (${analysis.nextTrainingSuggestion.distance})
- **Target:** ${analysis.nextTrainingSuggestion.targetMetrics}
- **Focus:** ${analysis.nextTrainingSuggestion.description}

Analysis created at: ${this.getCETTimestamp()}
*${STRAVAI_SIGNATURE}*
${BORDER}
    `.trim();
  }
}
