
import React, { useState, useEffect } from 'react';
import Layout from './components/Layout';
import ActivityCard from './components/ActivityCard';
import AnalysisView from './components/AnalysisView';
import { StravaService } from './services/stravaService';
import { GeminiCoachService } from './services/geminiService';
import { StravaActivity, AIAnalysis, GoalSettings } from './types';
import { MOCK_ACTIVITIES } from './services/mockData';

const App: React.FC = () => {
  const [activities, setActivities] = useState<StravaActivity[]>(MOCK_ACTIVITIES);
  const [selectedActivity, setSelectedActivity] = useState<StravaActivity | null>(null);
  const [analysis, setAnalysis] = useState<AIAnalysis | null>(null);
  const [loading, setLoading] = useState(false);
  const [isRefreshing, setIsRefreshing] = useState(false);

  // Goal settings (In a real app, these would come from user profile)
  const goals: GoalSettings = {
    raceType: "Marathon",
    raceDate: "2025-10-12",
    goalTime: "3:45:00"
  };

  const coach = new GeminiCoachService();
  const strava = new StravaService();

  const handleRefresh = async () => {
    setIsRefreshing(true);
    try {
      // In production, this would fetch from Strava API
      // For now, we simulate a slight delay
      await new Promise(r => setTimeout(r, 1000));
      setActivities([...MOCK_ACTIVITIES]);
    } catch (err) {
      console.error("Refresh failed", err);
    } finally {
      setIsRefreshing(false);
    }
  };

  const handleSelectActivity = async (activity: StravaActivity) => {
    setSelectedActivity(activity);
    setLoading(true);
    setAnalysis(null);

    try {
      // Analyze the selected activity in the context of recent history
      const result = await coach.analyzeActivity(
        activity, 
        activities.filter(a => a.id !== activity.id),
        goals
      );
      setAnalysis(result);
    } catch (err) {
      console.error("Analysis failed", err);
    } finally {
      setLoading(false);
    }
  };

  return (
    <Layout>
      <div className="grid grid-cols-1 lg:grid-cols-12 gap-8">
        {/* Left Column: Activity List */}
        <div className="lg:col-span-4 space-y-6">
          <div className="flex items-center justify-between mb-2">
            <h2 className="text-lg font-bold text-gray-900">Recent Activities</h2>
            <button 
              onClick={handleRefresh}
              disabled={isRefreshing}
              className={`text-xs font-bold text-orange-600 hover:text-orange-700 uppercase tracking-wider transition-all ${isRefreshing ? 'animate-pulse' : ''}`}
            >
              {isRefreshing ? 'Syncing...' : 'Sync Strava'}
            </button>
          </div>
          
          <div className="space-y-4 max-h-[calc(100vh-250px)] overflow-y-auto pr-2 custom-scroll">
            {activities.map((activity) => (
              <ActivityCard
                key={activity.id}
                activity={activity}
                isSelected={selectedActivity?.id === activity.id}
                onClick={() => handleSelectActivity(activity)}
              />
            ))}
          </div>
        </div>

        {/* Right Column: AI Analysis View */}
        <div className="lg:col-span-8">
          <AnalysisView analysis={analysis} loading={loading} />
        </div>
      </div>
    </Layout>
  );
};

export default App;
