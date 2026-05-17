"use client";

import { useEffect, useState } from "react";

const relativeTimeFormatter = new Intl.RelativeTimeFormat(undefined, { numeric: "always" });

export function LiveRelativeTime({ value }: { value?: string | null }) {
  const now = useLiveRelativeTimeNow();
  return <>{formatRelativeTime(value, now)}</>;
}

export function useLiveRelativeTimeNow() {
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const interval = window.setInterval(() => setNow(Date.now()), 1000);
    return () => window.clearInterval(interval);
  }, []);

  return now;
}

export function formatRelativeTime(value: string | null | undefined, now = Date.now()) {
  if (!value) {
    return "-";
  }

  const timestamp = Date.parse(value);
  if (!Number.isFinite(timestamp)) {
    return "-";
  }

  const elapsedSeconds = Math.max(0, (now - timestamp) / 1000);
  if (elapsedSeconds < 5) {
    return "just now";
  }

  if (elapsedSeconds < 60) {
    return relativeTimeFormatter.format(-Math.floor(elapsedSeconds), "second");
  }
  if (elapsedSeconds < 60 * 60) {
    return relativeTimeFormatter.format(-Math.floor(elapsedSeconds / 60), "minute");
  }
  if (elapsedSeconds < 24 * 60 * 60) {
    return relativeTimeFormatter.format(-Math.floor(elapsedSeconds / (60 * 60)), "hour");
  }

  return relativeTimeFormatter.format(-Math.floor(elapsedSeconds / (24 * 60 * 60)), "day");
}
