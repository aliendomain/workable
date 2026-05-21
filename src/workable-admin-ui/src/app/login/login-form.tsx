"use client";

import { useState } from "react";
import type { FormEvent } from "react";
import Image from "next/image";
import { useRouter } from "next/navigation";
import { LockKeyhole } from "lucide-react";
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
  CardTitle,
} from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import type { AdminAuthProvider } from "@/lib/admin-security";

export function LoginForm({
  authProvider,
  initialError,
  nextPath,
}: {
  authProvider: AdminAuthProvider;
  initialError: string | null;
  nextPath: string;
}) {
  const router = useRouter();
  const [userName, setUserName] = useState("");
  const [password, setPassword] = useState("");
  const [error, setError] = useState<string | null>(initialError);
  const [isSubmitting, setIsSubmitting] = useState(false);

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
    <main className="relative flex min-h-svh items-center justify-center overflow-hidden bg-background px-6 py-12 text-foreground">
      <div className="absolute inset-0 -z-10 bg-[radial-gradient(circle_at_top_left,oklch(0.58_0.16_170_/_0.24),transparent_28rem),radial-gradient(circle_at_bottom_right,oklch(0.72_0.12_80_/_0.18),transparent_24rem)]" />
      <section className="w-full max-w-md">
        <div className="mb-6 flex justify-center">
          <Image
            alt="Workable"
            className="h-14 w-auto object-contain"
            height={70}
            priority
            src="/workable-logo-transparent.png"
            width={280}
          />
        </div>

        <Card className="border-border/70 bg-card/95 shadow-2xl shadow-black/20 backdrop-blur">
          <CardHeader>
            <CardTitle className="flex items-center gap-2 text-xl">
              <LockKeyhole className="size-5" />
              Sign in
            </CardTitle>
          </CardHeader>
          <CardContent>
            {authProvider === "entra" ? (
              <div className="space-y-4">
                {error && (
                  <Alert variant="destructive">
                    <AlertTitle>Sign in failed</AlertTitle>
                    <AlertDescription>{error}</AlertDescription>
                  </Alert>
                )}

                <Button asChild className="w-full" size="lg">
                  <a href={`/api/auth/entra/login?next=${encodeURIComponent(nextPath)}`}>
                    Sign in with Microsoft
                  </a>
                </Button>
              </div>
            ) : (
              <form className="space-y-4" onSubmit={submit}>
                <div className="space-y-2">
                  <Label htmlFor="userName">Username</Label>
                  <Input
                    autoComplete="username"
                    autoFocus
                    id="userName"
                    name="userName"
                    onChange={(event) => setUserName(event.target.value)}
                    required
                    value={userName}
                  />
                </div>
                <div className="space-y-2">
                  <Label htmlFor="password">Password</Label>
                  <Input
                    autoComplete="current-password"
                    id="password"
                    name="password"
                    onChange={(event) => setPassword(event.target.value)}
                    required
                    type="password"
                    value={password}
                  />
                </div>

                {error && (
                  <Alert variant="destructive">
                    <AlertTitle>Sign in failed</AlertTitle>
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
      </section>
    </main>
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
