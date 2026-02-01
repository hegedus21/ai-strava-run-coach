
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
  pb: string; // Time format e.g. "19:45"
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
  intervals?: {
    reps: number;
    work: string; // e.g. "800m" or "3min"
    rest: string; // e.g. "200m" or "90s"
    pace: string;
    warmup: string;
    cooldown: string;
  };
  gymWorkout?: string[]; // List of dumbbell exercises
  isCompleted: boolean;
}

export interface AthleteProfile {
  lastUpdated: string;
  summary: string;
  coachNotes: string;
  stravaQuota: QuotaStatus;
  geminiQuota: QuotaStatus;
  
  // Categorized Stats
  milestones: {
    backyardLoop: RunCategory;
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
  max_heartrate?: number;
  average_speed?: number;
  max_speed?: number;
  kilojoules?: number;
  description?: string;
}

export interface AIAnalysis {
  summary: string;
  activityClassification: string;
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

export interface GoalSettings {
  raceType: string;
  raceDate: string;
  goalTime: string;
}

export interface StravaUpdateParams {
  description?: string;
  name?: string;
}
