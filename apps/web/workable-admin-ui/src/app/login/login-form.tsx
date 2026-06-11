"use client";

import { useEffect, useState } from "react";
import type { FormEvent } from "react";
import { useRouter } from "next/navigation";
import { AuthShell } from "@/components/layout/auth-shell";
import { FormField } from "@/components/features/console/form-controls";
import { WorkableLogo } from "@/components/shared/workable-logo";
import {
  Alert,
  AlertDescription,
  AlertTitle,
} from "@/components/ui/alert";
import { Button } from "@/components/ui/button";
import {
  Card,
  CardContent,
  CardHeader,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Skeleton } from "@/components/ui/skeleton";
import type { AdminAuthProvider } from "@/lib/admin-security";

type LoginFormProps = {
  authProvider: AdminAuthProvider;
  initialError: string | null;
  initialReason: "unauthorized" | null;
  nextPath: string;
};

export function LoginForm({
  authProvider,
  initialError,
  initialReason,
  nextPath,
}: LoginFormProps) {
  const router = useRouter();
  const [hasHydrated, setHasHydrated] = useState(false);
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(initialError);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const errorTitle = getErrorTitle(error, initialReason);

  useEffect(() => {
    setHasHydrated(true);
  }, []);

  async function submit(event: FormEvent<HTMLFormElement>) {
    event.preventDefault();
    setError(null);
    setIsSubmitting(true);

    try {
      const response = await fetch("/api/auth/login", {
        method: "POST",
        headers: {
          "content-type": "application/json",
        },
        body: JSON.stringify({ userName, password }),
      });

      if (!response.ok) {
        setError(await getErrorMessage(response));
        return;
      }

      router.replace(nextPath);
      router.refresh();
    } catch {
      setError("Unable to sign in to the Workable admin UI.");
    } finally {
      setIsSubmitting(false);
    }
  }

  return (
    <AuthShell>
      <Card className="border-border/70 bg-card/95 shadow-2xl shadow-black/20 backdrop-blur">
        <CardHeader className="space-y-4">
          <div className="flex justify-center">
            <WorkableLogo className="h-10 w-auto object-contain" priority />
          </div>
        </CardHeader>
        <CardContent>
          {authProvider === "entra" ? (
            <div className="space-y-4">
              {error && (
                <Alert variant="destructive">
                  <AlertTitle>{errorTitle}</AlertTitle>
                  <AlertDescription>{error}</AlertDescription>
                </Alert>
              )}

              <Button asChild className="w-full" size="lg">
                <a href={`/api/auth/entra/login?next=${encodeURIComponent(nextPath)}`}>
                  Sign in with Microsoft
                </a>
              </Button>
            </div>
          ) : !hasHydrated ? (
            // Password managers can mutate server-rendered inputs before React hydrates.
            <BasicLoginHydrationPlaceholder error={error} errorTitle={errorTitle} />
          ) : (
            <form className="space-y-4" onSubmit={submit}>
              <FormField htmlFor="userName" label="Username" maxWidth="none">
                <Input
                  autoComplete="username"
                  autoFocus
                  id="userName"
                  name="userName"
                  onChange={(event) => setUserName(event.target.value)}
                  required
                  value={userName}
                />
              </FormField>
              <FormField htmlFor="password" label="Password" maxWidth="none">
                <Input
                  autoComplete="current-password"
                  id="password"
                  name="password"
                  onChange={(event) => setPassword(event.target.value)}
                  required
                  type="password"
                  value={password}
                />
              </FormField>

              {error && (
                <Alert variant="destructive">
                  <AlertTitle>{errorTitle}</AlertTitle>
                  <AlertDescription>{error}</AlertDescription>
                </Alert>
              )}

              <Button className="w-full" disabled={isSubmitting} size="lg" type="submit">
                {isSubmitting ? "Signing in..." : "Sign in"}
              </Button>
            </form>
          )}
        </CardContent>
      </Card>
    </AuthShell>
  );
}

function BasicLoginHydrationPlaceholder({
  error,
  errorTitle,
}: {
  error: string | null;
  errorTitle: string;
}) {
  return (
    <div aria-busy="true" className="space-y-4">
      <p className="text-sm text-muted-foreground">Preparing secure sign-in...</p>
      <div className="space-y-3">
        <div className="grid gap-2">
          <Skeleton className="h-4 w-20" />
          <Skeleton className="h-8 w-full rounded-lg" />
        </div>
        <div className="grid gap-2">
          <Skeleton className="h-4 w-20" />
          <Skeleton className="h-8 w-full rounded-lg" />
        </div>
      </div>

      {error && (
        <Alert variant="destructive">
          <AlertTitle>{errorTitle}</AlertTitle>
          <AlertDescription>{error}</AlertDescription>
        </Alert>
      )}

      <Button className="w-full" disabled size="lg" type="button">
        Sign in
      </Button>
    </div>
  );
}

async function getErrorMessage(response: Response) {
  try {
    const body = await response.json();
    return typeof body.error === "string" && body.error.trim()
      ? body.error
      : `Sign in failed with ${response.status}.`;
  } catch {
    return `Sign in failed with ${response.status}.`;
  }
}

function getErrorTitle(error: string | null, reason: "unauthorized" | null) {
  if (reason === "unauthorized") {
    return "Unauthorized";
  }

  const normalized = error?.toLowerCase() ?? "";
  if (normalized.includes("unauthorized")) {
    return "Unauthorized";
  }

  if (normalized.includes("sign in again")) {
    return "Session expired";
  }

  return "Sign in failed";
}
