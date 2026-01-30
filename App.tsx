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
    try {
      const res = await fetch(`${backendUrl}/health`);
      if (res.ok) {
        setBackendStatus('ONLINE');
        const data = await res.json();
        setBackendHealth(data);
        
        const logsRes = await fetch(`${backendUrl}/logs`);
        if (logsRes.ok) {
            const newLogs = await logsRes.json();
            setBackendLogs(newLogs);
            if (remoteLogsRef.current) {
                remoteLogsRef.current.scrollTop = remoteLogsRef.current.scrollHeight;
            }
        }
      } else {
        setBackendStatus('OFFLINE');
      }
    } catch {
      setBackendStatus('OFFLINE');
    }
  }, [backendUrl]);

  const handleTestPing = async () => {
    if (!backendUrl) return;
    addLog("Sending test ping to backend...", "info");
    try {
        await fetch(`${backendUrl}/webhook/ping`, { method: 'POST' });
        addLog("Ping sent! Check Remote Diagnostics for verification.", "success");
        checkBackend();
    } catch (err: any) {
        addLog(`Ping failed: ${err.message}`, "error");
    }
  };

  const refreshSubscriptions = useCallback(async () => {
    if (!backendUrl || backendStatus !== 'ONLINE') return;
    try {
      const subs = await stravaService.getSubscriptionsViaBackend(backendUrl);
      setSubscriptions(subs);
      addLog(`Sync: Found ${subs.length} active webhooks.`, "success");
    } catch (err: any) {
      addLog(`Subscription Sync: ${err.message}`, "info");
    }
  }, [backendUrl, backendStatus, addLog, stravaService]);

  const handleRegisterWebhook = async () => {
    if (!regCallbackUrl || !regVerifyToken || !backendUrl) {
        alert("Config required.");
        return;
    }
    setIsRegistering(true);
    addLog(`Registering webhook: ${regCallbackUrl}...`, "info");
    try {
        await stravaService.createSubscriptionViaBackend(backendUrl, regCallbackUrl, regVerifyToken);
        addLog("SUCCESS: Connection Live.", "success");
        refreshSubscriptions();
    } catch (err: any) {
        addLog(`Error: ${err.message}`, "error");
    } finally {
        setIsRegistering(false);
    }
  };

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      if (!regCallbackUrl) setRegCallbackUrl(`${backendUrl.replace(/\/$/, '')}/webhook`);
      const int = setInterval(checkBackend, 10000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend, regCallbackUrl]);

  useEffect(() => {
    if (backendStatus === 'ONLINE') refreshSubscriptions();
  }, [backendStatus, refreshSubscriptions]);

  const saveCredentials = () => {
    localStorage.setItem('strava_token', token);
    localStorage.setItem('strava_client_id', clientId);
    localStorage.setItem('strava_client_secret', clientSecret);
    localStorage.setItem('strava_refresh_token', refreshToken);
    localStorage.setItem('stravai_backend_url', backendUrl);
    setShowSetup(false);
    addLog("Config saved.", "info");
    checkBackend();
  };

  const deleteSub = async (id: number) => {
    if (!confirm("Unsubscribe?")) return;
    try {
      await stravaService.deleteSubscription(id);
      addLog(`Sub ${id} removed.`, "warning");
      refreshSubscriptions();
    } catch (err: any) {
      addLog(`Delete failed: ${err.message}`, "error");
    }
  };

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[13px]">
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
            </div>
          </div>
        </div>
        <button onClick={() => setShowSetup(true)} className="px-4 py-2 text-xs font-bold border border-slate-700 rounded-md hover:bg-slate-800 text-orange-400">SETTINGS</button>
      </div>

      <div className="flex-grow flex flex-col md:flex-row min-h-0 overflow-hidden">
        <div className="w-full md:w-80 border-r border-slate-800 bg-slate-900/50 p-6 space-y-8 overflow-y-auto">
          <div>
            <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">Training_Parameters</h2>
            <div className="space-y-4">
              <input type="text" value={goals.raceType} onChange={e => setGoals({...goals, raceType: e.target.value})} className="w-full bg-slate-800 border border-slate-700 rounded p-2 text-xs text-white outline-none"/>
              <input type="date" value={goals.raceDate} onChange={e => setGoals({...goals, raceDate: e.target.value})} className="w-full bg-slate-800 border border-slate-700 rounded p-2 text-xs text-white outline-none"/>
            </div>
          </div>
          <div className="pt-6 border-t border-slate-800">
            <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">Service_Stats</h2>
            <div className="p-3 bg-slate-950 border border-slate-800 rounded-lg space-y-2">
              <div className="flex justify-between items-center"><span className="text-[10px] text-slate-500 uppercase font-bold">Webhooks</span><span className="text-[10px] font-bold text-cyan-400">{subscriptions.length}</span></div>
            </div>
          </div>
        </div>

        <div className="flex-grow flex flex-col bg-[#020617] relative overflow-hidden">
          <div className="flex bg-slate-900 border-b border-slate-800">
            <button onClick={() => setActiveTab('LOGS')} className={`px-6 py-3 text-[10px] font-bold uppercase tracking-widest border-b-2 ${activeTab === 'LOGS' ? 'border-cyan-400 text-cyan-400' : 'border-transparent text-slate-500'}`}>1. LOCAL_LOGS</button>
            <button onClick={() => setActiveTab('DIAGNOSTICS')} className={`px-6 py-3 text-[10px] font-bold uppercase tracking-widest border-b-2 ${activeTab === 'DIAGNOSTICS' ? 'border-amber-500 text-amber-500' : 'border-transparent text-slate-500'}`}>2. REMOTE_DIAGNOSTICS</button>
          </div>

          <div className="flex-grow overflow-hidden relative">
            {activeTab === 'LOGS' ? (
                <div className="absolute inset-0 overflow-y-auto p-6 space-y-1 text-[11px] font-mono">
                {logs.map((log) => (<div key={log.id} className="flex gap-4"><span className="text-slate-800">[{log.time}]</span><span className={log.type === 'success' ? 'text-green-400' : log.type === 'error' ? 'text-red-400' : 'text-slate-500'}>$ {log.msg}</span></div>))}
                </div>
            ) : (
                <div className="absolute inset-0 overflow-y-auto p-6 space-y-8">
                {/* Config Verification Panel */}
                <section className="bg-slate-900 border border-slate-800 rounded-lg p-6">
                    <h3 className="text-white text-xs font-bold uppercase mb-4">Remote Environment Verification</h3>
                    <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
                        {[
                            { label: 'GEMINI_API_KEY', status: backendHealth?.config.gemini_api_key },
                            { label: 'STRAVA_CLIENT_ID', status: backendHealth?.config.strava_client_id },
                            { label: 'STRAVA_CLIENT_SECRET', status: backendHealth?.config.strava_client_secret },
                            { label: 'STRAVA_REFRESH_TOKEN', status: backendHealth?.config.strava_refresh_token },
                            { label: 'STRAVA_VERIFY_TOKEN', status: backendHealth?.config.strava_verify_token }
                        ].map(cfg => (
                            <div key={cfg.label} className="flex items-center justify-between p-2 bg-slate-950 rounded border border-slate-800">
                                <span className="text-[10px] font-bold text-slate-500">{cfg.label}</span>
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
                    <div className="flex items-center justify-between mb-4"><h3 className="text-cyan-400 text-xs font-bold uppercase">Active Webhooks</h3><button onClick={refreshSubscriptions} className="px-3 py-1 bg-slate-800 text-[10px] rounded uppercase font-bold">REFRESH</button></div>
                    {subscriptions.length === 0 && (
                        <div className="mb-6 p-6 bg-slate-900 border border-amber-500/30 rounded-lg">
                            <h4 className="text-amber-500 text-[11px] font-bold mb-4 uppercase">New Registration</h4>
                            <div className="grid grid-cols-1 md:grid-cols-2 gap-4">
                                <input type="text" value={regCallbackUrl} onChange={e => setRegCallbackUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-[11px] text-white" placeholder="Callback URL"/>
                                <input type="text" value={regVerifyToken} onChange={e => setRegVerifyToken(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-[11px] text-white" placeholder="Verify Token"/>
                            </div>
                            <button onClick={handleRegisterWebhook} disabled={isRegistering || backendStatus !== 'ONLINE'} className="mt-4 w-full py-2 bg-amber-600 rounded font-bold text-[10px] uppercase">REGISTER NOW</button>
                        </div>
                    )}
                    <div className="bg-slate-900 border border-slate-800 rounded-lg overflow-hidden">
                    <table className="w-full text-left text-[11px]">
                        <thead className="bg-slate-800 text-slate-500"><tr><th className="p-3 uppercase">ID</th><th className="p-3 uppercase">Callback</th><th className="p-3 uppercase">Action</th></tr></thead>
                        <tbody>{subscriptions.map(s => (<tr key={s.id} className="border-t border-slate-800"><td className="p-3">{s.id}</td><td className="p-3 truncate max-w-xs">{s.callback_url}</td><td className="p-3"><button onClick={() => deleteSub(s.id)} className="text-red-500">DELETE</button></td></tr>))}</tbody>
                    </table>
                    </div>
                </section>

                <section>
                    <h3 className="text-amber-500 text-xs font-bold uppercase mb-4">Remote Logs</h3>
                    <div ref={remoteLogsRef} className="bg-slate-950 border border-slate-800 rounded-lg p-4 h-64 overflow-y-auto text-[11px] scroll-smooth">
                        {backendLogs.map((l, i) => (<div key={i} className={l.includes("SUCCESS") ? 'text-green-500' : l.includes("ERROR") ? 'text-red-400' : 'text-slate-500'}>{l}</div>))}
                    </div>
                </section>
                </div>
            )}
          </div>
        </div>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
          <div className="bg-slate-900 border border-slate-700 rounded-xl max-w-2xl w-full p-8 space-y-6">
            <h2 className="text-xl font-bold text-white uppercase tracking-tighter">Operational Config</h2>
            <div className="space-y-4">
              <input type="text" placeholder="Backend URL" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs text-cyan-400 outline-none"/>
              <input type="text" placeholder="Client ID" value={clientId} onChange={e => setClientId(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs"/>
              <input type="password" placeholder="Client Secret" value={clientSecret} onChange={e => setClientSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs"/>
              <input type="password" placeholder="Refresh Token" value={refreshToken} onChange={e => setRefreshToken(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs"/>
            </div>
            <button onClick={saveCredentials} className="w-full py-4 bg-orange-600 text-white rounded-lg font-bold uppercase text-xs">SAVE_CONFIG</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
