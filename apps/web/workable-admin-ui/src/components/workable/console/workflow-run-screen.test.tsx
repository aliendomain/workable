import assert from "node:assert/strict";
import test from "node:test";
import { act } from "react";
import { ConsoleHeaderCapabilitiesProvider } from "@/components/features/console/header-capabilities";
import {
  WorkflowRunConsoleView,
  createWorkflowGraphRenderModel,
  createWorkflowRunDetailPath,
} from "@/components/workable/console/workflow-run-screen";
import { renderDom } from "@/test/dom";
import type { WorkableConnection, WorkflowRunDetailView } from "@/lib/workable";

const connection: WorkableConnection = {
  apiUrl: "https://console.example.com/workable",
  systemName: "Ops",
};

test("workflow run detail path includes the requested child sample size", () => {
  assert.equal(
    createWorkflowRunDetailPath("run-123", { childSampleSize: 12 }),
    "workflow-runs/run-123?childSampleSize=12"
  );
});

test("workflow graph render model preserves nested branch structure without joins", () => {
  const model = createWorkflowGraphRenderModel(
    nestedBranchWorkflowRun().steps,
    {
      currentStepName: "branch-parallel-right",
      selectedStepName: "branch-finalize",
    }
  );

  assert.deepEqual(
    model.sequence.map((node) => node.name),
    ["prepare-nested", "outer-fan-out", "finish-nested"]
  );
  assert.deepEqual(model.hiddenJoinNames, [
    "branch-inner-join",
    "branch-join",
    "outer-join",
  ]);
  assert.deepEqual(model.currentPath, [
    "outer-fan-out",
    "branch-docs",
    "branch-parallel",
    "branch-parallel-right",
  ]);
  assert.deepEqual(model.selectedPath, [
    "outer-fan-out",
    "branch-docs",
    "branch-finalize",
  ]);

  const outerParallel = model.sequence[1];
  assert.equal(outerParallel.kind, "Parallel");
  assert.equal(outerParallel.containsCurrent, true);
  assert.equal(outerParallel.containsSelected, true);
  assert.deepEqual(
    outerParallel.branchLanes.map((lane) => lane.rootStepName),
    ["branch-docs", "branch-notify"]
  );

  const branchDocs = outerParallel.branchLanes[0].steps[0];
  assert.equal(branchDocs.kind, "Branch");
  assert.equal(branchDocs.containsCurrent, true);
  assert.equal(branchDocs.containsSelected, true);
  assert.deepEqual(
    branchDocs.nestedSequence.map((node) => node.name),
    ["branch-collect", "branch-parse", "branch-parallel", "branch-finalize"]
  );

  const branchParallel = branchDocs.nestedSequence[2];
  assert.equal(branchParallel.kind, "Parallel");
  assert.equal(branchParallel.containsCurrent, true);
  assert.equal(branchParallel.containsSelected, false);
  assert.deepEqual(
    branchParallel.branchLanes.map((lane) => lane.rootStepName),
    ["branch-parallel-left", "branch-parallel-right"]
  );
  assert.equal(branchParallel.branchLanes[1].containsCurrent, true);
});

test("workflow run screen renders structure nodes and drills into workers from the selected node", async () => {
  const openedWorkers: string[] = [];
  const scrolledNodeSnapshots: string[] = [];
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(parallelWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, parallelWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={(workerId) => openedWorkers.push(workerId)}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>,
    {
      setupWindow(window) {
        window.HTMLElement.prototype.scrollIntoView = function scrollIntoView() {
          scrolledNodeSnapshots.push(this.textContent ?? "");
        };
      },
    }
  );

  try {
    await result.waitFor(() => result.getByText("Workflow Graph"));
    result.getByLabelText(/Workflow worker progress/i);
    result.getByText("Current node details");
    result.getByText("fan-out");
    assert.equal(result.container.querySelectorAll('[aria-label="Executing"]').length >= 1, true);
    result.getByText("Completed");
    assert.equal(result.queryByText(/^Messages \(/), null);
    await result.waitFor(() => {
      assert.equal(
        scrolledNodeSnapshots.some((snapshot) => snapshot.includes("fan-out")),
        true
      );
    });
    result.getByRole("button", { name: /ImportInvoice/i });
    result.getByRole("button", { name: /ExportLedger/i });
    await result.click(result.getByRole("button", { name: /ImportInvoice/i }));
    assert.deepEqual(openedWorkers, ["worker-child-1"]);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen switches the worker list when a workflow node is clicked", async () => {
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(parallelWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, parallelWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: /ImportInvoice/i }));
    result.getByRole("button", { name: /ExportLedger/i });

    await result.click(result.getByRole("button", { name: /prepare/i }));

    await result.waitFor(() => result.getByRole("button", { name: /PrepareInvoices/i }));
    assert.equal(result.queryByText("ImportInvoice"), null);
    assert.equal(result.queryByText("ExportLedger"), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen restores the selected node from saved ui state", async () => {
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(parallelWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, parallelWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        initialUiState={{ autoFollowCurrentStep: false, runId: "run-123", selectedStepName: "prepare" }}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: /PrepareInvoices/i }));
    result.getByText("prepare");
    assert.equal(result.queryByText("ImportInvoice"), null);
    assert.equal(result.queryByText("ExportLedger"), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen highlights and restores the selected child worker row", async () => {
  const openedWorkers: string[] = [];
  const openedWorkerUiStates: unknown[] = [];
  let latestUiState: unknown = null;
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(parallelWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, parallelWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={(workerId, uiState) => {
          openedWorkers.push(workerId);
          openedWorkerUiStates.push(uiState);
        }}
        onRealtimePayloadOpenChange={() => undefined}
        onUiStateChange={(state) => {
          latestUiState = state;
        }}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: /ImportInvoice/i }));
    const workflowGraphScroll = result.container.querySelector(".workable-grid-scrollbar");
    assert.ok(workflowGraphScroll instanceof result.dom.window.HTMLElement);
    await result.scroll(workflowGraphScroll, {
      clientHeight: 360,
      scrollHeight: 1200,
      scrollTop: 148,
    });
    const importWorkerButton = result.getByRole("button", { name: /ImportInvoice/i });
    assert.equal(importWorkerButton.getAttribute("aria-pressed"), "false");

    await result.click(importWorkerButton);

    assert.deepEqual(openedWorkers, ["worker-child-1"]);
    assert.equal(
      result.getByRole("button", { name: /ImportInvoice/i }).getAttribute("aria-pressed"),
      "true"
    );
    assert.deepEqual(latestUiState, {
      autoFollowCurrentStep: true,
      collapsedBranchIds: [
        "fan-out:branch:0:branch-a",
        "fan-out:branch:1:branch-b",
      ],
      runId: "run-123",
      selectedChildWorkerId: "worker-child-1",
      selectedChildWorkerPageIndex: 0,
      selectedStepName: "fan-out",
      workflowGraphScrollLeft: 0,
      workflowGraphScrollTop: 148,
    });
    assert.deepEqual(openedWorkerUiStates, [latestUiState]);
    assert.equal(result.queryByText("Selected"), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }

  const restoredScrollMaxByElement = new WeakMap<HTMLElement, number>();
  const restoredScrollTopByElement = new WeakMap<HTMLElement, number>();
  const restoredResizeCallbacks: ResizeObserverCallback[] = [];
  const restoreFetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(parallelWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, parallelWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const restored = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        initialUiState={{
          autoFollowCurrentStep: true,
          runId: "run-123",
          selectedChildWorkerId: "worker-child-1",
          selectedChildWorkerPageIndex: 0,
          selectedStepName: "fan-out",
          workflowGraphScrollTop: 148,
        }}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
    , {
      setupWindow(window) {
        Object.defineProperty(window.HTMLElement.prototype, "scrollTop", {
          configurable: true,
          get() {
            return restoredScrollTopByElement.get(this) ?? 0;
          },
          set(value: number) {
            const maxScrollTop = restoredScrollMaxByElement.get(this) ?? 0;
            restoredScrollTopByElement.set(this, Math.min(value, maxScrollTop));
          },
        });

        class ManualResizeObserver implements ResizeObserver {
          constructor(private readonly callback: ResizeObserverCallback) {}

          disconnect() {}
          observe() {
            restoredResizeCallbacks.push(this.callback);
          }
          unobserve() {}
        }

        globalThis.ResizeObserver = ManualResizeObserver as typeof ResizeObserver;
        window.ResizeObserver = ManualResizeObserver as typeof ResizeObserver;
      },
    }
  );

  try {
    await restored.waitFor(() => restored.getByRole("button", { name: /ImportInvoice/i }));
    const restoredImportWorkerButton = restored.getByRole("button", { name: /ImportInvoice/i });
    assert.equal(restoredImportWorkerButton.getAttribute("aria-pressed"), "true");
    assert.equal(restored.queryByText("Selected"), null);
    const restoredWorkflowGraphScroll = restored.container.querySelector(".workable-grid-scrollbar");
    assert.ok(restoredWorkflowGraphScroll instanceof restored.dom.window.HTMLElement);
    assert.equal(restoredWorkflowGraphScroll.scrollTop, 0);
    restoredScrollMaxByElement.set(restoredWorkflowGraphScroll, 400);
    for (const callback of restoredResizeCallbacks) {
      callback([], {} as ResizeObserver);
    }
    await restored.waitFor(() => {
      assert.equal(restoredWorkflowGraphScroll.scrollTop, 148);
    });
  } finally {
    restoreFetchMock.restore();
    await restored.restore();
  }
});

test("workflow run screen stops auto-following after a manual node selection", async () => {
  let currentRun = parallelWorkflowRun();
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(currentRun);
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, currentRun);
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: /ImportInvoice/i }));
    await result.click(result.getByRole("button", { name: /prepare/i }));
    await result.waitFor(() => result.getByRole("button", { name: /PrepareInvoices/i }));

    currentRun = completedParallelWorkflowRun();
    await result.rerender(
      <ConsoleHeaderCapabilitiesProvider>
        <WorkflowRunConsoleView
          connection={connection}
          onActiveRealtimeConnectionCountChange={() => undefined}
          onOpenWorker={() => undefined}
          onRealtimePayloadOpenChange={() => undefined}
          realtimePayloadCaptureEnabled={false}
          realtimePayloadMaxMessages={20}
          realtimePayloadOpen={false}
          refreshToken={1}
          workflowRunId="run-123"
        />
      </ConsoleHeaderCapabilitiesProvider>
    );

    await result.waitFor(() => result.getByRole("button", { name: /PrepareInvoices/i }));
    assert.equal(result.queryByText("ProfileSummary"), null);
    assert.equal(result.container.querySelector('[aria-current="step"]'), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen renders nested child branch lanes and inspects nested branch nodes", async () => {
  const nestedRun = nestedBranchWorkflowRun();
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(nestedRun);
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, nestedRun);
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: /outer-fan-out/i }));
    result.getByRole("button", { name: /branch-docs/i });
    assert.equal(result.queryByText("branch-finalize"), null);
    await result.click(result.getByRole("button", { name: "Expand branch branch-docs" }));
    result.getByRole("button", { name: /branch-parallel/i });
    result.getByRole("button", { name: /branch-parallel-right/i });
    assert.equal((result.container.textContent?.match(/Branches/g) ?? []).length >= 1, true);
    assert.equal((result.container.textContent?.match(/Branch 1/g) ?? []).length >= 2, true);
    assert.equal(result.queryByText("branch-inner-join"), null);
    assert.equal(result.queryByText("branch-join"), null);
    assert.equal(result.queryByText("outer-join"), null);

    await result.click(result.getByRole("button", { name: /branch-finalize/i }));
    await result.waitFor(() => result.getByRole("button", { name: /FinalizeBranchDocuments/i }));
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen collapses branch lanes and saves the collapsed branch state", async () => {
  const nestedRun = nestedBranchWorkflowRun();
  let latestUiState: unknown = null;
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(nestedRun);
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, nestedRun);
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        onUiStateChange={(state) => {
          latestUiState = state;
        }}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Expand branch branch-docs" }));
    assert.equal(result.queryByText("branch-finalize"), null);
    assert.equal(result.queryByText("workflow nodes collapsed."), null);

    await result.click(result.getByRole("button", { name: "branch-docs" }));
    await result.waitFor(() => result.getByRole("button", { name: /CollectBranchDocuments/i }));
    assert.deepEqual(latestUiState, {
      autoFollowCurrentStep: false,
      collapsedBranchIds: [
        "branch-parallel:branch:0:branch-parallel-left",
        "branch-parallel:branch:1:branch-parallel-right",
        "outer-fan-out:branch:0:branch-docs",
        "outer-fan-out:branch:1:branch-notify",
      ],
      runId: "run-123",
      selectedChildWorkerId: null,
      selectedChildWorkerPageIndex: 0,
      selectedStepName: "branch-docs",
      workflowGraphScrollLeft: 0,
      workflowGraphScrollTop: 0,
    });

    await result.click(result.getByRole("button", { name: "Expand branch branch-docs" }));
    await result.waitFor(() => result.getByRole("button", { name: /branch-finalize/i }));

    await result.click(result.getByRole("button", { name: "Collapse branch branch-docs" }));

    await result.waitFor(() => result.getByRole("button", { name: "Expand branch branch-docs" }));
    assert.equal(result.queryByText("workflow nodes collapsed."), null);
    assert.deepEqual(latestUiState, {
      autoFollowCurrentStep: false,
      collapsedBranchIds: [
        "branch-parallel:branch:0:branch-parallel-left",
        "branch-parallel:branch:1:branch-parallel-right",
        "outer-fan-out:branch:0:branch-docs",
        "outer-fan-out:branch:1:branch-notify",
      ],
      runId: "run-123",
      selectedChildWorkerId: null,
      selectedChildWorkerPageIndex: 0,
      selectedStepName: "branch-docs",
      workflowGraphScrollLeft: 0,
      workflowGraphScrollTop: 0,
    });

    await result.click(result.getByRole("button", { name: "Expand branch branch-docs" }));

    await result.waitFor(() => result.getByRole("button", { name: /branch-finalize/i }));
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen focuses a branch lane and restores the full graph from breadcrumbs", async () => {
  const nestedRun = nestedBranchWorkflowRun();
  let latestUiState: unknown = null;
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(nestedRun);
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, nestedRun);
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        onUiStateChange={(state) => {
          latestUiState = state;
        }}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Focus branch branch-docs" }));
    assert.equal(result.queryByText("branch-finalize"), null);

    await result.click(result.getByRole("button", { name: "Focus branch branch-docs" }));

    await result.waitFor(() => result.getByText("Branch 1: branch-docs"));
    result.getByRole("button", { name: /branch-finalize/i });
    assert.equal(result.queryByText("finish-nested"), null);
    assert.equal(result.queryByText("branch-notify"), null);
    assert.deepEqual(latestUiState, {
      autoFollowCurrentStep: false,
      collapsedBranchIds: [
        "branch-parallel:branch:0:branch-parallel-left",
        "branch-parallel:branch:1:branch-parallel-right",
        "outer-fan-out:branch:0:branch-docs",
        "outer-fan-out:branch:1:branch-notify",
      ],
      focusedBranchId: "outer-fan-out:branch:0:branch-docs",
      runId: "run-123",
      selectedChildWorkerId: null,
      selectedChildWorkerPageIndex: 0,
      selectedStepName: "branch-docs",
      workflowGraphScrollLeft: 0,
      workflowGraphScrollTop: 0,
    });

    await result.click(result.getByRole("button", { name: "Focus branch branch-parallel-left" }));

    await result.waitFor(() => result.getByText("Branch 1: branch-parallel-left"));
    result.getByText("Branch 1: branch-docs");
    result.getByRole("button", { name: /RenderBranchPreview/i });
    assert.equal(result.queryByText("branch-finalize"), null);

    await result.click(result.getByRole("button", { name: "Branch 1: branch-docs" }));
    await result.waitFor(() => result.getByRole("button", { name: /branch-finalize/i }));
    assert.deepEqual(latestUiState, {
      autoFollowCurrentStep: false,
      collapsedBranchIds: [
        "branch-parallel:branch:0:branch-parallel-left",
        "branch-parallel:branch:1:branch-parallel-right",
        "outer-fan-out:branch:0:branch-docs",
        "outer-fan-out:branch:1:branch-notify",
      ],
      focusedBranchId: "outer-fan-out:branch:0:branch-docs",
      runId: "run-123",
      selectedChildWorkerId: null,
      selectedChildWorkerPageIndex: 0,
      selectedStepName: "branch-parallel-left",
      workflowGraphScrollLeft: 0,
      workflowGraphScrollTop: 0,
    });

    await result.click(result.getByRole("button", { name: "Workflow" }));

    await result.waitFor(() => result.getByRole("button", { name: /finish-nested/i }));
    assert.deepEqual(latestUiState, {
      autoFollowCurrentStep: false,
      collapsedBranchIds: [
        "branch-parallel:branch:0:branch-parallel-left",
        "branch-parallel:branch:1:branch-parallel-right",
        "outer-fan-out:branch:0:branch-docs",
        "outer-fan-out:branch:1:branch-notify",
      ],
      runId: "run-123",
      selectedChildWorkerId: null,
      selectedChildWorkerPageIndex: 0,
      selectedStepName: "branch-parallel-left",
      workflowGraphScrollLeft: 0,
      workflowGraphScrollTop: 0,
    });
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen toggles the workflow graph detailed view", async () => {
  const expandedStates: boolean[] = [];
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(parallelWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, parallelWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        onWorkflowGraphExpandedChange={(expanded) => {
          expandedStates.push(expanded);
        }}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByText("Workflow Graph"));

    await result.click(result.getByRole("button", { name: "Next view: Detailed" }));
    await result.waitFor(() => result.getByRole("button", { name: "Next view: Standard" }));
    assert.equal(expandedStates.at(-1), true);

    await result.click(result.getByRole("button", { name: "Next view: Standard" }));
    await result.waitFor(() => result.getByRole("button", { name: "Next view: Detailed" }));
    assert.equal(expandedStates.at(-1), false);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen lets the graph viewport be dragged to pan oversized graphs", async () => {
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(nestedBranchWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, nestedBranchWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>,
    {
      setupWindow(window) {
        class TestPointerEvent extends window.MouseEvent {
          readonly isPrimary: boolean;
          readonly pointerId: number;

          constructor(type: string, init: MouseEventInit & { isPrimary?: boolean; pointerId?: number } = {}) {
            super(type, init);
            this.isPrimary = init.isPrimary ?? true;
            this.pointerId = init.pointerId ?? 1;
          }
        }

        window.PointerEvent = TestPointerEvent as typeof window.PointerEvent;
      },
    }
  );

  try {
    await result.waitFor(() => result.getByText("Workflow Graph"));
    const workflowGraphScroll = result.container.querySelector(".workable-grid-scrollbar");
    assert.ok(workflowGraphScroll instanceof result.dom.window.HTMLElement);
    workflowGraphScroll.scrollLeft = 220;
    workflowGraphScroll.scrollTop = 120;
    const expandBranch = result.getByRole("button", { name: "Expand branch branch-docs" });

    await dispatchWorkflowPointer(result, expandBranch, "pointerdown", {
      clientX: 120,
      clientY: 120,
    });
    await dispatchWorkflowPointer(result, expandBranch, "pointermove", {
      clientX: 40,
      clientY: 70,
    });
    await dispatchWorkflowPointer(result, expandBranch, "pointerup", {
      clientX: 40,
      clientY: 70,
    });

    assert.equal(workflowGraphScroll.scrollLeft, 220);
    assert.equal(workflowGraphScroll.scrollTop, 120);
    await result.click(expandBranch);
    await result.waitFor(() => result.getByRole("button", { name: /branch-finalize/i }));

    await dispatchWorkflowPointer(result, workflowGraphScroll, "pointerdown", {
      clientX: 100,
      clientY: 100,
    });
    await dispatchWorkflowPointer(result, workflowGraphScroll, "pointermove", {
      clientX: 40,
      clientY: 70,
    });
    await dispatchWorkflowPointer(result, workflowGraphScroll, "pointerup", {
      clientX: 40,
      clientY: 70,
    });

    assert.equal(workflowGraphScroll.scrollLeft, 280);
    assert.equal(workflowGraphScroll.scrollTop, 150);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen shows settled parallel branch lanes without rendering joins", async () => {
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(completedParallelWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, completedParallelWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: /fan-out/i }));
    assert.equal((result.container.textContent?.match(/Branches/g) ?? []).length >= 1, true);
    result.getByText("Branch 1");
    result.getByText("Branch 2");
    result.getByRole("button", { name: /branch-a/i });
    result.getByRole("button", { name: /branch-b/i });
    assert.equal(result.queryByText("parallel branches collapsed"), null);
    assert.equal(result.queryByText("fan-out-complete"), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen includes dispatch-each children in the workflow progress summary", async () => {
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(dispatchEachWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, dispatchEachWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() =>
      result.getByLabelText("Workflow worker progress (4/5, 1 active)")
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen does not highlight a current node after the workflow is completed", async () => {
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(completedWorkflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, completedWorkflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByText("profile-summary"));
    assert.equal(result.container.querySelector('[aria-current="step"]'), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen follows the active node and switches the sampled workers as execution advances", async () => {
  let currentRun = parallelWorkflowRun();
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(currentRun);
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, currentRun);
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: /ImportInvoice/i }));
    result.getByRole("button", { name: /ExportLedger/i });
    result.getByText("fan-out");

    currentRun = completedParallelWorkflowRun();
    await result.rerender(
      <ConsoleHeaderCapabilitiesProvider>
        <WorkflowRunConsoleView
          connection={connection}
          onActiveRealtimeConnectionCountChange={() => undefined}
          onOpenWorker={() => undefined}
          onRealtimePayloadOpenChange={() => undefined}
          realtimePayloadCaptureEnabled={false}
          realtimePayloadMaxMessages={20}
          realtimePayloadOpen={false}
          refreshToken={1}
          workflowRunId="run-123"
        />
      </ConsoleHeaderCapabilitiesProvider>
    );

    await result.waitFor(() => result.getByRole("button", { name: /ProfileSummary/i }));
    result.getByText("profile-summary");
    assert.equal(result.queryByText("ImportInvoice"), null);
    assert.equal(result.queryByText("ExportLedger"), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen posts pause actions to the workflow action route", async () => {
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(workflowRun());
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, workflowRun());
    if (childPage) {
      return Response.json(childPage);
    }

    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123/actions/pause") {
      return Response.json({
        action: "Pause",
        messages: [{ code: "workflow.pause.accepted", occurredAt: "2026-06-27T12:06:00.000Z", severity: "Information", text: "Pause accepted." }],
        run: workflowRun(),
        status: "Accepted",
      });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByRole("button", { name: "Pause" }));
    await result.click(result.getByRole("button", { name: "Pause" }));
    await result.waitFor(() => result.getByText("Pause workflow?"));
    await result.click(result.getByRole("button", { name: "Pause workflow" }));
    await result.waitFor(() => {
      assert.equal(fetchMock.calls.some((call) =>
        call.input === "/api/workable/systems/Ops/workflow-runs/run-123/actions/pause" &&
        call.init?.method === "POST"
      ), true);
    });
    result.getByText("Pause accepted.");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen updates the header to canceled after canceling a paused run", async () => {
  let detailFetchCount = 0;
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      detailFetchCount += 1;
      return Response.json(workflowRun({
        availableActions: workflowAvailableActions("Paused"),
        status: "Paused",
      }));
    }

    const childPage = tryWorkflowStepChildrenPageResponse(
      call.input,
      workflowRun({
        availableActions: workflowAvailableActions("Paused"),
        status: "Paused",
      })
    );
    if (childPage) {
      return Response.json(childPage);
    }

    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123/actions/cancel") {
      return Response.json({
        action: "Cancel",
        messages: [{ code: "workflow.cancel.accepted", occurredAt: "2026-06-27T12:07:00.000Z", severity: "Information", text: "Cancel accepted." }],
        run: {
          availableActions: workflowAvailableActions("Canceled"),
          status: "Canceled",
        },
        runId: "run-123",
        status: "Accepted",
      });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByText("Paused"));
    await result.click(result.getByRole("button", { name: "Cancel" }));
    await result.waitFor(() => result.getByText("Cancel workflow?"));
    await result.click(result.getByRole("button", { name: "Cancel workflow" }));
    await result.waitFor(() => result.getByText("Canceled"));
    assert.equal(detailFetchCount >= 1, true);
    assert.equal(result.getByRole("button", { name: "Start" }).hasAttribute("disabled"), true);
    assert.equal(result.getByRole("button", { name: "Pause" }).hasAttribute("disabled"), true);
    assert.equal(result.getByRole("button", { name: "Cancel" }).hasAttribute("disabled"), true);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen uses server-provided workflow action availability", async () => {
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(workflowRun({
        availableActions: {
          cancel: false,
          pause: false,
          start: false,
        },
        status: "Paused",
      }));
    }

    const childPage = tryWorkflowStepChildrenPageResponse(
      call.input,
      workflowRun({
        availableActions: {
          cancel: false,
          pause: false,
          start: false,
        },
        status: "Paused",
      })
    );
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByText("Paused"));
    assert.equal(result.getByRole("button", { name: "Start" }).hasAttribute("disabled"), true);
    assert.equal(result.getByRole("button", { name: "Pause" }).hasAttribute("disabled"), true);
    assert.equal(result.getByRole("button", { name: "Cancel" }).hasAttribute("disabled"), true);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("workflow run screen pages selected-node workers for large fan-out steps", async () => {
  const largeRun = largeDispatchEachWorkflowRun();
  const openedWorkers: string[] = [];
  let latestUiState: unknown = null;
  const fetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(largeRun);
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, largeRun);
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={(workerId) => openedWorkers.push(workerId)}
        onRealtimePayloadOpenChange={() => undefined}
        onUiStateChange={(state) => {
          latestUiState = state;
        }}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await result.waitFor(() => result.getByText("1-12 of 15 workers"));
    result.getByRole("button", { name: /LoadWorker01/i });
    await result.click(result.getByRole("button", { name: "Next" }));
    await result.waitFor(() => result.getByText("13-15 of 15 workers"));
    const worker13Button = result.getByRole("button", { name: /LoadWorker13/i });
    assert.equal(result.queryByText("LoadWorker01"), null);
    await result.click(worker13Button);
    assert.deepEqual(openedWorkers, ["worker-child-13"]);
    assert.equal(
      result.getByRole("button", { name: /LoadWorker13/i }).getAttribute("aria-pressed"),
      "true"
    );
    assert.deepEqual(latestUiState, {
      autoFollowCurrentStep: true,
      collapsedBranchIds: [],
      runId: "run-123",
      selectedChildWorkerId: "worker-child-13",
      selectedChildWorkerPageIndex: 1,
      selectedStepName: "fan-out-batch",
      workflowGraphScrollLeft: 0,
      workflowGraphScrollTop: 0,
    });
    await result.click(result.getByRole("button", { name: "Previous" }));
    await result.waitFor(() => result.getByText("1-12 of 15 workers"));
    result.getByRole("button", { name: /LoadWorker01/i });
  } finally {
    fetchMock.restore();
    await result.restore();
  }

  const restoreFetchMock = installWorkflowFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/workflow-runs/run-123?childSampleSize=12") {
      return Response.json(largeRun);
    }

    const childPage = tryWorkflowStepChildrenPageResponse(call.input, largeRun);
    if (childPage) {
      return Response.json(childPage);
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const restored = await renderDom(
    <ConsoleHeaderCapabilitiesProvider>
      <WorkflowRunConsoleView
        connection={connection}
        initialUiState={{
          autoFollowCurrentStep: true,
          runId: "run-123",
          selectedChildWorkerId: "worker-child-13",
          selectedChildWorkerPageIndex: 1,
          selectedStepName: "fan-out-batch",
        }}
        onActiveRealtimeConnectionCountChange={() => undefined}
        onOpenWorker={() => undefined}
        onRealtimePayloadOpenChange={() => undefined}
        realtimePayloadCaptureEnabled={false}
        realtimePayloadMaxMessages={20}
        realtimePayloadOpen={false}
        refreshToken={0}
        workflowRunId="run-123"
      />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    await restored.waitFor(() => restored.getByText("13-15 of 15 workers"));
    assert.equal(
      restored.getByRole("button", { name: /LoadWorker13/i }).getAttribute("aria-pressed"),
      "true"
    );
  } finally {
    restoreFetchMock.restore();
    await restored.restore();
  }
});

type FetchCall = {
  input: string;
  init?: RequestInit;
};

function installWorkflowFetch(handler: (call: FetchCall) => Response | Promise<Response>) {
  const previousFetch = globalThis.fetch;
  const calls: FetchCall[] = [];
  globalThis.fetch = (async (input, init) => {
    const call = { input: String(input), init };
    calls.push(call);
    return handler(call);
  }) as typeof fetch;

  return {
    calls,
    restore() {
      globalThis.fetch = previousFetch;
    },
  };
}

function workflowRun(overrides: Partial<WorkflowRunDetailView> = {}): WorkflowRunDetailView {
  return {
    availableActions: workflowAvailableActions("Running"),
    createdAt: "2026-06-27T12:00:00.000Z",
    currentStepName: "Dispatch invoices",
    currentStepStatus: "Running",
    outstandingChildren: {
      active: 1,
      final: 0,
      total: 1,
    },
    status: "Running",
    steps: [
      {
        childSample: [
          {
            definitionName: "ImportInvoice",
            state: "Running",
            workerId: "worker-child-1",
          },
        ],
        children: {
          active: 1,
          final: 0,
          total: 1,
        },
        kind: "DispatchWork",
        name: "Dispatch invoices",
        status: "Running",
        steps: [],
      },
    ],
    ...overrides,
  };
}

function workflowAvailableActions(status: WorkflowRunDetailView["status"]) {
  switch (status) {
    case "Running":
      return { cancel: true, pause: true, start: false };
    case "Paused":
    case "Blocked":
      return { cancel: true, pause: false, start: true };
    default:
      return { cancel: false, pause: false, start: false };
  }
}

function parallelWorkflowRun(): WorkflowRunDetailView {
  return workflowRun({
    currentStepName: "fan-out",
    currentStepStatus: "WaitingOnChildren",
    outstandingChildren: {
      active: 2,
      final: 0,
      total: 2,
    },
    steps: [
      {
        childSample: [
          {
            definitionName: "PrepareInvoices",
            state: "Completed",
            workerId: "worker-prepare-1",
          },
        ],
        children: {
          active: 0,
          final: 1,
          total: 1,
        },
        kind: "DispatchWork",
        name: "prepare",
        status: "Completed",
        steps: [],
      },
      {
        childSample: [],
        children: {
          active: 2,
          final: 0,
          total: 2,
        },
        kind: "Parallel",
        name: "fan-out",
        status: "WaitingOnChildren",
        steps: [
          {
            childSample: [
              {
                definitionName: "ImportInvoice",
                state: "Running",
                workerId: "worker-child-1",
              },
            ],
            children: {
              active: 1,
              final: 0,
              total: 1,
            },
            kind: "DispatchWork",
            name: "branch-a",
            status: "Running",
            steps: [],
          },
          {
            childSample: [
              {
                definitionName: "ExportLedger",
                state: "Running",
                workerId: "worker-child-2",
              },
            ],
            children: {
              active: 1,
              final: 0,
              total: 1,
            },
            kind: "DispatchWork",
            name: "branch-b",
            status: "Running",
            steps: [],
          },
        ],
      },
      {
        childSample: [],
        children: {
          active: 2,
          final: 0,
          total: 2,
        },
        kind: "Join",
        name: "join",
        status: "WaitingOnChildren",
        steps: [],
      },
    ],
  });
}

function completedParallelWorkflowRun(): WorkflowRunDetailView {
  return workflowRun({
    currentStepName: "profile-summary",
    currentStepStatus: "Running",
    outstandingChildren: {
      active: 1,
      final: 0,
      total: 1,
    },
    steps: [
      {
        childSample: [
          {
            definitionName: "PrepareInvoices",
            state: "Completed",
            workerId: "worker-prepare-1",
          },
        ],
        children: {
          active: 0,
          final: 1,
          total: 1,
        },
        kind: "DispatchWork",
        name: "prepare",
        status: "Completed",
        steps: [],
      },
      {
        childSample: [],
        children: {
          active: 0,
          final: 2,
          total: 2,
        },
        kind: "Parallel",
        name: "fan-out",
        status: "Completed",
        steps: [
          {
            childSample: [
              {
                definitionName: "ImportInvoice",
                state: "Completed",
                workerId: "worker-child-1",
              },
            ],
            children: {
              active: 0,
              final: 1,
              total: 1,
            },
            kind: "DispatchWork",
            name: "branch-a",
            status: "Completed",
            steps: [],
          },
          {
            childSample: [
              {
                definitionName: "ExportLedger",
                state: "Completed",
                workerId: "worker-child-2",
              },
            ],
            children: {
              active: 0,
              final: 1,
              total: 1,
            },
            kind: "DispatchWork",
            name: "branch-b",
            status: "Completed",
            steps: [],
          },
        ],
      },
      {
        childSample: [],
        children: {
          active: 0,
          final: 0,
          total: 0,
        },
        kind: "Join",
        name: "fan-out-complete",
        status: "Completed",
        steps: [],
      },
      {
        childSample: [
          {
            definitionName: "ProfileSummary",
            state: "Running",
            workerId: "worker-profile-1",
          },
        ],
        children: {
          active: 1,
          final: 0,
          total: 1,
        },
        kind: "DispatchWork",
        name: "profile-summary",
        status: "Running",
        steps: [],
      },
    ],
  });
}

function completedWorkflowRun(): WorkflowRunDetailView {
  return {
    ...completedParallelWorkflowRun(),
    currentStepName: null,
    currentStepStatus: null,
    outstandingChildren: {
      active: 0,
      final: 3,
      total: 3,
    },
    status: "Completed",
    steps: [
      ...completedParallelWorkflowRun().steps.slice(0, 3),
      {
        childSample: [
          {
            definitionName: "ProfileSummary",
            state: "Completed",
            workerId: "worker-profile-1",
          },
        ],
        children: {
          active: 0,
          final: 1,
          total: 1,
        },
        kind: "DispatchWork",
        name: "profile-summary",
        status: "Completed",
        steps: [],
      },
    ],
  };
}

function nestedBranchWorkflowRun(): WorkflowRunDetailView {
  return workflowRun({
    currentStepName: "branch-parallel-right",
    currentStepStatus: "Running",
    outstandingChildren: {
      active: 2,
      final: 4,
      total: 6,
    },
    steps: [
      {
        childSample: [
          {
            definitionName: "PrepareNestedWorkflow",
            state: "Completed",
            workerId: "worker-prepare-nested",
          },
        ],
        children: {
          active: 0,
          final: 1,
          total: 1,
        },
        kind: "DispatchWork",
        name: "prepare-nested",
        status: "Completed",
        steps: [],
      },
      {
        childSample: [],
        children: {
          active: 2,
          final: 3,
          total: 5,
        },
        kind: "Parallel",
        name: "outer-fan-out",
        status: "WaitingOnChildren",
        steps: [
          {
            childSample: [],
            children: {
              active: 1,
              final: 2,
              total: 3,
            },
            kind: "Branch",
            name: "branch-docs",
            status: "WaitingOnChildren",
            steps: [
              {
                childSample: [
                  {
                    definitionName: "CollectBranchDocuments",
                    state: "Completed",
                    workerId: "worker-branch-docs",
                  },
                ],
                children: {
                  active: 0,
                  final: 1,
                  total: 1,
                },
                kind: "DispatchWork",
                name: "branch-collect",
                status: "Completed",
                steps: [],
              },
              {
                childSample: [
                  {
                    definitionName: "ParseBranchDocuments",
                    state: "Completed",
                    workerId: "worker-branch-parse",
                  },
                ],
                children: {
                  active: 0,
                  final: 1,
                  total: 1,
                },
                kind: "DispatchWork",
                name: "branch-parse",
                status: "Completed",
                steps: [],
              },
              {
                childSample: [],
                children: {
                  active: 1,
                  final: 1,
                  total: 2,
                },
                kind: "Parallel",
                name: "branch-parallel",
                status: "WaitingOnChildren",
                steps: [
                  {
                    childSample: [
                      {
                        definitionName: "RenderBranchPreview",
                        state: "Completed",
                        workerId: "worker-branch-left",
                      },
                    ],
                    children: {
                      active: 0,
                      final: 1,
                      total: 1,
                    },
                    kind: "DispatchEach",
                    name: "branch-parallel-left",
                    status: "Completed",
                    steps: [],
                  },
                  {
                    childSample: [
                      {
                        definitionName: "PublishBranchPreview",
                        state: "Running",
                        workerId: "worker-branch-right",
                      },
                    ],
                    children: {
                      active: 1,
                      final: 0,
                      total: 1,
                    },
                    kind: "DispatchWork",
                    name: "branch-parallel-right",
                    status: "Running",
                    steps: [],
                  },
                  {
                    childSample: [],
                    children: {
                      active: 1,
                      final: 1,
                      total: 2,
                    },
                    kind: "Join",
                    name: "branch-inner-join",
                    status: "WaitingOnChildren",
                    steps: [],
                  },
                ],
              },
              {
                childSample: [
                  {
                    definitionName: "FinalizeBranchDocuments",
                    state: "Queued",
                    workerId: "worker-branch-finalize",
                  },
                ],
                children: {
                  active: 0,
                  final: 1,
                  total: 1,
                },
                kind: "DispatchWork",
                name: "branch-finalize",
                status: "Pending",
                steps: [],
              },
              {
                childSample: [],
                children: {
                  active: 0,
                  final: 0,
                  total: 0,
                },
                kind: "Join",
                name: "branch-join",
                status: "WaitingOnChildren",
                steps: [],
              },
            ],
          },
          {
            childSample: [],
            children: {
              active: 0,
              final: 1,
              total: 1,
            },
            kind: "Branch",
            name: "branch-notify",
            status: "Completed",
            steps: [
              {
                childSample: [
                  {
                    definitionName: "NotifyBranchWatchers",
                    state: "Completed",
                    workerId: "worker-branch-notify",
                  },
                ],
                children: {
                  active: 0,
                  final: 1,
                  total: 1,
                },
                kind: "DispatchWork",
                name: "branch-notify-worker",
                status: "Completed",
                steps: [],
              },
            ],
          },
        ],
      },
      {
        childSample: [],
        children: {
          active: 2,
          final: 4,
          total: 6,
        },
        kind: "Join",
        name: "outer-join",
        status: "WaitingOnChildren",
        steps: [],
      },
      {
        childSample: [
          {
            definitionName: "FinishNestedWorkflow",
            state: "Queued",
            workerId: "worker-finish-nested",
          },
        ],
        children: {
          active: 0,
          final: 0,
          total: 1,
        },
        kind: "DispatchWork",
        name: "finish-nested",
        status: "Pending",
        steps: [],
      },
    ],
  });
}

function dispatchEachWorkflowRun(): WorkflowRunDetailView {
  return workflowRun({
    currentStepName: "fan-out-batch",
    currentStepStatus: "WaitingOnChildren",
    outstandingChildren: {
      active: 1,
      final: 4,
      total: 5,
    },
    steps: [
      {
        childSample: [
          {
            definitionName: "PrepareBatch",
            state: "Completed",
            workerId: "worker-prepare-1",
          },
        ],
        children: {
          active: 0,
          final: 1,
          total: 1,
        },
        kind: "DispatchWork",
        name: "prepare-batch",
        status: "Completed",
        steps: [],
      },
      {
        childSample: [
          {
            definitionName: "NormalizeCustomerProfile",
            state: "Completed",
            workerId: "worker-child-1",
          },
          {
            definitionName: "SyncEntitlementLedger",
            state: "Completed",
            workerId: "worker-child-2",
          },
          {
            definitionName: "RenderAuditArtifact",
            state: "Completed",
            workerId: "worker-child-3",
          },
          {
            definitionName: "PublishNotificationPayload",
            state: "Running",
            workerId: "worker-child-4",
          },
        ],
        children: {
          active: 1,
          final: 3,
          total: 4,
        },
        kind: "DispatchEach",
        name: "fan-out-batch",
        status: "WaitingOnChildren",
        steps: [],
      },
      {
        childSample: [],
        children: {
          active: 1,
          final: 4,
          total: 5,
        },
        kind: "Join",
        name: "fan-out-complete",
        status: "WaitingOnChildren",
        steps: [],
      },
    ],
  });
}

function largeDispatchEachWorkflowRun(): WorkflowRunDetailView {
  return workflowRun({
    currentStepName: "fan-out-batch",
    currentStepStatus: "WaitingOnChildren",
    outstandingChildren: {
      active: 15,
      final: 0,
      total: 15,
    },
    steps: [
      {
        childSample: [
          {
            definitionName: "PrepareBatch",
            state: "Completed",
            workerId: "worker-prepare-1",
          },
        ],
        children: {
          active: 0,
          final: 1,
          total: 1,
        },
        kind: "DispatchWork",
        name: "prepare-batch",
        status: "Completed",
        steps: [],
      },
      {
        childSample: Array.from({ length: 15 }, (_, index) => ({
          definitionName: `LoadWorker${String(index + 1).padStart(2, "0")}`,
          state: "Running" as const,
          workerId: `worker-child-${index + 1}`,
        })),
        children: {
          active: 15,
          final: 0,
          total: 15,
        },
        kind: "DispatchEach",
        name: "fan-out-batch",
        status: "WaitingOnChildren",
        steps: [],
      },
    ],
  });
}

function tryWorkflowStepChildrenPageResponse(input: string, run: WorkflowRunDetailView) {
  if (!input.startsWith("/api/workable/systems/Ops/workflow-runs/run-123/steps/")) {
    return null;
  }

  const url = new URL(`https://console.example.com${input}`);
  const stepIndex = url.pathname.indexOf("/steps/");
  const childrenIndex = url.pathname.lastIndexOf("/children");
  if (stepIndex < 0 || childrenIndex < 0 || childrenIndex <= stepIndex) {
    return null;
  }

  const stepName = decodeURIComponent(url.pathname.slice(stepIndex + "/steps/".length, childrenIndex));
  const step = findWorkflowStepForTest(run.steps, stepName);
  if (!step) {
    return null;
  }

  const workers = collectAssociatedWorkflowWorkersForTest(step);
  const skip = Number(url.searchParams.get("skip") ?? "0");
  const take = Number(url.searchParams.get("take") ?? "12");

  return {
    skip,
    take,
    totalCount: workers.length,
    workers: workers.slice(skip, skip + take),
  };
}

async function dispatchWorkflowPointer(
  result: Awaited<ReturnType<typeof renderDom>>,
  target: Element,
  type: string,
  init: PointerEventInit
) {
  await act(async () => {
    target.dispatchEvent(new result.dom.window.PointerEvent(type, {
      bubbles: true,
      button: 0,
      cancelable: true,
      isPrimary: true,
      pointerId: 1,
      ...init,
    }));
  });
}

function findWorkflowStepForTest(
  steps: WorkflowRunDetailView["steps"],
  stepName: string
): WorkflowRunDetailView["steps"][number] | null {
  for (const step of steps) {
    if (step.name === stepName) {
      return step;
    }

    const nested = findWorkflowStepForTest(step.steps, stepName);
    if (nested) {
      return nested;
    }
  }

  return null;
}

function collectAssociatedWorkflowWorkersForTest(step: WorkflowRunDetailView["steps"][number]) {
  const workers = new Map<string, WorkflowRunDetailView["steps"][number]["childSample"][number]>();
  const visit = (node: WorkflowRunDetailView["steps"][number]) => {
    for (const worker of node.childSample) {
      if (!workers.has(worker.workerId)) {
        workers.set(worker.workerId, worker);
      }
    }

    for (const childStep of node.steps) {
      visit(childStep);
    }
  };

  visit(step);
  return [...workers.values()];
}
