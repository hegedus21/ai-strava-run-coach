
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

const MilestoneCard = ({ milestone, category }: { milestone: RunCategory, category: string }) => (
  <div className="bg-slate-900/60 border border-slate-800 p-4 rounded-2xl hover:border-cyan-500/30 transition-all group">
    <div className="flex justify-between items-start mb-2">
      <span className="text-[9px] font-black uppercase text-slate-500 tracking-widest group-hover:text-cyan-400 transition-colors">{category}</span>
      <span className="text-[11px] font-black text-white bg-slate-800 px-2 py-0.5 rounded-full">{milestone.count}X</span>
    </div>
    <div className="flex flex-col">
       <span className="text-sm text-white font-black">{milestone.pb || 'N/A'}</span>
       <span className="text-[7px] font-bold text-slate-600 uppercase tracking-tighter">Personal_Best</span>
    </div>
  </div>
);

const CalendarSession = ({ session, isNext }: { session: TrainingSession, isNext: boolean }) => (
  <div className={`p-4 rounded-2xl border transition-all ${isNext ? 'bg-cyan-500/10 border-cyan-500 shadow-[0_0_20px_rgba(34,211,238,0.15)] ring-1 ring-cyan-500/20' : 'bg-slate-900/40 border-slate-800'}`}>
    <div className="flex justify-between items-center mb-2">
      <span className="text-[9px] font-black uppercase text-slate-500">{new Date(session.date).toLocaleDateString(undefined, { weekday: 'short', month: 'short', day: 'numeric' })}</span>
      {isNext && <span className="text-[8px] bg-cyan-500 text-black font-black px-2 py-0.5 rounded tracking-widest">NEXT</span>}
    </div>
    <div className="flex items-center gap-2 mb-1">
        <span className={`w-2 h-2 rounded-full ${session.type === 'Interval' ? 'bg-red-500' : session.type === 'Tempo' ? 'bg-orange-500' : session.type === 'Long Run' ? 'bg-blue-500' : session.type === 'Gym' ? 'bg-purple-500' : 'bg-emerald-500'}`} />
        <h4 className={`text-[11px] font-black uppercase truncate ${isNext ? 'text-cyan-400' : 'text-white'}`}>{session.title}</h4>
    </div>
    <p className="text-[10px] text-slate-400 mt-2 leading-relaxed line-clamp-3">{session.description}</p>
    
    {(session.distance || session.targetPace) && (
      <div className="mt-4 pt-3 border-t border-slate-800/50 flex gap-4">
        {session.distance && (
            <div>
                <p className="text-[7px] font-black text-slate-600 uppercase tracking-tighter">Dist</p>
                <p className="text-[10px] font-bold text-slate-300">{session.distance}</p>
            </div>
        )}
        {session.targetPace && (
            <div>
                <p className="text-[7px] font-black text-slate-600 uppercase tracking-tighter">Pace</p>
                <p className="text-[10px] font-bold text-slate-300">{session.targetPace}</p>
            </div>
        )}
      </div>
    )}
  </div>
);

const App: React.FC = () => {
  const [backendUrl, setBackendUrl] = useState<string>(localStorage.getItem('stravai_backend_url') || '');
  const [backendSecret, setBackendSecret] = useState<string>(localStorage.getItem('stravai_backend_secret') || '');
  const [profile, setProfile] = useState<AthleteProfile | null>(null);
  const [logs, setLogs] = useState<string[]>([]);
  const [activeTab, setActiveTab] = useState<'DASHBOARD' | 'PLAN' | 'LOGS'>('DASHBOARD');
  const [backendStatus, setBackendStatus] = useState<'OFFLINE' | 'ONLINE'>('OFFLINE');
  const [webhookStatus, setWebhookStatus] = useState<'IDLE' | 'ACTIVE' | 'ERROR'>('IDLE');
  const [auditPending, setAuditPending] = useState(false);
  const [showSetup, setShowSetup] = useState(!backendUrl);
  const [loadingProfile, setLoadingProfile] = useState(false);
  const [registeringWebhook, setRegisteringWebhook] = useState(false);

  const securedFetch = useCallback(async (url: string, options: RequestInit = {}) => {
    const headers = new Headers(options.headers || {});
    headers.set('X-StravAI-Secret', backendSecret);
    return fetch(url, { ...options, headers });
  }, [backendSecret]);

  const fetchProfile = useCallback(async () => {
    if (!backendUrl) return;
    setLoadingProfile(true);
    try {
      const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/profile`);
      if (res.ok) setProfile(await res.json());
      else if (res.status === 404) setProfile(null);
    } catch {
        setProfile(null);
    } finally {
      setLoadingProfile(false);
    }
  }, [backendUrl, securedFetch]);

  const fetchLogs = useCallback(async () => {
    if (!backendUrl) return;
    try {
      const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/logs`);
      if (res.ok) setLogs(await res.json());
    } catch {}
  }, [backendUrl, securedFetch]);

  const checkWebhook = useCallback(async () => {
    if (!backendUrl || backendStatus === 'OFFLINE') return;
    try {
        const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/webhook/status`);
        if (res.ok) {
            const data = await res.json();
            setWebhookStatus(data && data.length > 0 ? 'ACTIVE' : 'IDLE');
        } else {
            setWebhookStatus('ERROR');
        }
    } catch {
        setWebhookStatus('ERROR');
    }
  }, [backendUrl, backendStatus, securedFetch]);

  const registerWebhook = async () => {
    if (!backendUrl) return;
    setRegisteringWebhook(true);
    try {
        const callbackUrl = `${backendUrl.trim().replace(/\/$/, '')}/webhook`;
        const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/webhook/register?callbackUrl=${encodeURIComponent(callbackUrl)}`, { method: 'POST' });
        if (res.ok) {
            await checkWebhook();
            fetchLogs();
        } else {
            alert('Webhook registration failed. Check backend logs.');
        }
    } catch (e: any) {
        alert('Error: ' + e.message);
    } finally {
        setRegisteringWebhook(false);
    }
  };

  // Initial Data Fetch
  useEffect(() => {
    if (backendUrl && backendStatus === 'ONLINE') {
        fetchProfile();
        fetchLogs();
        checkWebhook();
    }
  }, [backendUrl, backendStatus]);

  // Health Only Polling
  useEffect(() => {
    if (backendUrl) {
      const check = async () => {
        try {
          const res = await fetch(`${backendUrl.trim().replace(/\/$/, '')}/health`);
          setBackendStatus(res.ok ? 'ONLINE' : 'OFFLINE');
        } catch { 
            setBackendStatus('OFFLINE'); 
        }
      };
      check();
      const int = setInterval(check, 15000);
      return () => clearInterval(int);
    }
  }, [backendUrl]);

  // Temporary Log Polling during Audit
  useEffect(() => {
    let int: any = null;
    if (auditPending) {
        int = setInterval(fetchLogs, 3000);
    }
    return () => { if (int) clearInterval(int); };
  }, [auditPending, fetchLogs]);

  const handleAudit = async () => {
    if (auditPending || backendStatus === 'OFFLINE') return;
    setAuditPending(true);
    try {
        await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/audit`, { method: 'POST' });
        setTimeout(async () => {
            await fetchProfile();
            setAuditPending(false);
        }, 25000);
    } catch {
        setAuditPending(false);
    }
  };

  const handleTabChange = (tab: 'DASHBOARD' | 'PLAN' | 'LOGS') => {
    setActiveTab(tab);
    if (tab === 'LOGS') fetchLogs();
    if (tab === 'DASHBOARD' && !profile) fetchProfile();
  };

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[11px]">
      <header className="px-6 py-4 bg-slate-900 border-b border-slate-800 flex justify-between items-center shrink-0">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-8 h-8" />
          <div>
            <h1 className="text-white font-black uppercase text-xs tracking-tighter">StravAI_TMS_v2.3</h1>
            <div className="flex gap-3">
                <div className={`text-[8px] font-bold uppercase flex items-center gap-1.5 ${backendStatus === 'ONLINE' ? 'text-cyan-400' : 'text-red-500'}`}>
                    <span className={`w-1.5 h-1.5 rounded-full ${backendStatus === 'ONLINE' ? 'bg-cyan-500 animate-pulse' : 'bg-red-500'}`} />
                    SRV:{backendStatus}
                </div>
                <div className={`text-[8px] font-bold uppercase flex items-center gap-1.5 ${webhookStatus === 'ACTIVE' ? 'text-emerald-400' : webhookStatus === 'ERROR' ? 'text-red-500' : 'text-amber-500'}`}>
                    <span className={`w-1.5 h-1.5 rounded-full ${webhookStatus === 'ACTIVE' ? 'bg-emerald-500' : 'bg-amber-500'}`} />
                    WEBHOOK:{webhookStatus}
                </div>
            </div>
          </div>
        </div>
        <div className="hidden md:flex gap-8 w-1/2">
          <QuotaProgressBar label="System_Sync" used={profile ? 35 : 0} limit={1000} color="bg-orange-600" />
          <QuotaProgressBar label="AI_Inference" used={profile ? 1 : 0} limit={1500} color="bg-cyan-500" />
        </div>
        <button onClick={() => setShowSetup(true)} className="p-2 border border-slate-800 rounded-xl hover:bg-slate-800 transition-colors"><svg className="w-4 h-4" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M10.325 4.317c.426-1.756 2.924-1.756 3.35 0a1.724 1.724 0 002.573 1.066c1.543-.94 3.31.826 2.37 2.37a1.724 1.724 0 001.065 2.572c1.756.426 1.756 2.924 0 3.35a1.724 1.724 0 00-1.066 2.573c.94 1.543-.826 3.31-2.37 2.37a1.724 1.724 0 00-2.572 1.065c-.426 1.756-2.924 1.756-3.35 0a1.724 1.724 0 00-2.573-1.066-1.543.94-3.31-.826-2.37-2.37a1.724 1.724 0 00-1.065-2.572c-1.756-.426-1.756-2.924 0-3.35a1.724 1.724 0 001.066-2.573c-.94-1.543.826-3.31 2.37-2.37.996.608 2.296.07 2.572-1.065z"/><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M15 12a3 3 0 11-6 0 3 3 0 016 0z"/></svg></button>
      </header>

      <div className="flex-grow flex overflow-hidden">
        <nav className="w-20 border-r border-slate-800 bg-slate-900/40 flex flex-col items-center py-8 gap-8 shrink-0">
           <div className="flex flex-col gap-4">
               <button onClick={() => handleTabChange('DASHBOARD')} className={`p-3 rounded-2xl transition-all ${activeTab === 'DASHBOARD' ? 'bg-cyan-500/10 text-cyan-400 border border-cyan-500/20 shadow-[0_0_15px_rgba(34,211,238,0.1)]' : 'text-slate-600 hover:text-slate-400'}`} title="Dashboard">
                 <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M4 6a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2H6a2 2 0 01-2-2V6zM14 6a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2h-2a2 2 0 01-2-2V6zM4 16a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2H6a2 2 0 01-2-2v-2zM14 16a2 2 0 012-2h2a2 2 0 012 2v2a2 2 0 01-2 2h-2a2 2 0 01-2-2v-2z"/></svg>
               </button>
               <button onClick={() => handleTabChange('PLAN')} className={`p-3 rounded-2xl transition-all ${activeTab === 'PLAN' ? 'bg-cyan-500/10 text-cyan-400 border border-cyan-500/20 shadow-[0_0_15px_rgba(34,211,238,0.1)]' : 'text-slate-600 hover:text-slate-400'}`} title="Training Plan">
                 <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M8 7V3m8 4V3m-9 8h10M5 21h14a2 2 0 002-2V7a2 2 0 00-2-2H5a2 2 0 00-2 2v12a2 2 0 002 2z"/></svg>
               </button>
               <button onClick={() => handleTabChange('LOGS')} className={`p-3 rounded-2xl transition-all ${activeTab === 'LOGS' ? 'bg-cyan-500/10 text-cyan-400 border border-cyan-500/20 shadow-[0_0_15px_rgba(34,211,238,0.1)]' : 'text-slate-600 hover:text-slate-400'}`} title="System Logs">
                 <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M9 12h6m-6 4h6m2 5H7a2 2 0 01-2-2V5a2 2 0 012-2h5.586a1 1 0 01.707.293l5.414 5.414a1 1 0 01.293.707V19a2 2 0 01-2 2z"/></svg>
               </button>
           </div>

           <div className="mt-auto flex flex-col items-center gap-6 pb-4">
               <div className="h-px w-8 bg-slate-800" />
               <button 
                onClick={handleAudit} 
                disabled={auditPending || backendStatus === 'OFFLINE'} 
                className={`group relative p-3 rounded-2xl border transition-all ${auditPending ? 'bg-amber-500/20 border-amber-500/40 text-amber-500 animate-pulse' : 'bg-slate-800 border-slate-700 text-slate-500 hover:text-cyan-400 hover:border-cyan-500/50'}`}
                title="Trigger System Audit"
               >
                 {auditPending ? (
                   <div className="w-6 h-6 border-2 border-amber-500/30 border-t-amber-500 rounded-full animate-spin" />
                 ) : (
                   <svg className="w-6 h-6" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M4 4v5h.582m15.356 2A8.001 8.001 0 004.582 9m0 0H9m11 11v-5h-.581m0 0a8.003 8.003 0 01-15.357-2m15.357 2H15"/></svg>
                 )}
               </button>
           </div>
        </nav>

        <main className="flex-grow overflow-y-auto p-10 custom-scroll bg-slate-950/50">
          {activeTab === 'DASHBOARD' && (
            <div className="max-w-7xl mx-auto">
              {loadingProfile ? (
                 <div className="flex flex-col items-center justify-center py-20 text-center">
                    <div className="w-10 h-10 border-2 border-cyan-500/20 border-t-cyan-500 rounded-full animate-spin mb-4" />
                    <p className="text-slate-600 font-black uppercase text-[10px] tracking-widest">Hydrating_System_Intelligence...</p>
                 </div>
              ) : !profile ? (
                <div className="flex flex-col items-center justify-center py-20 text-center space-y-8">
                    <div className="w-24 h-24 bg-slate-900 border border-slate-800 rounded-full flex items-center justify-center text-slate-700">
                        <svg className="w-12 h-12" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="1.5" d="M19 11H5m14 0a2 2 0 012 2v6a2 2 0 01-2 2H5a2 2 0 01-2-2v-6a2 2 0 012-2m14 0V9a2 2 0 00-2-2M5 11V9a2 2 0 012-2m0 0V5a2 2 0 012-2h6a2 2 0 012 2v2M7 7h10"/></svg>
                    </div>
                    <div className="space-y-2">
                        <h2 className="text-white font-black uppercase text-xl tracking-tighter">Profile_Empty</h2>
                        <p className="text-slate-500 max-w-md mx-auto text-[10px] leading-relaxed font-bold uppercase tracking-widest">Connect your Strava and run an audit to generate your coaching profile.</p>
                    </div>
                    <button 
                        onClick={handleAudit} 
                        disabled={auditPending || backendStatus === 'OFFLINE'}
                        className="px-10 py-5 bg-cyan-600 text-white font-black rounded-3xl uppercase text-[11px] tracking-[0.2em] hover:bg-cyan-500 transition-all shadow-[0_15px_40px_rgba(34,211,238,0.2)] disabled:opacity-50"
                    >
                        {auditPending ? 'Analyzing_History...' : 'Initialize_Audit'}
                    </button>
                </div>
              ) : (
                <div className="space-y-12">
                    <div className="grid grid-cols-1 lg:grid-cols-4 gap-10">
                        <div className="lg:col-span-3 space-y-10">
                        <section className="bg-gradient-to-br from-slate-900 to-slate-950 border border-slate-800 p-10 rounded-[3rem] relative overflow-hidden shadow-2xl">
                            <div className="absolute top-0 right-0 w-64 h-64 bg-cyan-500/5 blur-[120px] -mr-32 -mt-32" />
                            <h3 className="text-cyan-400 font-black uppercase text-xs mb-6 tracking-widest flex items-center gap-2">
                                <span className="w-4 h-0.5 bg-cyan-500" />
                                Coach_Intelligence_Aggregate
                            </h3>
                            <p className="text-slate-200 leading-relaxed text-[13px] font-medium">{profile.summary}</p>
                            <div className="mt-8 pt-6 border-t border-slate-800/50 italic text-slate-500 text-[11px] flex justify-between items-center">
                                <span>"{profile.coachNotes}"</span>
                                <span className="text-[9px] font-black uppercase text-slate-700">UPDATED: {profile.lastUpdated}</span>
                            </div>
                        </section>
                        
                        <section>
                            <h2 className="text-[10px] font-black uppercase text-slate-500 mb-6 tracking-[0.3em] px-2">Historical_Milestones</h2>
                            <div className="grid grid-cols-2 md:grid-cols-4 gap-4">
                            {Object.entries(profile.milestones || {}).map(([key, m], i) => (
                                <MilestoneCard key={i} category={key.replace(/([A-Z])/g, ' $1').trim()} milestone={m as any} />
                            ))}
                            </div>
                        </section>
                        </div>

                        <div className="space-y-8">
                            <div className="bg-slate-900/60 border border-slate-800 p-8 rounded-[2rem] space-y-6">
                                <h4 className="text-white font-black uppercase text-[10px] tracking-[0.2em] mb-4">Metric_Trends</h4>
                                <div className="space-y-4">
                                    <div className="flex justify-between text-[11px]">
                                        <span className="text-slate-500 font-bold uppercase">Week_Km</span>
                                        <span className="text-white font-black">{profile.periodic?.week?.distanceKm?.toFixed(1) || '0.0'}</span>
                                    </div>
                                    <div className="flex justify-between text-[11px]">
                                        <span className="text-slate-500 font-bold uppercase">Month_Km</span>
                                        <span className="text-white font-black">{profile.periodic?.month?.distanceKm?.toFixed(1) || '0.0'}</span>
                                    </div>
                                </div>
                            </div>
                        </div>
                    </div>
                </div>
              )}
            </div>
          )}

          {activeTab === 'PLAN' && profile && (
            <div className="space-y-10 max-w-7xl mx-auto">
               <div className="flex justify-between items-end">
                  <h2 className="text-2xl font-black text-white uppercase tracking-tighter">Training_Plan</h2>
               </div>
               <div className="grid grid-cols-1 sm:grid-cols-2 lg:grid-cols-3 xl:grid-cols-4 gap-6">
                  {profile.trainingPlan?.map((s, i) => <CalendarSession key={i} session={s} isNext={i === 0} />)}
               </div>
            </div>
          )}

          {activeTab === 'LOGS' && (
            <div className="h-full bg-slate-900/20 border border-slate-800 rounded-[2rem] p-8 font-mono text-[10px] overflow-y-auto custom-scroll">
               <div className="flex justify-between items-center mb-6 border-b border-slate-800 pb-4">
                    <h4 className="text-slate-500 font-black uppercase tracking-widest">Event_Stream</h4>
                    <button onClick={fetchLogs} className="text-[8px] font-black text-cyan-500 uppercase hover:text-cyan-400">Fetch_Manual_Update</button>
               </div>
               {logs.length === 0 ? (
                 <p className="text-slate-700 italic">No events recorded in session.</p>
               ) : (
                 logs.slice().reverse().map((l, i) => (
                   <div key={i} className={`mb-2 py-1 px-3 rounded ${l.includes('[ERROR]') ? 'text-red-400 bg-red-500/5' : l.includes('[SUCCESS]') ? 'text-cyan-400 bg-cyan-500/5' : 'text-slate-400'}`}>
                     {l}
                   </div>
                 ))
               )}
            </div>
          )}
        </main>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-[100] bg-black/90 backdrop-blur-3xl flex items-center justify-center p-8 overflow-y-auto">
          <div className="bg-slate-900 border border-slate-800 rounded-[3rem] p-12 max-w-md w-full space-y-8 shadow-[0_0_100px_rgba(34,211,238,0.05)] my-auto">
             <div className="text-center space-y-2">
                <h3 className="text-white font-black uppercase tracking-tighter text-xl">Initialization</h3>
                <p className="text-[9px] text-slate-500 uppercase tracking-widest font-black">Configure_System_Uplink</p>
             </div>
             
             <div className="space-y-4">
                <div className="space-y-2">
                    <label className="text-[8px] font-black text-slate-600 uppercase tracking-widest ml-1">Service_Endpoint</label>
                    <input type="text" placeholder="https://your-backend.koyeb.app" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 p-4 rounded-2xl text-xs font-mono outline-none focus:border-cyan-500 transition-colors" />
                </div>
                <div className="space-y-2">
                    <label className="text-[8px] font-black text-slate-600 uppercase tracking-widest ml-1">System_Secret</label>
                    <input type="password" placeholder="X-StravAI-Secret" value={backendSecret} onChange={e => setBackendSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 p-4 rounded-2xl text-xs font-mono outline-none focus:border-cyan-500 transition-colors" />
                </div>
             </div>

             <div className="p-4 bg-slate-950 border border-slate-800 rounded-2xl space-y-4 text-[9px] text-slate-500 leading-relaxed font-bold">
                <h4 className="text-cyan-500 font-black uppercase tracking-widest flex items-center gap-2 mb-1">
                    <svg className="w-3 h-3" fill="none" stroke="currentColor" viewBox="0 0 24 24"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth="2" d="M13 16h-1v-4h-1m1-4h.01M21 12a9 9 0 11-18 0 9 9 0 0118 0z"/></svg>
                    System_Architecture_Guide
                </h4>
                <p>1. <span className="text-slate-300">SYSTEM_CACHE:</span> We use a private Strava activity to store your athlete profile. This replaces a paid database.</p>
                <p>2. <span className="text-slate-300">WEBHOOKS:</span> Strava sends a signal here when you upload a run. AI then analyzes it instantly.</p>
                <p>3. <span className="text-slate-300">KEYS:</span> Ensure <code className="text-cyan-400">GEMINI_API_KEY</code> is set in your host env vars.</p>
             </div>

             <div className="p-4 bg-slate-950 border border-slate-800 rounded-2xl space-y-4">
                <div className="flex justify-between items-center">
                    <span className="text-[8px] font-black text-slate-500 uppercase tracking-widest">Strava_Webhook</span>
                    <span className={`text-[8px] font-black px-2 py-0.5 rounded ${webhookStatus === 'ACTIVE' ? 'bg-emerald-500/10 text-emerald-400' : 'bg-amber-500/10 text-amber-400'}`}>{webhookStatus}</span>
                </div>
                {webhookStatus !== 'ACTIVE' && (
                    <button 
                        onClick={registerWebhook} 
                        disabled={registeringWebhook || backendStatus === 'OFFLINE'} 
                        className="w-full py-2 bg-slate-800 hover:bg-slate-700 text-[10px] font-black uppercase tracking-widest rounded-xl transition-all disabled:opacity-50"
                    >
                        {registeringWebhook ? 'Registering...' : 'Fix_Webhook_Registration'}
                    </button>
                )}
             </div>

             <button onClick={() => { localStorage.setItem('stravai_backend_url', backendUrl); localStorage.setItem('stravai_backend_secret', backendSecret); setShowSetup(false); }} className="w-full py-5 bg-cyan-600 text-white font-black rounded-2xl uppercase text-[11px] tracking-[0.2em] hover:bg-cyan-500 shadow-[0_10px_30px_rgba(34,211,238,0.2)]">Connect_&_Verify</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
