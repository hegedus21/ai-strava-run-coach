
import { useState, useEffect, useCallback, useRef } from 'react';
import React from 'react';
import { StravAILogo } from './components/Icon';
import { AthleteProfile, HeartRateZones, QuotaStatus, TrainingSession, RunCategory } from './types';

const QuotaProgressBar = ({ used, limit, label, color }: { used: number, limit: number, label: string, color: string }) => {
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

const MilestoneCard = ({ milestone }: { milestone: RunCategory }) => (
  <div className="bg-slate-900/40 border border-slate-800 p-3 rounded-xl hover:border-slate-700 transition-colors">
    <div className="flex justify-between items-start mb-2">
      <span className="text-[8px] font-black uppercase text-slate-500 tracking-wider">{milestone.category}</span>
      <span className="text-[10px] font-black text-cyan-500">{milestone.count}X</span>
    </div>
    <div className="flex flex-col">
       <span className="text-[10px] text-white font-black">{milestone.pb || '--:--'}</span>
       <span className="text-[7px] font-bold text-slate-600 uppercase">Personal_Best</span>
    </div>
  </div>
);

const CalendarSession = ({ session, isNext }: { session: TrainingSession, isNext: boolean }) => (
  <div className={`p-3 rounded-xl border transition-all ${isNext ? 'bg-cyan-500/10 border-cyan-500 shadow-[0_0_15px_rgba(34,211,238,0.2)]' : 'bg-slate-900 border-slate-800'}`}>
    <div className="flex justify-between items-center mb-1">
      <span className="text-[8px] font-black uppercase text-slate-500">{new Date(session.date).toLocaleDateString(undefined, { weekday: 'short', day: 'numeric' })}</span>
      {isNext && <span className="text-[7px] bg-cyan-500 text-black font-black px-1 rounded">NEXT</span>}
    </div>
    <h4 className={`text-[10px] font-black uppercase truncate ${isNext ? 'text-cyan-400' : 'text-white'}`}>{session.title}</h4>
    <p className="text-[9px] text-slate-400 mt-1 line-clamp-2">{session.description}</p>
    {session.distance && <div className="mt-2 text-[8px] font-bold text-slate-500 uppercase">{session.distance} • {session.targetPace}</div>}
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

  const securedFetch = useCallback(async (url: string, options: RequestInit = {}) => {
    const headers = new Headers(options.headers || {});
    headers.set('X-StravAI-Secret', backendSecret);
    return fetch(url, { ...options, headers });
  }, [backendSecret]);

  const fetchProfile = useCallback(async () => {
    if (!backendUrl) return;
    try {
      const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/profile`);
      if (res.ok) setProfile(await res.json());
    } catch {}
  }, [backendUrl, securedFetch]);

  const fetchLogs = useCallback(async () => {
    if (!backendUrl) return;
    try {
      const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/logs`);
      if (res.ok) setLogs(await res.json());
    } catch {}
  }, [backendUrl, securedFetch]);

  useEffect(() => {
    if (backendUrl) {
      const check = async () => {
        try {
          const res = await fetch(`${backendUrl.trim().replace(/\/$/, '')}/health`);
          setBackendStatus(res.ok ? 'ONLINE' : 'OFFLINE');
          if (res.ok) { fetchProfile(); fetchLogs(); }
        } catch { setBackendStatus('OFFLINE'); }
      };
      check();
      const int = setInterval(check, auditPending ? 5000 : 30000);
      return () => clearInterval(int);
    }
  }, [backendUrl, auditPending, fetchProfile, fetchLogs]);

  const handleAudit = async () => {
    setAuditPending(true);
    await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/audit`, { method: 'POST' });
  };

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[11px]">
      <header className="px-6 py-4 bg-slate-900 border-b border-slate-800 flex justify-between items-center shrink-0">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-8 h-8" />
          <div>
            <h1 className="text-white font-black uppercase text-xs tracking-tighter">StravAI_TMS_v2</h1>
            <div className={`text-[8px] font-bold uppercase ${backendStatus === 'ONLINE' ? 'text-cyan-400' : 'text-red-500'}`}>{backendStatus}</div>
          </div>
        </div>
        <div className="flex gap-6 w-1/3">
          <QuotaProgressBar label="Strava_API" used={profile?.stravaQuota?.dailyUsed || 0} limit={profile?.stravaQuota?.dailyLimit || 1000} color="bg-orange-500" />
          <QuotaProgressBar label="Intelligence_API" used={profile?.geminiQuota?.dailyUsed || 0} limit={profile?.geminiQuota?.dailyLimit || 1500} color="bg-cyan-500" />
        </div>
        <button onClick={() => setShowSetup(true)} className="p-2 border border-slate-800 rounded hover:bg-slate-800"><svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066c-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z"/><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"/></svg></button>
      </header>

      <div className="flex-grow flex overflow-hidden">
        <nav className="w-16 border-r border-slate-800 bg-slate-900/40 flex flex-col items-center py-6 gap-8">
           <button onClick={() => setActiveTab('DASHBOARD')} className={`p-2 rounded ${activeTab === 'DASHBOARD' ? 'bg-cyan-500/10 text-cyan-400' : 'text-slate-600'}`}><svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M4 6a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2H6a2 2 0 01-2-2V6zM14 6a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2h-2a2 2 0 01-2-2V6zM4 16a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2H6a2 2 0 01-2-2v-2zM14 16a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2h-2a2 2 0 01-2-2v-2z"/></svg></button>
           <button onClick={() => setActiveTab('PLAN')} className={`p-2 rounded ${activeTab === 'PLAN' ? 'bg-cyan-500/10 text-cyan-400' : 'text-slate-600'}`}><svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z"/></svg></button>
           <button onClick={() => setActiveTab('LOGS')} className={`p-2 rounded ${activeTab === 'LOGS' ? 'bg-cyan-500/10 text-cyan-400' : 'text-slate-600'}`}><svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"/></svg></button>
        </nav>

        <main className="flex-grow overflow-y-auto p-8 custom-scroll">
          {activeTab === 'DASHBOARD' && profile && (
            <div className="space-y-10 max-w-7xl">
              <div className="grid grid-cols-1 lg:grid-cols-3 gap-8">
                <div className="lg:col-span-2 space-y-8">
                  <section className="bg-slate-900 border border-slate-800 p-6 rounded-3xl relative overflow-hidden">
                    <div className="absolute top-0 right-0 w-32 h-32 bg-cyan-500/5 blur-3xl -mr-16 -mt-16" />
                    <h3 className="text-cyan-400 font-black uppercase text-xs mb-4">Elite_Narrative_Analysis</h3>
                    <p className="text-slate-300 leading-relaxed text-[11px] font-medium">{profile.summary}</p>
                    <div className="mt-6 pt-4 border-t border-slate-800 italic text-slate-500 text-[10px]">"{profile.coachNotes}"</div>
                  </section>
                  
                  <section className="grid grid-cols-2 md:grid-cols-4 gap-4">
                    {Object.values(profile.milestones || {}).map((m, i) => <MilestoneCard key={i} milestone={m as any} />)}
                  </section>
                </div>

                <div className="space-y-6">
                   <div className="bg-slate-900 border border-slate-800 p-6 rounded-3xl space-y-4">
                      <h4 className="text-white font-black uppercase text-[10px] tracking-widest">Triathlon_Log</h4>
                      <div className="space-y-3">
                         {Object.entries(profile.triathlon || {}).map(([key, val]) => (
                           <div key={key} className="flex justify-between border-b border-slate-800 pb-2 last:border-0">
                              <span className="text-[8px] font-bold text-slate-500 uppercase">{key}</span>
                              <span className="text-[10px] font-black text-white">{val as any}</span>
                           </div>
                         ))}
                      </div>
                   </div>
                   <button onClick={handleAudit} disabled={auditPending} className="w-full py-4 bg-slate-800 border border-slate-700 rounded-2xl text-[10px] font-black uppercase hover:bg-slate-700 transition-all text-amber-400 tracking-widest">{auditPending ? 'AUDITING...' : 'Re-Generate_Training_Plan'}</button>
                </div>
              </div>
            </div>
          )}

          {activeTab === 'PLAN' && profile && (
            <div className="space-y-6">
               <h2 className="text-white font-black uppercase tracking-tighter">Current_Periodization_Calendar</h2>
               <div className="grid grid-cols-1 sm:grid-cols-2 md:grid-cols-4 lg:grid-cols-7 gap-3">
                  {profile.trainingPlan?.map((s, i) => <CalendarSession key={i} session={s} isNext={i === 0} />)}
               </div>
            </div>
          )}

          {activeTab === 'LOGS' && (
            <div className="h-full bg-black/40 border border-slate-800 rounded-2xl p-6 font-mono text-[10px] overflow-y-auto custom-scroll">
               {logs.map((l, i) => <div key={i} className="mb-1 text-slate-400"><span className="opacity-30">[{i}]</span> {l}</div>)}
            </div>
          )}
        </main>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-50 bg-black/95 backdrop-blur-xl flex items-center justify-center p-6">
          <div className="bg-slate-900 border border-slate-800 rounded-3xl p-10 max-w-sm w-full space-y-6">
             <h3 className="text-white font-black uppercase text-center">System_Initialization</h3>
             <div className="space-y-4">
                <input type="text" placeholder="BACKEND URL" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 p-4 rounded-xl text-xs font-mono outline-none focus:border-cyan-500 transition-colors" />
                <input type="password" placeholder="SYSTEM SECRET" value={backendSecret} onChange={e => setBackendSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 p-4 rounded-xl text-xs font-mono outline-none focus:border-cyan-500 transition-colors" />
             </div>
             <button onClick={() => { localStorage.setItem('stravai_backend_url', backendUrl); localStorage.setItem('stravai_backend_secret', backendSecret); setShowSetup(false); }} className="w-full py-4 bg-cyan-600 text-white font-black rounded-2xl uppercase text-[10px] tracking-widest hover:bg-cyan-500 transition-all shadow-[0_10px_30px_rgba(34,211,238,0.2)]">Establish_Uplink</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
