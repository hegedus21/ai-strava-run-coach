
import { useState, useEffect, useCallback } from 'react';
import React from 'react';
import { StravAILogo } from './components/Icon';
import { AthleteProfile, TrainingSession } from './types';

const QuotaProgressBar: React.FC<{ used: number, limit: number, label: string, color: string }> = ({ used, limit, label, color }) => {
  const pct = Math.min(100, (used / limit) * 100);
  return (
    <div className="space-y-1.5 flex-grow">
      <div className="flex justify-between text-[8px] font-black uppercase tracking-widest text-slate-500">
        <span>{label}</span>
        <span>{limit - used} LEFT</span>
      </div>
      <div className="h-1 w-full bg-slate-800 rounded-full overflow-hidden">
        <div className={`h-full transition-all duration-1000 ${color}`} style={{ width: `${pct}%` }} />
      </div>
    </div>
  );
};

const MilestoneCard: React.FC<{ milestone: any, category: string }> = ({ milestone, category }) => (
  <div className="bg-slate-900/60 border border-slate-800 p-4 rounded-2xl hover:border-cyan-500/30 transition-all group">
    <div className="flex justify-between items-start mb-2">
      <span className="text-[9px] font-black uppercase text-slate-500 tracking-widest group-hover:text-cyan-400 transition-colors">{category}</span>
      <span className="text-[11px] font-black text-white bg-slate-800 px-2 py-0.5 rounded-full">{milestone?.count || 0}X</span>
    </div>
    <div className="flex flex-col">
       <span className="text-sm text-white font-black">{milestone?.pb || 'N/A'}</span>
       <span className="text-[7px] font-bold text-slate-600 uppercase tracking-tighter">Personal_Best</span>
    </div>
  </div>
);

const CalendarSession: React.FC<{ session: TrainingSession, isNext: boolean }> = ({ session, isNext }) => (
  <div className={`p-4 rounded-2xl border transition-all ${isNext ? 'bg-cyan-500/10 border-cyan-500 shadow-[0_0_20px_rgba(34,211,238,0.15)] ring-1 ring-cyan-500/20' : 'bg-slate-900/40 border-slate-800'}`}>
    <div className="flex justify-between items-center mb-2">
      <span className="text-[9px] font-black uppercase text-slate-500">{session.date ? new Date(session.date).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' }) : 'TBD'}</span>
      {isNext && <span className="text-[8px] bg-cyan-500 text-black font-black px-2 py-0.5 rounded tracking-widest">NEXT</span>}
    </div>
    <div className="flex items-center gap-2 mb-1">
        <span className={`w-2 h-2 rounded-full ${session.type === 'Interval' ? 'bg-red-500' : session.type === 'Tempo' ? 'bg-orange-500' : session.type === 'Long Run' ? 'bg-blue-500' : 'bg-emerald-500'}`} />
        <h4 className={`text-[11px] font-black uppercase truncate ${isNext ? 'text-cyan-400' : 'text-white'}`}>{session.title}</h4>
    </div>
    <p className="text-[10px] text-slate-400 mt-2 leading-relaxed line-clamp-3">{session.description}</p>
  </div>
);

const App: React.FC = () => {
  const [backendUrl, setBackendUrl] = useState<string>(localStorage.getItem('stravai_backend_url') || '');
  const [backendSecret, setBackendSecret] = useState<string>(localStorage.getItem('stravai_backend_secret') || '');
  const [profile, setProfile] = useState<AthleteProfile | null>(null);
  const [logs, setLogs] = useState<string[]>([]);
  const [activeTab, setActiveTab] = useState<'DASHBOARD' | 'PLAN' | 'LOGS'>('DASHBOARD');
  const [backendStatus, setBackendStatus] = useState<'OFFLINE' | 'ONLINE'>('OFFLINE');
  const [auditPending, setAuditPending] = useState(false);
  const [showSetup, setShowSetup] = useState(!backendUrl);
  const [loadingProfile, setLoadingProfile] = useState(false);

  const securedFetch = useCallback(async (url: string, options: RequestInit = {}) => {
    const headers = new Headers(options.headers || {});
    headers.set('X-StravAI-Secret', backendSecret);
    return fetch(url, { ...options, headers });
  }, [backendSecret]);

  const fetchProfile = useCallback(async () => {
    if (!backendUrl || backendStatus === 'OFFLINE') return;
    setLoadingProfile(true);
    try {
      const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/profile`);
      if (res.ok) {
          const data = await res.json();
          console.debug('StravAI: Profile Hydrated', data);
          setProfile(data);
      } else {
          setProfile(null);
      }
    } catch (err) {
        console.error('StravAI: Profile Fetch Failed', err);
        setProfile(null);
    } finally {
      setLoadingProfile(false);
    }
  }, [backendUrl, backendStatus, securedFetch]);

  const fetchLogs = useCallback(async () => {
    if (!backendUrl || backendStatus === 'OFFLINE') return;
    try {
      const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/logs`);
      if (res.ok) setLogs(await res.json());
    } catch {}
  }, [backendUrl, backendStatus, securedFetch]);

  // Initial Data Fetch
  useEffect(() => {
    if (backendUrl && backendStatus === 'ONLINE') {
        fetchProfile();
        fetchLogs();
    }
  }, [backendUrl, backendStatus, fetchProfile, fetchLogs]);

  // Health Polling
  useEffect(() => {
    if (backendUrl) {
      const check = async () => {
        try {
          const res = await fetch(`${backendUrl.trim().replace(/\/$/, '')}/health`);
          setBackendStatus(res.ok ? 'ONLINE' : 'OFFLINE');
        } catch { setBackendStatus('OFFLINE'); }
      };
      check();
      const int = setInterval(check, 10000);
      return () => clearInterval(int);
    }
  }, [backendUrl]);

  // Audit Log Polling
  useEffect(() => {
    let int: any = null;
    if (auditPending) int = setInterval(fetchLogs, 2000);
    return () => { if (int) clearInterval(int); };
  }, [auditPending, fetchLogs]);

  const handleAudit = async () => {
    if (auditPending || backendStatus === 'OFFLINE') return;
    setAuditPending(true);
    setProfile(null); // Clear old profile while auditing
    try {
        await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/audit`, { method: 'POST' });
        // Poll for profile completion
        let attempts = 0;
        const checker = setInterval(async () => {
            attempts++;
            await fetchProfile();
            // If profile is populated, stop polling
            if (profile && profile.summary) {
                clearInterval(checker);
                setAuditPending(false);
            } else if (attempts > 20) {
                clearInterval(checker);
                setAuditPending(false);
                alert("Audit timeout. Check system logs for details.");
            }
        }, 5000);
    } catch {
        setAuditPending(false);
    }
  };

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[11px]">
      <header className="px-6 py-4 bg-slate-900 border-b border-slate-800 flex justify-between items-center shrink-0">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-8 h-8" />
          <div>
            <h1 className="text-white font-black uppercase text-xs tracking-tighter">StravAI_TMS_v2.6</h1>
            <div className={`text-[8px] font-bold uppercase flex items-center gap-1.5 ${backendStatus === 'ONLINE' ? 'text-cyan-400' : 'text-red-500'}`}>
                <span className={`w-1.5 h-1.5 rounded-full ${backendStatus === 'ONLINE' ? 'bg-cyan-500 animate-pulse' : 'bg-red-500'}`} />
                SRV:{backendStatus}
            </div>
          </div>
        </div>
        <div className="hidden md:flex gap-8 w-1/3">
           <QuotaProgressBar label="Intelligence_Core" used={profile ? 1 : 0} limit={1500} color="bg-cyan-500" />
        </div>
        <div className="flex gap-2">
            <button onClick={fetchProfile} className="p-2 border border-slate-800 rounded-xl hover:bg-slate-800 transition-colors" title="Refresh Profile">
                <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"/></svg>
            </button>
            <button onClick={() => setShowSetup(true)} className="p-2 border border-slate-800 rounded-xl hover:bg-slate-800 transition-colors">
              <svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24">
                <path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M12 6V4m0 2a2 2 0 100 4m0-4a2 2 0 110 4m-6 8a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4m6 6v10m6-2a2 2 0 100-4m0 4a2 2 0 110-4m0 4v2m0-6V4" />
              </svg>
            </button>
        </div>
      </header>

      <div className="flex-grow flex overflow-hidden">
        <nav className="w-20 border-r border-slate-800 bg-slate-900/40 flex flex-col items-center py-8 gap-8 shrink-0">
             <button onClick={() => setActiveTab('DASHBOARD')} className={`p-3 rounded-2xl ${activeTab === 'DASHBOARD' ? 'bg-cyan-500/10 text-cyan-400 border border-cyan-500/20 shadow-[0_0_15px_rgba(34,211,238,0.05)]' : 'text-slate-600 hover:text-slate-400'}`}><svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M4 6a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2H6a2 2 0 01-2-2V6zM14 6a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2h-2a2 2 0 01-2-2V6zM4 16a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2H6a2 2 0 01-2-2v-2zM14 16a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2h-2a2 2 0 01-2-2v-2z"/></svg></button>
             <button onClick={() => setActiveTab('PLAN')} className={`p-3 rounded-2xl ${activeTab === 'PLAN' ? 'bg-cyan-500/10 text-cyan-400 border border-cyan-500/20 shadow-[0_0_15px_rgba(34,211,238,0.05)]' : 'text-slate-600 hover:text-slate-400'}`}><svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2-2v12a2 2 0 002 2z"/></svg></button>
             <button onClick={() => setActiveTab('LOGS')} className={`p-3 rounded-2xl ${activeTab === 'LOGS' ? 'bg-cyan-500/10 text-cyan-400 border border-cyan-500/20 shadow-[0_0_15px_rgba(34,211,238,0.05)]' : 'text-slate-600 hover:text-slate-400'}`}><svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"/></svg></button>
             <div className="mt-auto pb-4">
                <button onClick={handleAudit} disabled={auditPending} className={`p-3 rounded-2xl border transition-all ${auditPending ? 'bg-amber-500/20 border-amber-500/40 text-amber-500 animate-pulse' : 'bg-slate-800 border-slate-700 text-slate-500 hover:text-cyan-400 hover:border-cyan-500/50'}`}>
                    <svg className={`w-6 h-6 ${auditPending ? 'animate-spin' : ''}`} fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"/></svg>
                </button>
             </div>
        </nav>

        <main className="flex-grow overflow-y-auto p-10 bg-slate-950/50 custom-scroll">
          {activeTab === 'DASHBOARD' && (
            <div className="max-w-6xl mx-auto space-y-10">
              {loadingProfile && !profile ? (
                 <div className="py-20 text-center uppercase tracking-widest text-[10px] animate-pulse">Syncing_Athlete_Intelligence_Cloud...</div>
              ) : !profile ? (
                <div className="py-20 text-center space-y-6">
                    <h2 className="text-white font-black text-xl uppercase tracking-tighter">System_Cache_Offline</h2>
                    <p className="text-slate-500 text-[10px] max-w-sm mx-auto uppercase font-bold tracking-widest leading-loose">No cached athlete profile detected. Initiate an audit to scan your history and build your coaching logic.</p>
                    <button onClick={handleAudit} disabled={auditPending} className="px-10 py-4 bg-cyan-600 text-white font-black rounded-3xl uppercase text-[10px] tracking-widest shadow-xl shadow-cyan-500/10 hover:bg-cyan-500 transition-all">{auditPending ? 'Processing_Strava_History...' : 'Initialize_System_Audit'}</button>
                </div>
              ) : (
                <div className="space-y-10 animate-in fade-in duration-700">
                    <section className="bg-slate-900 border border-slate-800 p-8 rounded-[2.5rem] shadow-2xl relative overflow-hidden">
                        <div className="absolute top-0 right-0 p-4 text-[7px] text-slate-800 font-black uppercase tracking-[0.3em]">AI_ANALYSIS_ACTIVE</div>
                        <h3 className="text-cyan-400 font-black uppercase text-[10px] mb-4 tracking-widest flex items-center gap-2">
                            <span className="w-4 h-0.5 bg-cyan-500" />
                            Coach_Diagnostic_Aggregate
                        </h3>
                        <p className="text-slate-200 text-[13px] leading-relaxed font-medium">{profile.summary || 'Summary generating...'}</p>
                        <div className="mt-6 pt-6 border-t border-slate-800/50 italic text-slate-500 text-[10px] flex justify-between">
                            <span>"{profile.coachNotes || 'Notes appearing soon'}"</span>
                            <span className="text-[8px] font-black uppercase text-slate-700">Refreshed: {profile.lastUpdated || 'Recently'}</span>
                        </div>
                    </section>
                    
                    <div>
                        <h4 className="text-[10px] font-black uppercase text-slate-600 mb-6 tracking-[0.2em] px-2">Historical_Personal_Bests</h4>
                        <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                            {Object.entries(profile.milestones || {}).map(([key, m], i) => (
                                <MilestoneCard key={i} category={key.replace(/([A-Z])/g, ' $1').trim()} milestone={m} />
                            ))}
                        </div>
                    </div>
                </div>
              )}
            </div>
          )}

          {activeTab === 'PLAN' && profile && (
            <div className="max-w-6xl mx-auto space-y-8 animate-in slide-in-from-bottom-4 duration-500">
               <div className="flex justify-between items-end mb-6">
                   <h2 className="text-xl font-black text-white uppercase tracking-tighter">7_Day_Performance_Prescription</h2>
               </div>
               <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-6">
                  {(profile.trainingPlan || []).length > 0 ? (
                    profile.trainingPlan.map((s, i) => <CalendarSession key={i} session={s} isNext={i === 0} />)
                  ) : (
                    <p className="col-span-full text-center py-10 text-slate-600 uppercase font-black tracking-widest text-[10px]">No sessions prescribed yet. Try re-auditing.</p>
                  )}
               </div>
            </div>
          )}

          {activeTab === 'LOGS' && (
            <div className="h-full bg-slate-900/40 border border-slate-800 rounded-[2rem] p-6 font-mono text-[9px] overflow-y-auto custom-scroll">
               <div className="flex justify-between items-center mb-4 border-b border-slate-800 pb-2">
                  <span className="text-slate-600 font-black uppercase tracking-widest">Realtime_Event_Telemetry</span>
                  <button onClick={fetchLogs} className="text-cyan-500 hover:text-cyan-400 font-black uppercase text-[8px]">Pull_Stream</button>
               </div>
               {logs.length === 0 ? (
                   <p className="text-slate-700 italic">Listening for system events...</p>
               ) : (
                   logs.slice().reverse().map((l, i) => (
                     <div key={i} className={`mb-1 py-1 px-2 rounded ${l.includes('[ERROR]') ? 'text-red-400 bg-red-500/5' : l.includes('[SUCCESS]') ? 'text-cyan-400 bg-cyan-500/5' : 'text-slate-500'}`}>{l}</div>
                   ))
               )}
            </div>
          )}
        </main>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-[100] bg-black/95 backdrop-blur-3xl flex items-center justify-center p-6">
          <div className="bg-slate-900 border border-slate-800 rounded-[3rem] p-12 max-w-sm w-full space-y-8 shadow-[0_0_80px_rgba(34,211,238,0.05)]">
             <div className="text-center space-y-2">
                <h3 className="text-white font-black uppercase tracking-tighter text-xl">Uplink_Configuration</h3>
                <p className="text-[9px] text-slate-500 uppercase tracking-widest font-black">StravAI_Network_Bridge</p>
             </div>
             <div className="space-y-4">
                <div className="space-y-1">
                    <label className="text-[8px] font-black text-slate-600 uppercase tracking-widest ml-1">Edge_Gateway</label>
                    <input type="text" placeholder="https://stravai.koyeb.app" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 p-4 rounded-2xl text-xs font-mono outline-none focus:border-cyan-500 transition-colors" />
                </div>
                <div className="space-y-1">
                    <label className="text-[8px] font-black text-slate-600 uppercase tracking-widest ml-1">System_Auth_Secret</label>
                    <input type="password" placeholder="••••••••" value={backendSecret} onChange={e => setBackendSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 p-4 rounded-2xl text-xs font-mono outline-none focus:border-cyan-500 transition-colors" />
                </div>
             </div>
             <button onClick={() => { localStorage.setItem('stravai_backend_url', backendUrl); localStorage.setItem('stravai_backend_secret', backendSecret); setShowSetup(false); fetchProfile(); }} className="w-full py-5 bg-cyan-600 text-white font-black rounded-2xl uppercase text-[11px] tracking-widest hover:bg-cyan-500 transition-all shadow-lg shadow-cyan-500/10">Synchronize_Terminal</button>
             <button onClick={() => setShowSetup(false)} className="w-full text-slate-600 text-[10px] font-black uppercase tracking-widest hover:text-slate-400">Cancel</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
