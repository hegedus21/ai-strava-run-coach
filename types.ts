
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
  average_speed: number;
  max_speed: number;
  description?: string;
  kilojoules?: number;
}

export interface GoalSettings {
  raceType: string;
  raceDate: string;
  goalTime: string;
}

export interface HeartRateZones {
  z1: number; // Percentage 0-100
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
  resetAt: string; // ISO string for estimated reset
}

export interface PeriodicStat {
  distanceKm: number;
  durationMins: number;
  count: number;
  zones: HeartRateZones;
}

export interface YearlyStat {
  year: number;
  distanceKm: number;
  durationHours: number;
  activityCount: number;
  zones: HeartRateZones;
}

export interface AthleteProfile {
  lastUpdated: string;
  summary: string;
  quota: QuotaStatus;
  stats: {
    marathons: number;
    halfMarathons: number;
    ironmans: number;
    seventyThrees: number;
    ultras: number;
    totalRuns: number;
    totalDistanceKm: number;
  };
  milestones: {
    fiveK: number;
    tenK: number;
    twentyK: number;
    halfMarathon: number;
    thirtyK: number;
    marathon: number;
    ultra: number;
  };
  periodic: {
    week: PeriodicStat;
    month: PeriodicStat;
    year: PeriodicStat;
  };
  yearlyHistory: YearlyStat[];
  triathlon: {
    sprint: number;
    olympic: number;
    halfIronman: number;
    ironman: number;
  };
  pbs: {
    fiveK?: string;
    tenK?: string;
    halfMarathon?: string;
    marathon?: string;
  };
  coachNotes: string;
}

export interface AIAnalysis {
  summary: string;
  activityClassification: 'Easy' | 'Tempo' | 'Long Run' | 'Intervals' | 'Threshold' | 'Other';
  effectivenessScore: number;
  pros: string[];
  cons: string[];
  trendImpact: string;
  goalProgressPercentage: number;
  daysRemaining: number;
  nextWeekFocus: string;
  nextTrainingSuggestion: {
    type: string;
    distance: string;
    duration: string;
    description: string;
    targetMetrics: string;
  };
}

export interface StravaUpdateParams {
  description: string;
  name?: string;
}

export interface StravaWebhookEvent {
  object_type: 'activity' | 'athlete';
  object_id: number;
  aspect_type: 'create' | 'update' | 'delete';
  updates: Record<string, string>;
  owner_id: number;
  subscription_id: number;
  event_time: number;
}
