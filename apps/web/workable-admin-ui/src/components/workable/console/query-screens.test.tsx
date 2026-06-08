import assert from "node:assert/strict";
import test from "node:test";
import {
  DurationValue,
  IdentifierSummary,
  QueryResultTotal,
  QueryTablePlaceholder,
  QueryTableStatus,
  TypedValueSummary,
  appendUniqueIterations,
  appendUniqueWorkers,
  formatWorkerDuration,
  getIterationRowKey,
  getNextVisibleWorkerHighlight,
  getWorkerActions,
  isNewerIterationRow,
  isNewerWorkerRow,
  isObjectWithMessages,
  isStartableWorker,
  isWorkerNotFoundError,
} from "@/components/workable/console/query-screens";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";
import {
  WorkableApiError,
  type WorkViewIterationGridDetailed,
  type WorkViewWorkerGridDetailed,
} from "@/lib/workable";

function worker(overrides: Partial<WorkViewWorkerGridDetailed>): WorkViewWorkerGridDetailed {
  return {
    definitionName: "ImportOrders",
    id: { value: "worker-1" },
    isFinal: false,
    revision: 1,
    state: "Queued",
    updatedAt: "2026-05-30T10:00:00.000Z",
    ...overrides,
  };
}

function iteration(overrides: Partial<WorkViewIterationGridDetailed>): WorkViewIterationGridDetailed {
  return {
    completedAt: "2026-05-30T10:00:00.000Z",
    definitionName: "ImportOrders",
    executionDuration: "00:00:01",
    isFinal: true,
    sequence: 1,
    status: "Completed",
    workerId: { value: "worker-1" },
    workerState: "Completed",
    ...overrides,
  };
}

test("typed value summaries render empty, limited, overflow, and identifier alias paths", () => {
  assertMarkupIncludes(renderMarkup(<TypedValueSummary values={[]} />), "-");
  assertMarkupIncludes(renderMarkup(<IdentifierSummary identifiers={null} />), "-");

  const markup = renderMarkup(
    <TypedValueSummary
      values={[
        { type: "Order", value: "100" },
        { type: "Customer", value: "Ada" },
        { type: "Region", value: "West" },
        { type: "Extra", value: "Hidden" },
        { type: "Another", value: "Also hidden" },
      ]}
    />
  );

  assertMarkupIncludes(markup, "Order");
  assertMarkupIncludes(markup, "Customer");
  assertMarkupIncludes(markup, "Region");
  assertMarkupIncludes(markup, "more");
  assertMarkupIncludes(markup, ">2<");
});

test("query table helper components render status, totals, placeholders, and duration tones", () => {
  assertMarkupIncludes(renderMarkup(<QueryTableStatus label="Loading workers" />), "Loading workers");
  assertMarkupIncludes(renderMarkup(<QueryTablePlaceholder />), "border-dashed");
  assert.equal(renderMarkup(<QueryResultTotal noun="worker" />), "");
  assertMarkupIncludes(renderMarkup(<QueryResultTotal noun="worker" totalCount={1} />), "1 worker");
  assertMarkupIncludes(renderMarkup(<QueryResultTotal noun="worker" totalCount={2} />), "2 workers");
  assertMarkupIncludes(
    renderMarkup(<DurationValue duration={{ isWarning: true, text: "2.00m" }} />),
    "text-[var(--status-warning-text)]"
  );
});

test("worker action helpers cover state action menus and startability", () => {
  assert.deepEqual(getWorkerActions("Queued"), ["Start", "Cancel"]);
  assert.deepEqual(getWorkerActions("Paused"), ["Start", "Cancel"]);
  assert.deepEqual(getWorkerActions("Failed"), ["Start", "Cancel"]);
  assert.deepEqual(getWorkerActions("Running"), ["Pause", "Cancel"]);
  assert.deepEqual(getWorkerActions("Waiting"), ["Pause", "Push", "Cancel"]);
  assert.deepEqual(getWorkerActions("Retrying"), ["Pause", "Push", "Cancel"]);
  assert.deepEqual(getWorkerActions("Interrupted"), ["Cancel"]);
  assert.deepEqual(getWorkerActions("Canceled"), ["Purge"]);
  assert.deepEqual(getWorkerActions("Completed"), ["Purge"]);
  assert.deepEqual(getWorkerActions("Interrupting"), []);
  assert.deepEqual(getWorkerActions("Canceling"), []);
  assert.deepEqual(getWorkerActions("Pausing"), []);
  assert.equal(isStartableWorker("Queued"), true);
  assert.equal(isStartableWorker("Paused"), true);
  assert.equal(isStartableWorker("Failed"), true);
  assert.equal(isStartableWorker("Running"), false);
});

test("worker error and highlight helpers identify not-found purges and fallback rows", () => {
  assert.equal(isObjectWithMessages({ messages: [] }), true);
  assert.equal(isObjectWithMessages({ messages: {} }), false);
  assert.equal(
    isWorkerNotFoundError(new WorkableApiError("missing", 404, {
      messages: [{ code: "workable.worker.not_found" }],
    })),
    true
  );
  assert.equal(
    isWorkerNotFoundError(new WorkableApiError("missing", 404, {
      messages: [{ code: "other" }],
    })),
    false
  );
  assert.equal(isWorkerNotFoundError(new Error("missing")), false);

  const workers = [
    worker({ id: { value: "first" } }),
    worker({ id: { value: "middle" } }),
    worker({ id: { value: "last" } }),
  ];
  assert.deepEqual(getNextVisibleWorkerHighlight(workers, "missing"), null);
  assert.deepEqual(getNextVisibleWorkerHighlight(workers, "first"), {
    fallbackIndex: 0,
    workerId: null,
  });
  assert.deepEqual(getNextVisibleWorkerHighlight(workers, "last"), {
    fallbackIndex: 1,
    workerId: null,
  });
});

test("query row merge helpers replace duplicate workers and iterations only with newer rows", () => {
  assert.equal(
    isNewerWorkerRow(
      worker({ revision: 2, updatedAt: "2026-05-30T10:00:00.000Z" }),
      worker({ revision: 1, updatedAt: "2026-05-30T11:00:00.000Z" })
    ),
    true
  );
  assert.equal(
    isNewerWorkerRow(
      worker({ revision: 2, updatedAt: "2026-05-30T11:00:00.000Z" }),
      worker({ revision: 1, updatedAt: "2026-05-30T10:00:00.000Z" })
    ),
    false
  );
  assert.deepEqual(
    appendUniqueWorkers(
      [worker({ id: { value: "a" }, revision: 1 }), worker({ id: { value: "b" }, revision: 1 })],
      [worker({ id: { value: "a" }, revision: 2 }), worker({ id: { value: "c" }, revision: 1 })]
    ).map((entry) => `${entry.id.value}:${entry.revision}`),
    ["a:2", "b:1", "c:1"]
  );

  assert.equal(getIterationRowKey(iteration({ workerId: { value: "worker-7" }, sequence: 3 })), "worker-7:3");
  assert.equal(
    isNewerIterationRow(
      iteration({ completedAt: "2026-05-30T10:00:00.000Z" }),
      iteration({ completedAt: "2026-05-30T10:00:01.000Z" })
    ),
    true
  );
  assert.deepEqual(
    appendUniqueIterations(
      [
        iteration({ sequence: 1, completedAt: "2026-05-30T10:00:00.000Z" }),
        iteration({ sequence: 2, completedAt: "2026-05-30T10:00:00.000Z" }),
      ],
      [
        iteration({ sequence: 1, completedAt: "2026-05-30T10:00:01.000Z" }),
        iteration({ sequence: 3, completedAt: "2026-05-30T10:00:00.000Z" }),
      ]
    ).map((entry) => `${getIterationRowKey(entry)}:${entry.completedAt}`),
    [
      "worker-1:1:2026-05-30T10:00:01.000Z",
      "worker-1:2:2026-05-30T10:00:00.000Z",
      "worker-1:3:2026-05-30T10:00:00.000Z",
    ]
  );
  assert.deepEqual(formatWorkerDuration(worker({ totalExecutionDuration: undefined })), {
    isWarning: false,
    text: "-",
  });
  assert.deepEqual(formatWorkerDuration(worker({ totalExecutionDuration: "00:00:12.3450000" })), {
    isWarning: false,
    text: "12.35s",
  });
});
