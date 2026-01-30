import { StravaActivity, StravaUpdateParams } from "../types";

export interface StravaSubscription {
  id: number;
  resource_state: number;
  application_id: number;
  callback_url: string;
  created_at: string;
  updated_at: string;
}

export class StravaService {
  private accessToken: string | null = null;
  private readonly API_BASE = "https://www.strava.com/api/v3";

  setToken(token: string): void {
    this.accessToken = token;
  }

  async refreshAuth(): Promise<void> {
    let clientId = typeof localStorage !== 'undefined' ? localStorage.getItem('strava_client_id') : process.env.STRAVA_CLIENT_ID;
    let clientSecret = typeof localStorage !== 'undefined' ? localStorage.getItem('strava_client_secret') : process.env.STRAVA_CLIENT_SECRET;
    let refreshToken = typeof localStorage !== 'undefined' ? localStorage.getItem('strava_refresh_token') : process.env.STRAVA_REFRESH_TOKEN;

    if (!clientId || !clientSecret || !refreshToken) {
      if (this.accessToken) return;
      throw new Error("Missing Strava OAuth credentials.");
    }

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

    if (!response.ok) throw new Error("Auth Refresh Failed");
    const data = await response.json();
    this.accessToken = data.access_token;
  }

  /**
   * Proxies subscription list through the backend to avoid CORS issues.
   */
  async getSubscriptionsViaBackend(backendUrl: string): Promise<StravaSubscription[]> {
    const url = `${backendUrl.replace(/\/$/, '')}/webhook/subscriptions`;
    const response = await fetch(url);
    if (!response.ok) {
        const txt = await response.text();
        throw new Error(`Backend Error: ${txt || response.statusText}`);
    }
    return response.json();
  }

  /**
   * Registers a webhook by proxying through the user's backend to avoid CORS and keep secrets safe.
   */
  async createSubscriptionViaBackend(backendUrl: string, callbackUrl: string, verifyToken: string): Promise<any> {
    const url = `${backendUrl.replace(/\/$/, '')}/webhook/register`;
    const response = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ callbackUrl, verifyToken })
    });

    if (!response.ok) {
        const err = await response.json();
        throw new Error(err.message || "Backend failed to register webhook with Strava.");
    }
    return response.json();
  }

  async deleteSubscription(id: number): Promise<void> {
    const clientId = typeof localStorage !== 'undefined' ? localStorage.getItem('strava_client_id') : process.env.STRAVA_CLIENT_ID;
    const clientSecret = typeof localStorage !== 'undefined' ? localStorage.getItem('strava_client_secret') : process.env.STRAVA_CLIENT_SECRET;
    
    await fetch(`${this.API_BASE}/push_subscriptions/${id}?client_id=${clientId}&client_secret=${clientSecret}`, {
      method: 'DELETE'
    });
  }

  async getRecentActivities(perPage: number = 10): Promise<StravaActivity[]> {
    if (!this.accessToken) await this.refreshAuth();
    const response = await fetch(`${this.API_BASE}/athlete/activities?per_page=${perPage}`, {
      headers: { 'Authorization': `Bearer ${this.accessToken}` }
    });
    if (!response.ok) throw new Error("Strava API Error");
    return response.json();
  }

  async updateActivity(activityId: number, params: StravaUpdateParams): Promise<void> {
    if (!this.accessToken) await this.refreshAuth();
    await fetch(`${this.API_BASE}/activities/${activityId}`, {
      method: 'PUT',
      headers: {
        'Authorization': `Bearer ${this.accessToken}`,
        'Content-Type': 'application/json'
      },
      body: JSON.stringify(params)
    });
  }
}
