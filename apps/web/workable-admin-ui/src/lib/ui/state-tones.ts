export type SemanticStateTone =
  | "danger"
  | "info"
  | "neutral"
  | "success"
  | "warning";

type ToneClassGroup = {
  badge: string;
  dot: string;
  icon: string;
  indicator: string;
  text: string;
  textStrong: string;
};

const toneClassNames: Record<SemanticStateTone, ToneClassGroup> = {
  danger: {
    badge:
      "border-[var(--status-danger-border)] bg-[var(--status-danger-soft)] text-[var(--status-danger-text)]",
    dot: "bg-[var(--status-danger-solid)]",
    icon: "text-[var(--status-danger-text)]",
    indicator:
      "bg-[var(--status-danger-solid)] text-[var(--status-danger-contrast)]",
    text: "text-[var(--status-danger-text)]",
    textStrong: "text-[var(--status-danger-strong)]",
  },
  info: {
    badge:
      "border-[var(--status-info-border)] bg-[var(--status-info-soft)] text-[var(--status-info-text)]",
    dot: "bg-[var(--status-info-solid)]",
    icon: "text-[var(--status-info-text)]",
    indicator:
      "bg-[var(--status-info-solid)] text-[var(--status-info-contrast)]",
    text: "text-[var(--status-info-text)]",
    textStrong: "text-[var(--status-info-strong)]",
  },
  neutral: {
    badge:
      "border-[var(--status-neutral-border)] bg-[var(--status-neutral-soft)] text-[var(--status-neutral-text)]",
    dot: "bg-[var(--status-neutral-solid)]",
    icon: "text-[var(--status-neutral-text)]",
    indicator:
      "bg-[var(--status-neutral-solid)] text-[var(--status-neutral-contrast)]",
    text: "text-[var(--status-neutral-text)]",
    textStrong: "text-[var(--status-neutral-strong)]",
  },
  success: {
    badge:
      "border-[var(--status-success-border)] bg-[var(--status-success-soft)] text-[var(--status-success-text)]",
    dot: "bg-[var(--status-success-solid)]",
    icon: "text-[var(--status-success-text)]",
    indicator:
      "bg-[var(--status-success-solid)] text-[var(--status-success-contrast)]",
    text: "text-[var(--status-success-text)]",
    textStrong: "text-[var(--status-success-strong)]",
  },
  warning: {
    badge:
      "border-[var(--status-warning-border)] bg-[var(--status-warning-soft)] text-[var(--status-warning-text)]",
    dot: "bg-[var(--status-warning-solid)]",
    icon: "text-[var(--status-warning-text)]",
    indicator:
      "bg-[var(--status-warning-solid)] text-[var(--status-warning-contrast)]",
    text: "text-[var(--status-warning-text)]",
    textStrong: "text-[var(--status-warning-strong)]",
  },
};

const toneGlowShadowClasses: Record<SemanticStateTone, string> = {
  danger: "shadow-[0_0_14px_var(--status-danger-glow)]",
  info: "shadow-[0_0_14px_var(--status-info-glow)]",
  neutral: "shadow-[0_0_14px_var(--status-neutral-glow)]",
  success: "shadow-[0_0_14px_var(--status-success-glow)]",
  warning: "shadow-[0_0_14px_var(--status-warning-glow)]",
};

const namedStateTones: Record<string, SemanticStateTone> = {
  active: "info",
  archived: "neutral",
  canceled: "warning",
  cancelled: "warning",
  canceling: "warning",
  complete: "success",
  completed: "success",
  connected: "success",
  connecting: "info",
  created: "neutral",
  critical: "danger",
  danger: "danger",
  debug: "neutral",
  disabled: "neutral",
  disconnected: "danger",
  draft: "neutral",
  enabled: "success",
  error: "danger",
  executing: "info",
  failed: "danger",
  failure: "danger",
  healthy: "success",
  inactive: "neutral",
  interrupted: "warning",
  interrupting: "warning",
  invalid: "warning",
  info: "info",
  information: "info",
  notfound: "neutral",
  paused: "warning",
  pausing: "warning",
  pending: "info",
  processing: "info",
  published: "success",
  queued: "info",
  reconnecting: "warning",
  retrying: "warning",
  running: "info",
  started: "success",
  starting: "info",
  stopped: "neutral",
  stopping: "info",
  success: "success",
  trace: "neutral",
  unavailable: "neutral",
  unhealthy: "danger",
  unknown: "neutral",
  waiting: "info",
  warning: "warning",
};

function normalizeStateKey(value?: string | null) {
  return (value ?? "").trim().toLowerCase().replace(/[\s_-]+/g, "");
}

export function semanticToneForStateName(value?: string | null): SemanticStateTone {
  return namedStateTones[normalizeStateKey(value)] ?? "neutral";
}

export function semanticToneForRealtimeConnectionState(
  connectionState: string,
  enabled: boolean
): SemanticStateTone {
  if (!enabled) {
    return "neutral";
  }

  return semanticToneForStateName(connectionState);
}

export function semanticToneForFeedbackTone(
  tone: "error" | "info" | "success" | "warning"
): SemanticStateTone {
  switch (tone) {
    case "error":
      return "danger";
    case "success":
      return "success";
    case "warning":
      return "warning";
    default:
      return "info";
  }
}

export function semanticToneForNotificationTone(
  tone: "critical" | "warning"
): SemanticStateTone {
  return tone === "critical" ? "danger" : "warning";
}

export function semanticBadgeToneClass(tone: SemanticStateTone) {
  return toneClassNames[tone].badge;
}

export function semanticTextToneClass(
  tone: SemanticStateTone,
  emphasis: "default" | "strong" = "default"
) {
  return emphasis === "strong"
    ? toneClassNames[tone].textStrong
    : toneClassNames[tone].text;
}

export function semanticIconToneClass(tone: SemanticStateTone) {
  return toneClassNames[tone].icon;
}

export function semanticDotToneClass(tone: SemanticStateTone) {
  return toneClassNames[tone].dot;
}

export function semanticIndicatorToneClass(tone: SemanticStateTone) {
  return toneClassNames[tone].indicator;
}

export function semanticGlowShadowClass(tone: SemanticStateTone) {
  return toneGlowShadowClasses[tone];
}

export function semanticColorValue(
  tone: SemanticStateTone,
  emphasis: "solid" | "text" | "strong" = "solid"
) {
  return `var(--status-${tone}-${emphasis})`;
}

export function semanticToneForEventType(eventType: string): SemanticStateTone {
  const normalized = eventType.trim().toLowerCase();
  if (normalized === "worker.log" || normalized.includes("failed")) {
    return "danger";
  }
  if (normalized.includes("completed")) {
    return "success";
  }
  if (
    normalized.includes("cancel") ||
    normalized.includes("paused") ||
    normalized.includes("pause") ||
    normalized.includes("retrying")
  ) {
    return "warning";
  }
  if (
    normalized.includes("queued") ||
    normalized.includes("started") ||
    normalized.includes("start") ||
    normalized.includes("waiting") ||
    normalized.includes("stopping") ||
    normalized.includes("stop") ||
    normalized.includes("push") ||
    normalized.includes("purge")
  ) {
    return "info";
  }

  return "neutral";
}
