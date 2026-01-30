import { StravaActivity, StravaUpdateParams } from "../types";

export class StravaService {
  private accessToken: string | null = null;
  private readonly API_BASE = "https://www.strava.com/api/v3";

  setToken(token: string): void {
    this.accessToken = token;
  }

  async refreshAuth(): Promise<void> {
    // Safely attempt to get credentials from localStorage if in a browser, 
    // or from environment variables if in Node.js
    let clientId: string | undefined | null;
    let clientSecret: string | undefined | null;
    let refreshToken: string | undefined | null;

    if (typeof localStorage !== 'undefined') {
      clientId = localStorage.getItem('strava_client_id');
      clientSecret = localStorage.getItem('strava_client_secret');
      refreshToken = localStorage.getItem('strava_refresh_token');
    }

    clientId = clientId || process.env.STRAVA_CLIENT_ID;
    clientSecret = clientSecret || process.env.STRAVA_CLIENT_SECRET;
    refreshToken = refreshToken || process.env.STRAVA_REFRESH_TOKEN;

    if (!clientId || !clientSecret || !refreshToken) {
      // If we already have an accessToken (maybe pasted manually in UI), we can proceed
      if (this.accessToken) {
        return;
      }
      throw new Error("Missing Strava OAuth credentials. Please enter your Client ID, Secret, and Refresh Token in the Setup Console or set environment variables.");
    }

    try {
      const response = await fetch("https://www.strava.com/oauth/token", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({
          client_id: clientId,
          client_secret: clientSecret,
          refresh_token: refreshToken,
          grant_type: "refresh_token",
        }),
      });

      if (!response.ok) {
        const errData = await response.json().catch(() => ({}));
        throw new Error(`Auth Refresh Failed: ${response.status} ${JSON.stringify(errData)}`);
      }

      const data = await response.json();
      this.accessToken = data.access_token;
    } catch (err: any) {
      console.error("refreshAuth Error:", err);
      throw err;
    }
  }

  async getRecentActivities(perPage: number = 10): Promise<StravaActivity[]> {
    // Determine if we have credentials to attempt a refresh
    const hasLocalStorage = typeof localStorage !== 'undefined';
    const hasPermAuth = !!(
      (hasLocalStorage && localStorage.getItem('strava_refresh_token')) || 
      process.env.STRAVA_REFRESH_TOKEN
    );

    // Always attempt refresh if we have permanent credentials or no token yet
    if (!this.accessToken || hasPermAuth) {
      await this.refreshAuth();
    }
    
    try {
      const response = await fetch(`${this.API_BASE}/athlete/activities?per_page=${perPage}`, {
        headers: { 'Authorization': `Bearer ${this.accessToken}` }
      });
      
      if (!response.ok) {
        const errorBody = await response.json().catch(() => ({}));
        if (response.status === 401) {
          throw new Error("Session Expired (401). Please update your credentials.");
        }
        throw new Error(`Strava API Error (${response.status})`);
      }
      
      return response.json();
    } catch (err: any) {
      if (err.message.includes("Failed to fetch")) {
        throw new Error("Network Error: Connection to Strava was blocked. Check your internet or CORS settings.");
      }
      throw err;
    }
  }

  async getHistoryForBaseline(count: number = 50): Promise<StravaActivity[]> {
    return this.getRecentActivities(count);
  }

  async updateActivity(activityId: number, params: StravaUpdateParams): Promise<void> {
    if (!this.accessToken) await this.refreshAuth();

    const response = await fetch(`${this.API_BASE}/activities/${activityId}`, {
      method: 'PUT',
      headers: {
        'Authorization': `Bearer ${this.accessToken}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(params)
    });

    if (!response.ok) {
      const error = await response.json().catch(() => ({}));
      throw new Error(`Update failed (${response.status})`);
    }
  }
}