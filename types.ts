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