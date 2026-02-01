

export interface HeartRateZones {
  z1: number;
  z2: number;
  z3: number;
  z4: number;
  z5: number;
}

export interface QuotaStatus {
  dailyUsed: number;
  dailyLimit: number;
  minuteUsed: number;
  minuteLimit: number;
  resetAt: string;
}

export interface RunCategory {
  count: number;
  pb: string; // Time format or N/A
  category: string;
}

export interface TrainingSession {
  date: string;
  type: 'Easy' | 'Tempo' | 'Interval' | 'Long Run' | 'Gym' | 'Rest';
  title: string;
  description: string;
  distance?: string;
  duration?: string;
  targetPace?: string;
  intervals?: string; // e.g. "6x800m @ 3:45"
  isCompleted: boolean;
}

// Added AIAnalysis interface for coach analysis results
export interface AIAnalysis {
  summary: string;
  activityClassification: 'Easy' | 'Tempo' | 'Long Run' | 'Intervals' | 'Threshold' | 'Other';
  effectivenessScore: number;
  pros: string[];
  cons: string[];
  trendImpact: string;
  goalProgressPercentage: number;
  nextWeekFocus: string;
  nextTrainingSuggestion: {
    type: string;
    distance: string;
    duration: string;
    description: string;
    targetMetrics: string;
  };
  daysRemaining: number;
}

// Added GoalSettings interface for athlete training goals
export interface GoalSettings {
  raceType: string;
  raceDate: string;
  goalTime: string;
}

// Added StravaUpdateParams interface for activity update requests
export interface StravaUpdateParams {
  description?: string;
  name?: string;
}

export interface AthleteProfile {
  lastUpdated: string;
  summary: string;
  coachNotes: string;
  stravaQuota: QuotaStatus;
  geminiQuota: QuotaStatus;
  
  // Categorized Stats
  milestones: {
    backyardLoops: RunCategory;
    fiveK: RunCategory;
    tenK: RunCategory;
    twentyK: RunCategory;
    halfMarathon: RunCategory;
    marathon: RunCategory;
    ultra: RunCategory;
    other: RunCategory;
  };
  
  triathlon: {
    sprint: number;
    olympic: number;
    halfIronman: number;
    ironman: number;
  };

  periodic: {
    week: { distanceKm: number; zones: HeartRateZones };
    month: { distanceKm: number; zones: HeartRateZones };
    year: { distanceKm: number; zones: HeartRateZones };
  };

  trainingPlan: TrainingSession[];
  yearlyHistory: Array<{
    year: number;
    activityCount: number;
    distanceKm: number;
    zones: HeartRateZones;
  }>;
}

export interface StravaActivity {
  id: number;
  name: string;
  type: string;
  start_date: string;
  distance: number;
  moving_time: number;
  total_elevation_gain: number;
  average_heartrate?: number;
  // Added optional fields to match API responses and mock data
  max_heartrate?: number;
  average_speed?: number;
  max_speed?: number;
  kilojoules?: number;
  description?: string;
}
