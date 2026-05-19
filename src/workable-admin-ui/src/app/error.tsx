"use client";

import { useEffect } from "react";
import { RefreshCw } from "lucide-react";
import { Button } from "@/components/ui/button";

export default function Error({
  error,
  unstable_retry,
}: {
  error: Error & { digest?: string };
  unstable_retry: () => void;
}) {
  useEffect(() => {
    console.error(error);
  }, [error]);

  return (
    <main className="flex min-h-svh items-center justify-center bg-background p-6 text-foreground">
      <section className="w-full max-w-xl rounded-lg border border-border bg-card p-6 shadow-sm">
        <div className="space-y-2">
          <p className="text-muted-foreground text-sm">Workable Console</p>
          <h1 className="font-semibold text-xl">Something went wrong</h1>
          <p className="text-muted-foreground text-sm">
            The console hit an unexpected UI error. You can retry the render without losing your saved server settings.
          </p>
        </div>
        <div className="mt-5 rounded-md border border-border bg-muted/30 p-3">
          <p className="font-mono text-muted-foreground text-xs">
            {error.message || error.digest || "Unknown error"}
          </p>
        </div>
        <Button className="mt-5 gap-2" onClick={() => unstable_retry()} type="button">
          <RefreshCw className="size-4" />
          Retry
        </Button>
      </section>
    </main>
  );
}
