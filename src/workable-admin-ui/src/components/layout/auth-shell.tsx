import type { ReactNode } from "react";

export function AuthShell({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}) {
  return (
    <main className="relative flex min-h-svh items-center justify-center overflow-hidden bg-background px-6 py-12 text-foreground">
      <div className="absolute inset-0 -z-10 bg-[radial-gradient(circle_at_top_left,oklch(0.58_0.16_170_/_0.24),transparent_28rem),radial-gradient(circle_at_bottom_right,oklch(0.72_0.12_80_/_0.18),transparent_24rem)]" />
      <section className={className ?? "w-full max-w-md"}>{children}</section>
    </main>
  );
}
