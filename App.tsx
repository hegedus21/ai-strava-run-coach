import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import React from 'react';
import { StravaService, StravaSubscription } from './services/stravaService';
import { GoalSettings } from './types';
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
  const [showSetup, setShowSetup] = useState(!backendUrl);
  
  const [subscriptions, setSubscriptions] = useState<StravaSubscription[]>([]);
  const [backendLogs, setBackendLogs] = useState<string[]>([]);
  const [backendStatus, setBackendStatus] = useState<'UNKNOWN' | 'ONLINE' | 'OFFLINE'>('UNKNOWN');
  const [backendHealth, setBackendHealth] = useState<BackendHealth | null>(null);
  const [isChecking, setIsChecking] = useState(false);
  const [isSyncing, setIsSyncing] = useState(false);
  const remoteLogsRef = useRef<HTMLDivElement>(null);

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
    setLogs(prev => [{ id, msg, type, time }, ...prev].slice(0, 50));
  }, []);

  const checkBackend = useCallback(async () => {
    if (!backendUrl) return;
    setIsChecking(true);
    let cleanUrl = backendUrl.trim().replace(/\/$/, '');
    if (!cleanUrl.startsWith('http')) cleanUrl = 'https://' + cleanUrl;

    try {
      const res = await fetch(`${cleanUrl}/health`);
      if (res.ok) {
        setBackendStatus('ONLINE');
        setBackendHealth(await res.json());
        
        const logsRes = await fetch(`${cleanUrl}/logs`);
        if (logsRes.ok) {
            const newLogs = await logsRes.json();
            setBackendLogs(newLogs);
        }
      } else {
        setBackendStatus('OFFLINE');
      }
    } catch (err: any) {
      setBackendStatus('OFFLINE');
    } finally {
      setIsChecking(false);
    }
  }, [backendUrl]);

  useEffect(() => {
    if (remoteLogsRef.current) {
        remoteLogsRef.current.scrollTo({
            top: remoteLogsRef.current.scrollHeight,
            behavior: 'smooth'
        });
    }
  }, [backendLogs]);

  const handleManualSync = async () => {
    if (isSyncing || backendStatus !== 'ONLINE') return;
    setIsSyncing(true);
    addLog("Triggering manual history scan...", "info");
    try {
        let cleanUrl = backendUrl.trim().replace(/\/$/, '');
        const res = await fetch(`${cleanUrl}/sync`, { method: 'POST' });
        if (res.ok) {
            addLog("Sync initiated. Monitor Remote Logs.", "success");
            setActiveTab('DIAGNOSTICS');
        }
    } catch (err: any) {
        addLog("Sync failed: " + err.message, "error");
    } finally {
        setTimeout(() => setIsSyncing(false), 2000);
    }
  };

  const refreshSubscriptions = useCallback(async () => {
    if (!backendUrl || backendStatus !== 'ONLINE') return;
    try {
      const subs = await stravaService.getSubscriptionsViaBackend(backendUrl);
      setSubscriptions(subs);
    } catch (err: any) {
      addLog(`Sync error: ${err.message}`, "error");
    }
  }, [backendUrl, backendStatus, addLog, stravaService]);

  const handleDeleteSubscription = async (id: number) => {
    if (!window.confirm("Are you sure you want to revoke this webhook? This will stop real-time processing.")) return;
    try {
        await stravaService.deleteSubscriptionViaBackend(backendUrl, id);
        addLog(`Subscription ${id} revoked.`, "warning");
        refreshSubscriptions();
    } catch (err: any) {
        addLog("Revoke failed: " + err.message, "error");
    }
  };

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      const int = setInterval(checkBackend, 10000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend]);

  useEffect(() => {
    if (backendStatus === 'ONLINE') refreshSubscriptions();
  }, [backendStatus, refreshSubscriptions]);

  const saveCredentials = () => {
    localStorage.setItem('stravai_backend_url', backendUrl);
    setShowSetup(false);
    checkBackend();
  };

  const configCheckItems = useMemo(() => {
    const config = backendHealth?.config;
    return [
        { label: 'GEMINI_API_KEY', status: !!config?.gemini_api_key },
        { label: 'STRAVA_CLIENT_ID', status: !!config?.strava_client_id },
        { label: 'STRAVA_CLIENT_SECRET', status: !!config?.strava_client_secret },
        { label: 'STRAVA_REFRESH_TOKEN', status: !!config?.strava_refresh_token },
        { label: 'STRAVA_VERIFY_TOKEN', status: !!config?.strava_verify_token }
    ];
  }, [backendHealth]);

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[13px]">
      <div className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shrink-0 shadow-xl">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-10 h-10" />
          <div>
            <h1 className="text-white font-bold tracking-tight uppercase text-base">StravAI_Command_Center</h1>
            <div className="flex items-center gap-3 text-[10px] uppercase font-bold tracking-widest mt-0.5">
              <div className="flex items-center gap-2">
                <span className={`w-2 h-2 rounded-full ${backendStatus === 'ONLINE' ? 'bg-green-400 animate-pulse' : 'bg-red-50'}`}></span>
                <span className={backendStatus === 'ONLINE' ? 'text-green-400' : 'text-slate-500'}>
                  {backendStatus}
                </span>
              </div>
            </div>
          </div>
        </div>
        <div className="flex items-center gap-3">
            <button onClick={handleManualSync} disabled={isSyncing || backendStatus !== 'ONLINE'} className="px-4 py-2 bg-cyan-600 hover:bg-cyan-500 text-white rounded font-bold uppercase text-[10px] tracking-widest disabled:opacity-30 transition-all">
                {isSyncing ? 'SYNCING...' : 'Trigger_Batch_Sync'}
            </button>
            <button onClick={() => setShowSetup(true)} className="px-4 py-2 border border-slate-700 rounded hover:bg-slate-800 text-[10px] uppercase font-bold tracking-widest transition-all">Settings</button>
        </div>
      </div>

      <div className="flex-grow flex flex-col md:flex-row min-h-0 overflow-hidden">
        <div className="w-full md:w-72 border-r border-slate-800 bg-slate-900/40 p-6 space-y-8 overflow-y-auto">
            <div>
                <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">Training_Goal</h2>
                <div className="p-4 bg-slate-950 border border-slate-800 rounded-lg space-y-3 shadow-inner">
                    <div className="text-white font-bold">{goals.raceType}</div>
                    <div className="text-slate-500 text-[11px]">{goals.raceDate}</div>
                </div>
            </div>
            <div>
                <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">Active_Subscriptions</h2>
                <div className="space-y-2">
                    {subscriptions.map(s => (
                        <div key={s.id} className="p-3 bg-slate-950 border border-slate-800 rounded text-[10px] group relative shadow-inner">
                            <div className="text-cyan-400 font-bold truncate pr-12">{s.callback_url}</div>
                            <div className="text-slate-600 mt-1">ID: {s.id}</div>
                            <div className="absolute top-2 right-2 flex items-center gap-1.5 opacity-0 group-hover:opacity-100 transition-opacity">
                                <button onClick={() => {navigator.clipboard.writeText(s.callback_url); addLog("URL Copied", "info")}} className="p-1 hover:text-white" title="Copy URL">
                                    <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M8 7v8a2 2 0 002 2h6M8 7V5a2 2 0 012-2h4.586a1 1 0 01.707.293l4.414 4.414a1 1 0 01.293.707V15a2 2 0 01-2 2h-2M8 7H6a2 2 0 00-2 2v10a2 2 0 002 2h8a2 2 0 002-2v-2" /></svg>
                                </button>
                                <button onClick={() => handleDeleteSubscription(s.id)} className="p-1 text-slate-700 hover:text-red-500" title="Revoke Subscription">
                                    <svg className="w-3.5 h-3.5" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={2} d="M19 7l-.867 12.142A2 2 0 0116.138 21H7.862a2 2 0 01-1.995-1.858L5 7m5 4v6m4-6v6m1-10V4a1 1 0 00-1-1h-4a1 1 0 00-1 1v3M4 7h16" /></svg>
                                </button>
                            </div>
                        </div>
                    ))}
                    {subscriptions.length === 0 && <div className="text-slate-700 italic text-[11px]">No active webhooks found.</div>}
                </div>
            </div>
        </div>

        <div className="flex-grow flex flex-col bg-[#020617] overflow-hidden">
          <div className="flex bg-slate-900 border-b border-slate-800">
            <button onClick={() => setActiveTab('LOGS')} className={`px-6 py-3 text-[10px] font-bold uppercase tracking-widest border-b-2 transition-all ${activeTab === 'LOGS' ? 'border-cyan-400 text-cyan-400 bg-slate-800/50' : 'border-transparent text-slate-500'}`}>Local_Console</button>
            <button onClick={() => setActiveTab('DIAGNOSTICS')} className={`px-6 py-3 text-[10px] font-bold uppercase tracking-widest border-b-2 transition-all ${activeTab === 'DIAGNOSTICS' ? 'border-amber-500 text-amber-500 bg-slate-800/50' : 'border-transparent text-slate-500'}`}>Remote_Engine</button>
          </div>

          <div className="flex-grow overflow-hidden relative">
            {activeTab === 'LOGS' ? (
                <div className="absolute inset-0 overflow-y-auto p-6 space-y-1 text-[11px] font-mono leading-relaxed bg-black/20">
                    {logs.map((log) => (
                        <div key={log.id} className="flex gap-4">
                            <span className="text-slate-800 shrink-0">[{log.time}]</span>
                            <span className={log.type === 'success' ? 'text-green-400' : log.type === 'error' ? 'text-red-400' : log.type === 'warning' ? 'text-amber-400' : 'text-slate-400'}>
                                {log.msg}
                            </span>
                        </div>
                    ))}
                    {logs.length === 0 && <div className="text-slate-700 italic">Waiting for events...</div>}
                </div>
            ) : (
                <div className="absolute inset-0 overflow-y-auto p-6 space-y-8">
                    <section className="bg-slate-900/50 border border-slate-800 rounded-xl p-6 shadow-xl">
                        <h3 className="text-white text-xs font-bold uppercase mb-6 flex items-center gap-2">
                            <span className="w-1.5 h-1.5 bg-cyan-400 rounded-full"></span> Service_Health_Audit
                        </h3>
                        <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 gap-4">
                            {configCheckItems.map(cfg => (
                                <div key={cfg.label} className="flex items-center justify-between p-3 bg-slate-950 rounded border border-slate-800 shadow-inner">
                                    <span className="text-[10px] font-bold text-slate-500">{cfg.label}</span>
                                    <span className={cfg.status ? 'text-green-500' : 'text-red-500' + ' text-[10px] font-bold'}>
                                        {cfg.status ? 'CONNECTED' : 'NOT_FOUND'}
                                    </span>
                                </div>
                            ))}
                        </div>
                    </section>

                    <section className="flex flex-col h-[500px]">
                        <div className="flex items-center justify-between mb-4">
                            <h3 className="text-amber-500 text-xs font-bold uppercase">Cloud_Live_Stream</h3>
                            <button onClick={checkBackend} className="text-[10px] text-slate-600 hover:text-white uppercase font-bold transition-colors">Manual_Refresh</button>
                        </div>
                        <div ref={remoteLogsRef} className="flex-grow bg-slate-950 border border-slate-800 rounded-xl p-5 overflow-y-auto text-[11px] font-mono scroll-smooth shadow-inner bg-[radial-gradient(circle_at_bottom_right,_var(--tw-gradient-stops))] from-slate-900/40 to-transparent">
                            {backendLogs.length === 0 ? (
                                <div className="h-full flex items-center justify-center text-slate-800 italic">Connecting to remote log buffer...</div>
                            ) : (
                                backendLogs.map((l, i) => {
                                    const isErr = l.includes("ERROR") || l.includes("FAILURE");
                                    const isSucc = l.includes("SUCCESS") || l.includes("COMPLETE");
                                    return (
                                        <div key={i} className={`py-0.5 border-l-2 pl-3 mb-0.5 transition-all ${isErr ? 'border-red-500 text-red-400' : isSucc ? 'border-green-500 text-green-500' : 'border-slate-800 text-slate-500'}`}>
                                            <span className="opacity-10 mr-2 text-[8px] font-bold">EVENT</span>{l}
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
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
          <div className="bg-slate-900 border border-slate-700 rounded-2xl max-w-lg w-full p-8 space-y-6 shadow-2xl">
            <h2 className="text-xl font-bold text-white uppercase tracking-tighter">Service_Configuration</h2>
            <div className="space-y-4">
              <div className="space-y-2">
                <label className="text-[10px] text-slate-500 uppercase font-black tracking-widest">Koyeb Backend URL</label>
                <input type="text" placeholder="https://stravai-app.koyeb.app" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-lg p-3 text-xs text-cyan-400 outline-none focus:border-cyan-500 font-bold transition-all"/>
              </div>
            </div>
            <div className="flex gap-4">
                <button onClick={() => setShowSetup(false)} className="flex-1 py-3 bg-slate-800 text-slate-400 hover:text-white rounded-xl font-bold uppercase text-[10px] transition-all">Dismiss</button>
                <button onClick={saveCredentials} className="flex-[2] py-3 bg-cyan-600 hover:bg-cyan-500 text-white rounded-xl font-bold uppercase text-[10px] transition-all shadow-lg shadow-cyan-900/20">Initialize_Bridge</button>
            </div>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
