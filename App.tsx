import { useState, useEffect, useCallback, useRef } from 'react';
import React from 'react';
import { StravAILogo } from './components/Icon';

const App: React.FC = () => {
  const [backendUrl, setBackendUrl] = useState<string>(localStorage.getItem('stravai_backend_url') || '');
  const [backendSecret, setBackendSecret] = useState<string>(localStorage.getItem('stravai_backend_secret') || '');
  const [activeTab, setActiveTab] = useState<'LOGS' | 'DIAGNOSTICS'>('LOGS');
  const [showSetup, setShowSetup] = useState(!backendUrl || !backendSecret);
  
  const [crName, setCrName] = useState('');
  const [crDist, setCrDist] = useState('');
  const [crDate, setCrDate] = useState('');
  const [crTarget, setCrTarget] = useState('');
  const [crDetails, setCrDetails] = useState('');

  const [backendLogs, setBackendLogs] = useState<string[]>([]);
  const [backendStatus, setBackendStatus] = useState<'UNKNOWN' | 'ONLINE' | 'UNAUTHORIZED' | 'OFFLINE'>('UNKNOWN');
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
        const logsRes = await securedFetch(`${cleanUrl}/logs`);
        if (logsRes.status === 401) setBackendStatus('UNAUTHORIZED');
        else if (logsRes.ok) {
            const newLogs = await logsRes.json();
            if (Array.isArray(newLogs)) setBackendLogs(newLogs);
        }
      } else { setBackendStatus('OFFLINE'); }
    } catch { setBackendStatus('OFFLINE'); }
  }, [backendUrl, securedFetch]);

  const handleSync = async (type: 'SEASON' | 'CUSTOM') => {
    if (backendStatus !== 'ONLINE' || isProcessing) return;
    setIsProcessing(type);
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    
    let endpoint = type === 'CUSTOM' ? `${cleanUrl}/sync/custom-race` : `${cleanUrl}/sync/season`;
    let body = type === 'CUSTOM' ? JSON.stringify({ 
      Name: crName, 
      Distance: crDist, 
      Date: crDate, 
      TargetTime: crTarget, 
      RaceDetails: crDetails 
    }) : undefined;
    
    addLocalLog(`Deploying ${type} Request...`, "info");
    try {
      const res = await securedFetch(endpoint, { 
        method: 'POST', 
        body, 
        headers: body ? { 'Content-Type': 'application/json' } : {} 
      });
      if (res.ok) {
        addLocalLog(`${type} Analysis Accepted. Check Strava shortly.`, "success");
        setActiveTab('DIAGNOSTICS');
      } else if (res.status === 429) {
        addLocalLog("Engine Quota Warning. Retry in 60s.", "error");
      } else {
        addLocalLog("Engine Error Response.", "error");
      }
    } catch (e: any) { addLocalLog("Transmission Failure.", "error"); }
    finally { setTimeout(() => setIsProcessing(null), 2000); }
  };

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      const int = setInterval(checkBackend, 5000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend]);

  useEffect(() => {
    if (remoteLogsRef.current) {
        remoteLogsRef.current.scrollTo({ top: remoteLogsRef.current.scrollHeight, behavior: 'smooth' });
    }
  }, [backendLogs, localLogs, activeTab]);

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[11px] selection:bg-cyan-500 selection:text-white">
      <header className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shrink-0 shadow-2xl z-20">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-9 h-9" />
          <div>
            <h1 className="text-white font-black tracking-tighter uppercase text-sm flex items-center gap-2">
              StravAI_Command_Center
              <span className="text-[10px] text-cyan-500 font-bold border border-cyan-500/20 px-1.5 rounded text-glow">v1.2.2_ULTRA</span>
            </h1>
            <div className="flex items-center gap-1.5 text-[9px] uppercase font-bold tracking-widest mt-0.5">
              <span className={`w-1.5 h-1.5 rounded-full ${backendStatus === 'ONLINE' ? 'bg-cyan-400 animate-pulse' : 'bg-red-500'}`}></span>
              <span className={backendStatus === 'ONLINE' ? 'text-cyan-400' : 'text-red-500'}>{backendStatus}</span>
            </div>
          </div>
        </div>
        <button onClick={() => setShowSetup(true)} className="px-3 py-1.5 bg-slate-800 hover:bg-slate-700 border border-slate-700 rounded transition-all font-bold uppercase text-[10px]">Config</button>
      </header>

      <div className="flex-grow flex flex-col md:flex-row min-h-0">
        <aside className="w-full md:w-80 border-r border-slate-800 bg-slate-900/40 p-6 space-y-8 overflow-y-auto shrink-0 scrollbar-hide">
          <section>
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-[0.2em] mb-4">Race_Deployment</h2>
            <div className="p-5 bg-slate-950 border border-slate-800 rounded-2xl space-y-4 shadow-xl">
               <div className="space-y-3">
                 <input type="text" placeholder="RACE NAME" value={crName} onChange={e=>setCrName(e.target.value)} className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none focus:border-cyan-500 uppercase font-bold"/>
                 <div className="flex gap-2">
                   <input type="text" placeholder="DISTANCE (e.g. 50K)" value={crDist} onChange={e=>setCrDist(e.target.value)} className="w-1/2 bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none focus:border-cyan-500"/>
                   <input type="text" placeholder="DATE (YYYY-MM-DD)" value={crDate} onChange={e=>setCrDate(e.target.value)} className="w-1/2 bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none focus:border-cyan-500"/>
                 </div>
                 <input type="text" placeholder="TARGET TIME (e.g. 4:30:00)" value={crTarget} onChange={e=>setCrTarget(e.target.value)} className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none focus:border-cyan-500"/>
                 
                 <div className="space-y-1">
                   <label className="text-[8px] text-slate-600 font-bold uppercase ml-1 tracking-widest">Race_Specifics_&_Nutrition</label>
                   <textarea 
                     rows={7}
                     placeholder="e.g. 1686.8m loops, no elevation, water, iso, magnesium, salt tablets, bread with butter, chocolate..." 
                     value={crDetails} 
                     onChange={e=>setCrDetails(e.target.value)} 
                     className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none focus:border-cyan-500 text-[10px] resize-none leading-relaxed"
                   />
                 </div>
               </div>
               <button 
                  onClick={() => handleSync('CUSTOM')} 
                  disabled={!crName || !crDate || !crDetails || !!isProcessing || backendStatus !== 'ONLINE'}
                  className="w-full py-4 bg-cyan-900/20 hover:bg-cyan-900/40 text-cyan-400 rounded-xl border border-cyan-500/20 font-black uppercase text-[10px] tracking-[0.2em] transition-all disabled:opacity-20 shadow-glow"
                >
                  {isProcessing === 'CUSTOM' ? 'Processing_Ultra_Strategy...' : 'Deploy_Analysis'}
                </button>
            </div>
          </section>

          <section>
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-[0.2em] mb-4">Core_Systems</h2>
            <button onClick={() => handleSync('SEASON')} className="w-full py-2.5 bg-slate-900 hover:bg-slate-800 text-slate-400 rounded-xl border border-slate-800 text-[9px] uppercase font-black tracking-widest transition-all">Full_Season_Sync</button>
          </section>
        </aside>

        <main className="flex-grow flex flex-col bg-slate-950 overflow-hidden relative">
          <div className="flex bg-slate-900 border-b border-slate-800 z-10 sticky top-0">
            <button onClick={() => setActiveTab('LOGS')} className={`px-8 py-4 text-[10px] font-black uppercase tracking-[0.2em] border-b-2 transition-all ${activeTab === 'LOGS' ? 'border-cyan-400 text-cyan-400 bg-slate-800/30' : 'border-transparent text-slate-500 hover:text-slate-400'}`}>Terminal</button>
            <button onClick={() => setActiveTab('DIAGNOSTICS')} className={`px-8 py-4 text-[10px] font-black uppercase tracking-[0.2em] border-b-2 transition-all ${activeTab === 'DIAGNOSTICS' ? 'border-cyan-400 text-cyan-400 bg-slate-800/30' : 'border-transparent text-slate-500 hover:text-slate-400'}`}>Cloud_Metrics</button>
          </div>

          <div className="flex-grow overflow-hidden p-6 md:p-10">
            <div ref={remoteLogsRef} className="h-full bg-slate-900/40 border border-slate-800/50 rounded-3xl p-6 md:p-8 overflow-y-auto space-y-1 shadow-2xl backdrop-blur-sm">
                {activeTab === 'LOGS' ? (
                  localLogs.map(log => (
                    <div key={log.id} className="flex gap-6 py-0.5 border-b border-white/5 last:border-0">
                      <span className="text-slate-700 whitespace-nowrap font-bold">[{log.time}]</span>
                      <span className={log.type === 'success' ? 'text-cyan-400' : log.type === 'error' ? 'text-red-400' : 'text-slate-500'}>{log.msg}</span>
                    </div>
                  ))
                ) : (
                  backendLogs.map((l, i) => (
                    <div key={i} className={`py-1.5 border-l-4 pl-6 mb-1 transition-all ${
                        l.includes("ERROR") ? 'border-red-500 text-red-400 bg-red-500/5' : 
                        l.includes("SUCCESS") ? 'border-cyan-400 text-cyan-400 font-bold bg-cyan-400/5' : 
                        'border-slate-800 text-slate-600'
                    }`}>
                        {l}
                    </div>
                  ))
                )}
            </div>
          </div>
        </main>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/95 backdrop-blur-xl p-4">
          <div className="bg-slate-900 border border-slate-700/50 rounded-[2rem] max-w-lg w-full p-10 space-y-8 shadow-2xl">
            <h2 className="text-2xl font-black text-white uppercase tracking-tighter">System_Link</h2>
            <div className="space-y-5">
              <input type="text" value={backendUrl} placeholder="GATEWAY URL" onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-2xl p-4 text-xs text-cyan-400 outline-none focus:border-cyan-500 font-bold"/>
              <input type="password" value={backendSecret} placeholder="VERIFY TOKEN" onChange={e => setBackendSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-2xl p-4 text-xs text-cyan-400 outline-none focus:border-cyan-500 font-bold"/>
            </div>
            <button onClick={() => {localStorage.setItem('stravai_backend_url', backendUrl); localStorage.setItem('stravai_backend_secret', backendSecret); setShowSetup(false); checkBackend();}} className="w-full py-5 bg-cyan-600 hover:bg-cyan-500 text-white rounded-2xl font-black uppercase text-[11px] tracking-widest">Connect</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
