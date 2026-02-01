
import { useState, useEffect, useCallback, useRef } from 'react';
import React from 'react';
import { StravAILogo } from './components/Icon';
import { AthleteProfile, HeartRateZones, QuotaStatus } from './types';

const ZoneBar = ({ zones, label, pulse }: { zones?: HeartRateZones, label: string, pulse?: boolean }) => {
  const z = zones || { z1: 0, z2: 0, z3: 0, z4: 0, z5: 0 };
  const total = z.z1 + z.z2 + z.z3 + z.z4 + z.z5 || 100;
  return (
    <div className={`space-y-2 transition-all duration-700 ${pulse ? 'ring-2 ring-cyan-500/50 rounded-lg p-1 -m-1' : ''}`}>
      <div className="flex justify-between items-center text-[9px] font-black uppercase tracking-widest">
        <span className="text-slate-500">{label}</span>
      </div>
      <div className="h-3 w-full flex rounded-full overflow-hidden border border-slate-800 bg-slate-900 shadow-inner">
        <div style={{ width: `${(z.z1/total)*100}%` }} className="bg-blue-500 h-full" title="Z1" />
        <div style={{ width: `${(z.z2/total)*100}%` }} className="bg-emerald-500 h-full" title="Z2" />
        <div style={{ width: `${(z.z3/total)*100}%` }} className="bg-yellow-500 h-full" title="Z3" />
        <div style={{ width: `${(z.z4/total)*100}%` }} className="bg-orange-500 h-full" title="Z4" />
        <div style={{ width: `${(z.z5/total)*100}%` }} className="bg-red-500 h-full" title="Z5" />
      </div>
    </div>
  );
};

const LogViewer = ({ logs }: { logs: string[] }) => {
  const scrollRef = useRef<HTMLDivElement>(null);
  useEffect(() => {
    if (scrollRef.current) {
      scrollRef.current.scrollTop = scrollRef.current.scrollHeight;
    }
  }, [logs]);

  return (
    <div ref={scrollRef} className="h-full bg-black/40 rounded-xl border border-slate-800 p-4 font-mono text-[10px] overflow-y-auto custom-scroll">
      {logs.length === 0 && <p className="text-slate-700 italic">Listening for system events...</p>}
      {logs.map((log, i) => {
        const isError = log.includes('[ERROR]');
        const isWarn = log.includes('[WARN]');
        const isSuccess = log.includes('SUCCESS');
        return (
          <div key={i} className={`py-0.5 border-b border-slate-900/50 ${isError ? 'text-red-400' : isWarn ? 'text-amber-400' : isSuccess ? 'text-cyan-400 font-bold' : 'text-slate-400'}`}>
            <span className="opacity-30 mr-2">[{i.toString().padStart(3, '0')}]</span>
            {log}
          </div>
        );
      })}
    </div>
  );
};

const QuotaMeter = ({ quota }: { quota: QuotaStatus }) => {
  const [timeLeftStr, setTimeLeftStr] = useState('Calculating...');
  const dailyPct = (quota.dailyUsed / quota.dailyLimit) * 100;
  
  useEffect(() => {
    const updateTimer = () => {
      const resetDate = new Date(quota.resetAt);
      const diff = resetDate.getTime() - Date.now();
      if (diff <= 0) { setTimeLeftStr('Ready to Reset'); return; }
      const h = Math.floor(diff / (1000 * 60 * 60));
      const m = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));
      setTimeLeftStr(`${h}h ${m}m`);
    };
    updateTimer();
    const interval = setInterval(updateTimer, 60000);
    return () => clearInterval(interval);
  }, [quota.resetAt]);

  const remaining = quota.dailyLimit - quota.dailyUsed;

  return (
    <div className="bg-slate-900 border border-slate-800 rounded-2xl p-6 space-y-6 relative overflow-hidden shadow-2xl">
      <div className="absolute top-0 right-0 w-32 h-32 bg-cyan-500/5 blur-3xl rounded-full -mr-16 -mt-16 pointer-events-none" />
      <div className="flex justify-between items-start">
        <div>
          <h3 className="text-white font-black uppercase text-xs">Intelligence_Fuel</h3>
          <p className="text-[9px] text-slate-500 font-bold mt-0.5 tracking-tight uppercase">Strava_Quota_Guard</p>
        </div>
        <div className={`px-2 py-0.5 rounded text-[8px] font-black uppercase ${quota.dailyUsed < quota.dailyLimit * 0.9 ? 'bg-cyan-500/20 text-cyan-400' : 'bg-red-500/20 text-red-400'}`}>
          {quota.dailyUsed < quota.dailyLimit * 0.9 ? 'OPTIMAL' : 'CRITICAL'}
        </div>
      </div>
      <div className="space-y-4">
        <div className="space-y-1.5">
          <div className="flex justify-between text-[9px] font-bold uppercase tracking-widest">
            <span className="text-slate-500">Daily_Credits</span>
            <span className="text-white">{remaining} LEFT</span>
          </div>
          <div className="h-1.5 w-full bg-slate-800/50 rounded-full overflow-hidden">
            <div className={`h-full transition-all duration-1000 ease-out ${dailyPct > 90 ? 'bg-red-500' : 'bg-cyan-500'}`} style={{ width: `${dailyPct}%` }} />
          </div>
        </div>
        <div className="flex justify-between border-t border-slate-800/50 pt-4">
          <div>
            <p className="text-[8px] text-slate-500 font-bold uppercase tracking-tighter">Full_Reset</p>
            <p className="text-xs font-black text-white tabular-nums">{timeLeftStr}</p>
          </div>
          <div className="text-right">
             <p className="text-[8px] text-slate-500 font-bold uppercase tracking-tighter">Mode</p>
             <p className="text-[9px] font-bold text-slate-400 uppercase tracking-widest">Adaptive_Polling</p>
          </div>
        </div>
      </div>
    </div>
  );
};

const App: React.FC = () => {
  const [backendUrl, setBackendUrl] = useState<string>(localStorage.getItem('stravai_backend_url') || '');
  const [backendSecret, setBackendSecret] = useState<string>(localStorage.getItem('stravai_backend_secret') || '');
  const [profile, setProfile] = useState<AthleteProfile | null>(null);
  const [logs, setLogs] = useState<string[]>([]);
  const [activeTab, setActiveTab] = useState<'PROFILE' | 'LOGS'>('PROFILE');
  const [backendStatus, setBackendStatus] = useState<'UNKNOWN' | 'ONLINE' | 'OFFLINE'>('UNKNOWN');
  const [auditPending, setAuditPending] = useState(false);
  const [showSetup, setShowSetup] = useState(!backendUrl || !backendSecret);
  const [notification, setNotification] = useState<{msg: string, type: 'info' | 'success' | 'error'} | null>(null);
  const [justUpdated, setJustUpdated] = useState(false);
  
  const lastUpdatedRef = useRef<string | null>(null);

  const securedFetch = useCallback(async (url: string, options: RequestInit = {}) => {
    const headers = new Headers(options.headers || {});
    headers.set('X-StravAI-Secret', backendSecret);
    return fetch(url, { ...options, headers });
  }, [backendSecret]);

  const fetchProfile = useCallback(async () => {
    if (!backendUrl) return;
    try {
      const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/profile`);
      if (res.ok) {
        const data = await res.json();
        if (lastUpdatedRef.current && data.lastUpdated !== lastUpdatedRef.current) {
          setJustUpdated(true);
          setTimeout(() => setJustUpdated(false), 3000);
        }
        lastUpdatedRef.current = data.lastUpdated;
        setProfile(data);
      }
    } catch {}
  }, [backendUrl, securedFetch]);

  const fetchLogs = useCallback(async () => {
    if (!backendUrl) return;
    try {
      const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/logs`);
      if (res.ok) {
        const newLogs: string[] = await res.json();
        
        if (auditPending && newLogs.some(l => l.includes('AUTH_ERR') || l.includes('AI_ERR: Unauthorized'))) {
          setAuditPending(false);
          setNotification({ msg: 'CRITICAL_AUTH_FAILURE: Check Backend API Keys.', type: 'error' });
          setTimeout(() => setNotification(null), 10000);
        }

        if (auditPending && newLogs.some(l => l.includes('AUDIT_SUCCESS'))) {
          setAuditPending(false);
          setNotification({ msg: 'AUDIT_COMPLETE: System Intelligence Updated.', type: 'success' });
          setTimeout(() => setNotification(null), 5000);
          fetchProfile();
        }
        setLogs(newLogs);
      }
    } catch {}
  }, [backendUrl, securedFetch, auditPending, fetchProfile]);

  const checkBackend = useCallback(async () => {
    if (!backendUrl) return;
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    try {
      const res = await fetch(`${cleanUrl}/health`);
      if (res.ok) {
        setBackendStatus('ONLINE');
        fetchProfile();
        fetchLogs();
      } else { setBackendStatus('OFFLINE'); }
    } catch { setBackendStatus('OFFLINE'); }
  }, [backendUrl, fetchProfile, fetchLogs]);

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      const intervalMs = auditPending ? 5000 : 30000;
      const int = setInterval(checkBackend, intervalMs);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend, auditPending]);

  const handleAudit = async () => {
    if (backendStatus !== 'ONLINE') return;
    setAuditPending(true);
    setNotification({ msg: 'AUDIT_START: Initiating physiological crawl...', type: 'info' });
    try {
      const res = await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/audit`, { method: 'POST' });
      if (res.status === 401) {
          setAuditPending(false);
          setNotification({ msg: 'AUTH_FAILED: Invalid System Secret.', type: 'error' });
      }
    } catch (e) {
      setAuditPending(false);
      setNotification(null);
    }
  };

  const saveConfig = () => {
    localStorage.setItem('stravai_backend_url', backendUrl);
    localStorage.setItem('stravai_backend_secret', backendSecret);
    setShowSetup(false);
    checkBackend();
  };

  return (
    <div className="flex flex-col h-screen bg-slate-950 text-slate-300 font-mono text-[11px]">
      <header className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shrink-0 shadow-lg">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-9 h-9" />
          <div>
            <h1 className="text-white font-black tracking-tighter uppercase text-sm">StravAI_Command_Center</h1>
            <div className={`flex items-center gap-2 text-[9px] uppercase font-bold mt-0.5 ${backendStatus === 'ONLINE' ? 'text-cyan-400' : 'text-red-500'}`}>
              <span className={`w-1.5 h-1.5 rounded-full ${backendStatus === 'ONLINE' ? 'bg-cyan-400 animate-pulse' : 'bg-red-500'}`}></span>
              {backendStatus} {justUpdated && '• REFRESHED'}
            </div>
          </div>
        </div>
        <button onClick={() => setShowSetup(true)} className="px-3 py-1.5 bg-slate-800 border border-slate-700 rounded text-[10px] font-bold hover:bg-slate-700 transition-colors uppercase">Auth</button>
      </header>

      <div className="flex-grow flex overflow-hidden">
        <aside className="w-64 border-r border-slate-800 bg-slate-900/40 p-6 space-y-6 overflow-y-auto shrink-0">
            <section className="space-y-4">
              <h2 className="text-[9px] font-black text-slate-600 uppercase tracking-widest">Navigation</h2>
              <button onClick={() => setActiveTab('PROFILE')} className={`w-full text-left px-3 py-2 rounded text-[10px] font-bold uppercase transition-colors ${activeTab === 'PROFILE' ? 'bg-cyan-500/10 text-cyan-400 border border-cyan-500/20' : 'text-slate-500 hover:text-slate-300'}`}>Dashboard</button>
              <button onClick={() => setActiveTab('LOGS')} className={`w-full text-left px-3 py-2 rounded text-[10px] font-bold uppercase transition-colors ${activeTab === 'LOGS' ? 'bg-cyan-500/10 text-cyan-400 border border-cyan-500/20' : 'text-slate-500 hover:text-slate-300'}`}>Logs</button>
            </section>
            <section className="pt-6 border-t border-slate-800/50">
              <h2 className="text-[9px] font-black text-slate-600 uppercase tracking-widest mb-4">Actions</h2>
              <button 
                onClick={handleAudit} 
                disabled={auditPending || backendStatus !== 'ONLINE' || (profile?.quota?.dailyUsed || 0) > 1485}
                className={`w-full py-3 rounded border font-bold uppercase text-[10px] transition-all disabled:opacity-50 ${auditPending ? 'bg-amber-500/10 border-amber-500/40 text-amber-500 animate-pulse' : 'bg-slate-800 hover:bg-slate-700 text-amber-400 border-amber-500/20'}`}
              >
                {auditPending ? 'AUDITING...' : 'Trigger_Audit'}
              </button>
            </section>
        </aside>

        <main className="flex-grow flex flex-col bg-slate-950 overflow-hidden relative">
          {notification && (
            <div className={`absolute top-6 right-6 z-50 px-4 py-3 rounded-lg border shadow-2xl animate-bounce flex items-center gap-3 ${notification.type === 'success' ? 'bg-cyan-900/90 border-cyan-500 text-cyan-100' : notification.type === 'error' ? 'bg-red-900/90 border-red-500 text-red-100' : 'bg-slate-800 border-slate-700 text-slate-300'}`}>
              <div className={`w-2 h-2 rounded-full ${notification.type === 'success' ? 'bg-cyan-400 shadow-[0_0_10px_#22d3ee]' : notification.type === 'error' ? 'bg-red-400 shadow-[0_0_10px_#f87171]' : 'bg-amber-400 animate-ping'}`} />
              <span className="text-[10px] font-bold tracking-tight uppercase">{notification.msg}</span>
            </div>
          )}

          <div className="flex-grow overflow-y-auto p-8 space-y-10 pb-24 scroll-smooth custom-scroll h-full">
            {activeTab === 'LOGS' ? (
              <LogViewer logs={logs} />
            ) : profile ? (
              <div className="max-w-6xl space-y-10">
                <div className="grid grid-cols-1 md:grid-cols-2 gap-8">
                   <QuotaMeter quota={profile.quota} />
                   <div className={`bg-slate-900 border border-slate-800 rounded-2xl p-6 relative overflow-hidden group transition-all duration-1000 ${justUpdated ? 'shadow-[0_0_30px_rgba(34,211,238,0.1)] border-cyan-500/50' : ''}`}>
                      <h3 className="text-cyan-400 font-black uppercase text-xs mb-4">Coach's_Summary</h3>
                      <p className="text-slate-400 leading-relaxed text-[11px] h-32 overflow-y-auto pr-2 custom-scroll">{profile.summary}</p>
                      <div className="mt-4 pt-4 border-t border-slate-800 italic text-slate-500 text-[10px]">
                         "{profile.coachNotes || "Keep pushing your limits."}"
                      </div>
                   </div>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
                  <div className="bg-slate-900/40 p-6 border border-slate-800 rounded-2xl space-y-4">
                    <ZoneBar zones={profile.periodic?.week?.zones} label="Weekly_Intensity" pulse={justUpdated} />
                    <p className="text-lg font-black text-white">{profile.periodic?.week?.distanceKm?.toFixed(1) || "0.0"} km</p>
                  </div>
                  <div className="bg-slate-900/40 p-6 border border-slate-800 rounded-2xl space-y-4">
                    <ZoneBar zones={profile.periodic?.month?.zones} label="Monthly_Intensity" pulse={justUpdated} />
                    <p className="text-lg font-black text-white">{profile.periodic?.month?.distanceKm?.toFixed(1) || "0.0"} km</p>
                  </div>
                  <div className="bg-slate-900/40 p-6 border border-slate-800 rounded-2xl space-y-4">
                    <ZoneBar zones={profile.periodic?.year?.zones} label="Annual_Intensity" pulse={justUpdated} />
                    <p className="text-lg font-black text-white">{profile.periodic?.year?.distanceKm?.toFixed(0) || "0"} km</p>
                  </div>
                </div>

                <section className="space-y-4">
                   <h3 className="text-white font-black uppercase text-xs tracking-widest px-1">Physiological_History</h3>
                   <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
                      {profile.yearlyHistory?.map(y => (
                        <div key={y.year} className="bg-slate-900/50 p-4 border border-slate-800 rounded-xl space-y-4 hover:border-slate-700 transition-colors shadow-inner">
                           <div className="flex justify-between items-center">
                              <span className="text-sm font-black text-white">{y.year}</span>
                              <span className="text-[10px] font-black text-cyan-500 uppercase">{y.activityCount || 0} runs</span>
                           </div>
                           <ZoneBar zones={y.zones} label={`Career_Split`} />
                        </div>
                      ))}
                   </div>
                </section>
              </div>
            ) : (
              <div className="h-64 flex flex-col items-center justify-center border-2 border-dashed border-slate-800 rounded-3xl opacity-50">
                <p className="text-slate-600 font-black uppercase tracking-widest mb-4">System_Bus_Idle: No Profile Detected</p>
                <button onClick={handleAudit} className="px-6 py-2 bg-slate-800 hover:bg-slate-700 text-white rounded-lg text-[10px] font-bold uppercase transition-colors">Start_Initial_Audit</button>
              </div>
            )}
          </div>
        </main>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-[100] flex items-center justify-center bg-black/90 backdrop-blur-md">
          <div className="bg-slate-900 border border-slate-800 rounded-2xl w-full max-w-sm p-10 space-y-6 shadow-2xl">
            <h2 className="text-white font-black uppercase text-center">Engine_Access_Auth</h2>
            <div className="space-y-4">
              <input type="text" placeholder="Backend URL" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-4 text-xs outline-none focus:border-cyan-500 font-mono transition-colors"/>
              <input type="password" placeholder="System Secret" value={backendSecret} onChange={e => setBackendSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-4 text-xs outline-none focus:border-cyan-500 font-mono transition-colors"/>
            </div>
            <button onClick={saveConfig} className="w-full py-4 bg-cyan-600 hover:bg-cyan-500 text-white rounded font-bold uppercase text-[10px] transition-all tracking-widest">Establish_Uplink</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
