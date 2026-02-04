
import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import React from 'react';
import { StravaSubscription } from './services/stravaService';
import { StravAILogo } from './components/Icon';

interface BackendHealth {
  status: string;
  engine: string;
  config: {
    gemini_ready: boolean;
    strava_ready: boolean;
    security_active: boolean;
  };
}

const App: React.FC = () => {
  const [backendUrl, setBackendUrl] = useState<string>(localStorage.getItem('stravai_backend_url') || '');
  const [backendSecret, setBackendSecret] = useState<string>(localStorage.getItem('stravai_backend_secret') || '');
  const [targetActivityId, setTargetActivityId] = useState<string>('');
  const [activeTab, setActiveTab] = useState<'LOGS' | 'DIAGNOSTICS'>('LOGS');
  const [showSetup, setShowSetup] = useState(!backendUrl || !backendSecret);
  
  // Custom Race Planner States
  const [crName, setCrName] = useState('');
  const [crDist, setCrDist] = useState('');
  const [crDate, setCrDate] = useState('');
  const [crTarget, setCrTarget] = useState('');
  const [crUrl, setCrUrl] = useState('');

  const [subscriptions, setSubscriptions] = useState<StravaSubscription[]>([]);
  const [backendLogs, setBackendLogs] = useState<string[]>([]);
  const [backendStatus, setBackendStatus] = useState<'UNKNOWN' | 'ONLINE' | 'UNAUTHORIZED' | 'OFFLINE'>('UNKNOWN');
  const [backendHealth, setBackendHealth] = useState<BackendHealth | null>(null);
  const [isProcessing, setIsProcessing] = useState<string | null>(null);
  
  const remoteLogsRef = useRef<HTMLDivElement>(null);
  const [localLogs, setLocalLogs] = useState<{ id: string; msg: string; type: 'info' | 'success' | 'error'; time: string }[]>([]);

  const addLocalLog = useCallback((msg: string, type: 'info' | 'success' | 'error' = 'info') => {
    const id = Math.random().toString(36).substr(2, 9);
    const time = new Date().toLocaleTimeString();
    setLocalLogs(prev => [{ id, msg, type, time }, ...prev].slice(0, 50));
  }, []);

  const securedFetch = useCallback(async (url: string, options: RequestInit = {}) => {
    const headers = new Headers(options.headers || {});
    headers.set('X-StravAI-Secret', backendSecret);
    return fetch(url, { ...options, headers });
  }, [backendSecret]);

  const checkBackend = useCallback(async () => {
    if (!backendUrl) return;
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    try {
      const res = await fetch(`${cleanUrl}/health`);
      if (res.ok) {
        setBackendStatus('ONLINE');
        setBackendHealth(await res.json());
        const logsRes = await securedFetch(`${cleanUrl}/logs`);
        if (logsRes.status === 401) setBackendStatus('UNAUTHORIZED');
        else if (logsRes.ok) {
            const newLogs = await logsRes.json();
            if (Array.isArray(newLogs)) setBackendLogs(newLogs);
        }
      } else { setBackendStatus('OFFLINE'); }
    } catch { setBackendStatus('OFFLINE'); }
  }, [backendUrl, securedFetch]);

  const handleSync = async (type: 'BATCH' | 'TARGET' | 'PULSE' | 'SEASON' | 'CUSTOM', id?: string) => {
    if (backendStatus !== 'ONLINE' || isProcessing) return;
    setIsProcessing(type);
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    
    let endpoint = `${cleanUrl}/sync`;
    let method = 'POST';
    let body: any = null;

    if (type === 'TARGET' && id) endpoint = `${cleanUrl}/sync/${id}`;
    if (type === 'PULSE') endpoint = `${cleanUrl}/sync?hours=24`;
    if (type === 'SEASON') endpoint = `${cleanUrl}/sync/season`;
    if (type === 'CUSTOM') {
        endpoint = `${cleanUrl}/sync/custom-race`;
        body = JSON.stringify({ Name: crName, Distance: crDist, Date: crDate, TargetTime: crTarget, InfoUrl: crUrl });
    }
    
    addLocalLog(`Triggering ${type} Sync...`, "info");
    try {
      const res = await securedFetch(endpoint, { method, body: body || undefined, headers: body ? { 'Content-Type': 'application/json' } : {} });
      if (res.ok) {
        addLocalLog(`${type} command accepted.`, "success");
        setActiveTab('DIAGNOSTICS');
      } else {
        addLocalLog(res.status === 401 ? "Unauthorized." : "Command failed.", "error");
      }
    } catch (e: any) { addLocalLog("Network Error: " + e.message, "error"); }
    finally { setTimeout(() => setIsProcessing(null), 2000); }
  };

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      const int = setInterval(checkBackend, 5000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend]);

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[11px] selection:bg-cyan-500 selection:text-white">
      {/* Header */}
      <header className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shrink-0 shadow-2xl z-20">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-9 h-9" />
          <div>
            <h1 className="text-white font-black tracking-tighter uppercase text-sm flex items-center gap-2">
              StravAI_Command_Center
              <span className="text-[10px] text-cyan-500 font-bold border border-cyan-500/20 px-1.5 rounded text-glow">v1.2.0_MULTI_PLANNER</span>
            </h1>
            <div className="flex items-center gap-3 text-[9px] uppercase font-bold tracking-widest mt-0.5">
              <div className="flex items-center gap-1.5">
                <span className={`w-1.5 h-1.5 rounded-full ${backendStatus === 'ONLINE' ? 'bg-cyan-400 animate-pulse' : 'bg-red-500'}`}></span>
                <span className={backendStatus === 'ONLINE' ? 'text-cyan-400' : 'text-red-500'}>{backendStatus}</span>
              </div>
              <span className="text-slate-700">|</span>
              <span className="text-slate-500">{backendHealth?.engine || 'Engine_Offline'}</span>
            </div>
          </div>
        </div>
        <button onClick={() => setShowSetup(true)} className="px-3 py-1.5 bg-slate-800 hover:bg-slate-700 border border-slate-700 rounded transition-all font-bold uppercase text-[10px]">Settings</button>
      </header>

      <div className="flex-grow flex flex-col md:flex-row min-h-0">
        {/* Sidebar */}
        <aside className="w-full md:w-80 border-r border-slate-800 bg-slate-900/40 p-6 space-y-8 overflow-y-auto shrink-0">
          
          <section>
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-[0.2em] mb-4">Multi-Race_Planner</h2>
            <div className="p-4 bg-slate-950 border border-slate-800 rounded-xl space-y-3">
               <div className="space-y-2">
                 <input type="text" placeholder="RACE NAME" value={crName} onChange={e=>setCrName(e.target.value)} className="w-full bg-slate-900 border border-slate-800 rounded px-2 py-1.5 text-cyan-400 outline-none focus:border-cyan-500"/>
                 <div className="flex gap-2">
                   <input type="text" placeholder="DIST (e.g. 50K)" value={crDist} onChange={e=>setCrDist(e.target.value)} className="w-1/2 bg-slate-900 border border-slate-800 rounded px-2 py-1.5 text-cyan-400 outline-none focus:border-cyan-500"/>
                   <input type="text" placeholder="YYYY-MM-DD" value={crDate} onChange={e=>setCrDate(e.target.value)} className="w-1/2 bg-slate-900 border border-slate-800 rounded px-2 py-1.5 text-cyan-400 outline-none focus:border-cyan-500"/>
                 </div>
                 <input type="text" placeholder="TARGET TIME (e.g. 4:30:00)" value={crTarget} onChange={e=>setCrTarget(e.target.value)} className="w-full bg-slate-900 border border-slate-800 rounded px-2 py-1.5 text-cyan-400 outline-none focus:border-cyan-500"/>
                 <input type="text" placeholder="RACE URL (For Elevation/Grounding)" value={crUrl} onChange={e=>setCrUrl(e.target.value)} className="w-full bg-slate-900 border border-slate-800 rounded px-2 py-1.5 text-cyan-400 outline-none focus:border-cyan-500"/>
               </div>
               <button 
                  onClick={() => handleSync('CUSTOM')} 
                  disabled={!crName || !crDate || !!isProcessing || backendStatus !== 'ONLINE'}
                  className="w-full py-2.5 bg-cyan-900/30 hover:bg-cyan-900/50 text-cyan-400 rounded border border-cyan-500/30 font-bold uppercase text-[10px] tracking-widest transition-all disabled:opacity-30"
                >
                  {isProcessing === 'CUSTOM' ? 'Grounding_AI...' : 'Deploy_Race_Strategy'}
                </button>
            </div>
          </section>

          <section>
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-[0.2em] mb-4">System_Triggers</h2>
            <div className="space-y-2">
                <button onClick={() => handleSync('SEASON')} className="w-full py-2 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded border border-slate-700 text-[10px] uppercase font-bold">Sync_Main_Season</button>
                <button onClick={() => handleSync('PULSE')} className="w-full py-2 bg-slate-800 hover:bg-slate-700 text-slate-300 rounded border border-slate-700 text-[10px] uppercase font-bold">Daily_Pulse_Sync</button>
            </div>
          </section>
        </aside>

        {/* Viewport */}
        <main className="flex-grow flex flex-col bg-slate-950 overflow-hidden">
          <div className="flex bg-slate-900 border-b border-slate-800 z-10">
            <button onClick={() => setActiveTab('LOGS')} className={`px-6 py-4 text-[10px] font-black uppercase tracking-[0.2em] border-b-2 transition-all ${activeTab === 'LOGS' ? 'border-cyan-400 text-cyan-400 bg-slate-800/30' : 'border-transparent text-slate-500'}`}>Console</button>
            <button onClick={() => setActiveTab('DIAGNOSTICS')} className={`px-6 py-4 text-[10px] font-black uppercase tracking-[0.2em] border-b-2 transition-all ${activeTab === 'DIAGNOSTICS' ? 'border-cyan-400 text-cyan-400 bg-slate-800/30' : 'border-transparent text-slate-500'}`}>Cloud_Metrics</button>
          </div>

          <div className="flex-grow overflow-hidden p-6">
            <div ref={remoteLogsRef} className="h-full bg-slate-950 border border-slate-800 rounded-xl p-5 overflow-y-auto space-y-1">
                {activeTab === 'LOGS' ? (
                  localLogs.map(log => (
                    <div key={log.id} className="flex gap-4">
                      <span className="text-slate-700 whitespace-nowrap">[{log.time}]</span>
                      <span className={log.type === 'success' ? 'text-green-400' : log.type === 'error' ? 'text-red-400' : 'text-slate-400'}>{log.msg}</span>
                    </div>
                  ))
                ) : (
                  backendLogs.map((l, i) => (
                    <div key={i} className={`py-1 border-l-2 pl-4 ${l.includes("ERROR") ? 'border-red-500 text-red-400' : l.includes("SUCCESS") ? 'border-cyan-400 text-cyan-400' : 'border-slate-800 text-slate-500'}`}>
                        {l}
                    </div>
                  ))
                )}
            </div>
          </div>
        </main>
      </div>

      {/* Setup Modal */}
      {showSetup && (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/90 backdrop-blur-md p-4">
          <div className="bg-slate-900 border border-slate-700 rounded-2xl max-w-lg w-full p-8 space-y-6 shadow-2xl">
            <h2 className="text-xl font-black text-white uppercase tracking-tighter">System_Link</h2>
            <div className="space-y-4">
              <div className="space-y-2">
                <label className="text-[10px] text-slate-500 uppercase font-black tracking-widest">Koyeb_App_URL</label>
                <input type="text" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-lg p-3 text-xs text-cyan-400 outline-none focus:border-cyan-500 font-bold"/>
              </div>
              <div className="space-y-2">
                <label className="text-[10px] text-slate-500 uppercase font-black tracking-widest">Verify_Token</label>
                <input type="password" value={backendSecret} onChange={e => setBackendSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-lg p-3 text-xs text-cyan-400 outline-none focus:border-cyan-500 font-bold"/>
              </div>
            </div>
            <button onClick={() => {localStorage.setItem('stravai_backend_url', backendUrl); localStorage.setItem('stravai_backend_secret', backendSecret); setShowSetup(false); checkBackend();}} className="w-full py-4 bg-cyan-600 hover:bg-cyan-500 text-white rounded-xl font-black uppercase text-[11px] transition-all">Authorize_Session</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
