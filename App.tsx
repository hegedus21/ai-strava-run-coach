import { useState, useEffect, useCallback, useRef } from 'react';
import React from 'react';
import { StravAILogo } from './components/Icon';
import Presentation from './components/Presentation';
import { LiveRaceStatus, LiveRaceConfig, RaceCheckpoint } from './types';

type ValidationError = { [key: string]: string };

const App: React.FC = () => {
  const [backendUrl, setBackendUrl] = useState<string>(localStorage.getItem('stravai_backend_url') || '');
  const [backendSecret, setBackendSecret] = useState<string>(localStorage.getItem('stravai_backend_secret') || '');
  const [activeTab, setActiveTab] = useState<'LOGS' | 'DIAGNOSTICS' | 'LIVE_OPS'>('LOGS');
  const [showSetup, setShowSetup] = useState(!backendUrl || !backendSecret);
  const [showStory, setShowStory] = useState(false);
  
  // Custom Race State
  const [crName, setCrName] = useState('');
  const [crDate, setCrDate] = useState('');
  const [crTarget, setCrTarget] = useState('');
  const [crDetails, setCrDetails] = useState('');
  const [syncId, setSyncId] = useState('');

  // Live Race State
  const [liveUrl, setLiveUrl] = useState('');
  const [tgToken, setTgToken] = useState('');
  const [tgChatId, setTgChatId] = useState('');
  const [raceTotalDist, setRaceTotalDist] = useState('211');
  const [raceName, setRaceName] = useState('Ultrabalaton 2025');
  const [raceStatus, setRaceStatus] = useState<LiveRaceStatus | null>(null);
  const [testResults, setTestResults] = useState<{ checkpoints: RaceCheckpoint[], logs: string[] } | null>(null);

  const [validationErrors, setValidationErrors] = useState<ValidationError>({});
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
    const response = await fetch(url, { ...options, headers });
    
    if (response.status === 401) {
        addLocalLog("SECURITY_ALERT: Session Unauthorized.", "error");
        setBackendStatus('UNAUTHORIZED');
        setShowSetup(true);
    }
    
    return response;
  }, [backendSecret, addLocalLog]);

  const checkBackend = useCallback(async () => {
    if (!backendUrl) return;
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    try {
      const res = await fetch(`${cleanUrl}/health`);
      if (res.ok) {
        const logsRes = await securedFetch(`${cleanUrl}/logs`);
        if (logsRes.ok) {
            setBackendStatus('ONLINE');
            const newLogs = await logsRes.json();
            if (Array.isArray(newLogs)) setBackendLogs(newLogs);
            
            // Also check race status
            const raceRes = await securedFetch(`${cleanUrl}/race/status`);
            if (raceRes.ok) setRaceStatus(await raceRes.json());
        }
      } else { 
        setBackendStatus('OFFLINE'); 
      }
    } catch { 
      setBackendStatus('OFFLINE'); 
    }
  }, [backendUrl, securedFetch]);

  const handleTestParse = async () => {
    if (!liveUrl) return;
    setIsProcessing('TESTING');
    setTestResults(null);
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    addLocalLog(`DIAG_INIT: Probing athlete telemetry at ${liveUrl}`, "info");
    try {
      const res = await securedFetch(`${cleanUrl}/race/test-parse`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ url: liveUrl })
      });
      
      if (res.ok) {
        const data = await res.json();
        setTestResults({ checkpoints: data.checkpoints, logs: data.debugLogs || [] });
        if (data.success && data.count > 0) {
           addLocalLog(`Scraper Test: SUCCESS. Identified ${data.count} checkpoints.`, "success");
           data.checkpoints.forEach((cp: RaceCheckpoint) => {
             addLocalLog(` >> CP_DETECTED: [${cp.distanceKm.toFixed(2)}km] ${cp.name} | ARRIVAL: ${cp.time}`, "info");
           });
        } else {
           addLocalLog("Scraper Test: 0 data points found. The engine could not map any checkpoint rows.", "error");
        }
      } else {
        addLocalLog(`Scraper Test Error: ${res.status}`, "error");
        setTestResults({ checkpoints: [], logs: [`FATAL_HTTP_ERROR: ${res.status}`, `HINT: Check if backend is Online and Program.cs built correctly.`] });
      }
    } catch (e: any) {
      addLocalLog(`Connection Failed: ${e.message}`, "error");
    } finally {
      setIsProcessing(null);
    }
  };

  const handleRaceAction = async (action: 'START' | 'STOP') => {
    if (backendStatus !== 'ONLINE') return;
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    
    if (action === 'START') {
      const config: LiveRaceConfig = {
        timingUrl: liveUrl,
        telegramBotToken: tgToken,
        telegramChatId: tgChatId,
        targetPace: "6:00",
        raceName: raceName,
        totalDistance: parseFloat(raceTotalDist) || 100
      };
      const res = await securedFetch(`${cleanUrl}/race/start`, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(config)
      });
      if (res.ok) addLocalLog(`Live Tracker Engaged`, "success");
    } else {
      const res = await securedFetch(`${cleanUrl}/race/stop`, { method: 'POST' });
      if (res.ok) addLocalLog("Live Tracker Disengaged", "info");
    }
    checkBackend();
  };

  const handleSync = async (type: 'SEASON' | 'CUSTOM' | 'BATCH' | 'ID') => {
    if (backendStatus !== 'ONLINE') return;
    if (isProcessing) return;
    setIsProcessing(type);
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    let endpoint = '';
    let body = undefined;

    switch(type) {
      case 'CUSTOM': 
        endpoint = `${cleanUrl}/sync/custom-race`;
        body = JSON.stringify({ Name: crName, Date: crDate, TargetTime: crTarget, RaceDetails: crDetails });
        break;
      case 'SEASON': endpoint = `${cleanUrl}/sync/season`; break;
      case 'BATCH': endpoint = `${cleanUrl}/sync`; break;
      case 'ID': endpoint = `${cleanUrl}/sync/${syncId}`; break;
    }
    
    try {
      const res = await securedFetch(endpoint, { 
        method: 'POST', 
        body, 
        headers: body ? { 'Content-Type': 'application/json' } : {} 
      });
      if (res.ok) addLocalLog(`${type} protocol started`, "success");
    } catch (e) { addLocalLog("Network error", "error"); }
    finally { setTimeout(() => setIsProcessing(null), 1000); }
  };

  const saveConfig = () => {
    localStorage.setItem('stravai_backend_url', backendUrl);
    localStorage.setItem('stravai_backend_secret', backendSecret);
    setShowSetup(false);
    checkBackend();
  };

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      const int = setInterval(checkBackend, 10000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend]);

  const getInputClass = (errorKey: string, extraClass: string = "") => {
    const base = `w-full bg-slate-900 border rounded-lg px-3 py-2 text-cyan-400 outline-none text-xs transition-all ${extraClass}`;
    return validationErrors[errorKey] ? `${base} border-red-500` : `${base} border-slate-800 focus:border-cyan-500/50`;
  };

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[11px]">
      <header className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shrink-0">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-9 h-9" />
          <div>
            <h1 className="text-white font-black uppercase text-sm flex items-center gap-2">
              StravAI_Command
              <span className="text-[10px] text-cyan-500 font-bold border border-cyan-500/20 px-1.5 rounded">v1.4.9_STABLE</span>
            </h1>
            <div className={`text-[9px] uppercase font-bold tracking-widest mt-0.5 ${backendStatus === 'ONLINE' ? 'text-cyan-400' : 'text-red-500'}`}>
              {backendStatus}
            </div>
          </div>
        </div>
        <div className="flex gap-2">
          <button onClick={() => setShowStory(true)} className="px-3 py-1.5 bg-cyan-900/20 border border-cyan-500/20 rounded uppercase font-bold text-[10px] text-cyan-400 hover:bg-cyan-900/40">Project_Story</button>
          <button onClick={() => setShowSetup(true)} className="px-3 py-1.5 bg-slate-800 border border-slate-700 rounded uppercase font-bold text-[10px] hover:bg-slate-700">Config</button>
        </div>
      </header>

      <div className="flex-grow flex flex-col md:flex-row min-h-0">
        <aside className="w-full md:w-80 border-r border-slate-800 bg-slate-900/40 p-6 space-y-8 overflow-y-auto">
          {activeTab === 'LIVE_OPS' ? (
            <section className="space-y-4">
              <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-widest">LIVE_TRACKER_INIT</h2>
              <div className="p-4 bg-slate-950 border border-slate-800 rounded-xl space-y-3 shadow-xl">
                 <div className="space-y-1">
                    <p className="text-[8px] text-slate-500 uppercase font-bold">ATHLETE PROFILE URL (REQUIRED)</p>
                    <input placeholder="https://runtiming.hu/.../versenyzo/56564" value={liveUrl} onChange={e=>setLiveUrl(e.target.value)} className={getInputClass('')} />
                 </div>
                 <button 
                  onClick={handleTestParse} 
                  disabled={isProcessing === 'TESTING' || !liveUrl}
                  className="w-full py-2 bg-slate-800 hover:bg-slate-700 text-cyan-400 text-[9px] font-black rounded uppercase border border-slate-700 disabled:opacity-50 transition-all"
                 >
                   {isProcessing === 'TESTING' ? 'PROBING SITE...' : 'TEST_SCRAPER_PROTOCOL'}
                 </button>
                 <div className="h-px bg-slate-800 my-2"></div>
                 <input placeholder="TG BOT TOKEN" value={tgToken} onChange={e=>setTgToken(e.target.value)} className={getInputClass('')} />
                 <input placeholder="TG CHAT ID" value={tgChatId} onChange={e=>setTgChatId(e.target.value)} className={getInputClass('')} />
                 <div className="flex gap-2">
                    <input placeholder="TOTAL KM" value={raceTotalDist} onChange={e=>setRaceTotalDist(e.target.value)} className={getInputClass('')} />
                    <input placeholder="RACE NAME" value={raceName} onChange={e=>setRaceName(e.target.value)} className={getInputClass('')} />
                 </div>
                 {raceStatus?.isActive ? (
                    <button onClick={() => handleRaceAction('STOP')} className="w-full py-3 rounded-lg bg-red-900/20 text-red-400 border border-red-500/20 font-black uppercase text-[10px]">DISENGAGE</button>
                 ) : (
                    <button onClick={() => handleRaceAction('START')} className="w-full py-3 rounded-lg bg-cyan-900/20 text-cyan-400 border border-cyan-500/20 font-black uppercase text-[10px]">ENGAGE TRACKER</button>
                 )}
              </div>
            </section>
          ) : (
            <>
              <section className="space-y-4">
                <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-widest flex justify-between">CREATE RACE SPECIFIC ANALYSIS</h2>
                <div className="p-4 bg-slate-950 border border-slate-800 rounded-xl space-y-3 shadow-xl">
                   <input placeholder="RACE NAME" value={crName} onChange={e=>setCrName(e.target.value)} className={getInputClass('crName')} />
                   <div className="flex gap-2">
                     <input placeholder="YYYY-MM-DD" value={crDate} onChange={e=>setCrDate(e.target.value)} className={getInputClass('crDate')} />
                     <input placeholder="GOAL TIME" value={crTarget} onChange={e=>setCrTarget(e.target.value)} className={getInputClass('crTarget')} />
                   </div>
                   <textarea rows={3} placeholder="DETAILS" value={crDetails} onChange={e=>setCrDetails(e.target.value)} className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none text-[10px] resize-none" />
                   <button onClick={() => handleSync('CUSTOM')} disabled={!!isProcessing} className="w-full py-3 rounded-lg bg-cyan-900/20 text-cyan-400 border border-cyan-500/20 font-black uppercase text-[10px]">ANALYSE</button>
                </div>
              </section>
              <section className="space-y-4">
                <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-widest">Operations</h2>
                <div className="space-y-2">
                   <button onClick={() => handleSync('BATCH')} className="w-full py-2.5 bg-slate-900 hover:bg-slate-800 text-slate-400 rounded-lg border border-slate-800 text-[9px] uppercase font-black">ANALYSE RECENT</button>
                </div>
              </section>
            </>
          )}
        </aside>

        <main className="flex-grow flex flex-col bg-slate-950 overflow-hidden">
          <div className="flex bg-slate-900 border-b border-slate-800">
            <button onClick={() => setActiveTab('LOGS')} className={`px-8 py-4 text-[10px] font-black uppercase border-b-2 transition-all ${activeTab === 'LOGS' ? 'border-cyan-400 text-cyan-400' : 'border-transparent text-slate-500'}`}>Terminal</button>
            <button onClick={() => setActiveTab('DIAGNOSTICS')} className={`px-8 py-4 text-[10px] font-black uppercase border-b-2 transition-all ${activeTab === 'DIAGNOSTICS' ? 'border-cyan-400 text-cyan-400' : 'border-transparent text-slate-500'}`}>Cloud_Metrics</button>
            <button onClick={() => setActiveTab('LIVE_OPS')} className={`px-8 py-4 text-[10px] font-black uppercase border-b-2 transition-all ${activeTab === 'LIVE_OPS' ? 'border-orange-500 text-orange-500' : 'border-transparent text-slate-500'}`}>Live_Ops</button>
          </div>
          
          <div className="flex-grow overflow-hidden p-6 md:p-10">
            {activeTab === 'LIVE_OPS' ? (
              <div className="space-y-6 h-full flex flex-col">
                {testResults ? (
                  <div className="grid grid-cols-1 lg:grid-cols-2 gap-6 h-full overflow-hidden">
                    <div className="bg-slate-900/40 border border-cyan-500/20 rounded-2xl p-6 space-y-4 overflow-hidden flex flex-col">
                      <div className="flex justify-between items-center">
                        <h3 className="text-sm font-black text-cyan-400 uppercase tracking-tighter">SCRAPER_TELEMETRY</h3>
                        <button onClick={() => setTestResults(null)} className="text-[9px] font-bold text-slate-500 hover:text-white">RESET</button>
                      </div>
                      <div className="flex-grow overflow-y-auto border border-slate-800 rounded-xl bg-slate-950/50">
                        <table className="w-full text-left text-[10px]">
                          <thead className="sticky top-0 bg-slate-900 text-slate-500 uppercase font-black border-b border-slate-800">
                            <tr>
                              <th className="px-4 py-2">Checkpoint</th>
                              <th className="px-4 py-2">KM</th>
                              <th className="px-4 py-2">Time</th>
                            </tr>
                          </thead>
                          <tbody>
                            {testResults.checkpoints.length > 0 ? (
                                testResults.checkpoints.map((cp, i) => (
                                <tr key={i} className="border-b border-slate-800/50 hover:bg-slate-800/20">
                                    <td className="px-4 py-2 font-bold text-white">{cp.name}</td>
                                    <td className="px-4 py-2 text-cyan-400">{cp.distanceKm.toFixed(2)}</td>
                                    <td className="px-4 py-2 italic">{cp.time}</td>
                                </tr>
                                ))
                            ) : (
                                <tr>
                                    <td colSpan={3} className="px-4 py-8 text-center text-slate-600 uppercase font-black">Zero_Data_Extracted</td>
                                </tr>
                            )}
                          </tbody>
                        </table>
                      </div>
                    </div>
                    
                    <div className="bg-slate-900/40 border border-slate-800 rounded-2xl p-6 flex flex-col overflow-hidden">
                        <h3 className="text-sm font-black text-slate-400 uppercase tracking-tighter mb-4">ENGINE_DEBUG_LOGS</h3>
                        <div className="flex-grow overflow-y-auto bg-black rounded-xl p-4 font-mono text-[9px] space-y-1">
                            {testResults.logs.map((log, i) => (
                                <div key={i} className="flex gap-2">
                                    <span className="text-slate-700">[{i}]</span>
                                    <span className={log.includes("ERROR") || log.includes("EXCEPTION") || log.includes("HTTP_ERROR") ? "text-red-500" : "text-green-500"}>{log}</span>
                                </div>
                            ))}
                            {testResults.logs.length === 0 && <p className="text-slate-800">AWAITING_INPUT...</p>}
                        </div>
                    </div>
                  </div>
                ) : raceStatus?.isActive ? (
                  <div className="space-y-6 flex flex-col h-full">
                    <div className="bg-slate-900/40 border border-orange-500/20 rounded-2xl p-8 space-y-6 shadow-2xl">
                       <div className="flex justify-between items-start">
                          <div>
                             <h2 className="text-2xl font-black text-white uppercase tracking-tighter">ULTRA_LIVE_INTEL</h2>
                             <p className="text-orange-500 text-[10px] font-bold uppercase tracking-widest mt-1 animate-pulse">● TRACKING_ACTIVE</p>
                          </div>
                       </div>
                       <div className="grid grid-cols-3 gap-6">
                          <div className="bg-slate-950/50 p-4 rounded-xl border border-slate-800">
                             <p className="text-[9px] text-slate-500 font-bold uppercase">Last_Checkpoint</p>
                             <p className="text-lg font-black text-cyan-400 truncate">{raceStatus.lastCheckpoint?.name || '---'}</p>
                          </div>
                          <div className="bg-slate-950/50 p-4 rounded-xl border border-slate-800">
                             <p className="text-[9px] text-slate-500 font-bold uppercase">Split_Pace</p>
                             <p className="text-lg font-black text-white">{raceStatus.lastCheckpoint?.pace || '---'}</p>
                          </div>
                          <div className="bg-slate-950/50 p-4 rounded-xl border border-slate-800">
                             <p className="text-[9px] text-slate-500 font-bold uppercase">Total_KM</p>
                             <p className="text-lg font-black text-white">{(raceStatus.lastCheckpoint?.distanceKm || 0).toFixed(2)} / {raceTotalDist}</p>
                          </div>
                       </div>
                    </div>
                  </div>
                ) : (
                  <div className="flex-grow flex items-center justify-center border-2 border-dashed border-slate-800 rounded-3xl opacity-50 text-center">
                    <div className="space-y-4 max-w-sm px-6">
                       <p className="text-xs font-black uppercase tracking-widest text-slate-600">No_Live_Intelligence_Session</p>
                       <p className="text-[10px] text-slate-700 leading-relaxed">
                          Enter your <span className="text-cyan-500 font-bold underline">Individual Athlete Profile URL</span> from RunTiming.hu to begin.
                       </p>
                    </div>
                  </div>
                )}
              </div>
            ) : (
              <div ref={remoteLogsRef} className="h-full bg-slate-900/40 border border-slate-800/50 rounded-2xl p-6 overflow-y-auto space-y-1 font-mono text-[10px]">
                  {activeTab === 'LOGS' ? (
                      localLogs.map(log => (
                        <div key={log.id} className="flex gap-4">
                          <span className="text-slate-700">[{log.time}]</span>
                          <span className={log.type === 'success' ? 'text-cyan-400' : log.type === 'error' ? 'text-red-400' : 'text-slate-500'}>{log.msg}</span>
                        </div>
                      ))
                  ) : (
                      backendLogs.map((l, i) => <div key={i} className={`py-1 border-l-2 pl-4 mb-0.5 ${l.includes("ERROR") ? 'border-red-500 text-red-400' : 'border-slate-800 text-slate-600'}`}>{l}</div>)
                  )}
              </div>
            )}
          </div>
        </main>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/95 backdrop-blur-md p-4">
          <div className="bg-slate-900 border border-slate-700 rounded-3xl max-w-md w-full p-8 space-y-6 shadow-2xl">
            <h2 className="text-xl font-black text-white uppercase tracking-tighter">System_Link</h2>
            <div className="space-y-4">
                <input value={backendUrl} placeholder="GATEWAY URL" onChange={e => setBackendUrl(e.target.value)} className={getInputClass('')} />
                <input type="password" value={backendSecret} placeholder="VERIFY TOKEN" onChange={e => setBackendSecret(e.target.value)} className={getInputClass('')} />
            </div>
            <button onClick={saveConfig} className="w-full py-4 bg-cyan-600 text-white rounded-xl font-black uppercase text-[10px]">Establish_Connection</button>
          </div>
        </div>
      )}

      {showStory && <Presentation onClose={() => setShowStory(false)} />}
    </div>
  );
};

export default App;
