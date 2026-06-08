"use client";

import { CheckCircle2, CircleDot, ShieldAlert, TriangleAlert, X } from "lucide-react";
import { useState } from "react";
import { Alert, AlertAction, AlertDescription, AlertTitle } from "@/components/ui/alert";
import {
  semanticBadgeToneClass,
  semanticTextToneClass,
  semanticToneForFeedbackTone,
} from "@/lib/ui/state-tones";

export type FeedbackTone = "error" | "info" | "success" | "warning";

const feedbackToneStyles = {
  error: {
    button:
      `${semanticTextToneClass("danger")} hover:bg-[var(--status-danger-soft)] hover:text-[var(--status-danger-strong)]`,
    className: `pr-10 ${semanticBadgeToneClass(semanticToneForFeedbackTone("error"))}`,
    icon: ShieldAlert,
  },
  info: {
    button:
      `${semanticTextToneClass("info")} hover:bg-[var(--status-info-soft)] hover:text-[var(--status-info-strong)]`,
    className: `pr-10 ${semanticBadgeToneClass(semanticToneForFeedbackTone("info"))}`,
    icon: CircleDot,
  },
  success: {
    button:
      `${semanticTextToneClass("success")} hover:bg-[var(--status-success-soft)] hover:text-[var(--status-success-strong)]`,
    className: `pr-10 ${semanticBadgeToneClass(semanticToneForFeedbackTone("success"))}`,
    icon: CheckCircle2,
  },
  warning: {
    button:
      `${semanticTextToneClass("warning")} hover:bg-[var(--status-warning-soft)] hover:text-[var(--status-warning-strong)]`,
    className: `pr-10 ${semanticBadgeToneClass(semanticToneForFeedbackTone("warning"))}`,
    icon: TriangleAlert,
  },
} satisfies Record<FeedbackTone, {
  button: string;
  className: string;
  icon: typeof ShieldAlert;
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
    <Alert className={toneStyle.className}>
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
