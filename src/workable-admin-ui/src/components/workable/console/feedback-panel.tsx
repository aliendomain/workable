"use client";

import { CheckCircle2, CircleDot, ShieldAlert, TriangleAlert, X } from "lucide-react";
import { useState } from "react";
import { Alert, AlertAction, AlertDescription, AlertTitle } from "@/components/ui/alert";

export type FeedbackTone = "error" | "info" | "success" | "warning";

const feedbackToneStyles = {
  error: {
    button: "text-destructive/70 hover:bg-destructive/10 hover:text-destructive",
    className: "pr-10",
    icon: ShieldAlert,
    variant: "destructive" as const,
  },
  info: {
    button: "text-slate-300/80 hover:bg-slate-700/40 hover:text-white",
    className: "border-slate-600/80 bg-slate-950 pr-10 text-slate-50 shadow-lg",
    icon: CircleDot,
    variant: "default" as const,
  },
  success: {
    button: "text-emerald-300/70 hover:bg-emerald-500/10 hover:text-emerald-200",
    className: "border-emerald-500/30 bg-emerald-500/10 pr-10 text-emerald-100",
    icon: CheckCircle2,
    variant: "default" as const,
  },
  warning: {
    button: "text-amber-300/70 hover:bg-amber-500/10 hover:text-amber-200",
    className: "border-amber-500/30 bg-amber-500/10 pr-10 text-amber-100",
    icon: TriangleAlert,
    variant: "default" as const,
  },
} satisfies Record<FeedbackTone, {
  button: string;
  className: string;
  icon: typeof ShieldAlert;
  variant: "default" | "destructive";
}>;

export function ErrorPanel({
  errors,
  title = "Connection issue",
}: {
  errors: Array<string | undefined>;
  title?: string;
}) {
  return <FeedbackPanel messages={errors} title={title} tone="error" />;
}

export function FeedbackPanel({
  messages,
  title,
  tone,
}: {
  messages: Array<string | undefined>;
  title: string;
  tone: FeedbackTone;
}) {
  const visibleMessages = [...new Set(messages.filter((message): message is string => Boolean(message)))];
  const [dismissedMessages, setDismissedMessages] = useState<ReadonlySet<string>>(() => new Set());
  const activeMessages = visibleMessages.filter((message) => !dismissedMessages.has(message));

  if (activeMessages.length === 0) {
    return null;
  }

  return (
    <div className="space-y-2">
      {activeMessages.map((message) => (
        <FeedbackBanner
          key={message}
          message={message}
          onDismiss={() => setDismissedMessages((current) => new Set(current).add(message))}
          title={title}
          tone={tone}
        />
      ))}
    </div>
  );
}

export function ErrorBanner({
  message,
  onDismiss,
  title,
}: {
  message: string;
  onDismiss?: () => void;
  title: string;
}) {
  return (
    <FeedbackBanner
      message={message}
      onDismiss={onDismiss}
      title={title}
      tone="error"
    />
  );
}

export function FeedbackBanner({
  message,
  onDismiss,
  title,
  tone,
}: {
  message: string;
  onDismiss?: () => void;
  title: string;
  tone: FeedbackTone;
}) {
  const [dismissed, setDismissed] = useState(false);

  if (!message || dismissed) {
    return null;
  }

  const toneStyle = feedbackToneStyles[tone];
  const Icon = toneStyle.icon;

  return (
    <Alert className={toneStyle.className} variant={toneStyle.variant}>
      <Icon className="size-4" />
      <AlertTitle>{title}</AlertTitle>
      <AlertDescription>{message}</AlertDescription>
      <AlertAction>
        <button
          aria-label="Dismiss message"
          className={`inline-flex size-6 items-center justify-center rounded-sm ${toneStyle.button}`}
          onClick={() => {
            setDismissed(true);
            onDismiss?.();
          }}
          type="button"
        >
          <X className="size-3.5" />
        </button>
      </AlertAction>
    </Alert>
  );
}
