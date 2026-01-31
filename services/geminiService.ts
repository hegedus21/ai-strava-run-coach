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
   * Determines if an activity needs analysis.
   * Skips if it already contains a valid StravAI Report.
   */
  static needsAnalysis(description: string | undefined): boolean {
    if (!description || description.trim() === "") return true;
    
    const hasReport = description.includes("StravAI Report");
    const hasBorder = description.includes("#");
    const hasProcessedTag = description.includes(STRAVAI_SIGNATURE);
    const hasPlaceholder = description.includes(STRAVAI_PLACEHOLDER);

    // If it's just a placeholder, we definitely need to analyze it properly
    if (hasPlaceholder) return true;

    // Skip if it meets the criteria of a completed report
    const alreadyDone = (hasReport && hasBorder) || hasProcessedTag;
    return !alreadyDone;
  }

  async analyzeActivity(
    activity: StravaActivity, 
    history: StravaActivity[], 
    goals: GoalSettings
  ): Promise<AIAnalysis> {
    const maxRetries = 3;
    let attempt = 0;

    // Helper to extract previous coaching insights to give the AI context
    const historySummary = (list: StravaActivity[]) => list
      .map(h => {
        const date = new Date(h.start_date).toLocaleDateString();
        const stats = `${h.type} (${date}): ${(h.distance/1000).toFixed(2)}km, HR: ${h.average_heartrate || '?'}`;
        
        let prevInsights = "";
        if (h.description && h.description.includes("StravAI Report")) {
          // Extract just the coach's summary from previous reports if available
          const match = h.description.match(/\*\*Coach's Summary:\*\*\n(.*?)\n/s);
          if (match) prevInsights = ` | PREV_ADVICE: ${match[1].substring(0, 100)}...`;
        }
        
        return `- ${stats}${prevInsights}`;
      })
      .join("\n");

    const now = new Date();
    const raceDate = new Date(goals.raceDate);
    const diffTime = raceDate.getTime() - now.getTime();
    const daysRemaining = Math.max(0, Math.ceil(diffTime / (1000 * 60 * 60 * 24)));

    const prompt = `
      ROLE: Professional Athletic Performance Coach.
      GOAL: ${goals.raceType} | DATE: ${goals.raceDate} | TARGET: ${goals.goalTime}.
      DAYS REMAINING: ${daysRemaining}.
      
      CURRENT SESSION:
      - Name: ${activity.name}
      - Distance: ${(activity.distance / 1000).toFixed(2)} km
      - Avg HR: ${activity.average_heartrate ?? 'N/A'}
      
      TRAINING CONTEXT (Recent Activities & Your Previous Advice):
      ${historySummary(history.slice(0, 10))}

      TASK:
      1. Analyze the current run. 
      2. If "PREV_ADVICE" is present in history, check if the athlete followed it.
      3. Provide a summary, readiness score, and next workout.
      4. Output strictly in JSON format.
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
        if (!text) throw new Error("Empty AI response");
        const parsed = JSON.parse(text);
        return { ...parsed, daysRemaining };
      } catch (err: any) {
        attempt++;
        const errStr = err.message?.toLowerCase() || "";
        if (errStr.includes("quota exceeded") || errStr.includes("resource_exhausted")) {
          throw new QuotaExhaustedError("Daily API Quota Exceeded.");
        }
        if (attempt < maxRetries) {
          await this.sleep(2000 * attempt);
          continue;
        }
        throw err;
      }
    }
    throw new Error("Analysis failed after retries.");
  }

  private getCETTimestamp(): string {
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
*${STRAVAI_SIGNATURE}-PENDING*
${BORDER}
    `.trim();
  }

  formatDescription(analysis: AIAnalysis): string {
    return `
${BORDER}
StravAI Report
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
