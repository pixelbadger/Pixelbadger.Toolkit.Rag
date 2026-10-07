import { Outlet } from "react-router-dom";

import { AppBreadcrumbs } from "@/components/app-breadcrumbs";
import { AppSidebar, MobileNav } from "@/components/app-sidebar";
import { TooltipProvider } from "@/components/ui/tooltip";

export function AppLayout() {
  return (
    <TooltipProvider>
      <div className="flex min-h-screen">
        <AppSidebar />
        <div className="flex min-w-0 flex-1 flex-col">
          <header className="flex h-14 items-center gap-2 border-b px-4">
            <MobileNav />
            <AppBreadcrumbs />
          </header>
          <main className="flex-1 space-y-6 p-4 md:p-6">
            <Outlet />
          </main>
        </div>
      </div>
    </TooltipProvider>
  );
}
