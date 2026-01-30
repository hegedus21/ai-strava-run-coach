
import { StravaActivity } from "../types";

export const MOCK_ACTIVITIES: StravaActivity[] = [
  {
    id: 1,
    name: "Morning Tempo Run",
    type: "Run",
    start_date: new Date().toISOString(),
    distance: 8500,
    moving_time: 2400,
    total_elevation_gain: 45,
    average_heartrate: 162,
    max_heartrate: 178,
    average_speed: 3.54,
    max_speed: 4.8,
    kilojoules: 650
  },
  {
    id: 2,
    name: "Easy Recovery Run",
    type: "Run",
    start_date: new Date(Date.now() - 86400000).toISOString(),
    distance: 5000,
    moving_time: 1800,
    total_elevation_gain: 20,
    average_heartrate: 135,
    max_heartrate: 145,
    average_speed: 2.77,
    max_speed: 3.2,
    kilojoules: 400
  },
  {
    id: 3,
    name: "Hilly Interval Session",
    type: "Run",
    start_date: new Date(Date.now() - 172800000).toISOString(),
    distance: 12000,
    moving_time: 3600,
    total_elevation_gain: 250,
    average_heartrate: 168,
    max_heartrate: 185,
    average_speed: 3.33,
    max_speed: 5.5,
    kilojoules: 950
  },
  {
    id: 4,
    name: "Lunch Trail Jog",
    type: "Run",
    start_date: new Date(Date.now() - 259200000).toISOString(),
    distance: 6500,
    moving_time: 2100,
    total_elevation_gain: 120,
    average_heartrate: 148,
    max_heartrate: 165,
    average_speed: 3.09,
    max_speed: 4.1,
    kilojoules: 550
  }
];
