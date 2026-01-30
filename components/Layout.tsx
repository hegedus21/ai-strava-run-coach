
import React from 'react';

interface LayoutProps {
  children: React.ReactNode;
}

const Layout: React.FC<LayoutProps> = ({ children }) => {
  return (
    <div className="min-h-screen flex flex-col">
      <header className="bg-white border-b sticky top-0 z-50">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8 h-16 flex items-center justify-between">
          <div className="flex items-center gap-2">
            <div className="w-8 h-8 rounded-md strava-orange flex items-center justify-center">
              <span className="text-white font-bold italic">AI</span>
            </div>
            <h1 className="text-xl font-bold tracking-tight text-gray-900">
              StravAI <span className="text-orange-600">Coach</span>
            </h1>
          </div>
          <nav className="flex items-center gap-6 text-sm font-medium text-gray-600">
            <button className="hover:text-orange-600 transition-colors">Dashboard</button>
            <button className="hover:text-orange-600 transition-colors">History</button>
            <button className="px-4 py-2 rounded-full border border-orange-600 text-orange-600 hover:bg-orange-50 transition-colors">
              Connect Strava
            </button>
          </nav>
        </div>
      </header>
      <main className="flex-grow max-w-7xl mx-auto w-full px-4 sm:px-6 lg:px-8 py-8">
        {children}
      </main>
      <footer className="bg-white border-t py-8">
        <div className="max-w-7xl mx-auto px-4 text-center text-sm text-gray-500">
          Powered by Gemini 3 & Strava API
        </div>
      </footer>
    </div>
  );
};

export default Layout;
