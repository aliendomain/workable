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
    const interval = window.setInterval(() => setNow(Date.now()), 100);
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

  const deltaSeconds = (timestamp - now) / 1000;
  const absoluteSeconds = Math.abs(deltaSeconds);
  if (absoluteSeconds < 10) {
    const label = absoluteSeconds.toFixed(2);
    return deltaSeconds >= 0
      ? `in ${label}s`
      : `${label}s ago`;
  }

  if (absoluteSeconds < 60) {
    return relativeTimeFormatter.format(
      deltaSeconds >= 0 ? Math.ceil(deltaSeconds) : Math.floor(deltaSeconds),
      "second"
    );
  }
  if (absoluteSeconds < 60 * 60) {
    const deltaMinutes = deltaSeconds / 60;
    return relativeTimeFormatter.format(
      deltaMinutes >= 0 ? Math.ceil(deltaMinutes) : Math.floor(deltaMinutes),
      "minute"
    );
  }
  if (absoluteSeconds < 24 * 60 * 60) {
    const deltaHours = deltaSeconds / (60 * 60);
    return relativeTimeFormatter.format(
      deltaHours >= 0 ? Math.ceil(deltaHours) : Math.floor(deltaHours),
      "hour"
    );
  }

  const deltaDays = deltaSeconds / (24 * 60 * 60);
  return relativeTimeFormatter.format(
    deltaDays >= 0 ? Math.ceil(deltaDays) : Math.floor(deltaDays),
    "day"
  );
}
