import assert from "node:assert/strict";
import test from "node:test";
import {
  formatDuration,
  formatDurationSeconds,
  formatExecutionDuration,
  formatQueueAge,
  parseDurationSeconds,
  parseTimeSpanMilliseconds,
} from "./console-format.ts";

test("time span parsing handles days and fractional seconds", () => {
  assert.equal(parseTimeSpanMilliseconds("1.02:03:04.5000000"), 93_784_500);
  assert.equal(parseDurationSeconds("00:00:00.8000000"), 0.8);
  assert.equal(parseDurationSeconds("not-a-timespan"), null);
});

test("duration display preserves compact warning behavior", () => {
  assert.deepEqual(formatDurationSeconds(0), { isWarning: false, text: "~0s" });
  assert.deepEqual(formatExecutionDuration("00:00:12.3450000"), {
    isWarning: false,
    text: "12.35s",
  });
  assert.deepEqual(formatExecutionDuration("00:02:00"), {
    isWarning: true,
    text: "2.00m",
  });
});

test("diagnostic and queue age duration formatting stay stable", () => {
  assert.equal(formatDuration("00:00:00.1250000"), "125 ms");
  assert.deepEqual(
    formatQueueAge("2026-05-30T12:00:00.000Z", Date.parse("2026-05-30T12:01:30.000Z")),
    { isWarning: true, text: "1.50m" }
  );
  assert.deepEqual(formatQueueAge("not-a-date"), { isWarning: false, text: "-" });
});
