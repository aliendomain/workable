import assert from "node:assert/strict";
import test from "node:test";
import {
  chartPoint,
  chartY,
  createAreaPath,
  createEmptyThroughputBucket,
  createExecutionPressureMetric,
  createLinePath,
  createRealtimeEventMessage,
  createThroughputMetrics,
  createThroughputSeries,
  createTimeAxisTicks,
  createYAxisTicks,
  formatFailedWorkerDuration,
  formatIterationCount,
  formatMilliseconds,
  formatRate,
  formatThroughputAxisValue,
  formatThroughputWindowLabel,
  getNiceChartMax,
  getOverviewPanelShape,
  getThroughputBuckets,
  getWorkerRowActions,
  isDetailedWorkerOverviewItem,
  isThroughputSeriesId,
  isZeroOnlySeries,
  measureJsonBytes,
  parseChartTimestamp,
  pluralize,
  toFailedWorkerActionTarget,
} from "@/components/workable/console/overview-screen";
import type { OverviewPanelShapeMap } from "@/components/features/console/overview-panels";
import type { WorkSystemThroughput } from "@/lib/workable";

test("overview panel shape helper keeps supported shapes and falls back to panel defaults", () => {
  const shapes = {
    completedIterations: "compact",
    failedIterations: "detailed",
    failedWorkers: "standard",
    iterations: "detailed",
    throughput: "compact",
    workers: "unsupported",
  } as unknown as OverviewPanelShapeMap;

  assert.equal(getOverviewPanelShape(shapes, "throughput"), "compact");
  assert.equal(getOverviewPanelShape(shapes, "failedWorkers"), "standard");
  assert.equal(getOverviewPanelShape(shapes, "workers"), "standard");
  assert.equal(getOverviewPanelShape(shapes, "completedIterations"), "standard");
});

function throughput(overrides: Partial<WorkSystemThroughput> = {}): WorkSystemThroughput {
  return {
    bucketSeconds: 1,
    buckets: [],
    executionSummary: {
      averageExecutionMilliseconds: 1250,
      executionCount: 2,
      p95ExecutionMilliseconds: 2000,
      p99ExecutionMilliseconds: 2500,
      slowestExecutionMilliseconds: 3000,
    },
    from: "2026-05-30T10:00:00.000Z",
    liveSummary: {
      canceledPerSecond: 0,
      completedPerSecond: 2,
      failedPerSecond: 0.5,
      inFlightDeltaPerSecond: 1.25,
      rateWindowSeconds: 60,
      startedPerSecond: 4,
    },
    settledCount: 5,
    to: "2026-05-30T10:00:03.000Z",
    windowSeconds: 3,
    ...overrides,
  };
}

test("overview worker helpers expose row actions and detailed failed-worker detection", () => {
  assert.deepEqual(getWorkerRowActions({ definitionName: "Import", id: { value: "w1" }, revision: 1, state: "Queued" }), ["Start", "Cancel"]);
  assert.deepEqual(getWorkerRowActions({ definitionName: "Import", id: { value: "w1" }, revision: 1, state: "Running" }), ["Cancel"]);
  assert.deepEqual(getWorkerRowActions({ definitionName: "Import", id: { value: "w1" }, revision: 1, state: "Completed" }), []);
  assert.deepEqual(toFailedWorkerActionTarget({
    definitionName: "Import",
    id: { value: "w1" },
    revision: 7,
    updatedAt: "2026-05-30T10:00:00.000Z",
  }), {
    definitionName: "Import",
    id: { value: "w1" },
    revision: 7,
    state: "Failed",
  });
  assert.equal(isDetailedWorkerOverviewItem({
    definitionName: "Import",
    id: { value: "w1" },
    identifiers: [],
    revision: 1,
    state: "Failed",
    updatedAt: "2026-05-30T10:00:00.000Z",
  }), true);
  assert.equal(isDetailedWorkerOverviewItem({
    definitionName: "Import",
    id: { value: "w1" },
    revision: 1,
    updatedAt: "2026-05-30T10:00:00.000Z",
  }), false);
});

test("throughput bucket and chart helpers normalize missing data and format series", () => {
  const buckets = getThroughputBuckets(throughput({
    buckets: [
      {
        at: "2026-05-30T10:00:01.000Z",
        averageExecutionMilliseconds: 100,
        canceled: 0,
        completed: 1,
        failed: 0,
        started: 2,
      },
      {
        at: "2026-05-30T10:00:03.000Z",
        averageExecutionMilliseconds: 300,
        canceled: 1,
        completed: 2,
        failed: 1,
        started: 4,
      },
    ],
  }));
  assert.deepEqual(
    buckets.map((bucket) => ({
      at: bucket.at,
      completed: bucket.completed,
      started: bucket.started,
    })),
    [
      { at: "2026-05-30T10:00:01.000Z", completed: 1, started: 2 },
      { at: "2026-05-30T10:00:02.000Z", completed: 0, started: 0 },
      { at: "2026-05-30T10:00:03.000Z", completed: 2, started: 4 },
    ]
  );
  assert.deepEqual(createEmptyThroughputBucket(0), {
    at: "1970-01-01T00:00:00.000Z",
    averageExecutionMilliseconds: 0,
    canceled: 0,
    completed: 0,
    failed: 0,
    started: 0,
  });
  assert.deepEqual(createThroughputSeries("execution", buckets, 1).map((series) => ({
    id: series.id,
    values: series.values,
  })), [{ id: "execution-average", values: [100, 0, 300] }]);
  assert.deepEqual(createThroughputSeries("completion", buckets, 2)[0].values, [1, 0, 2]);
  assert.equal(createLinePath([], 10), "");
  assert.equal(createLinePath([0, 10], 10), "M 0.00 190.00 L 1000.00 20.00");
  assert.equal(createAreaPath([], 10), "");
  assert.equal(createAreaPath([0, 10], 10), "M 0.00 190.00 L 1000.00 20.00 L 1000.00 190.00 L 0.00 190.00 Z");
  assert.deepEqual(chartPoint(5, 0, 1, 10), { x: 0, y: 105 });
  assert.equal(chartY(0, 10), 190);
  assert.equal(isZeroOnlySeries([0, 0]), true);
  assert.equal(isZeroOnlySeries([]), false);
});

test("throughput metrics and axis helpers cover empty, completion, execution, and pressure branches", () => {
  assert.deepEqual(createThroughputMetrics("completion", undefined, 60).map((metric) => metric.id), [
    "started",
    "completed",
    "failed",
    "canceled",
    "execution-pressure",
    "total",
    "window-average",
  ]);
  assert.deepEqual(createThroughputMetrics("execution", undefined, 3600).map((metric) => metric.id), [
    "execution-average",
    "execution-p95",
    "execution-p99",
    "execution-slowest",
    "execution-count",
  ]);
  assert.deepEqual(createThroughputMetrics("completion", throughput(), 60).map((metric) => `${metric.id}:${metric.value}`), [
    "started:4.00/s",
    "completed:2.00/s",
    "failed:0.50/s",
    "canceled:0.00/s",
    "execution-pressure:+1.25/s",
    "total:5",
    "window-average:1.3s",
  ]);
  assert.deepEqual(createThroughputMetrics("execution", throughput(), 60).map((metric) => `${metric.id}:${metric.value}`), [
    "execution-average:1.3s",
    "execution-p95:2.0s",
    "execution-p99:2.5s",
    "execution-slowest:3.0s",
    "execution-count:2",
  ]);
  assert.equal(createExecutionPressureMetric({ ...throughput().liveSummary, inFlightDeltaPerSecond: -0.5 }).value, "-0.50/s");
  assert.equal(createExecutionPressureMetric({ ...throughput().liveSummary, inFlightDeltaPerSecond: 0 }).value, "0/s");
  assert.equal(formatThroughputWindowLabel(60), "60-second");
  assert.equal(formatThroughputWindowLabel(3600), "1-hour");
  assert.equal(formatThroughputWindowLabel(7200), "2-hour");
  assert.equal(formatThroughputWindowLabel(300), "5-minute");
  assert.equal(formatThroughputWindowLabel(45), "45-second");
  assert.equal(getNiceChartMax(0, "execution"), 100);
  assert.equal(getNiceChartMax(21, "completion"), 50);
  assert.deepEqual(createYAxisTicks(9), [9, 6, 3, 0]);
  assert.equal(formatThroughputAxisValue("execution", 1500), "1.5s");
  assert.equal(formatThroughputAxisValue("completion", 0.25), "0.25/s");
  assert.equal(parseChartTimestamp(undefined), null);
  assert.equal(parseChartTimestamp("not-a-date"), null);
  assert.equal(parseChartTimestamp("2026-05-30T10:00:00.000Z"), Date.parse("2026-05-30T10:00:00.000Z"));
  assert.deepEqual(createTimeAxisTicks(throughput(), getThroughputBuckets(throughput())).map((tick) => tick.position), [0, 0.25, 0.5, 0.75, 1]);
  assert.equal(formatRate(100), "100");
  assert.equal(formatRate(10), "10.0");
  assert.equal(formatRate(1), "1.00");
  assert.equal(formatRate(0.5), "0.50");
  assert.equal(formatMilliseconds(999), "999ms");
  assert.equal(formatMilliseconds(1500), "1.5s");
  assert.equal(formatMilliseconds(60_000), "60s");
  assert.equal(pluralize("iteration", 1), "iteration");
  assert.equal(pluralize("iteration", 2), "iterations");
  assert.equal(isThroughputSeriesId("started"), true);
  assert.equal(isThroughputSeriesId("other"), false);
  assert.equal(formatIterationCount(1), "1 iteration");
  assert.equal(formatIterationCount(2), "2 iterations");
  assert.deepEqual(formatFailedWorkerDuration({
    definitionName: "Import",
    id: { value: "worker-1" },
    revision: 1,
    updatedAt: "2026-05-30T10:00:00.000Z",
  }), { isWarning: false, text: "-" });
});

test("realtime event messages and byte measurement cover single, batch, budget, and circular payloads", () => {
  const single = createRealtimeEventMessage([
    {
      eventType: "worker.completed",
      sentAt: "2026-05-30T10:00:00.000Z",
      workerId: { value: "worker-1" },
      workDefinitionId: { value: "definition-1" },
    } as never,
  ], "event-1", Date.parse("2026-05-30T10:00:01.000Z"));
  assert.equal(single.batchId, undefined);
  assert.equal(single.eventTypes[0], "worker.completed");
  assert.equal(single.bytesEstimated, false);

  const batch = createRealtimeEventMessage([
    { eventType: "worker.completed" } as never,
    { eventType: "worker.failed" } as never,
  ], "batch-1", Date.parse("2026-05-30T10:00:01.000Z"));
  assert.equal(batch.batchId, "batch-1");
  assert.equal(batch.batchSize, 2);
  assert.deepEqual(batch.eventTypes, ["worker.completed", "worker.failed"]);

  assert.deepEqual(measureJsonBytes("abcdef", 3), { bytes: 3, estimated: true });
  const circular: { self?: unknown } = {};
  circular.self = circular;
  assert.equal(measureJsonBytes(circular).estimated, true);
});
