
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
   * Strictly skips if it already contains a valid StravAI Report.
   */
  static needsAnalysis(description: string | undefined): boolean {
    if (!description) return true;
    
    const trimmed = description.trim().toLowerCase();
    if (trimmed === "") return true;
    
    // Check for our signature markers
    const hasReportHeader = trimmed.includes("stravai report");
    const hasSignature = trimmed.includes(STRAVAI_SIGNATURE.toLowerCase());
    const hasAsteriskSignature = trimmed.includes("*[stravai-processed]*");
    
    // Exception: If it's a placeholder, we definitely need to re-analyze it
    if (trimmed.includes("activity will be analysed later")) return true;

    // Skip if it meets the criteria of a completed report
    return !(hasReportHeader || hasSignature || hasAsteriskSignature);
  }

  async analyzeActivity(
    activity: StravaActivity, 
    history: StravaActivity[], 
    goals: GoalSettings
  ): Promise<AIAnalysis> {
    const maxRetries = 3;
    let attempt = 0;

    // Build a rich historical context for the AI
    const historySummary = (list: StravaActivity[]) => list
      .map(h => {
        const date = new Date(h.start_date).toLocaleDateString();
        const stats = `${h.type} (${date}): ${(h.distance/1000).toFixed(2)}km, Pace: ${((h.moving_time/60)/(h.distance/1000)).toFixed(2)}m/k, HR: ${h.average_heartrate || '?'}`;
        
        let prevInsights = "";
        if (h.description && h.description.includes("StravAI Report")) {
          const match = h.description.match(/\*\*Coach's Summary:\*\*\n(.*?)\n/s);
          if (match) prevInsights = ` | PREV_ANALYSIS: ${match[1].substring(0, 150)}`;
        }
        
        return `- ${stats}${prevInsights}`;
      })
      .join("\n");

    const now = new Date();
    const raceDate = new Date(goals.raceDate);
    const diffTime = raceDate.getTime() - now.getTime();
    const daysRemaining = Math.max(0, Math.ceil(diffTime / (1000 * 60 * 60 * 24)));

    const prompt = `
      ROLE: Elite Performance Running Coach (Personality: Analytical, Encouraging, Precise).
      CONTEXT: Athlete is training for a ${goals.raceType} on ${goals.raceDate} (Target: ${goals.goalTime}).
      T-MINUS: ${daysRemaining} days.
      
      CURRENT WORKOUT:
      - Name: ${activity.name}
      - Distance: ${(activity.distance / 1000).toFixed(2)} km
      - Moving Time: ${(activity.moving_time / 60).toFixed(1)} mins
      - Avg HR: ${activity.average_heartrate ?? 'N/A'} bpm
      - Max HR: ${activity.max_heartrate ?? 'N/A'} bpm
      
      ATHLETE HISTORY & PREVIOUS ADVICE:
      ${historySummary(history.slice(0, 12))}

      TASK:
      1. Analyze the performance relative to the goal race. 
      2. Identify strengths (Pros) and areas for improvement (Cons).
      3. Provide a clear, actionable focus for the next 7 days.
      4. Prescribe exactly ONE specific session for their next workout.
      5. OUTPUT: Strictly JSON format.
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
        if (!text) throw new Error("Empty AI result");
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
    throw new Error("Coach unreachable.");
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
