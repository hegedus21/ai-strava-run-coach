
import React from 'react';
import { StravaActivity } from '../types';

interface ActivityCardProps {
  activity: StravaActivity;
  isSelected: boolean;
  onClick: () => void;
}

const ActivityCard: React.FC<ActivityCardProps> = ({ activity, isSelected, onClick }) => {
  const date = new Date(activity.start_date).toLocaleDateString('en-US', {
    weekday: 'short',
    month: 'short',
    day: 'numeric'
  });

  const pace = activity.moving_time > 0 
    ? (activity.moving_time / 60) / (activity.distance / 1000) 
    : 0;
    
  const formatPace = (p: number) => {
    const mins = Math.floor(p);
    const secs = Math.round((p - mins) * 60).toString().padStart(2, '0');
    return `${mins}:${secs}/km`;
  };

  return (
    <div 
      onClick={onClick}
      className={`p-4 rounded-xl border transition-all cursor-pointer group hover:shadow-md ${
        isSelected ? 'border-orange-500 bg-orange-50 ring-1 ring-orange-500' : 'border-gray-200 bg-white'
      }`}
    >
      <div className="flex justify-between items-start mb-2">
        <div>
          <h3 className="font-bold text-gray-900 group-hover:text-orange-600 transition-colors">{activity.name}</h3>
          <p className="text-xs text-gray-500">{date}</p>
        </div>
        <span className="text-xs font-semibold px-2 py-1 rounded-full bg-gray-100 text-gray-600">
          {activity.type}
        </span>
      </div>
      
      <div className="grid grid-cols-2 gap-4 mt-4">
        <div>
          <p className="text-[10px] text-gray-400 uppercase font-bold tracking-wider">Distance</p>
          <p className="text-sm font-semibold">{(activity.distance / 1000).toFixed(2)} km</p>
        </div>
        <div>
          <p className="text-[10px] text-gray-400 uppercase font-bold tracking-wider">Avg Pace</p>
          <p className="text-sm font-semibold">{formatPace(pace)}</p>
        </div>
        <div>
          <p className="text-[10px] text-gray-400 uppercase font-bold tracking-wider">Elevation</p>
          <p className="text-sm font-semibold">{activity.total_elevation_gain}m</p>
        </div>
        <div>
          <p className="text-[10px] text-gray-400 uppercase font-bold tracking-wider">Heart Rate</p>
          <p className="text-sm font-semibold">{activity.average_heartrate ? `${activity.average_heartrate} bpm` : 'N/A'}</p>
        </div>
      </div>
    </div>
  );
};

export default ActivityCard;
