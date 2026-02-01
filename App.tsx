
import { useState, useEffect, useCallback } from 'react';
import React from 'react';
import { StravAILogo } from './components/Icon';
import { AthleteProfile, HeartRateZones, QuotaStatus } from './types';

const ZoneBar = ({ zones, label }: { zones: HeartRateZones, label: string }) => {
  const total = zones.z1 + zones.z2 + zones.z3 + zones.z4 + zones.z5 || 100;
  return (
    <div className="space-y-2">
      <div className="flex justify-between items-center text-[9px] font-black uppercase tracking-widest">
        <span className="text-slate-500">{label}</span>
      </div>
      <div className="h-3 w-full flex rounded-full overflow-hidden border border-slate-800 bg-slate-900 shadow-inner">
        <div style={{ width: `${(zones.z1/total)*100}%` }} className="bg-blue-500 h-full" title="Z1" />
        <div style={{ width: `${(zones.z2/total)*100}%` }} className="bg-emerald-500 h-full" title="Z2" />
        <div style={{ width: `${(zones.z3/total)*100}%` }} className="bg-yellow-500 h-full" title="Z3" />
        <div style={{ width: `${(zones.z4/total)*100}%` }} className="bg-orange-500 h-full" title="Z4" />
        <div style={{ width: `${(zones.z5/total)*100}%` }} className="bg-red-500 h-full" title="Z5" />
      </div>
    </div>
  );
};

const QuotaMeter = ({ quota }: { quota: QuotaStatus }) => {
  const [timeLeftStr, setTimeLeftStr] = useState('Calculating...');
  const dailyPct = (quota.dailyUsed / quota.dailyLimit) * 100;
  const minutePct = (quota.minuteUsed / quota.minuteLimit) * 100;
  
  useEffect(() => {
    const updateTimer = () => {
      const resetDate = new Date(quota.resetAt);
      const diff = resetDate.getTime() - Date.now();
      
      if (diff <= 0) {
        setTimeLeftStr('Ready to Reset');
        return;
      }

      const h = Math.floor(diff / (1000 * 60 * 60));
      const m = Math.floor((diff % (1000 * 60 * 60)) / (1000 * 60));
      const s = Math.floor((diff % (1000 * 60)) / 1000);
      setTimeLeftStr(`${h}h ${m}m ${s}s`);
    };

    updateTimer();
    const interval = setInterval(updateTimer, 1000);
    return () => clearInterval(interval);
  }, [quota.resetAt]);

  const remaining = quota.dailyLimit - quota.dailyUsed;

  return (
    <div className="bg-slate-900 border border-slate-800 rounded-2xl p-6 space-y-6 relative overflow-hidden">
      <div className="absolute top-0 right-0 w-32 h-32 bg-cyan-500/5 blur-3xl rounded-full -mr-16 -mt-16 pointer-events-none" />

      <div className="flex justify-between items-start relative">
        <div>
          <h3 className="text-white font-black uppercase text-xs">Intelligence_Fuel_Gauge</h3>
          <p className="text-[9px] text-slate-500 font-bold mt-0.5 tracking-tight">GEMINI 2.0 FLASH • FREE TIER</p>
        </div>
        <div className={`px-2 py-0.5 rounded text-[8px] font-black uppercase ${quota.dailyUsed < quota.dailyLimit * 0.9 ? 'bg-cyan-500/20 text-cyan-400' : 'bg-red-500/20 text-red-400'}`}>
          {quota.dailyUsed < quota.dailyLimit * 0.9 ? 'SYSTEMS_OPTIMAL' : 'CAPACITY_CRITICAL'}
        </div>
      </div>

      <div className="space-y-5">
        <div className="space-y-1.5">
          <div className="flex justify-between text-[9px] font-bold uppercase tracking-widest">
            <span className="text-slate-500">Daily_Credits_Remaining</span>
            <span className="text-white">{remaining} / {quota.dailyLimit}</span>
          </div>
          <div className="h-2 w-full bg-slate-800/50 rounded-full overflow-hidden border border-slate-800">
            <div className={`h-full transition-all duration-1000 ease-out ${dailyPct > 90 ? 'bg-red-500' : dailyPct > 70 ? 'bg-amber-500' : 'bg-cyan-500 shadow-[0_0_10px_rgba(34,211,238,0.5)]'}`} style={{ width: `${dailyPct}%` }} />
          </div>
        </div>

        <div className="space-y-1.5">
          <div className="flex justify-between text-[9px] font-bold uppercase tracking-widest">
            <span className="text-slate-500">Minute_Burst_Capacity</span>
            <span className="text-white">{quota.minuteUsed} / {quota.minuteLimit} RPM</span>
          </div>
          <div className="h-1.5 w-full bg-slate-800/50 rounded-full overflow-hidden">
            <div className="h-full bg-slate-400 transition-all duration-300" style={{ width: `${minutePct}%` }} />
          </div>
        </div>
      </div>

      <div className="pt-4 border-t border-slate-800/50 grid grid-cols-2 gap-4">
        <div>
          <p className="text-[8px] text-slate-500 font-bold uppercase tracking-tighter">Full_Tank_In</p>
          <p className="text-sm font-black text-white tabular-nums">{timeLeftStr}</p>
        </div>
        <div className="text-right">
          <p className="text-[8px] text-slate-500 font-bold uppercase tracking-tighter">Operational_Status</p>
          <p className="text-[10px] font-bold text-cyan-400 uppercase">
            {remaining > 50 ? 'Full_Audit_Ready' : remaining > 5 ? 'Sync_Only' : 'Locked'}
          </p>
        </div>
      </div>

      <div className="bg-slate-950/50 p-3 rounded-lg border border-slate-800/50 space-y-2">
        <p className="text-[8px] font-black text-slate-600 uppercase tracking-widest border-b border-slate-800/50 pb-1">Cost_Logic_Legend</p>
        <div className="flex justify-between text-[8px] font-bold text-slate-500 uppercase">
          <span>Manual_Audit</span>
          <span className="text-amber-500">~15 Units</span>
        </div>
        <div className="flex justify-between text-[8px] font-bold text-slate-500 uppercase">
          <span>Webhook_Sync</span>
          <span className="text-cyan-500">1 Unit</span>
        </div>
      </div>
    </div>
  );
};

const App: React.FC = () => {
  const [backendUrl, setBackendUrl] = useState<string>(localStorage.getItem('stravai_backend_url') || '');
  const [backendSecret, setBackendSecret] = useState<string>(localStorage.getItem('stravai_backend_secret') || '');
  const [profile, setProfile] = useState<AthleteProfile | null>(null);
  const [backendStatus, setBackendStatus] = useState<'UNKNOWN' | 'ONLINE' | 'OFFLINE'>('UNKNOWN');
  const [isProcessing, setIsProcessing] = useState(false);
  const [showSetup, setShowSetup] = useState(!backendUrl || !backendSecret);

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

  const checkBackend = useCallback(async () => {
    if (!backendUrl) return;
    const cleanUrl = backendUrl.trim().replace(/\/$/, '');
    try {
      const res = await fetch(`${cleanUrl}/health`);
      if (res.ok) {
        setBackendStatus('ONLINE');
        fetchProfile();
      } else {
        setBackendStatus('OFFLINE');
      }
    } catch {
      setBackendStatus('OFFLINE');
    }
  }, [backendUrl, fetchProfile]);

  useEffect(() => {
    if (backendUrl) {
      checkBackend();
      const int = setInterval(checkBackend, 10000);
      return () => clearInterval(int);
    }
  }, [backendUrl, checkBackend]);

  const handleAudit = async () => {
    if (backendStatus !== 'ONLINE') return;
    setIsProcessing(true);
    try {
      await securedFetch(`${backendUrl.trim().replace(/\/$/, '')}/audit`, { method: 'POST' });
    } finally {
      setTimeout(() => setIsProcessing(false), 2000);
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
      <header className="flex items-center justify-between px-6 py-4 bg-slate-900 border-b border-slate-800 shrink-0">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-9 h-9" />
          <div>
            <h1 className="text-white font-black tracking-tighter uppercase text-sm">StravAI_Command_Center <span className="text-slate-500 border border-slate-700 px-1 rounded ml-1">v1.6.3</span></h1>
            <div className={`flex items-center gap-2 text-[9px] uppercase font-bold mt-0.5 ${backendStatus === 'ONLINE' ? 'text-cyan-400' : 'text-red-500'}`}>
              <span className={`w-1.5 h-1.5 rounded-full ${backendStatus === 'ONLINE' ? 'bg-cyan-400' : 'bg-red-500'}`}></span>
              {backendStatus}
            </div>
          </div>
        </div>
        <button onClick={() => setShowSetup(true)} className="px-3 py-1.5 bg-slate-800 border border-slate-700 rounded text-[10px] font-bold">SETTINGS</button>
      </header>

      <div className="flex-grow flex overflow-hidden">
        <aside className="w-64 border-r border-slate-800 bg-slate-900/40 p-6 space-y-6 overflow-y-auto">
            <section>
              <h2 className="text-[9px] font-black text-slate-600 uppercase tracking-widest mb-4">Core_Actions</h2>
              <button 
                onClick={handleAudit} 
                disabled={isProcessing || backendStatus !== 'ONLINE' || (profile?.quota.dailyUsed || 0) > 1485}
                className="w-full py-2 bg-slate-800 hover:bg-slate-700 text-amber-400 rounded border border-amber-500/20 font-bold uppercase text-[10px] transition-all disabled:opacity-50"
              >
                {isProcessing ? 'Processing...' : (profile?.quota.dailyUsed || 0) > 1485 ? 'Fuel_Empty' : 'Trigger_Full_Audit'}
              </button>
            </section>
        </aside>

        <main className="flex-grow flex flex-col bg-slate-950 overflow-hidden">
          <div className="flex-grow overflow-y-auto p-6 space-y-8 pb-20 scroll-smooth custom-scroll">
            {profile ? (
              <div className="max-w-5xl space-y-8">
                <div className="grid grid-cols-1 md:grid-cols-2 gap-8">
                   <QuotaMeter quota={profile.quota} />
                   <div className="bg-slate-900 border border-slate-800 rounded-2xl p-6 relative overflow-hidden group">
                      <div className="absolute top-0 right-0 p-4 opacity-5 group-hover:opacity-10 transition-opacity">
                         <StravAILogo className="w-20 h-20" />
                      </div>
                      <h3 className="text-cyan-400 font-black uppercase text-xs mb-4">Coach's_Active_Strategy</h3>
                      <p className="text-slate-400 leading-relaxed text-[11px] h-32 overflow-y-auto pr-2 custom-scroll">{profile.summary}</p>
                      <div className="mt-4 pt-4 border-t border-slate-800 italic text-slate-500 text-[10px]">
                         "{profile.coachNotes}"
                      </div>
                   </div>
                </div>

                <div className="grid grid-cols-1 md:grid-cols-3 gap-6">
                  <div className="bg-slate-900/50 p-6 border border-slate-800 rounded-2xl space-y-4">
                    <ZoneBar zones={profile.periodic.week.zones} label="Current_Week_Intensity" />
                    <div className="flex justify-between items-end">
                       <div>
                          <p className="text-[8px] text-slate-500 font-bold uppercase">Volume</p>
                          <p className="text-lg font-black text-white">{profile.periodic.week.distanceKm.toFixed(1)} km</p>
                       </div>
                    </div>
                  </div>
                  <div className="bg-slate-900/50 p-6 border border-slate-800 rounded-2xl space-y-4">
                    <ZoneBar zones={profile.periodic.month.zones} label="Current_Month_Intensity" />
                    <div className="flex justify-between items-end">
                       <div>
                          <p className="text-[8px] text-slate-500 font-bold uppercase">Volume</p>
                          <p className="text-lg font-black text-white">{profile.periodic.month.distanceKm.toFixed(1)} km</p>
                       </div>
                    </div>
                  </div>
                  <div className="bg-slate-900/50 p-6 border border-slate-800 rounded-2xl space-y-4">
                    <ZoneBar zones={profile.periodic.year.zones} label="Current_Year_Intensity" />
                    <div className="flex justify-between items-end">
                       <div>
                          <p className="text-[8px] text-slate-500 font-bold uppercase">Volume</p>
                          <p className="text-lg font-black text-white">{profile.periodic.year.distanceKm.toFixed(0)} km</p>
                       </div>
                    </div>
                  </div>
                </div>

                <section className="space-y-4">
                   <h3 className="text-white font-black uppercase text-xs tracking-widest">Physiological_Career_Log</h3>
                   <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-4 gap-4">
                      {profile.yearlyHistory.map(y => (
                        <div key={y.year} className="bg-slate-900 p-4 border border-slate-800 rounded-xl space-y-4 hover:border-slate-700 transition-colors">
                           <div className="flex justify-between items-center">
                              <span className="text-sm font-black text-white">{y.year}</span>
                              <span className="text-[10px] font-black text-cyan-500">{y.distanceKm.toFixed(0)}k</span>
                           </div>
                           <ZoneBar zones={y.zones} label={`Distribution`} />
                        </div>
                      ))}
                   </div>
                </section>
              </div>
            ) : (
              <div className="h-64 flex flex-col items-center justify-center border-2 border-dashed border-slate-800 rounded-3xl opacity-50">
                <p className="text-slate-600 font-black uppercase tracking-widest mb-4">No Data Found on System Bus</p>
                <button onClick={handleAudit} className="px-4 py-2 bg-slate-800 text-white rounded-lg text-[10px] font-bold uppercase">Begin_Initial_Audit</button>
              </div>
            )}
          </div>
        </main>
      </div>

      {showSetup && (
        <div className="fixed inset-0 z-50 flex items-center justify-center bg-black/80 backdrop-blur-sm">
          <div className="bg-slate-900 border border-slate-800 rounded-2xl w-full max-w-sm p-8 space-y-6">
            <h2 className="text-white font-black uppercase">System_Sync</h2>
            <div className="space-y-4">
              <input type="text" placeholder="Backend URL" value={backendUrl} onChange={e => setBackendUrl(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-3 text-xs outline-none focus:border-cyan-500"/>
              <input type="password" placeholder="System Secret" value={backendSecret} onChange={e => setBackendSecret(e.target.value)} className="w-full bg-slate-950 border border-slate-800 rounded p-3 text-xs outline-none focus:border-cyan-500"/>
            </div>
            <button onClick={saveConfig} className="w-full py-3 bg-cyan-600 text-white rounded font-bold uppercase text-[10px]">Authorize</button>
          </div>
        </div>
      )}
    </div>
  );
};

export default App;
