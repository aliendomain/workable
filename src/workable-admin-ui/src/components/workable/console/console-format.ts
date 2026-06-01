import type { WorkCompletionStatus } from "@/lib/workable";

export type DurationDisplay = {
  isWarning: boolean;
  text: string;
};

export function formatNumber(value?: number | null) {
  return typeof value === "number" ? value.toLocaleString() : "-";
}

export function formatLocalTime(value: Date) {
  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
  }).format(value);
}

export function formatDateTimeShort(value?: string | null) {
  if (!value) {
    return "-";
  }

  return new Intl.DateTimeFormat(undefined, {
    hour: "numeric",
    minute: "2-digit",
    second: "2-digit",
  }).format(new Date(value));
}

export function formatDuration(value?: string | null) {
  if (!value) {
    return "-";
  }

  const milliseconds = parseTimeSpanMilliseconds(value);
  if (milliseconds === null) {
    return value;
  }

  return `${milliseconds.toLocaleString(undefined, {
    maximumFractionDigits: milliseconds < 10 ? 3 : 1,
  })} ms`;
}

export function formatExecutionDuration(value?: string | null): DurationDisplay {
  const seconds = parseDurationSeconds(value);
  if (seconds === null) {
    return { isWarning: false, text: "-" };
  }

  return formatDurationSeconds(seconds);
}

export function formatDurationSeconds(seconds: number): DurationDisplay {
  if (seconds < 0.005) {
    return { isWarning: false, text: "~0s" };
  }
  if (seconds < 60) {
    return { isWarning: false, text: `${seconds.toFixed(2)}s` };
  }

  return { isWarning: true, text: `${(seconds / 60).toFixed(2)}m` };
}

export function formatQueueAge(value?: string | null, now = Date.now()): DurationDisplay {
  if (!value) {
    return { isWarning: false, text: "-" };
  }

  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return { isWarning: false, text: "-" };
  }

  return formatDurationSeconds(Math.max(0, (now - timestamp) / 1000));
}

export function parseDurationSeconds(value?: string | null) {
  if (!value) {
    return null;
  }

  const milliseconds = parseTimeSpanMilliseconds(value);
  return milliseconds === null ? null : milliseconds / 1000;
}

export function parseTimeSpanMilliseconds(value: string) {
  const match = /^(?:(\d+)\.)?(\d{1,2}):(\d{2}):(\d{2})(?:\.(\d+))?$/.exec(value);
  if (!match) {
    return null;
  }

  const days = Number(match[1] ?? 0);
  const hours = Number(match[2]);
  const minutes = Number(match[3]);
  const seconds = Number(match[4]);
  const fraction = match[5] ? Number(`0.${match[5]}`) : 0;
  return (((days * 24 + hours) * 60 + minutes) * 60 + seconds + fraction) * 1000;
}

export function completionTone(status: WorkCompletionStatus) {
  switch (status) {
    case "Executing":
      return "border-emerald-500/40 bg-emerald-500/10 text-emerald-300";
    case "Completed":
      return "border-emerald-500/40 bg-emerald-500/10 text-emerald-300";
    case "Failed":
    case "Canceled":
      return "bg-red-500/15 text-red-300 border-red-500/30";
    case "Paused":
    case "Interrupted":
      return "border-amber-500/40 bg-amber-500/10 text-amber-300";
    default:
      return "border-muted-foreground/30 text-muted-foreground";
  }
}
