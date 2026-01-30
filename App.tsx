import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import React from 'react';
import { StravaService, StravaSubscription } from './services/stravaService';
import { GeminiCoachService, QuotaExhaustedError, STRAVAI_PLACEHOLDER } from './services/geminiService';
import { GoalSettings, StravaActivity } from './types';
import { StravAILogo } from './components/Icon';

const App: React.FC = () => {
  const [token, setToken] = useState<string>(localStorage.getItem('strava_token') || '');
  const [clientId, setClientId] = useState<string>(localStorage.getItem('strava_client_id') || '');
  const [clientSecret, setClientSecret] = useState<string>(localStorage.getItem('strava_client_secret') || '');
  const [refreshToken, setRefreshToken] = useState<string>(localStorage.getItem('strava_refresh_token') || '');
  const [backendUrl, setBackendUrl] = useState<string>(localStorage.getItem('stravai_backend_url') || '');
  
  const [activeTab, setActiveTab] = useState<'LOGS' | 'DIAGNOSTICS'>('LOGS');
  const [showSetup, setShowSetup] = useState(!token && !refreshToken || !backendUrl);
  
  const [subscriptions, setSubscriptions] = useState<StravaSubscription[]>([]);
  const [backendLogs, setBackendLogs] = useState<string[]>([]);
  const [backendStatus, setBackendStatus] = useState<'UNKNOWN' | 'ONLINE' | 'OFFLINE'>('UNKNOWN');

  // Webhook Registration Form State
  const [regCallbackUrl, setRegCallbackUrl] = useState('');
  const [regVerifyToken, setRegVerifyToken] = useState('STRAVAI_SECURE_TOKEN');
  const [isRegistering, setIsRegistering] = useState(false);

  const [goals, setGoals] = useState<GoalSettings>({
    raceType: localStorage.getItem('goal_race_type') || 'Marathon',
    raceDate: localStorage.getItem('goal_race_date') || '2025-10-12',
    goalTime: localStorage.getItem('goal_race_time') || '3:30:00'
  });
  
  const [logs, setLogs] = useState<{ id: string; msg: string; type: 'info' | 'success' | 'error' | 'ai' | 'warning'; time: string }[]>([]);
  
  const stravaService = useMemo(() => new StravaService(), []);

  const addLog = useCallback((msg: string, type: 'info' | 'success' | 'error' | 'ai' | 'warning' = 'info') => {
    const id = Math.random().toString(36).substr(2, 9);
    const time = new Date().toLocaleTimeString();
    setLogs(prev => [{ id, msg, type, time }, ...prev].slice(0, 100));
  }, []);

  const checkBackend = useCallback(async () => {
    if (!backendUrl) {
        setBackendStatus('UNKNOWN');
        return;
    }
    try {
      const res = await fetch(`${backendUrl}/health`);
      if (res.ok) {
        setBackendStatus('ONLINE');
        const logsRes = await fetch(`${backendUrl}/logs`);
        if (logsRes.ok) setBackendLogs(await logsRes.json());
      } else {
        setBackendStatus('OFFLINE');
      }
    } catch {
      setBackendStatus('OFFLINE');
    }
  }, [backendUrl]);

  const refreshSubscriptions = useCallback(async () => {
    if (!backendUrl || backendStatus !== 'ONLINE') return;
    
    try {
      const subs = await stravaService.getSubscriptionsViaBackend(backendUrl);
      setSubscriptions(subs);
      addLog(`Sync: Found ${subs.length} active Strava webhooks via backend.`, "success");
    } catch (err: any) {
      addLog(`Subscription Sync: ${err.message}`, "info");
    }
  }, [backendUrl, backendStatus, addLog, stravaService]);

  const handleRegisterWebhook = async () => {
    if (!regCallbackUrl || !regVerifyToken || !backendUrl) {
        alert("Backend URL, Callback URL, and Verify Token are all required.");
        return;
    }
    setIsRegistering(true);
    addLog(`Instructing backend to register webhook: ${regCallbackUrl}...`, "info");
    try {
        await stravaService.createSubscriptionViaBackend(backendUrl, regCallbackUrl, regVerifyToken);
        addLog("SUCCESS: Backend verified handshake with Strava. Connection Live.", "success");
        refreshSubscriptions();
    } catch (err: any) {
        addLog(`Registration Error: ${err.message}`, "error");
        alert(`Failed: ${err.message}\n\nCheck your backend logs for the specific Strava error.`);
    } finally {
        setIsRegistering(false);
    }
  };

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      if (!regCallbackUrl) setRegCallbackUrl(`${backendUrl.replace(/\/$/, '')}/webhook`);
      
      const int = setInterval(checkBackend, 15000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend, regCallbackUrl]);

  useEffect(() => {
    if (backendStatus === 'ONLINE') {
        refreshSubscriptions();
    }
  }, [backendStatus, refreshSubscriptions]);

  const saveCredentials = () => {
    localStorage.setItem('strava_token', token);
    localStorage.setItem('strava_client_id', clientId);
    localStorage.setItem('strava_client_secret', clientSecret);
    localStorage.setItem('strava_refresh_token', refreshToken);
    localStorage.setItem('stravai_backend_url', backendUrl);
    setShowSetup(false);
    addLog("Config saved. Refreshing systems...", "info");
    checkBackend();
  };

  const deleteSub = async (id: number) => {
    if (!confirm("Remove this webhook? Your server will stop receiving new activities.")) return;
    try {
      await stravaService.deleteSubscription(id);
      addLog(`Subscription ${id} removed.`, "warning");
      refreshSubscriptions();
    } catch (err: any) {
      addLog(`Delete failed: ${err.message}`, "error");
    }
  };

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[13px]">
      {/* Configuration Alert */}
      {!backendUrl && (
        <div className="bg-orange-600 text-white px-6 py-2 flex items-center justify-between animate-pulse">
            <span className="text-[11px] font-bold uppercase tracking-widest">⚠️ Setup Required: Missing Backend URL</span>
            <button onClick={() => setShowSetup(true)} className="bg-white text-orange-600 px-3 py-1 rounded text-[10px] font-bold uppercase">Configure Now</button>
        </div>
      )}

      <div className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shadow-2xl">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-10 h-10" />
          <div>
            <h1 className="text-white font-bold tracking-tight uppercase text-base">StravAI_Command_Center</h1>
            <div className="flex items-center gap-3 text-[10px] uppercase font-bold tracking-widest mt-0.5">
              <span className={`flex items-center gap-1 ${backendStatus === 'ONLINE' ? 'text-green-400' : 'text-red-500'}`}>
                <span className={`w-2 h-2 rounded-full ${backendStatus === 'ONLINE' ? 'bg-green-400 animate-pulse' : 'bg-red-600'}`}></span>
                SERVER_{backendStatus}
              </span>
              <span className="text-slate-600">|</span>
              <span className="text-cyan-400">SESSION_ACTIVE</span>
            </div>
          </div>
        </div>

        <div className="flex items-center gap-3">
          <button 
            onClick={() => setShowSetup(true)}
            className="px-4 py-2 text-xs font-bold border border-slate-700 rounded-md hover:bg-slate-800 transition-all text-orange-400 flex items-center gap-2"
          >
            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z" /><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 12a3 3 0 11-6 0 3 3 0 016 0z" /></svg>
            SETTINGS
          </button>
        </div>
      </div>

      <div className="flex-grow flex flex-col md:flex-row min-h-0 overflow-hidden">
        {/* Sidebar */}
        <div className="w-full md:w-80 border-r border-slate-800 bg-slate-900/50 p-6 space-y-8 overflow-y-auto">
          <div>
            <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">Training_Parameters</h2>
            <div className="space-y-4">
              <div>
                <label className="text-[10px] text-slate-400 block mb-1 uppercase">Target Race</label>
                <input type="text" value={goals.raceType} onChange={e => setGoals({...goals, raceType: e.target.value})} className="w-full bg-slate-800 border border-slate-700 rounded p-2 text-xs text-white outline-none focus:border-orange-500"/>
              </div>
              <div>
                <label className="text-[10px] text-slate-400 block mb-1 uppercase">Target Date</label>
                <input type="date" value={goals.raceDate} onChange={e => setGoals({...goals, raceDate: e.target.value})} className="w-full bg-slate-800 border border-slate-700 rounded p-2 text-xs text-white outline-none focus:border-orange-500"/>
              </div>
            </div>
          </div>

          <div className="pt-6 border-t border-slate-800">
            <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">Service_Stats</h2>
            <div className="p-3 bg-slate-950 border border-slate-800 rounded-lg space-y-2">
              <div className="flex justify-between items-center">
                <span className="text-[10px] text-slate-500 uppercase font-bold">Webhooks</span>
                <span className="text-[10px] font-bold text-cyan-400">{subscriptions.length}</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-[10px] text-slate-500 uppercase font-bold">Backend</span>
                <span className={`text-[10px] font-bold ${backendStatus === 'ONLINE' ? 'text-green-500' : 'text-red-500'}`}>{backendStatus}</span>
              </div>
            </div>
          </div>
        </div>

        {/* Main Console */}
        <div className="flex-grow flex flex-col bg-[#020617] relative overflow-hidden">
          {/* Navigation Tabs */}
          <div className="flex bg-slate-900 border-b border-slate-800">
            <button 
                onClick={() => setActiveTab('LOGS')}
                className={`px-6 py-3 text-[10px] font-bold uppercase tracking-widest transition-all border-b-2 ${activeTab === 'LOGS' ? 'border-cyan-400 text-cyan-400 bg-slate-800/50' : 'border-transparent text-slate-500 hover:text-slate-300'}`}
            >
                1. LOCAL_LOGS
            </button>
            <button 
                onClick={() => setActiveTab('DIAGNOSTICS')}
                className={`px-6 py-3 text-[10px] font-bold uppercase tracking-widest transition-all border-b-2 ${activeTab === 'DIAGNOSTICS' ? 'border-amber-500 text-amber-500 bg-slate-800/50' : 'border-transparent text-slate-500 hover:text-slate-300'}`}
            >
                2. REMOTE_DIAGNOSTICS
            </button>
          </div>

          <div className="flex-grow overflow-hidden relative">
            {activeTab === 'LOGS' ? (
                <div className="absolute inset-0 overflow-y-auto p-6 space-y-1 text-[11px] font-mono leading-relaxed">
                <div className="text-slate-500 border-b border-slate-800 pb-2 mb-4 uppercase tracking-widest font-bold flex justify-between items-center">
                    <span>Local Session Feed</span>
                    <button onClick={() => setLogs([])} className="text-[9px] hover:text-red-400">CLEAR_HISTORY</button>
                </div>
                {logs.length === 0 && <div className="text-slate-700 italic">No events recorded in this session.</div>}
                {logs.map((log) => (
                    <div key={log.id} className="flex gap-4">
                    <span className="text-slate-800 shrink-0 font-bold">[{log.time}]</span>
                    <span className={`${log.type === 'success' ? 'text-green-400' : log.type === 'error' ? 'text-red-400' : log.type === 'warning' ? 'text-amber-500' : 'text-slate-500'}`}>
                        <span className="text-slate-800 mr-2">$</span>{log.msg}
                    </span>
                    </div>
                ))}
                </div>
            ) : (
                <div className="absolute inset-0 overflow-y-auto p-6 space-y-8">
                <section>
                    <div className="flex items-center justify-between mb-4">
                        <h3 className="text-cyan-400 text-xs font-bold uppercase tracking-widest">Active Strava Webhooks</h3>
                        <button onClick={refreshSubscriptions} className="px-3 py-1 bg-slate-800 hover:bg-slate-700 text-[10px] rounded border border-slate-700">REFRESH_LIST</button>
                    </div>
                    
                    {subscriptions.length === 0 && (
                        <div className="mb-6 p-6 bg-slate-900 border border-amber-500/30 rounded-lg">
                            <h4 className="text-amber-500 text-[11px] font-bold uppercase mb-4 flex items-center gap-2">
                                <span className="w-2 h-2 bg-amber-500 rounded-full animate-pulse"></span>
                                Setup Needed: Register Webhook
                            </h4>
                            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                <div>
                                    <label className="text-[10px] text-slate-500 block mb-1 uppercase">Callback Endpoint</label>
                                    <input 
                                        type="text" 
                                        value={regCallbackUrl} 
                                        onChange={e => setRegCallbackUrl(e.target.value)}
                                        className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-[11px] text-white outline-none"
                                        placeholder="https://your-app.koyeb.app/webhook"
                                    />
                                </div>
                                <div>
                                    <label className="text-[10px] text-slate-500 block mb-1 uppercase">Verify Token (Shared Secret)</label>
                                    <input 
                                        type="text" 
                                        value={regVerifyToken} 
                                        onChange={e => setRegVerifyToken(e.target.value)}
                                        className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-[11px] text-white outline-none"
                                        placeholder="STRAVAI_SECURE_TOKEN"
                                    />
                                </div>
                            </div>
                            <button 
                                onClick={handleRegisterWebhook}
                                disabled={isRegistering || backendStatus !== 'ONLINE'}
                                className={`mt-4 w-full py-2 rounded font-bold text-[10px] uppercase tracking-widest transition-all ${backendStatus === 'ONLINE' ? 'bg-amber-600 hover:bg-amber-500 text-white' : 'bg-slate-800 text-slate-600 cursor-not-allowed'}`}
                            >
                                {isRegistering ? 'PROCESSING...' : backendStatus !== 'ONLINE' ? 'WAITING FOR SERVER ONLINE...' : 'REGISTER_WEBHOOK_NOW'}
                            </button>
                            <p className="text-[9px] text-slate-600 mt-2 italic">Note: The Verify Token must match the value set in your Koyeb environment variables.</p>
                        </div>
                    )}

                    <div className="bg-slate-900 border border-slate-800 rounded-lg overflow-hidden">
                    <table className="w-full text-left text-[11px]">
                        <thead className="bg-slate-800 text-slate-500 uppercase">
                        <tr>
                            <th className="p-3">ID</th>
                            <th className="p-3">Callback URL</th>
                            <th className="p-3">Created</th>
                            <th className="p-3">Action</th>
                        </tr>
                        </thead>
                        <tbody>
                        {subscriptions.length === 0 ? (
                            <tr><td colSpan={4} className="p-6 text-center text-slate-600 italic font-bold">No active webhooks detected. Register one above to begin.</td></tr>
                        ) : (
                            subscriptions.map(s => (
                            <tr key={s.id} className="border-t border-slate-800">
                                <td className="p-3 font-bold">{s.id}</td>
                                <td className="p-3 text-cyan-500 truncate max-w-xs">{s.callback_url}</td>
                                <td className="p-3 text-slate-500">{new Date(s.created_at).toLocaleDateString()}</td>
                                <td className="p-3"><button onClick={() => deleteSub(s.id)} className="text-red-500 hover:underline">UNSUBSCRIBE</button></td>
                            </tr>
                            ))
                        )}
                        </tbody>
                    </table>
                    </div>
                </section>

                <section>
                    <h3 className="text-amber-500 text-xs font-bold uppercase tracking-widest mb-4 flex justify-between">
                    <span>Remote Backend Activity (Koyeb/Cloud Logs)</span>
                    <span className="text-[10px] text-slate-600 uppercase font-bold">Status: {backendStatus}</span>
                    </h3>
                    <div className="bg-slate-950 border border-slate-800 rounded-lg p-4 h-64 overflow-y-auto font-mono text-[11px] text-slate-400 space-y-1">
                    {backendUrl ? (
                        backendLogs.length > 0 ? (
                        backendLogs.map((l, i) => <div key={i}>{l}</div>)
                        ) : (
                        <div className="text-slate-700 italic flex items-center gap-2">
                            <span className="w-2 h-2 bg-slate-700 rounded-full animate-pulse"></span>
                            Listening for activity from {backendUrl}...
                        </div>
                        )
                    ) : (
                        <div className="text-red-900/50 flex flex-col items-center justify-center h-full text-center p-8">
                        <p className="font-bold text-red-500 mb-2 underline uppercase">Backend Not Connected</p>
                        <p>You must provide your Backend URL in the Settings menu to view processing logs.</p>
                        </div>
                    )}
                    </div>
                </section>
                </div>
            )}
          </div>
        </div>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
          <div className="bg-slate-900 border border-slate-700 rounded-xl max-w-2xl w-full p-8 shadow-2xl space-y-6">
            <div className="flex justify-between items-center border-b border-slate-800 pb-4">
                <h2 className="text-xl font-bold text-white uppercase tracking-tighter">Operational Config</h2>
                <button onClick={() => setShowSetup(false)} className="text-slate-500 hover:text-white">✕</button>
            </div>
            
            <div className="space-y-4">
              <div>
                <label className="text-[10px] text-slate-400 block mb-1 uppercase font-bold flex justify-between">
                    <span>Backend Service URL</span>
                    {backendStatus === 'ONLINE' && <span className="text-green-400">✓ Connected</span>}
                </label>
                <input type="text" placeholder="https://stravai-backend.koyeb.app" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs text-cyan-400 outline-none focus:border-cyan-500"/>
                <p className="text-[9px] text-slate-600 mt-1">This is the public URL of your .NET service running on Koyeb or similar.</p>
              </div>

              <div className="grid grid-cols-2 gap-4">
                <div>
                  <label className="text-[10px] text-slate-400 block mb-1 uppercase">Client ID</label>
                  <input type="text" value={clientId} onChange={e => setClientId(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs outline-none focus:border-cyan-500"/>
                </div>
                <div>
                  <label className="text-[10px] text-slate-400 block mb-1 uppercase">Client Secret</label>
                  <input type="password" value={clientSecret} onChange={e => setClientSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs outline-none focus:border-cyan-500"/>
                </div>
              </div>

              <div>
                <label className="text-[10px] text-slate-400 block mb-1 uppercase">Refresh Token (Forever Token)</label>
                <input type="password" value={refreshToken} onChange={e => setRefreshToken(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs outline-none focus:border-cyan-500"/>
              </div>
            </div>

            <button onClick={saveCredentials} className="w-full py-4 bg-orange-600 hover:bg-orange-500 text-white rounded-lg font-bold transition-all uppercase tracking-widest text-xs">SAVE_CONFIG</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
