import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import React from 'react';
import { StravaService, StravaSubscription } from './services/stravaService';
import { GeminiCoachService, QuotaExhaustedError, STRAVAI_PLACEHOLDER } from './services/geminiService';
import { GoalSettings, StravaActivity } from './types';
import { StravAILogo } from './components/Icon';

interface BackendHealth {
  status: string;
  config: {
    gemini_api_key: boolean;
    strava_client_id: boolean;
    strava_client_secret: boolean;
    strava_refresh_token: boolean;
    strava_verify_token: boolean;
  };
}

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
  const [backendHealth, setBackendHealth] = useState<BackendHealth | null>(null);
  const [isChecking, setIsChecking] = useState(false);
  const remoteLogsRef = useRef<HTMLDivElement>(null);

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

    setIsChecking(true);
    // Sanitize URL: strip trailing slash and ensure https if missing
    let cleanUrl = backendUrl.trim().replace(/\/$/, '');
    if (!cleanUrl.startsWith('http')) cleanUrl = 'https://' + cleanUrl;

    try {
      const controller = new AbortController();
      const timeoutId = setTimeout(() => controller.abort(), 8000);
      
      const res = await fetch(`${cleanUrl}/health`, { signal: controller.signal });
      clearTimeout(timeoutId);

      if (res.ok) {
        setBackendStatus('ONLINE');
        const data = await res.json();
        setBackendHealth(data);
        
        const logsRes = await fetch(`${cleanUrl}/logs`);
        if (logsRes.ok) {
            const newLogs = await logsRes.json();
            setBackendLogs(newLogs);
            if (remoteLogsRef.current) {
                remoteLogsRef.current.scrollTop = remoteLogsRef.current.scrollHeight;
            }
        }
      } else {
        setBackendStatus('OFFLINE');
        addLog(`Backend error: HTTP ${res.status} from ${cleanUrl}`, "error");
      }
    } catch (err: any) {
      setBackendStatus('OFFLINE');
      const errorMsg = err.name === 'AbortError' ? 'Connection timed out' : err.message;
      addLog(`Connectivity failure to ${cleanUrl}: ${errorMsg}`, "error");
    } finally {
      setIsChecking(false);
    }
  }, [backendUrl, addLog]);

  const handleTestPing = async () => {
    if (!backendUrl) return;
    addLog("Sending diagnostic ping...", "info");
    let cleanUrl = backendUrl.trim().replace(/\/$/, '');
    if (!cleanUrl.startsWith('http')) cleanUrl = 'https://' + cleanUrl;

    try {
        const res = await fetch(`${cleanUrl}/webhook/ping`, { method: 'POST' });
        if (res.ok) {
          addLog("Ping acknowledged. Connection is active.", "success");
        } else {
          addLog(`Ping rejected: ${res.status}`, "error");
        }
        checkBackend();
    } catch (err: any) {
        addLog(`Ping delivery failed: ${err.message}`, "error");
    }
  };

  const refreshSubscriptions = useCallback(async () => {
    if (!backendUrl || backendStatus !== 'ONLINE') return;
    let cleanUrl = backendUrl.trim().replace(/\/$/, '');
    if (!cleanUrl.startsWith('http')) cleanUrl = 'https://' + cleanUrl;

    try {
      const subs = await stravaService.getSubscriptionsViaBackend(cleanUrl);
      setSubscriptions(subs);
      addLog(`Subscription Sync: ${subs.length} active webhook(s) detected.`, "success");
    } catch (err: any) {
      addLog(`Failed to list webhooks: ${err.message}`, "error");
    }
  }, [backendUrl, backendStatus, addLog, stravaService]);

  const handleRegisterWebhook = async () => {
    if (!regCallbackUrl || !regVerifyToken || !backendUrl) {
        alert("Configuration missing. Check Settings.");
        return;
    }
    setIsRegistering(true);
    let cleanUrl = backendUrl.trim().replace(/\/$/, '');
    if (!cleanUrl.startsWith('http')) cleanUrl = 'https://' + cleanUrl;

    addLog(`Initiating registration handshake...`, "info");
    try {
        await stravaService.createSubscriptionViaBackend(cleanUrl, regCallbackUrl, regVerifyToken);
        addLog("Handshake SUCCESS. Webhook is now active.", "success");
        refreshSubscriptions();
    } catch (err: any) {
        addLog(`Registration failed: ${err.message}`, "error");
        alert(`Handshake Failed: ${err.message}`);
    } finally {
        setIsRegistering(false);
    }
  };

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      const cleanUrl = backendUrl.trim().replace(/\/$/, '');
      if (!regCallbackUrl) setRegCallbackUrl(`${cleanUrl}/webhook`);
      const int = setInterval(checkBackend, 15000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend, regCallbackUrl]);

  useEffect(() => {
    if (backendStatus === 'ONLINE') refreshSubscriptions();
  }, [backendStatus, refreshSubscriptions]);

  const saveCredentials = () => {
    // Sanitize URL before saving
    let cleanUrl = backendUrl.trim();
    if (cleanUrl && !cleanUrl.startsWith('http')) cleanUrl = 'https://' + cleanUrl;
    
    localStorage.setItem('strava_token', token);
    localStorage.setItem('strava_client_id', clientId);
    localStorage.setItem('strava_client_secret', clientSecret);
    localStorage.setItem('strava_refresh_token', refreshToken);
    localStorage.setItem('stravai_backend_url', cleanUrl);
    setBackendUrl(cleanUrl);
    setShowSetup(false);
    addLog("Config updated. Re-initiating connection...", "info");
    checkBackend();
  };

  const deleteSub = async (id: number) => {
    if (!confirm("Remove this webhook?")) return;
    try {
      await stravaService.deleteSubscription(id);
      addLog(`Webhook ${id} unsubscribed.`, "warning");
      refreshSubscriptions();
    } catch (err: any) {
      addLog(`Deletion failed: ${err.message}`, "error");
    }
  };

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[13px]">
      {!backendUrl && (
        <div className="bg-orange-600 text-white px-6 py-2 flex items-center justify-between animate-pulse">
            <span className="text-[11px] font-bold uppercase tracking-widest">⚠️ Connection Required: Setup Backend URL</span>
            <button onClick={() => setShowSetup(true)} className="bg-white text-orange-600 px-3 py-1 rounded text-[10px] font-bold uppercase">Configure</button>
        </div>
      )}

      <div className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shadow-2xl shrink-0">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-10 h-10" />
          <div>
            <h1 className="text-white font-bold tracking-tight uppercase text-base">StravAI_Command_Center</h1>
            <div className="flex items-center gap-3 text-[10px] uppercase font-bold tracking-widest mt-0.5">
              <div className="flex items-center gap-2">
                <span className={`w-2 h-2 rounded-full ${backendStatus === 'ONLINE' ? 'bg-green-400 animate-pulse' : backendStatus === 'OFFLINE' ? 'bg-red-500' : 'bg-slate-600'}`}></span>
                <span className={backendStatus === 'ONLINE' ? 'text-green-400' : backendStatus === 'OFFLINE' ? 'text-red-500' : 'text-slate-500'}>
                  SERVER_{backendStatus}
                </span>
              </div>
              {backendStatus === 'OFFLINE' && (
                <button onClick={checkBackend} disabled={isChecking} className="text-cyan-400 hover:underline flex items-center gap-1">
                  {isChecking ? 'RETRYING...' : '[RETRY_CONNECTION]'}
                </button>
              )}
            </div>
          </div>
        </div>
        <div className="flex items-center gap-3">
            <button onClick={() => setShowSetup(true)} className="px-4 py-2 text-xs font-bold border border-slate-700 rounded-md hover:bg-slate-800 text-orange-400 transition-colors uppercase">Console_Settings</button>
        </div>
      </div>

      <div className="flex-grow flex flex-col md:flex-row min-h-0 overflow-hidden">
        <div className="w-full md:w-80 border-r border-slate-800 bg-slate-900/50 p-6 space-y-8 overflow-y-auto">
          <div>
            <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">Training_Parameters</h2>
            <div className="space-y-4">
              <div className="space-y-1">
                <label className="text-[9px] text-slate-500 uppercase font-bold">Primary Goal</label>
                <input type="text" value={goals.raceType} onChange={e => setGoals({...goals, raceType: e.target.value})} className="w-full bg-slate-800 border border-slate-700 rounded p-2 text-xs text-white outline-none focus:border-cyan-500 transition-colors"/>
              </div>
              <div className="space-y-1">
                <label className="text-[9px] text-slate-500 uppercase font-bold">Event Date</label>
                <input type="date" value={goals.raceDate} onChange={e => setGoals({...goals, raceDate: e.target.value})} className="w-full bg-slate-800 border border-slate-700 rounded p-2 text-xs text-white outline-none focus:border-cyan-500 transition-colors"/>
              </div>
            </div>
          </div>
          
          <div className="pt-6 border-t border-slate-800">
            <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">Service_Stats</h2>
            <div className="p-3 bg-slate-950 border border-slate-800 rounded-lg space-y-3">
              <div className="flex justify-between items-center">
                <span className="text-[10px] text-slate-500 uppercase font-bold">Webhooks</span>
                <span className="text-[10px] font-bold text-cyan-400">{subscriptions.length}</span>
              </div>
              <div className="flex justify-between items-center">
                <span className="text-[10px] text-slate-500 uppercase font-bold">Uptime</span>
                <span className="text-[10px] font-bold text-green-500">99.9%</span>
              </div>
            </div>
          </div>
          
          <div className="pt-6">
             <button onClick={handleTestPing} disabled={backendStatus !== 'ONLINE'} className="w-full py-2 bg-slate-800 hover:bg-slate-700 text-slate-400 text-[10px] font-bold uppercase rounded border border-slate-700 disabled:opacity-30 transition-all">
                Send_Diagnostic_Ping
             </button>
          </div>
        </div>

        <div className="flex-grow flex flex-col bg-[#020617] relative overflow-hidden">
          <div className="flex bg-slate-900 border-b border-slate-800">
            <button onClick={() => setActiveTab('LOGS')} className={`px-6 py-3 text-[10px] font-bold uppercase tracking-widest border-b-2 transition-all ${activeTab === 'LOGS' ? 'border-cyan-400 text-cyan-400 bg-slate-800/50' : 'border-transparent text-slate-500 hover:text-slate-300'}`}>1. LOCAL_LOGS</button>
            <button onClick={() => setActiveTab('DIAGNOSTICS')} className={`px-6 py-3 text-[10px] font-bold uppercase tracking-widest border-b-2 transition-all ${activeTab === 'DIAGNOSTICS' ? 'border-amber-500 text-amber-500 bg-slate-800/50' : 'border-transparent text-slate-500 hover:text-slate-300'}`}>2. REMOTE_DIAGNOSTICS</button>
          </div>

          <div className="flex-grow overflow-hidden relative">
            {activeTab === 'LOGS' ? (
                <div className="absolute inset-0 overflow-y-auto p-6 space-y-1 text-[11px] font-mono leading-relaxed">
                <div className="text-slate-600 mb-4 text-[10px] flex items-center gap-2">
                    <span className="w-1.5 h-1.5 bg-cyan-400 rounded-full"></span>
                    MONITORING LOCAL SESSION...
                </div>
                {logs.length === 0 && <div className="text-slate-700 italic">Listening for session events...</div>}
                {logs.map((log) => (
                    <div key={log.id} className="flex gap-4 group">
                        <span className="text-slate-800 shrink-0 select-none">[{log.time}]</span>
                        <span className={log.type === 'success' ? 'text-green-400' : log.type === 'error' ? 'text-red-400' : log.type === 'warning' ? 'text-amber-500' : 'text-slate-500'}>
                            <span className="text-slate-800 mr-2">$</span>{log.msg}
                        </span>
                    </div>
                ))}
                </div>
            ) : (
                <div className="absolute inset-0 overflow-y-auto p-6 space-y-8">
                {/* Config Verification Panel */}
                <section className="bg-slate-900/80 border border-slate-800 rounded-xl p-6 shadow-lg">
                    <div className="flex items-center justify-between mb-6">
                        <h3 className="text-white text-xs font-bold uppercase tracking-wider flex items-center gap-2">
                            <svg className="w-4 h-4 text-cyan-400" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M9 12l2 2 4-4m5.618-4.016A11.955 11.955 0 0112 2.944a11.955 11.955 0 01-8.618 3.04A12.02 12.02 0 003 9c0 5.591 3.824 10.29 9 11.622 5.176-1.332 9-6.03 9-11.622 0-1.042-.133-2.052-.382-3.016z" /></svg>
                            Remote Environment Verification
                        </h3>
                        {backendStatus !== 'ONLINE' && <span className="text-[9px] text-red-500 font-bold animate-pulse">OFFLINE</span>}
                    </div>
                    <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
                        {[
                            { label: 'GEMINI_API_KEY', status: backendHealth?.config.gemini_api_key },
                            { label: 'STRAVA_CLIENT_ID', status: backendHealth?.config.strava_client_id },
                            { label: 'STRAVA_CLIENT_SECRET', status: backendHealth?.config.strava_client_secret },
                            { label: 'STRAVA_REFRESH_TOKEN', status: backendHealth?.config.strava_refresh_token },
                            { label: 'STRAVA_VERIFY_TOKEN', status: backendHealth?.config.strava_verify_token }
                        ].map(cfg => (
                            <div key={cfg.label} className="flex items-center justify-between p-3 bg-slate-950 rounded border border-slate-800 group hover:border-slate-700 transition-colors">
                                <span className="text-[10px] font-bold text-slate-500 group-hover:text-slate-400">{cfg.label}</span>
                                {cfg.status ? (
                                    <span className="text-green-500 text-[10px] font-bold">✓ LOADED</span>
                                ) : (
                                    <span className="text-red-500 text-[10px] font-bold">✕ MISSING</span>
                                )}
                            </div>
                        ))}
                    </div>
                </section>

                <section>
                    <div className="flex items-center justify-between mb-4">
                        <h3 className="text-cyan-400 text-xs font-bold uppercase tracking-widest flex items-center gap-2">
                            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M15 17h5l-1.405-1.405A2.032 2.032 0 0118 14.158V11a6.002 6.002 0 00-4-5.659V5a2 2 0 10-4 0v.341C7.67 6.165 6 8.388 6 11v3.159c0 .538-.214 1.055-.595 1.436L4 17h5m6 0v1a3 3 0 11-6 0v-1m6 0H9" /></svg>
                            Active Webhooks
                        </h3>
                        <button onClick={refreshSubscriptions} disabled={backendStatus !== 'ONLINE'} className="px-3 py-1 bg-slate-800 hover:bg-slate-700 text-[10px] rounded uppercase font-bold border border-slate-700 disabled:opacity-50 transition-colors">REFRESH_LIST</button>
                    </div>
                    
                    {subscriptions.length === 0 && backendStatus === 'ONLINE' && (
                        <div className="mb-6 p-6 bg-slate-900 border border-amber-500/30 rounded-lg">
                            <h4 className="text-amber-500 text-[11px] font-bold mb-4 uppercase flex items-center gap-2">
                                <span className="w-2 h-2 bg-amber-500 rounded-full animate-pulse"></span>
                                New Registration Required
                            </h4>
                            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                <div className="space-y-1">
                                    <label className="text-[9px] text-slate-500 uppercase font-bold">Callback URL</label>
                                    <input type="text" value={regCallbackUrl} onChange={e => setRegCallbackUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-[11px] text-white outline-none focus:border-cyan-500" placeholder="e.g. https://app.koyeb.app/webhook"/>
                                </div>
                                <div className="space-y-1">
                                    <label className="text-[9px] text-slate-500 uppercase font-bold">Verify Token</label>
                                    <input type="text" value={regVerifyToken} onChange={e => setRegVerifyToken(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-[11px] text-white outline-none focus:border-cyan-500" placeholder="STRAVAI_SECURE_TOKEN"/>
                                </div>
                            </div>
                            <button onClick={handleRegisterWebhook} disabled={isRegistering || backendStatus !== 'ONLINE'} className="mt-4 w-full py-2 bg-amber-600 hover:bg-amber-500 text-white rounded font-bold text-[10px] uppercase tracking-widest transition-all">
                                {isRegistering ? 'PROCESSING HANDSHAKE...' : 'INITIATE_REGISTER_NOW'}
                            </button>
                        </div>
                    )}
                    
                    <div className="bg-slate-900 border border-slate-800 rounded-xl overflow-hidden shadow-lg">
                    <table className="w-full text-left text-[11px]">
                        <thead className="bg-slate-800/50 text-slate-500">
                            <tr>
                                <th className="p-4 uppercase tracking-tighter">ID</th>
                                <th className="p-4 uppercase tracking-tighter">Callback_Endpoint</th>
                                <th className="p-4 uppercase tracking-tighter text-right">Operations</th>
                            </tr>
                        </thead>
                        <tbody className="divide-y divide-slate-800">
                            {subscriptions.length === 0 ? (
                                <tr><td colSpan={3} className="p-8 text-center text-slate-600 italic">No webhooks registered with Strava yet.</td></tr>
                            ) : (
                                subscriptions.map(s => (
                                <tr key={s.id} className="hover:bg-slate-800/30 transition-colors">
                                    <td className="p-4 font-bold text-slate-400">{s.id}</td>
                                    <td className="p-4 text-cyan-500 truncate max-w-xs">{s.callback_url}</td>
                                    <td className="p-4 text-right">
                                        <button onClick={() => deleteSub(s.id)} className="text-red-500 hover:text-red-400 font-bold uppercase text-[9px] tracking-widest border border-red-900/50 px-2 py-1 rounded">UNSUBSCRIBE</button>
                                    </td>
                                </tr>
                                ))
                            )}
                        </tbody>
                    </table>
                    </div>
                </section>

                <section>
                    <div className="flex items-center justify-between mb-4">
                        <h3 className="text-amber-500 text-xs font-bold uppercase tracking-widest flex items-center gap-2">
                            <svg className="w-4 h-4" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10" /></svg>
                            Remote Processing Logs
                        </h3>
                        <span className="text-[9px] text-slate-600 font-bold">STREAM_ACTIVE</span>
                    </div>
                    <div ref={remoteLogsRef} className="bg-slate-950 border border-slate-800 rounded-xl p-5 h-64 overflow-y-auto text-[11px] font-mono scroll-smooth shadow-inner leading-relaxed">
                        {backendLogs.length === 0 ? (
                            <div className="h-full flex flex-col items-center justify-center text-slate-700 space-y-2">
                                <div className="w-8 h-8 border-2 border-slate-800 border-t-cyan-500 rounded-full animate-spin"></div>
                                <p>Listening for cloud activities...</p>
                            </div>
                        ) : (
                            backendLogs.map((l, i) => {
                                const isSuccess = l.includes("SUCCESS");
                                const isError = l.includes("ERROR") || l.includes("FAILURE");
                                return (
                                    <div key={i} className={`py-0.5 border-b border-slate-900/50 ${isSuccess ? 'text-green-500' : isError ? 'text-red-400' : 'text-slate-500'}`}>
                                        <span className="text-slate-800 mr-2">»</span>{l}
                                    </div>
                                );
                            })
                        )}
                    </div>
                </section>
                </div>
            )}
          </div>
        </div>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/90 backdrop-blur-md p-4">
          <div className="bg-slate-900 border border-slate-700 rounded-2xl max-w-2xl w-full p-8 space-y-8 shadow-2xl overflow-hidden relative">
            <div className="absolute top-0 left-0 w-full h-1 bg-gradient-to-r from-orange-500 via-cyan-500 to-orange-500"></div>
            <div className="flex justify-between items-center">
                <h2 className="text-xl font-black text-white uppercase tracking-tighter">System_Configuration</h2>
                <button onClick={() => setShowSetup(false)} className="text-slate-500 hover:text-white transition-colors">✕</button>
            </div>
            
            <div className="space-y-6">
              <div className="space-y-2">
                <label className="text-[10px] text-slate-400 block uppercase font-black tracking-widest flex justify-between">
                    <span>Backend Endpoint (Koyeb URL)</span>
                    {backendStatus === 'ONLINE' ? <span className="text-green-500">● LIVE</span> : <span className="text-slate-600">● OFFLINE</span>}
                </label>
                <input type="text" placeholder="https://stravai-backend.koyeb.app" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-lg p-3 text-xs text-cyan-400 outline-none focus:border-cyan-500 transition-all font-bold"/>
                <p className="text-[9px] text-slate-500 italic">This must be your public backend URL from Koyeb or other cloud provider.</p>
              </div>

              <div className="grid grid-cols-2 gap-6">
                <div className="space-y-2">
                  <label className="text-[10px] text-slate-400 block uppercase font-black tracking-widest">Strava Client ID</label>
                  <input type="text" placeholder="12345" value={clientId} onChange={e => setClientId(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-lg p-3 text-xs outline-none focus:border-cyan-500 transition-all"/>
                </div>
                <div className="space-y-2">
                  <label className="text-[10px] text-slate-400 block uppercase font-black tracking-widest">Strava Secret</label>
                  <input type="password" placeholder="••••••••" value={clientSecret} onChange={e => setClientSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-lg p-3 text-xs outline-none focus:border-cyan-500 transition-all"/>
                </div>
              </div>

              <div className="space-y-2">
                <label className="text-[10px] text-slate-400 block uppercase font-black tracking-widest">Refresh Token (Forever Key)</label>
                <input type="password" placeholder="••••••••••••••••••••••••••••" value={refreshToken} onChange={e => setRefreshToken(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-lg p-3 text-xs outline-none focus:border-cyan-500 transition-all"/>
              </div>
            </div>

            <div className="flex gap-4">
                <button onClick={() => setShowSetup(false)} className="flex-1 py-3 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded-xl font-black uppercase text-[10px] tracking-widest transition-all">Cancel</button>
                <button onClick={saveCredentials} className="flex-[2] py-3 bg-orange-600 hover:bg-orange-500 text-white rounded-xl font-black uppercase text-[10px] tracking-widest transition-all shadow-lg shadow-orange-900/20">COMMIT_OPERATIONAL_CONFIG</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
