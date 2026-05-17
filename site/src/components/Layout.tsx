import { Outlet, Link, useLocation } from "react-router-dom";

const navLinks = [
  { to: "/", label: "home" },
  { to: "/downloads", label: "downloads" },
  { to: "/donate", label: "donate" },
];

export default function Layout() {
  const location = useLocation();

  return (
    <div className="relative min-h-screen flex flex-col bg-transparent text-[#171512]">
      <header
        className="sticky top-0 z-50"
        style={{ paddingTop: "env(safe-area-inset-top)" }}
      >
        <div className="mx-auto max-w-[1040px] px-6 sm:px-8 h-16 flex items-center justify-between gap-4">
          <nav className="flex min-w-0 items-center gap-4 sm:gap-5 text-[13px] sm:text-[14px] overflow-x-auto no-scrollbar" aria-label="Primary navigation">
            {navLinks.map((link) => (
              <Link
                key={link.to}
                to={link.to}
                className={`shrink-0 transition-colors ${
                  location.pathname === link.to
                    ? "text-[#171512] underline underline-offset-4 decoration-[#8A6A3D]"
                    : "text-[#171512]/60 hover:text-[#171512]"
                }`}
              >
                {link.label}
              </Link>
            ))}
          </nav>

        </div>
      </header>

      <div className="page-edge-fade page-edge-fade-top" aria-hidden="true" />

      <main
        className="flex-1 mx-auto w-full max-w-[1040px] px-6 sm:px-8"
        style={{
          paddingLeft: "max(1.5rem, env(safe-area-inset-left))",
          paddingRight: "max(1.5rem, env(safe-area-inset-right))",
        }}
      >
        <Outlet />
      </main>

      <div className="page-edge-fade page-edge-fade-bottom" aria-hidden="true" />

      <footer
        className="mt-20"
        style={{ paddingBottom: "env(safe-area-inset-bottom)" }}
      >
        <div className="mx-auto max-w-[1040px] px-6 sm:px-8 py-8 flex flex-col sm:flex-row items-start sm:items-center justify-between gap-3 text-[13px] text-[#171512]/50">
          <span>CyberSnap is open source under gpl-3.0.</span>
          <div className="flex items-center gap-4">
            <a href="https://ko-fi.com/jasperdevs" target="_blank" rel="noopener noreferrer" className="hover:text-[#171512] transition-colors">ko-fi</a>
            <Link to="/downloads" className="hover:text-[#171512] transition-colors">downloads</Link>
            <Link to="/privacy" className="hover:text-[#171512] transition-colors">privacy</Link>
          </div>
        </div>
      </footer>
    </div>
  );
}
