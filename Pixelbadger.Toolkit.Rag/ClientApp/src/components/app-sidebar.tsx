import { useState } from "react";
import { NavLink } from "react-router-dom";
import { Database, FileUp, ListChecks, Menu, PanelLeftClose, PanelLeftOpen, Search } from "lucide-react";

import { Button } from "@/components/ui/button";
import { Dialog, DialogContent, DialogDescription, DialogTitle } from "@/components/ui/dialog";
import { cn } from "@/lib/utils";

const NAV_ITEMS = [
  { to: "/", label: "Jobs", icon: ListChecks, end: true },
  { to: "/query", label: "Query", icon: Search, end: false },
  { to: "/ingest", label: "Ingest", icon: FileUp, end: false },
];

function NavList({ collapsed = false, onNavigate }: { collapsed?: boolean; onNavigate?: () => void }) {
  return (
    <nav aria-label="Primary" className="flex flex-col gap-1 p-2">
      {NAV_ITEMS.map(({ to, label, icon: Icon, end }) => (
        <NavLink
          key={to}
          to={to}
          end={end}
          onClick={onNavigate}
          title={collapsed ? label : undefined}
          className={({ isActive }) =>
            cn(
              "flex items-center gap-2 rounded-md px-3 py-2 text-sm font-medium outline-none hover:bg-sidebar-accent focus-visible:ring-2 focus-visible:ring-ring",
              isActive && "bg-sidebar-accent",
              collapsed && "justify-center px-2",
            )
          }
        >
          <Icon className="size-4 shrink-0" aria-hidden="true" />
          <span className={cn(collapsed && "sr-only")}>{label}</span>
        </NavLink>
      ))}
    </nav>
  );
}

function Brand({ collapsed = false }: { collapsed?: boolean }) {
  return (
    <div className={cn("flex h-14 items-center gap-2 px-4 font-semibold", collapsed && "justify-center px-2")}>
      <Database className="size-5 shrink-0" aria-hidden="true" />
      <span className={cn(collapsed && "sr-only")}>Pixelbadger RAG</span>
    </div>
  );
}

/** Persistent navigation: a collapsible rail on desktop, a drawer on small screens. */
export function AppSidebar() {
  const [collapsed, setCollapsed] = useState(false);
  return (
    <aside
      className={cn(
        "hidden shrink-0 flex-col border-r border-sidebar-border bg-sidebar text-sidebar-foreground md:flex",
        collapsed ? "w-14" : "w-56",
      )}
    >
      <Brand collapsed={collapsed} />
      <div className="flex-1">
        <NavList collapsed={collapsed} />
      </div>
      <div className={cn("flex p-2", collapsed ? "justify-center" : "justify-end")}>
        <Button
          variant="ghost"
          size="icon-sm"
          aria-label={collapsed ? "Expand sidebar" : "Collapse sidebar"}
          onClick={() => setCollapsed((c) => !c)}
        >
          {collapsed ? <PanelLeftOpen /> : <PanelLeftClose />}
        </Button>
      </div>
    </aside>
  );
}

export function MobileNav() {
  const [open, setOpen] = useState(false);
  return (
    <div className="md:hidden">
      <Button variant="ghost" size="icon-sm" aria-label="Open navigation" onClick={() => setOpen(true)}>
        <Menu />
      </Button>
      <Dialog open={open} onOpenChange={setOpen}>
        <DialogContent side="left" className="bg-sidebar text-sidebar-foreground">
          <DialogTitle className="sr-only">Navigation</DialogTitle>
          <DialogDescription className="sr-only">Primary navigation</DialogDescription>
          <Brand />
          <NavList onNavigate={() => setOpen(false)} />
        </DialogContent>
      </Dialog>
    </div>
  );
}
