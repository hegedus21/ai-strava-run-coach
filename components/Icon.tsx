
import React from 'react';

export const StravAILogo: React.FC<{ className?: string }> = ({ className = "w-10 h-10" }) => (
  <svg viewBox="0 0 100 100" className={className} fill="none" xmlns="http://www.w3.org/2000/svg">
    <path d="M20 80L50 20L80 80H65L50 50L35 80H20Z" fill="#FC6100" />
    <path d="M45 40L55 40L55 60L45 60L45 40Z" fill="white" opacity="0.8" />
    <circle cx="50" cy="50" r="48" stroke="#FC6100" strokeWidth="2" strokeDasharray="4 4" />
    <path d="M10 50C10 27.9086 27.9086 10 50 10" stroke="#00F2FF" strokeWidth="2" strokeLinecap="round" />
  </svg>
);
