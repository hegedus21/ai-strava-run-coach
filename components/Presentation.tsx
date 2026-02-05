import React, { useState, useEffect } from 'react';
import { StravAILogo } from './Icon';

// Importing local assets so Vite can resolve the paths correctly
import stravaImg1 from './Athelte_Intelligence_Strava.jpg';
import stravaImg2 from './Athelte_Intelligence_Strava_2.jpg';

interface Slide {
  title: string;
  subtitle: string;
  content: string[];
  tech?: string[];
  tag: string;
  images?: string[];
}

const slides: Slide[] = [
  {
    tag: "01_START",
    title: "STRAVAI COACH",
    subtitle: "The Personal Training Revolution.",
    content: [
      "A service built in .NET to bridge the gap between human athletic potential and artificial intelligence.",
      "Listens to new Strava activities and provides elite-level coaching feedback automatically.",
      "Engineered for the data-driven athlete who wants more than just a summary."
    ],
    tech: [".NET 9", "Minimal API", "Strava API"],
    images: ["https://images.unsplash.com/photo-1476480862126-209bfaa8edc8?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "02_WHY",
    title: "THE CATALYST",
    subtitle: "Trial Ended, Mission Started.",
    content: [
      "My 30-day Strava Premium trial ended. I lost access to 'Athlete Intelligence' and training suggestions.",
      "I tried manual prompting in ChatGPT, but sharing fitness levels and history was a massive bottleneck.",
      "The friction of manual exports led to one conclusion: I needed a direct API bridge to the AI coach."
    ],
    tech: ["API Integration", "Automation"],
    // Using the imported image variables here
    images: [stravaImg1, stravaImg2]
  },
  {
    tag: "03_RESEARCH",
    title: "MARKET GAP",
    subtitle: "Identifying the Intelligence Ceiling.",
    content: [
      "Existing tools have limits: Strava AI only scans 30 days. Garmin plans stop at the Half Marathon.",
      "StravAI scans up to 1,000 activities to build a true multi-season fitness signature.",
      "Our focus: Personalized race-day nutrition, pace plans, and 'Readiness %' based on long-term data."
    ],
    tech: ["Competitive Analysis", "Strategy"],
    images: ["https://images.unsplash.com/photo-1551288049-bbbda536339a?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "04_GENESIS",
    title: "RAPID GENESIS",
    subtitle: "Documentation via AI Studio.",
    content: [
      "I found the Strava API docs but didn't want to spend weeks studying the endpoints.",
      "I used Google AI Studio to translate my high-level logic into a fully structured .NET project.",
      "Within hours, we moved from an abstract idea to a working cloud-native engine."
    ],
    tech: ["Google AI Studio", "Rapid PoC"],
    images: ["https://images.unsplash.com/photo-1587620962725-abab7fe55159?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "05_EVOLUTION",
    title: "INFRASTRUCTURE",
    subtitle: "The Power of the Free Tier.",
    content: [
      "The mission: High-grade coaching with zero running costs.",
      "Engineered to run on Koyeb's free tier with deployment handled by GitHub Actions.",
      "AI guided the entire setup of secure token management and the Zero Trust security model."
    ],
    tech: ["Koyeb", "GitHub Actions", "Cloud Native"],
    images: ["https://images.unsplash.com/photo-1451187580459-43490279c0fa?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "06_RECIPROCITY",
    title: "AI SYNERGY",
    subtitle: "Developed by AI, Analyzed by AI.",
    content: [
      "A recursive loop: The codebase was written by Gemini. The athlete's fitness is now coached by Gemini.",
      "By eliminating manual dev work, we reached a stable production state in record time.",
      "Leveraging Gemini Flash for high-speed, cost-effective reasoning."
    ],
    tech: ["Gemini 3 Flash", "Neural Logic"],
    images: ["https://images.unsplash.com/photo-1677442136019-21780ecad995?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "07_BENEFITS",
    title: "THE PROS",
    subtitle: "Velocity and Precision.",
    content: [
      "Near-instant Proof of Concept development without reading thousands of documentation pages.",
      "Perfectly optimized for private use—professional coaching at no cost.",
      "Scalable architecture that treats every run as a data point for a larger season strategy."
    ],
    tech: ["Velocity", "Efficiency"],
    images: ["https://images.unsplash.com/photo-1461896756913-647eecc9a29e?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "08_REFLECTIONS",
    title: "CHALLENGES",
    subtitle: "The 'Forgetful' AI Coach.",
    content: [
      "AI Forgetfulness: The model sometimes overwrites established logic or ignores code constraints.",
      "Instruction Drift: Requests for simple discussions often result in accidental code modifications.",
      "Managing context windows is critical to prevent the coach from losing the long-term roadmap."
    ],
    tech: ["Context Management", "AI Alignment"],
    images: ["https://images.unsplash.com/photo-1517245386807-bb43f82c33c4?auto=format&fit=crop&q=80&w=800"]
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
    <div className="fixed inset-0 z-[200] bg-slate-950/98 backdrop-blur-3xl flex flex-col font-mono overflow-hidden">
      {/* Header */}
      <div className="flex justify-between items-center p-6 border-b border-slate-800 bg-slate-900/80 backdrop-blur-md">
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
                    <span className="text-cyan-600 font-bold shrink-0 mt-1">{" >> "}</span>
                    <p className="text-slate-300 text-sm md:text-lg leading-relaxed group-hover:text-white transition-colors">
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
            <div className={`grid gap-4 w-full h-full min-h-[350px] ${s.images && s.images.length > 1 ? 'grid-cols-2' : 'grid-cols-1'}`}>
              {s.images && s.images.length > 0 ? (
                s.images.map((img, idx) => (
                  <div key={idx} className="relative aspect-[4/5] rounded-3xl overflow-hidden border border-slate-800 group shadow-2xl shadow-cyan-500/5 bg-slate-900">
                    <img 
                      src={img} 
                      alt={`${s.title} ${idx + 1}`} 
                      className="w-full h-full object-cover grayscale opacity-60 group-hover:grayscale-0 group-hover:opacity-100 transition-all duration-1000 scale-105 group-hover:scale-100" 
                      onError={(e) => {
                        // Clean fallback for missing assets
                        (e.target as HTMLImageElement).style.display = 'none';
                        const parent = (e.target as HTMLImageElement).parentElement;
                        if (parent) {
                          parent.classList.add('flex', 'items-center', 'justify-center');
                          parent.innerHTML = `<div class="flex flex-col items-center gap-2 opacity-20"><svg class="w-12 h-12 text-slate-400" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path stroke-linecap="round" stroke-linejoin="round" stroke-width="1" d="M4 16l4.586-4.586a2 2 0 012.828 0L16 16m-2-2l1.586-1.586a2 2 0 012.828 0L20 14m-6-6h.01M6 20h12a2 2 0 002-2V6a2 2 0 00-2-2H6a2 2 0 00-2 2v12a2 2 0 002 2z" /></svg><span class="text-[8px] uppercase">Missing_Asset</span></div>`;
                        }
                      }}
                    />
                    <div className="absolute inset-0 bg-gradient-to-t from-slate-950/90 via-transparent to-transparent opacity-80"></div>
                    <div className="absolute bottom-4 left-4 right-4">
                      <div className="bg-slate-900/60 backdrop-blur-md border border-slate-700/30 p-2 rounded-lg flex justify-between items-center">
                        <span className="text-[7px] text-cyan-500 font-black uppercase tracking-widest">VISUAL_{idx + 1}</span>
                        <div className="w-1.5 h-1.5 bg-cyan-500 rounded-full animate-pulse"></div>
                      </div>
                    </div>
                  </div>
                ))
              ) : (
                <div className="aspect-square rounded-3xl overflow-hidden border border-slate-800 bg-slate-900 flex items-center justify-center">
                  <StravAILogo className="w-32 h-32 opacity-10 animate-pulse" />
                </div>
              )}
            </div>
          </div>
        </div>
      </div>

      {/* Navigation Footer */}
      <div className="p-8 flex justify-between items-center border-t border-slate-800 bg-slate-900/50 backdrop-blur-md">
        <div className="flex gap-2">
          {slides.map((_, i) => (
            <button 
              key={i} 
              onClick={() => setCurrent(i)}
              className={`h-1.5 transition-all duration-500 rounded-full ${i === current ? 'w-12 bg-cyan-500 shadow-[0_0_10px_rgba(6,182,212,0.5)]' : 'w-3 bg-slate-800 hover:bg-slate-700'}`}
              title={`Slide ${i + 1}`}
            />
          ))}
        </div>
        <div className="flex gap-4">
          <button 
            disabled={current === 0}
            onClick={() => setCurrent(p => p - 1)}
            className="group flex items-center gap-2 px-6 py-3 bg-slate-900 hover:bg-slate-800 disabled:opacity-20 border border-slate-800 rounded-xl text-white transition-all active:scale-95"
          >
            <svg className="w-4 h-4 group-hover:-translate-x-1 transition-transform" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={3} d="M15 19l-7-7 7-7" /></svg>
            <span className="text-[10px] font-black uppercase hidden sm:inline">Prev</span>
          </button>
          <button 
            disabled={current === slides.length - 1}
            onClick={() => setCurrent(p => p + 1)}
            className="group flex items-center gap-2 px-8 py-3 bg-cyan-600 hover:bg-cyan-500 disabled:opacity-20 border border-cyan-400/50 rounded-xl text-white transition-all shadow-xl shadow-cyan-500/20 active:scale-95"
          >
            <span className="text-[10px] font-black uppercase hidden sm:inline">Continue</span>
            <svg className="w-4 h-4 group-hover:translate-x-1 transition-transform" fill="none" viewBox="0 0 24 24" stroke="currentColor"><path strokeLinecap="round" strokeLinejoin="round" strokeWidth={3} d="M9 5l7 7-7 7" /></svg>
          </button>
        </div>
      </div>
    </div>
  );
};

export default Presentation;
