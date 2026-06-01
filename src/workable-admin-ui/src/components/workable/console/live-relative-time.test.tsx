import assert from "node:assert/strict";
import test from "node:test";
import {
  LiveRelativeTime,
  formatRelativeTime,
} from "@/components/workable/console/live-relative-time";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("relative time formatter covers empty, invalid, near, second, minute, hour, and day ranges", () => {
  const now = Date.parse("2026-05-30T12:00:00Z");

  assert.equal(formatRelativeTime(null, now), "-");
  assert.equal(formatRelativeTime("not a date", now), "-");
  assert.equal(formatRelativeTime("2026-05-30T12:00:04Z", now), "in 4.00s");
  assert.equal(formatRelativeTime("2026-05-30T11:59:56Z", now), "4.00s ago");
  assert.equal(formatRelativeTime("2026-05-30T12:00:30Z", now), "in 30 seconds");
  assert.equal(formatRelativeTime("2026-05-30T11:59:30Z", now), "30 seconds ago");
  assert.equal(formatRelativeTime("2026-05-30T12:12:00Z", now), "in 12 minutes");
  assert.equal(formatRelativeTime("2026-05-30T09:00:00Z", now), "3 hours ago");
  assert.equal(formatRelativeTime("2026-06-01T12:00:00Z", now), "in 2 days");
});

test("live relative time component renders the formatted value on the server", () => {
  const markup = renderMarkup(<LiveRelativeTime value={new Date(Date.now() - 4_000).toISOString()} />);
  assertMarkupIncludes(markup, "ago");
});
