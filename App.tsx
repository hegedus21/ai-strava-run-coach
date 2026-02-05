import { useState, useEffect, useCallback, useRef } from 'react';
import React from 'react';
import { StravAILogo } from './components/Icon';

type ValidationError = { [key: string]: string };

const App: React.FC = () => {
  const [backendUrl, setBackendUrl] = useState<string>(localStorage.getItem('stravai_backend_url') || '');
  const [backendSecret, setBackendSecret] = useState<string>(localStorage.getItem('stravai_backend_secret') || '');
  const [activeTab, setActiveTab] = useState<'LOGS' | 'DIAGNOSTICS'>('LOGS');
  const [showSetup, setShowSetup] = useState(!backendUrl || !backendSecret);
  
  const [crName, setCrName] = useState('');
  const [crDate, setCrDate] = useState('');
  const [crTarget, setCrTarget] = useState('');
  const [crDetails, setCrDetails] = useState('');
  const [syncId, setSyncId] = useState('');

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

  const validateCustomRace = () => {
    const errors: ValidationError = {};
    if (!crName.trim()) errors.crName = "Race name is required";
    if (!crDate.trim()) errors.crDate = "Date is required";
    if (!crTarget.trim()) errors.crTarget = "Goal time is required";
    setValidationErrors(prev => ({ ...prev, ...errors }));
    return Object.keys(errors).length === 0;
  };

  const validateManualId = () => {
    const errors: ValidationError = {};
    if (!syncId.trim()) errors.syncId = "Activity ID required";
    setValidationErrors(prev => ({ ...prev, ...errors }));
    return Object.keys(errors).length === 0;
  };

  const handleSync = async (type: 'SEASON' | 'CUSTOM' | 'BATCH' | 'ID') => {
    if (backendStatus !== 'ONLINE') {
        addLocalLog("COMMAND_FAILED: Backend is offline.", "error");
        return;
    }
    if (isProcessing) return;

    // Clear specific errors before validation
    setValidationErrors({});

    if (type === 'CUSTOM' && !validateCustomRace()) {
        addLocalLog("VALIDATION_FAILURE: Custom race data incomplete.", "error");
        return;
    }
    if (type === 'ID' && !validateManualId()) {
        addLocalLog("VALIDATION_FAILURE: Activity ID missing.", "error");
        return;
    }

    setIsProcessing(type);
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    
    let endpoint = '';
    let body = undefined;

    switch(type) {
      case 'CUSTOM': 
        endpoint = `${cleanUrl}/sync/custom-race`;
        body = JSON.stringify({ Name: crName, Distance: '', Date: crDate, TargetTime: crTarget, RaceDetails: crDetails });
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
      } else { 
        const errText = await res.text();
        addLocalLog(`Server Error: ${res.status} ${errText}`, "error"); 
      }
    } catch (e: any) { addLocalLog("Network Transmission Failure.", "error"); }
    finally { setTimeout(() => setIsProcessing(null), 1000); }
  };

  const saveConfig = () => {
    const errors: ValidationError = {};
    if (!backendUrl.trim()) errors.setupUrl = "Gateway URL required";
    if (!backendSecret.trim()) errors.setupSecret = "Verify Token required";
    
    if (Object.keys(errors).length > 0) {
        setValidationErrors(errors);
        return;
    }

    localStorage.setItem('stravai_backend_url', backendUrl);
    localStorage.setItem('stravai_backend_secret', backendSecret);
    setShowSetup(false);
    setValidationErrors({});
    checkBackend();
  };

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      const int = setInterval(checkBackend, 5000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend]);

  const getInputClass = (errorKey: string, extraClass: string = "") => {
    const base = `w-full bg-slate-900 border rounded-lg px-3 py-2 text-cyan-400 outline-none text-xs transition-all ${extraClass}`;
    return validationErrors[errorKey] 
        ? `${base} border-red-500 shadow-[0_0_8px_rgba(239,68,68,0.4)] animate-pulse` 
        : `${base} border-slate-800 focus:border-cyan-500/50`;
  };

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
        <button onClick={() => setShowSetup(true)} className="px-3 py-1.5 bg-slate-800 border border-slate-700 rounded uppercase font-bold text-[10px] hover:bg-slate-700 transition-colors">Config</button>
      </header>

      <div className="flex-grow flex flex-col md:flex-row min-h-0">
        <aside className="w-full md:w-80 border-r border-slate-800 bg-slate-900/40 p-6 space-y-8 overflow-y-auto">
          <section className="space-y-4">
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-widest flex justify-between">
              CREATE RACE SPECIFIC ANALYSIS
              {Object.keys(validationErrors).some(k => k.startsWith('cr')) && <span className="text-red-500 animate-pulse">! MISSING</span>}
            </h2>
            <div className="p-4 bg-slate-950 border border-slate-800 rounded-xl space-y-3 shadow-xl">
               <div>
                  <input 
                    type="text" 
                    placeholder="RACE NAME" 
                    value={crName} 
                    onChange={e=>{setCrName(e.target.value); setValidationErrors(p => ({...p, crName: ''}))}} 
                    className={getInputClass('crName')}
                  />
                  {validationErrors.crName && <p className="text-[8px] text-red-500 mt-1 uppercase font-bold">{validationErrors.crName}</p>}
               </div>
               
               <div className="flex gap-2">
                 <div className="w-1/2">
                   <input 
                    type="text" 
                    placeholder="YYYY-MM-DD" 
                    value={crDate} 
                    onChange={e=>{setCrDate(e.target.value); setValidationErrors(p => ({...p, crDate: ''}))}} 
                    className={getInputClass('crDate', "placeholder:text-[9px]")}
                   />
                   <p className="text-[8px] text-slate-600 mt-1 uppercase font-bold">DATE (YYYY-MM-DD)</p>
                   {validationErrors.crDate && <p className="text-[8px] text-red-500 uppercase font-bold">REQUIRED</p>}
                 </div>
                 <div className="w-1/2">
                   <input 
                    type="text" 
                    placeholder="GOAL TIME" 
                    value={crTarget} 
                    onChange={e=>{setCrTarget(e.target.value); setValidationErrors(p => ({...p, crTarget: ''}))}} 
                    className={getInputClass('crTarget', "placeholder:text-[9px]")}
                   />
                   <p className="text-[8px] text-slate-600 mt-1 uppercase font-bold">GOAL TIME</p>
                   {validationErrors.crTarget && <p className="text-[8px] text-red-500 uppercase font-bold">REQUIRED</p>}
                 </div>
               </div>

               <textarea 
                rows={5} 
                placeholder="DETAILS (Optional: Loops, nutrition...)" 
                value={crDetails} 
                onChange={e=>setCrDetails(e.target.value)} 
                className="w-full bg-slate-900 border border-slate-800 rounded-lg px-3 py-2 text-cyan-400 outline-none text-[10px] resize-none focus:border-cyan-500/30"
               />
               
               <button 
                onClick={() => handleSync('CUSTOM')} 
                disabled={!!isProcessing} 
                className={`w-full py-3 rounded-lg border font-black uppercase text-[10px] tracking-widest transition-all ${isProcessing === 'CUSTOM' ? 'bg-cyan-900/40 text-cyan-200 border-cyan-500/40 cursor-wait' : 'bg-cyan-900/20 text-cyan-400 border-cyan-500/20 hover:bg-cyan-900/30'}`}
               >
                {isProcessing === 'CUSTOM' ? 'Analyzing...' : 'ANALYSE'}
               </button>
            </div>
          </section>

          <section className="space-y-4">
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-widest">Operations</h2>
            <div className="space-y-2">
               <button onClick={() => handleSync('SEASON')} className="w-full py-2.5 bg-slate-900 hover:bg-slate-800 text-slate-400 rounded-lg border border-slate-800 text-[9px] uppercase font-black transition-all">ANALYSE MAIN RACE</button>
               <button onClick={() => handleSync('BATCH')} className="w-full py-2.5 bg-slate-900 hover:bg-slate-800 text-slate-400 rounded-lg border border-slate-800 text-[9px] uppercase font-black transition-all">ANALYSE RECENT ACTIVITIES</button>
            </div>
          </section>

          <section className="space-y-4">
            <h2 className="text-[9px] font-black text-slate-500 uppercase tracking-widest">Analyse Specific Activity</h2>
            <div className="space-y-1">
                <div className="flex gap-2">
                  <input 
                    type="text" 
                    placeholder="ACTIVITY_ID" 
                    value={syncId} 
                    onChange={e=>{setSyncId(e.target.value); setValidationErrors(p => ({...p, syncId: ''}))}} 
                    className={getInputClass('syncId')}
                  />
                  <button onClick={() => handleSync('ID')} className="px-4 bg-slate-800 hover:bg-slate-700 rounded-lg border border-slate-700 text-cyan-500 font-bold uppercase transition-colors">Run</button>
                </div>
                {validationErrors.syncId && <p className="text-[8px] text-red-500 uppercase font-bold">{validationErrors.syncId}</p>}
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
                  localLogs.length > 0 ? (
                      localLogs.map(log => (
                        <div key={log.id} className="flex gap-4">
                          <span className="text-slate-700">[{log.time}]</span>
                          <span className={log.type === 'success' ? 'text-cyan-400' : log.type === 'error' ? 'text-red-400' : 'text-slate-500'}>{log.msg}</span>
                        </div>
                      ))
                  ) : (
                      <div className="text-slate-800 uppercase italic">Command Terminal Idle...</div>
                  )
                ) : (
                  backendLogs.length > 0 ? (
                      backendLogs.map((l, i) => <div key={i} className={`py-1 border-l-2 pl-4 mb-0.5 ${l.includes("ERROR") ? 'border-red-500 text-red-400' : l.includes("SUCCESS") ? 'border-cyan-400 text-cyan-400' : 'border-slate-800 text-slate-600'}`}>{l}</div>)
                  ) : (
                      <div className="text-slate-800 uppercase italic">Awaiting Telemetry from Cloud...</div>
                  )
                )}
            </div>
          </div>
        </main>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/95 backdrop-blur-md p-4">
          <div className="bg-slate-900 border border-slate-700 rounded-3xl max-w-md w-full p-8 space-y-6 shadow-2xl">
            <div>
                <h2 className="text-xl font-black text-white uppercase tracking-tighter">System_Link</h2>
                <p className="text-[9px] text-slate-500 uppercase mt-1">Configure secure access to StravAI Cloud Engine</p>
            </div>
            
            <div className="space-y-4">
              <div className="space-y-1">
                <input 
                    type="text" 
                    value={backendUrl} 
                    placeholder="GATEWAY URL (e.g., https://api.stravai.com)" 
                    onChange={e => {setBackendUrl(e.target.value); setValidationErrors(p => ({...p, setupUrl: ''}))}} 
                    className={getInputClass('setupUrl')}
                />
                {validationErrors.setupUrl && <p className="text-[8px] text-red-500 uppercase font-bold">{validationErrors.setupUrl}</p>}
              </div>

              <div className="space-y-1">
                <input 
                    type="password" 
                    value={backendSecret} 
                    placeholder="VERIFY TOKEN" 
                    onChange={e => {setBackendSecret(e.target.value); setValidationErrors(p => ({...p, setupSecret: ''}))}} 
                    className={getInputClass('setupSecret')}
                />
                {validationErrors.setupSecret && <p className="text-[8px] text-red-500 uppercase font-bold">{validationErrors.setupSecret}</p>}
              </div>
            </div>
            
            <button 
                onClick={saveConfig} 
                className="w-full py-4 bg-cyan-600 hover:bg-cyan-500 text-white rounded-xl font-black uppercase text-[10px] tracking-widest transition-all shadow-lg active:scale-95"
            >
                Establish_Connection
            </button>
            
            {(!backendUrl || !backendSecret) && (
                <div className="p-3 bg-red-900/10 border border-red-900/20 rounded-lg">
                    <p className="text-[9px] text-red-500/80 uppercase leading-normal">
                        Note: Credentials are stored locally in your browser. Ensure the Gateway URL points to an active StravAI Backend instance.
                    </p>
                </div>
            )}
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
