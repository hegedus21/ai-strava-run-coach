import React, { useState, useEffect } from 'react';
import { StravAILogo } from './Icon';

interface Slide {
  title: string;
  subtitle: string;
  content: string[];
  tech?: string[];
  tag: string;
  image?: string; // Add your image URL here
}

const slides: Slide[] = [
  {
    tag: "01_THE_SPARK",
    title: "STRAVAI COACH",
    subtitle: "Necessity is the Mother of Innovation.",
    content: [
      "It started when my Strava Premium trial ended. I lost access to 'Athlete Intelligence' and training suggestions.",
      "I didn't want a monthly subscription—I wanted my data to work for me.",
      "Manually exporting logs to ChatGPT was a pain. I needed a direct bridge between my Strava history and an LLM."
    ],
    tech: ["Strava API", "Gemini 3"],
    image: "https://images.unsplash.com/photo-1476480862126-209bfaa8edc8?auto=format&fit=crop&q=80&w=800" // Placeholder
  },
  {
    tag: "02_MARKET_GAP",
    title: "THE MISSING PIECE",
    subtitle: "Beyond General Suggestions.",
    content: [
      "Strava AI only looks at the last 30 days. Garmin plans stop at the Half Marathon.",
      "StravAI scans up to 1,000 activities to find your true fitness signature.",
      "Features: Specific race-day nutrition, pace plans (Pessimistic vs. Optimistic), and 'Readiness %' based on months of data."
    ],
    tech: ["Market Research", "Competitive Edge"],
    image: "https://images.unsplash.com/photo-1552674605-db6ffd4facb5?auto=format&fit=crop&q=80&w=800" // Placeholder
  },
  {
    tag: "03_AI_SYNERGY",
    title: "ZERO TO PODIUM",
    subtitle: "Developing without Documentation.",
    content: [
      "I didn't study the Strava API docs. I used Google AI Studio as my lead architect.",
      "From initial idea to a working Proof of Concept (PoC) in just a few hours.",
      "Rapid prototyping: AI generated the .NET backend, the React UI, and the deployment scripts simultaneously."
    ],
    tech: ["Google AI Studio", "Rapid PoC"],
    image: "https://images.unsplash.com/photo-1677442136019-21780ecad995?auto=format&fit=crop&q=80&w=800" // Placeholder
  },
  {
    tag: "04_EVOLUTION",
    title: "HARDENING THE CORE",
    subtitle: "Refining the Vision.",
    content: [
      "We moved from simple syncs to real-time Webhooks and Custom Race configuration.",
      "Criteria: Everything must run on 'Free Tiers' (Koyeb, GitHub, Gemini Flash).",
      "The service evolved into a 'Zero Trust' architecture to protect athlete privacy."
    ],
    tech: [".NET 9", "Koyeb", "Cloud Native"],
    image: "https://images.unsplash.com/photo-1451187580459-43490279c0fa?auto=format&fit=crop&q=80&w=800" // Placeholder
  },
  {
    tag: "05_REFLECTIONS",
    title: "LESSONS LEARNED",
    subtitle: "The Reality of AI-Driven Development.",
    content: [
      "PROS: Massive speed, no API study needed, perfectly optimized for private use.",
      "CONS: AI 'forgetfulness'—sometimes it overwrites agreed logic or ignores specific constraints.",
      "The 'Forgetful Coach' syndrome requires human strategy to maintain project direction."
    ],
    tech: ["Human-in-the-loop", "Future Growth"],
    image: "https://images.unsplash.com/photo-1517245386807-bb43f82c33c4?auto=format&fit=crop&q=80&w=800" // Placeholder
  }
];

const Presentation: React.FC<{ onClose: () => void }> = ({ onClose }) => {
  const [current, setCurrent] = useState(0);

  useEffect(() => {
    const handleKey = (e: KeyboardEvent) => {
      if (e.key === 'ArrowRight') setCurrent(prev => Math.min(prev + 1, slides.length - 1));
      if (e.key === 'ArrowLeft') setCurrent(prev => Math.max(prev - 1, 0));
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', handleKey);
    return () => window.removeEventListener('keydown', handleKey);
  }, [onClose]);

  const s = slides[current];

  return (
    <div className="fixed inset-0 z-[200] bg-slate-950/98 backdrop-blur-2xl flex flex-col font-mono overflow-hidden">
      {/* Header */}
      <div className="flex justify-between items-center p-6 border-b border-slate-800 bg-slate-900/50">
        <div className="flex items-center gap-4">
          <StravAILogo className="w-8 h-8" />
          <span className="text-white font-black tracking-tighter text-lg uppercase">StravAI_Chronicles</span>
        </div>
        <button onClick={onClose} className="text-slate-500 hover:text-white transition-colors uppercase text-[10px] font-bold px-4 py-2 border border-slate-800 rounded-lg">Exit_View</button>
      </div>

      {/* Slide Body */}
      <div className="flex-grow flex items-center justify-center p-6 md:p-12 overflow-y-auto">
        <div className="max-w-6xl w-full grid grid-cols-1 lg:grid-cols-12 gap-12 items-center">
          
          {/* Left Column: Text Content */}
          <div className="lg:col-span-7 space-y-8 order-2 lg:order-1">
            <div className="space-y-4">
              <div className="inline-block px-3 py-1 bg-cyan-900/30 border border-cyan-500/20 text-cyan-400 text-[10px] font-black rounded uppercase tracking-widest">
                {s.tag}
              </div>
              <h2 className="text-4xl md:text-6xl font-black text-white leading-tight tracking-tighter uppercase">
                {s.title}
              </h2>
              <div className="h-1.5 w-24 bg-cyan-500 rounded-full"></div>
            </div>

            <div className="space-y-6">
              <h3 className="text-xl md:text-2xl font-bold text-cyan-400/90 italic">{s.subtitle}</h3>
              <div className="space-y-5">
                {s.content.map((p, i) => (
                  <div key={i} className="flex gap-4 group">
                    <span className="text-cyan-600 font-bold shrink-0 mt-1">>></span>
                    <p className="text-slate-300 text-sm md:text-base leading-relaxed group-hover:text-white transition-colors">
                      {p}
                    </p>
                  </div>
                ))}
              </div>
            </div>

            {s.tech && (
              <div className="flex flex-wrap gap-2 pt-6">
                {s.tech.map(t => (
                  <span key={t} className="px-3 py-1 bg-slate-900 border border-slate-800 text-slate-500 text-[9px] font-black rounded uppercase tracking-wider">
                    {t}
                  </span>
                ))}
              </div>
            )}
          </div>

          {/* Right Column: Visual/Image */}
          <div className="lg:col-span-5 order-1 lg:order-2">
            <div className="relative aspect-square md:aspect-video lg:aspect-square rounded-3xl overflow-hidden border border-slate-800 group shadow-2xl shadow-cyan-500/5">
              {s.image ? (
                <img 
                  src={s.image} 
                  alt={s.title} 
                  className="w-full h-full object-cover grayscale opacity-50 group-hover:grayscale-0 group-hover:opacity-100 transition-all duration-1000 scale-110 group-hover:scale-100" 
                />
              ) : (
                <div className="w-full h-full bg-slate-900 flex items-center justify-center">
                  <StravAILogo className="w-32 h-32 opacity-10" />
                </div>
              )}
              {/* Overlay Decor */}
              <div className="absolute inset-0 bg-gradient-to-t from-slate-950 via-transparent to-transparent opacity-60"></div>
              <div className="absolute bottom-6 left-6 right-6">
                <div className="w-full bg-slate-900/80 backdrop-blur-md border border-slate-700/50 p-4 rounded-xl">
                  <div className="flex justify-between items-center text-[8px] text-cyan-500 font-black uppercase">
                    <span>Visual_Asset_{current + 1}</span>
                    <span>Status: Verified</span>
                  </div>
                </div>
              </div>
            </div>
          </div>
        </div>
      </div>

      {/* Navigation Footer */}
      <div className="p-8 flex justify-between items-center border-t border-slate-800 bg-slate-900/30">
        <div className="flex gap-3">
          {slides.map((_, i) => (
            <button 
              key={i} 
              onClick={() => setCurrent(i)}
              className={`h-1.5 transition-all duration-500 rounded-full ${i === current ? 'w-16 bg-cyan-500' : 'w-4 bg-slate-800 hover:bg-slate-700'}`}
            />
          ))}
        </div>
        <div className="flex gap-4">
          <button 
            disabled={current === 0}
            onClick={() => setCurrent(p => p - 1)}
            className="group flex items-center gap-2 px-6 py-3 bg-slate-900 hover:bg-slate-800 disabled:opacity-20 border border-slate-800 rounded-xl text-white transition-all"
          >
            <svg className="w-4 h-4 group-hover:-translate-x-1 transition-transform" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={3} d="M15 19l-7-7 7-7" /></svg>
            <span className="text-[10px] font-black uppercase hidden md:inline">Previous</span>
          </button>
          <button 
            disabled={current === slides.length - 1}
            onClick={() => setCurrent(p => p + 1)}
            className="group flex items-center gap-2 px-8 py-3 bg-cyan-600 hover:bg-cyan-500 disabled:opacity-20 border border-cyan-400/50 rounded-xl text-white transition-all shadow-xl shadow-cyan-500/20"
          >
            <span className="text-[10px] font-black uppercase hidden md:inline">Next_Chapter</span>
            <svg className="w-4 h-4 group-hover:translate-x-1 transition-transform" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={3} d="M9 5l7 7-7 7" /></svg>
          </button>
        </div>
      </div>
    </div>
  );
};

export default Presentation;
