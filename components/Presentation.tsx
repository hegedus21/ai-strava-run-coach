import React, { useState, useEffect } from 'react';
import { StravAILogo } from './Icon';

interface Slide {
  title: string;
  subtitle: string;
  content: string[];
  tech?: string[];
  tag: string;
  images?: string[]; // Support multiple images
}

const slides: Slide[] = [
  {
    tag: "01_START",
    title: "STRAVAI COACH",
    subtitle: "The Project Inception.",
    content: [
      "Born from a desire to turn raw GPS data into actionable coaching intelligence.",
      "A bridge between the Strava API and the reasoning power of Gemini 3.",
      "Developed entirely through AI-human collaboration."
    ],
    tech: ["React", ".NET 9", "Gemini 3"],
    images: ["https://images.unsplash.com/photo-1476480862126-209bfaa8edc8?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "02_WHY",
    title: "THE CATALYST",
    subtitle: "Beyond the Paywall.",
    content: [
      "My Strava Premium trial ended, taking 'Athlete Intelligence' and training suggestions with it.",
      "I didn't want to pay a subscription for features I could build myself.",
      "Manual exporting of logs to LLMs was tedious; I needed an automated API-driven solution."
    ],
    tech: ["Strava API", "Automation"],
    // Referencing two images. Update these paths to your local ones if needed (e.g., ["./components/images/image1.jpg", "./components/images/image2.jpg"])
    images: [
      "https://images.unsplash.com/photo-1517430816045-df4b7de11d1d?auto=format&fit=crop&q=80&w=800",
      "https://images.unsplash.com/photo-1512446813985-4a0eb173b7a6?auto=format&fit=crop&q=80&w=800"
    ]
  },
  {
    tag: "03_RESEARCH",
    title: "MARKET GAP",
    subtitle: "Scanning 1,000 Activities, Not Just 30.",
    content: [
      "Strava AI is limited to 30 days. Garmin plans only go up to the Half Marathon.",
      "StravAI scans your entire history (up to 1,000 activities) for true fitness mapping.",
      "Focus: Race-specific plans, pace strategies (Optimistic/Realistic), and nutrition logic."
    ],
    tech: ["Big Data", "Fitness Intelligence"],
    images: ["https://images.unsplash.com/photo-1552674605-db6ffd4facb5?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "04_GENESIS",
    title: "THE FIRST SPARK",
    subtitle: "Google AI Studio as Architect.",
    content: [
      "Found the Strava API docs but didn't want to spend weeks studying them.",
      "Used Google AI Studio to translate my ideas into a working project structure.",
      "Within hours, the first functional version of the cloud engine was alive."
    ],
    tech: ["AI Studio", "Rapid Prototyping"],
    images: ["https://images.unsplash.com/photo-1677442136019-21780ecad995?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "05_EVOLUTION",
    title: "REFINING THE ENGINE",
    subtitle: "The 'Everything Free' Architecture.",
    content: [
      "Criterion: Zero cost. Hosted on Koyeb (Free Tier) with GitHub Actions.",
      "Evolved from simple activity analysis to deep Custom Race deployments.",
      "AI guided the setup of tokens, keys, and the Zero Trust security model."
    ],
    tech: ["Koyeb", "GitHub Actions", ".NET 9"],
    images: ["https://images.unsplash.com/photo-1451187580459-43490279c0fa?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "06_POWERED_BY_AI",
    title: "AI INCEPTION",
    subtitle: "Developed by AI, Analyzed by AI.",
    content: [
      "The code was written by AI. The athletic performance is analyzed by AI.",
      "Leveraging Gemini Flash (Free Tier) for efficient, high-speed reasoning.",
      "A recursive cycle of intelligence where the tool improves itself."
    ],
    tech: ["Gemini 3 Flash", "Neural Analysis"],
    images: ["https://images.unsplash.com/photo-1620712943543-bcc4628c9757?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "07_PROS",
    title: "THE SUCCESS",
    subtitle: "Rapid Proof of Concept.",
    content: [
      "Zero documentation study required—the AI understood the API requirements instantly.",
      "Speed: From concept to production in days rather than months.",
      "High-level coaching logic available at no cost for private, personal use."
    ],
    tech: ["Efficiency", "Low Overhead"],
    images: ["https://images.unsplash.com/photo-1461896756913-647eecc9a29e?auto=format&fit=crop&q=80&w=800"]
  },
  {
    tag: "08_CHALLENGES",
    title: "IMPROVEMENTS",
    subtitle: "Dealing with the 'Forgetful' AI Coach.",
    content: [
      "AI 'Forgetfulness': Sometimes it overwrites logic we previously established.",
      "Instruction Drift: Asking it to 'just discuss' often results in unwanted code changes.",
      "Context Management: It occasionally refers back to stale requirements from past sessions."
    ],
    tech: ["Context Management", "Instruction Following"],
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
                    <span className="text-cyan-600 font-bold shrink-0 mt-1">>></span>
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
            <div className={`grid gap-4 w-full h-full min-h-[300px] ${s.images && s.images.length > 1 ? 'grid-cols-2' : 'grid-cols-1'}`}>
              {s.images && s.images.length > 0 ? (
                s.images.map((img, idx) => (
                  <div key={idx} className="relative aspect-square rounded-3xl overflow-hidden border border-slate-800 group shadow-2xl shadow-cyan-500/5 bg-slate-900">
                    <img 
                      src={img} 
                      alt={`${s.title} ${idx + 1}`} 
                      className="w-full h-full object-cover grayscale opacity-60 group-hover:grayscale-0 group-hover:opacity-100 transition-all duration-1000 scale-105 group-hover:scale-100" 
                      onError={(e) => {
                        (e.target as HTMLImageElement).src = 'https://images.unsplash.com/photo-1461896756913-647eecc9a29e?auto=format&fit=crop&q=80&w=800';
                      }}
                    />
                    <div className="absolute inset-0 bg-gradient-to-t from-slate-950/80 via-transparent to-transparent opacity-60"></div>
                    <div className="absolute bottom-4 left-4 right-4">
                      <div className="bg-slate-900/80 backdrop-blur-md border border-slate-700/50 p-2 rounded-lg">
                        <span className="text-[7px] text-cyan-500 font-black uppercase tracking-widest">Asset_{idx + 1}</span>
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
