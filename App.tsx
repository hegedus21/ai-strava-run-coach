import { useState, useEffect, useCallback, useRef, useMemo } from 'react';
import React from 'react';
import { StravaService } from './services/stravaService';
import { GeminiCoachService, QuotaExhaustedError, STRAVAI_PLACEHOLDER } from './services/geminiService';
import { GoalSettings, StravaActivity } from './types';
import { StravAILogo } from './components/Icon';

const SYNC_INTERVAL_MS = 60000;

const App: React.FC = () => {
  const [token, setToken] = useState<string>(localStorage.getItem('strava_token') || '');
  const [clientId, setClientId] = useState<string>(localStorage.getItem('strava_client_id') || '');
  const [clientSecret, setClientSecret] = useState<string>(localStorage.getItem('strava_client_secret') || '');
  const [refreshToken, setRefreshToken] = useState<string>(localStorage.getItem('strava_refresh_token') || '');
  
  const [isActive, setIsActive] = useState(false);
  const [isVerifying, setIsVerifying] = useState(false);
  const [showSetup, setShowSetup] = useState(!token && !refreshToken);
  const [nextSyncIn, setNextSyncIn] = useState(SYNC_INTERVAL_MS / 1000);
  const [quotaExhausted, setQuotaExhausted] = useState(localStorage.getItem('quota_exhausted_date') === new Date().toDateString());
  const [webhookStatus, setWebhookStatus] = useState<'IDLE' | 'LISTENING' | 'ERROR'>('IDLE');

  const [goals, setGoals] = useState<GoalSettings>({
    raceType: localStorage.getItem('goal_race_type') || 'Marathon',
    raceDate: localStorage.getItem('goal_race_date') || '2025-10-12',
    goalTime: localStorage.getItem('goal_race_time') || '3:30:00'
  });
  
  const [logs, setLogs] = useState<{ id: string; msg: string; type: 'info' | 'success' | 'error' | 'ai' | 'warning'; time: string }[]>([]);
  const [stats, setStats] = useState({ checked: 0, updated: 0, lastRun: 'Never' });
  
  const logContainerRef = useRef<HTMLDivElement>(null);
  const stravaService = useMemo(() => new StravaService(), []);
  const coachService = useMemo(() => new GeminiCoachService(), []);

  const addLog = useCallback((msg: string, type: 'info' | 'success' | 'error' | 'ai' | 'warning' = 'info') => {
    const id = Math.random().toString(36).substr(2, 9);
    const time = new Date().toLocaleTimeString();
    setLogs(prev => [{ id, msg, type, time }, ...prev].slice(0, 100));
  }, []);

  const handleQuotaError = useCallback(() => {
    const today = new Date().toDateString();
    setQuotaExhausted(true);
    setIsActive(false);
    localStorage.setItem('quota_exhausted_date', today);
    addLog("CRITICAL: Daily Gemini Quota Exhausted. System in standby until tomorrow.", "error");
  }, [addLog]);

  useEffect(() => {
    localStorage.setItem('goal_race_type', goals.raceType);
    localStorage.setItem('goal_race_date', goals.raceDate);
    localStorage.setItem('goal_race_time', goals.goalTime);
  }, [goals]);

  const runVerification = async () => {
    const hasAuth = token || (clientId && clientSecret && refreshToken);
    if (!hasAuth) return addLog("Auth Credentials required. Use 'SETUP' button.", "error");
    if (quotaExhausted) return addLog("Cannot verify: Quota exhausted.", "error");
    
    setIsVerifying(true);
    addLog("Manual trigger: scanning for new or pending runs in the last 24h...", "info");
    
    try {
      if (token) stravaService.setToken(token);
      
      const activities = await stravaService.getRecentActivities(20);
      const runs = activities.filter(a => a.type === 'Run');
      const twentyFourHoursAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);

      const targets = runs.filter(a => new Date(a.start_date) > twentyFourHoursAgo && GeminiCoachService.needsAnalysis(a.description));

      if (targets.length === 0) {
        addLog("No new or pending runs found from the last 24 hours.", "warning");
        return;
      }

      addLog(`Found ${targets.length} target(s). Commencing analysis...`, "info");

      for (const target of targets) {
        addLog(`Consulting Coach Gemini for "${target.name}"...`, "ai");
        const history = runs.filter(a => a.id !== target.id);
        
        try {
          const analysis = await coachService.analyzeActivity(target, history, goals);
          const formattedReport = coachService.formatDescription(analysis);
          
          const cleanDesc = (target.description || "").split("################################")[0].trim();
          const newDescription = `${cleanDesc}\n\n${formattedReport}`;

          await stravaService.updateActivity(target.id, { description: newDescription });
          addLog(`✅ Manual Sync Successful: ${target.name} updated.`, "success");
          setStats(s => ({ ...s, updated: s.updated + 1 }));
        } catch (err: any) {
          if (err instanceof QuotaExhaustedError) {
            if (!target.description?.includes(STRAVAI_PLACEHOLDER)) {
               const placeholderReport = coachService.formatPlaceholder();
               const cleanDesc = (target.description || "").split("################################")[0].trim();
               const newDescription = `${cleanDesc}\n\n${placeholderReport}`;
               await stravaService.updateActivity(target.id, { description: newDescription });
               addLog(`Warning: Limit reached. ${target.name} marked for later analysis.`, "warning");
            }
            throw err; 
          }
          throw err;
        }
      }
    } catch (err: any) {
      if (err instanceof QuotaExhaustedError) {
        handleQuotaError();
      } else {
        addLog(`Sync Failed: ${err.message}`, "error");
      }
    } finally {
      setIsVerifying(false);
    }
  };

  const processSync = useCallback(async (depth: number = 20) => {
    const hasAuth = token || (clientId && clientSecret && refreshToken);
    if (!hasAuth || quotaExhausted) return;
    
    addLog(`Pulse check: scanning 24h window...`, "info");
    if (token) stravaService.setToken(token);

    try {
      const activities = await stravaService.getRecentActivities(depth);
      setStats(s => ({ ...s, checked: s.checked + 1, lastRun: new Date().toLocaleTimeString() }));

      const runs = activities.filter(a => a.type === 'Run');
      const twentyFourHoursAgo = new Date(Date.now() - 24 * 60 * 60 * 1000);

      const targets = runs.filter(a => new Date(a.start_date) > twentyFourHoursAgo && GeminiCoachService.needsAnalysis(a.description));

      if (targets.length > 0) {
        addLog(`Batch found: ${targets.length} new/pending run(s). Processing...`, "ai");
        for (const target of targets) {
          try {
            const history = runs.filter(a => a.id !== target.id);
            const analysis = await coachService.analyzeActivity(target, history, goals);
            const report = coachService.formatDescription(analysis);
            
            const cleanDesc = (target.description || "").split("################################")[0].trim();
            const newDescription = `${cleanDesc}\n\n${report}`;
            
            await stravaService.updateActivity(target.id, { description: newDescription });
            
            addLog(`✅ Sync Complete: ${target.name} updated.`, "success");
            setStats(s => ({ ...s, updated: s.updated + 1 }));
          } catch (innerErr: any) {
            if (innerErr instanceof QuotaExhaustedError) {
              if (!target.description?.includes(STRAVAI_PLACEHOLDER)) {
                const placeholder = coachService.formatPlaceholder();
                const cleanDesc = (target.description || "").split("################################")[0].trim();
                await stravaService.updateActivity(target.id, { description: `${cleanDesc}\n\n${placeholder}` });
              }
              throw innerErr;
            }
            throw innerErr;
          }
        }
      } else {
        addLog("Status: Up to date (No pending runs in 24h).", "info");
      }
    } catch (err: any) {
      if (err instanceof QuotaExhaustedError) {
        handleQuotaError();
      } else {
        addLog(`Sync Issue: ${err.message}`, "error");
      }
    }
    setNextSyncIn(SYNC_INTERVAL_MS / 1000);
  }, [token, clientId, clientSecret, refreshToken, quotaExhausted, stravaService, coachService, goals, handleQuotaError, addLog]);

  useEffect(() => {
    let interval: number | undefined;
    let countdown: number | undefined;
    
    if (isActive && !quotaExhausted) {
      processSync(20);
      setWebhookStatus('LISTENING');
      interval = window.setInterval(() => processSync(20), SYNC_INTERVAL_MS);
      countdown = window.setInterval(() => {
        setNextSyncIn(prev => (prev > 0 ? prev - 1 : SYNC_INTERVAL_MS / 1000));
      }, 1000);
    } else {
      setNextSyncIn(SYNC_INTERVAL_MS / 1000);
      setWebhookStatus('IDLE');
    }
    
    return () => {
      clearInterval(interval);
      clearInterval(countdown);
    };
  }, [isActive, quotaExhausted, processSync]);

  const saveCredentials = () => {
    localStorage.setItem('strava_token', token);
    localStorage.setItem('strava_client_id', clientId);
    localStorage.setItem('strava_client_secret', clientSecret);
    localStorage.setItem('strava_refresh_token', refreshToken);
    setShowSetup(false);
    addLog("Credentials updated. Ready for operations.", "success");
  };

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[13px]">
      <div className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shadow-2xl">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-10 h-10" />
          <div>
            <h1 className="text-white font-bold tracking-tight uppercase text-base">StravAI_Command_Center</h1>
            <div className="flex items-center gap-3 text-[10px] uppercase font-bold tracking-widest mt-0.5">
              <span className={`flex items-center gap-1 ${isActive ? 'text-green-400' : quotaExhausted ? 'text-red-500' : 'text-amber-400'}`}>
                <span className={`w-2 h-2 rounded-full ${isActive ? 'bg-green-400 animate-pulse' : quotaExhausted ? 'bg-red-600' : 'bg-amber-400'}`}></span>
                {quotaExhausted ? 'QUOTA_DEPLETED' : isActive ? `Active_Listening` : 'Standby'}
              </span>
              <span className="text-slate-600">|</span>
              <span className="text-cyan-400 font-bold">API_V3_BRIDGE_CONNECTED</span>
            </div>
          </div>
        </div>

        <div className="flex items-center gap-3">
          <button 
            disabled={isVerifying || quotaExhausted}
            onClick={runVerification}
            className={`px-4 py-2 text-xs font-bold border rounded-md transition-all ${
              isVerifying || quotaExhausted ? 'bg-slate-800 border-slate-700 opacity-50' : 'bg-white/5 border-white/10 hover:bg-white/10 text-cyan-400'
            }`}
          >
            {isVerifying ? 'PROCESSING...' : quotaExhausted ? 'LOCKED' : 'FORCE_RESCAN'}
          </button>
          <button 
            onClick={() => setShowSetup(true)}
            className="px-4 py-2 text-xs font-bold border border-slate-700 rounded-md hover:bg-slate-800 transition-all text-orange-400"
          >
            OPS_MANUAL
          </button>
          <button 
            disabled={quotaExhausted}
            onClick={() => setIsActive(!isActive)}
            className={`px-6 py-2 rounded-md font-bold text-xs transition-all ${
              quotaExhausted ? 'bg-slate-800 text-slate-600 border border-slate-700 opacity-50' :
              isActive 
              ? 'bg-red-500/10 text-red-500 border border-red-500/50' 
              : 'bg-green-500/10 text-green-500 border border-green-500/50'
            }`}
          >
            {isActive ? 'STOP_LISTENER' : 'START_LISTENER'}
          </button>
        </div>
      </div>

      <div className="flex-grow flex flex-col md:flex-row min-h-0 overflow-hidden">
        <div className="w-full md:w-80 border-r border-slate-800 bg-slate-900/50 p-6 space-y-8 overflow-y-auto">
          {quotaExhausted && (
            <div className="p-4 bg-red-900/20 border border-red-900/50 rounded-lg text-[10px] text-red-400 leading-tight">
              <p className="font-bold mb-1 underline">QUOTA DEPLETED</p>
              The daily limit is reached. The system is in safe-mode. Manual and auto-syncs are disabled until Google resets your counter.
            </div>
          )}
          
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
              <div>
                <label className="text-[10px] text-slate-400 block mb-1 uppercase">Pace/Time Goal</label>
                <input type="text" value={goals.goalTime} onChange={e => setGoals({...goals, goalTime: e.target.value})} className="w-full bg-slate-800 border border-slate-700 rounded p-2 text-xs text-white outline-none focus:border-orange-500"/>
              </div>
            </div>
          </div>

          <div className="pt-6 border-t border-slate-800">
            <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">Backend_Status (.NET)</h2>
            <div className="p-3 bg-slate-950 border border-slate-800 rounded-lg space-y-2">
              <div className="flex justify-between items-center">
                <span className="text-[10px] text-slate-500 uppercase font-bold">Webhook Listener</span>
                <span className={`text-[10px] font-bold ${webhookStatus === 'LISTENING' ? 'text-green-400' : 'text-slate-600'}`}>
                   {webhookStatus === 'LISTENING' ? 'CONNECTED' : 'OFFLINE'}
                </span>
              </div>
              <div className="w-full bg-slate-800 h-1 rounded-full overflow-hidden">
                <div className={`h-full transition-all duration-1000 ${webhookStatus === 'LISTENING' ? 'w-full bg-green-500' : 'w-0 bg-slate-600'}`}></div>
              </div>
              <p className="text-[9px] text-slate-600 leading-tight italic">
                Listening for Strava pushed events via Minimal API bridge.
              </p>
            </div>
          </div>

          <div className="pt-6 border-t border-slate-800">
            <h2 className="text-[10px] font-bold text-slate-500 uppercase tracking-widest mb-4">System_Telemetry</h2>
            <div className="space-y-3 text-xs">
              <div className="flex justify-between"><span className="text-slate-500">Scan Cycles:</span><span>{stats.checked}</span></div>
              <div className="flex justify-between"><span className="text-slate-500">AI Analyses:</span><span className="text-[#FC6100] font-bold">{stats.updated}</span></div>
              <div className="flex justify-between"><span className="text-slate-500">Last_Activity:</span><span className="text-slate-400">{stats.lastRun}</span></div>
            </div>
          </div>
        </div>

        <div className="flex-grow flex flex-col bg-[#020617] relative">
          <div ref={logContainerRef} className="flex-grow overflow-y-auto p-6 space-y-1 text-[11px] font-mono leading-relaxed">
            {logs.length === 0 ? (
              <div className="text-slate-700 italic opacity-50 uppercase tracking-widest text-[9px] animate-pulse">Initializing StravAI secure session... awaiting pulse.</div>
            ) : (
              logs.map((log) => (
                <div key={log.id} className="flex gap-4">
                  <span className="text-slate-800 shrink-0 font-bold select-none">[{log.time}]</span>
                  <span className={`
                    ${log.type === 'success' ? 'text-green-400' : ''}
                    ${log.type === 'error' ? 'text-red-400' : ''}
                    ${log.type === 'ai' ? 'text-cyan-400' : ''}
                    ${log.type === 'info' ? 'text-slate-500' : ''}
                    ${log.type === 'warning' ? 'text-amber-400' : ''}
                  `}>
                    <span className="text-slate-800 mr-2 select-none">$</span>
                    {log.msg}
                  </span>
                </div>
              ))
            )}
          </div>
        </div>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm p-4">
          <div className="bg-slate-900 border border-slate-700 rounded-xl max-w-4xl w-full max-h-[90vh] overflow-y-auto p-8 shadow-2xl space-y-6">
            <div className="flex justify-between items-start border-b border-slate-800 pb-4">
              <div>
                <h2 className="text-xl font-bold text-white mb-1 uppercase tracking-tighter">Maintenance & Operations Guide</h2>
                <p className="text-slate-400 text-xs italic">Configure your secure local environment.</p>
              </div>
              <button onClick={() => setShowSetup(false)} className="text-slate-500 hover:text-white transition-colors">✕</button>
            </div>

            <div className="grid grid-cols-1 md:grid-cols-2 gap-8">
              <div className="space-y-6">
                 <section className="space-y-3">
                  <h3 className="text-amber-400 font-bold uppercase text-xs tracking-widest border-b border-amber-400/20 pb-1">Option 1: Permanent Auth (Recommended)</h3>
                  <p className="text-slate-400 text-[10px] leading-relaxed">Enter your Strava API credentials below. They stay in your browser's local storage and auto-refresh your session forever.</p>
                  <div className="space-y-3">
                    <input type="text" placeholder="Client ID" value={clientId} onChange={e => setClientId(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs outline-none focus:border-cyan-500"/>
                    <input type="password" placeholder="Client Secret" value={clientSecret} onChange={e => setClientSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs outline-none focus:border-cyan-500"/>
                    <input type="password" placeholder="Refresh Token" value={refreshToken} onChange={e => setRefreshToken(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs outline-none focus:border-cyan-500"/>
                  </div>
                </section>
                
                <section className="space-y-3">
                  <h3 className="text-orange-500 font-bold uppercase text-xs tracking-widest border-b border-orange-500/20 pb-1">Option 2: Temporary Access Token</h3>
                  <p className="text-slate-400 text-[10px] leading-relaxed">Fastest for monitoring. Valid for 6 hours. Copy from your Strava API settings page.</p>
                  <input 
                    type="password" 
                    placeholder="Paste 6-hour Access Token..." 
                    value={token} 
                    onChange={e => setToken(e.target.value)}
                    className="w-full bg-slate-950 border border-slate-800 rounded p-2 text-xs outline-none focus:border-orange-500"
                  />
                </section>
              </div>

              <div className="space-y-6 bg-slate-950 p-6 rounded-lg border border-slate-800">
                <h3 className="text-blue-400 font-bold uppercase text-xs tracking-widest mb-4">Operational Status</h3>
                <ul className="text-[10px] space-y-3 text-slate-400">
                  <li className="flex gap-2">
                    <span className="text-blue-400 font-bold">Hybrid_Service:</span>
                    <span>This UI works with either local polling or a remote .NET Webhook API.</span>
                  </li>
                  <li className="flex gap-2">
                    <span className="text-blue-400 font-bold">Webhook_Setup:</span>
                    <span>To "listen" to activities, you must register a callback URL in your Strava API settings that points to your .NET service.</span>
                  </li>
                  <li className="flex gap-2">
                    <span className="text-blue-400 font-bold">Push_Notifications:</span>
                    <span>When a Webhook event occurs, the backend processes it instantly, reducing latency compared to polling.</span>
                  </li>
                  <li className="flex gap-2 border-t border-slate-800 pt-3">
                    <span className="text-slate-500 italic">Credentials are stored locally in your browser and never sent to our servers.</span>
                  </li>
                </ul>
              </div>
            </div>

            <button 
              onClick={saveCredentials}
              className="w-full py-4 bg-orange-600 hover:bg-orange-500 text-white rounded-lg font-bold transition-all uppercase tracking-widest text-xs shadow-lg shadow-orange-900/20"
            >
              COMMIT_CHANGES_TO_LOCAL_STORAGE
            </button>
          </div>
        </div>
      )}

      <div className="px-6 py-2 bg-slate-900 border-t border-slate-800 text-[9px] text-slate-600 flex justify-between uppercase">
        <span>StravAI.OS v1.6.0 (WEBHOOK_HYBRID_READY)</span>
        <div className="flex items-center gap-6">
           <span className="flex items-center gap-2">
             <span className="text-slate-500">AUTH:</span>
             {refreshToken ? (
               <span className="text-cyan-400 font-bold flex items-center gap-1">
                 <span className="w-1.5 h-1.5 bg-cyan-400 rounded-full animate-pulse"></span> PERMANENT_VAULT
               </span>
             ) : token ? (
               <span className="text-orange-400 font-bold">TEMPORARY_SESSION</span>
             ) : (
               <span className="text-red-500 font-bold">UNAUTHENTICATED</span>
             )}
           </span>
           <span className="text-slate-500">GEMINI_QUOTA: {quotaExhausted ? '0/20' : 'ACTIVE'}</span>
        </div>
      </div>
    </div>
  );
};

export default App;