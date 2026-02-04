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
  const [syncId, setSyncId] = useState('');

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

  const handleSync = async (type: 'SEASON' | 'CUSTOM' | 'BATCH' | 'ID') => {
    if (backendStatus !== 'ONLINE' || isProcessing) return;
    setIsProcessing(type);
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    
    let endpoint = '';
    let body = undefined;

    switch(type) {
      case 'CUSTOM': 
        endpoint = `${cleanUrl}/sync/custom-race`;
        body = JSON.stringify({ Name: crName, Distance: crDist, Date: crDate, TargetTime: crTarget, RaceDetails: crDetails });
        break;
      case 'SEASON': endpoint = `${cleanUrl}/sync/season`; break;
      case 'BATCH': endpoint = `${cleanUrl}/sync`; break;
      case 'ID': endpoint = `${cleanUrl}/sync/${syncId}`; break;
    }
    
    addLocalLog(`Deploying ${type} Protocol...`, "info");
    try {
      const res = await securedFetch(endpoint, { 
        method: 'POST', 
        body, 
        headers: body ? { 'Content-Type': 'application/json' } : {} 
      });
      if (res.ok) {
        addLocalLog(`${type} Request Accepted.`, "success");
        setActiveTab('DIAGNOSTICS');
      } else { addLocalLog(`Server Error: ${res.status}`, "error"); }
    } catch (e: any) { addLocalLog("Network Transmission Failure.", "error"); }
    finally { setTimeout(() => setIsProcessing(null), 1000); }
  };

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      const int = setInterval(checkBackend, 5000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend]);

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[11px]">
      <header className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shrink-0">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-9 h-9" />
          <div>
            <h1 className="text-white font-black uppercase text-sm flex items-center gap-2">
              StravAI_Command
              <span className="text-[10px] text-cyan-500 font-bold border border-cyan-500/20 px-1.5 rounded">v1.2.3_ULTRA</span>
            </h1>
            <div className={`text-[9px] uppercase font-bold tracking-widest mt-0.5 ${backendStatus === 'ONLINE' ? 'text-cyan-400' : 'text-red-500'}`}>
              {backendStatus}
            </div>
          </div>
        </div>
        <button onClick={() => setShowSetup(true)} className="px-3 py-1.5 bg-slate-800 border border-slate-700 rounded uppercase font-bold text-[10px]">Config</button>
      </header>

      <div className="flex-grow flex flex-col md:flex-row min-h-0">
        <aside className="w-full md:w-80 border-r border-slate-800 bg-slate-900/40 p-6 space-y-8 overflow-y-auto">
          <section className="space-y-4">
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-widest">Race_Deployment</h2>
            <div className="p-4 bg-slate-950 border border-slate-800 rounded-xl space-y-3 shadow-xl">
               <input type="text" placeholder="RACE NAME" value={crName} onChange={e=>setCrName(e.target.value)} className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none text-xs"/>
               <div className="flex gap-2">
                 <input type="text" placeholder="DATE" value={crDate} onChange={e=>setCrDate(e.target.value)} className="w-1/2 bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none text-xs"/>
                 <input type="text" placeholder="GOAL" value={crTarget} onChange={e=>setCrTarget(e.target.value)} className="w-1/2 bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none text-xs"/>
               </div>
               <textarea rows={5} placeholder="Loops, nutrition details..." value={crDetails} onChange={e=>setCrDetails(e.target.value)} className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none text-[10px] resize-none"/>
               <button onClick={() => handleSync('CUSTOM')} disabled={!crName || !crDate || !!isProcessing} className="w-full py-3 bg-cyan-900/20 text-cyan-400 rounded-lg border border-cyan-500/20 font-black uppercase text-[10px] tracking-widest disabled:opacity-20 transition-all">
                {isProcessing === 'CUSTOM' ? 'Analyzing...' : 'Deploy_Analysis'}
               </button>
            </div>
          </section>

          <section className="space-y-4">
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-widest">Operations</h2>
            <div className="space-y-2">
               <button onClick={() => handleSync('SEASON')} className="w-full py-2.5 bg-slate-900 hover:bg-slate-800 text-slate-400 rounded-lg border border-slate-800 text-[9px] uppercase font-black transition-all">Season_Update</button>
               <button onClick={() => handleSync('BATCH')} className="w-full py-2.5 bg-slate-900 hover:bg-slate-800 text-slate-400 rounded-lg border border-slate-800 text-[9px] uppercase font-black transition-all">Batch_Sync_Recent</button>
            </div>
          </section>

          <section className="space-y-4">
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-widest">Manual_Override</h2>
            <div className="flex gap-2">
              <input type="text" placeholder="ACTIVITY_ID" value={syncId} onChange={e=>setSyncId(e.target.value)} className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none text-xs"/>
              <button onClick={() => handleSync('ID')} disabled={!syncId} className="px-4 bg-slate-800 rounded-lg border border-slate-700 text-cyan-500 font-bold uppercase">Run</button>
            </div>
          </section>
        </aside>

        <main className="flex-grow flex flex-col bg-slate-950 overflow-hidden">
          <div className="flex bg-slate-900 border-b border-slate-800">
            <button onClick={() => setActiveTab('LOGS')} className={`px-8 py-4 text-[10px] font-black uppercase border-b-2 transition-all ${activeTab === 'LOGS' ? 'border-cyan-400 text-cyan-400' : 'border-transparent text-slate-500'}`}>Terminal</button>
            <button onClick={() => setActiveTab('DIAGNOSTICS')} className={`px-8 py-4 text-[10px] font-black uppercase border-b-2 transition-all ${activeTab === 'DIAGNOSTICS' ? 'border-cyan-400 text-cyan-400' : 'border-transparent text-slate-500'}`}>Cloud_Metrics</button>
          </div>
          <div className="flex-grow overflow-hidden p-6 md:p-10">
            <div ref={remoteLogsRef} className="h-full bg-slate-900/40 border border-slate-800/50 rounded-2xl p-6 overflow-y-auto space-y-1 font-mono text-[10px]">
                {activeTab === 'LOGS' ? (
                  localLogs.map(log => (
                    <div key={log.id} className="flex gap-4">
                      <span className="text-slate-700">[{log.time}]</span>
                      <span className={log.type === 'success' ? 'text-cyan-400' : log.type === 'error' ? 'text-red-400' : 'text-slate-500'}>{log.msg}</span>
                    </div>
                  ))
                ) : (
                  backendLogs.map((l, i) => <div key={i} className={`py-1 border-l-2 pl-4 mb-0.5 ${l.includes("ERROR") ? 'border-red-500 text-red-400' : l.includes("SUCCESS") ? 'border-cyan-400 text-cyan-400' : 'border-slate-800 text-slate-600'}`}>{l}</div>)
                )}
            </div>
          </div>
        </main>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/95 backdrop-blur-md p-4">
          <div className="bg-slate-900 border border-slate-700 rounded-3xl max-w-md w-full p-8 space-y-6">
            <h2 className="text-xl font-black text-white uppercase">System_Link</h2>
            <div className="space-y-4">
              <input type="text" value={backendUrl} placeholder="GATEWAY URL" onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-xl p-4 text-xs text-cyan-400 outline-none font-bold"/>
              <input type="password" value={backendSecret} placeholder="VERIFY TOKEN" onChange={e => setBackendSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded-xl p-4 text-xs text-cyan-400 outline-none font-bold"/>
            </div>
            <button onClick={() => {localStorage.setItem('stravai_backend_url', backendUrl); localStorage.setItem('stravai_backend_secret', backendSecret); setShowSetup(false); checkBackend();}} className="w-full py-4 bg-cyan-600 text-white rounded-xl font-black uppercase text-[10px] tracking-widest">Connect</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
