import assert from "node:assert/strict";
import test from "node:test";
import {
  SchedulesView,
  formatScheduleDateTime,
  formatScheduleTiming,
  getUpcomingSchedules,
} from "@/components/workable/console/schedules-screen";
import { clearDefinitionCatalogLevelCache } from "@/components/workable/console/catalog-browser-data";
import { renderDom } from "@/test/dom";
import type {
  WorkScheduleSnapshot,
  WorkableConnection,
} from "@/lib/workable";

const connection: WorkableConnection = {
  apiUrl: "https://console.example.com/workable",
  schedulingAvailable: true,
  systemName: "Ops",
};
const activeScheduleId = "11111111-2222-3333-4444-555555555555";
const workerId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

test("schedule helpers format timing and select active upcoming work in due order", () => {
  const later = schedule({
    id: { value: "later" },
    nextRunAt: "2099-01-02T10:00:00Z",
    timing: { firstRunAt: "2099-01-01T00:00:00Z", interval: "01:00:00", runMissedExecution: true },
  });
  const earlier = schedule({
    id: { value: "earlier" },
    nextRunAt: "2099-01-01T10:00:00Z",
    timing: {
      firstRunAt: "2099-01-01T00:00:00Z",
      cronExpression: "0 9 * * 1-5",
      runMissedExecution: false,
      timeZoneId: "UTC",
    },
  });
  const canceled = schedule({ id: { value: "canceled" }, status: "Canceled" });

  assert.deepEqual(getUpcomingSchedules([later, canceled, earlier]).map((item) => item.id.value), [
    "earlier",
    "later",
  ]);
  assert.equal(formatScheduleTiming(later), "Every 01:00:00");
  assert.equal(formatScheduleTiming(earlier), "0 9 * * 1-5 · UTC");
  assert.equal(formatScheduleTiming(schedule({
    timing: {
      firstRunAt: "2099-01-01T00:00:00Z",
      cronExpression: "0 0 * * *",
      runMissedExecution: true,
    },
  })), "0 0 * * * · UTC");
  assert.equal(formatScheduleTiming(schedule()), "Once");
  assert.equal(formatScheduleDateTime(null), "-");
  assert.equal(formatScheduleDateTime("not-a-date"), "not-a-date");
  assert.notEqual(formatScheduleDateTime("2099-01-01T10:00:00Z"), "-");
});

test("schedules screen shows upcoming work and history and cancels an active schedule", async () => {
  let isCanceled = false;
  const openedWorkers: string[] = [];
  const fetchMock = installFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/schedules?take=1000") {
      return Response.json({
        schedules: [
          schedule({
            status: isCanceled ? "Canceled" : "Active",
            nextRunAt: isCanceled ? null : "2099-01-01T10:00:00Z",
            canceledAt: isCanceled ? "2098-12-01T12:00:00Z" : null,
            timing: { firstRunAt: "2099-01-01T10:00:00Z", interval: "01:00:00", runMissedExecution: true },
          }),
          schedule({
            definitionName: "Cleanup",
            id: { value: "99999999-8888-7777-6666-555555555555" },
            status: "Completed",
          }),
          schedule({
            createdBy: { id: "operator-2" },
            definitionName: "OperatorTask",
            id: { value: "88888888-9999-aaaa-bbbb-cccccccccccc" },
            nextRunAt: "2099-01-01T11:00:00Z",
          }),
          schedule({
            createdBy: { email: "nightly@example.test" },
            definitionName: "NightlyReport",
            id: { value: "77777777-8888-9999-aaaa-bbbbbbbbbbbb" },
            nextRunAt: "2099-01-01T09:00:00Z",
            timing: {
              cronExpression: "0 9 * * *",
              firstRunAt: "2099-01-01T00:00:00Z",
              runMissedExecution: false,
              timeZoneId: "UTC",
            },
          }),
          schedule({
            createdBy: {},
            definitionName: "ArchivedTask",
            id: { value: "66666666-7777-8888-9999-aaaaaaaaaaaa" },
            nextRunAt: null,
            status: "Canceled",
          }),
          schedule({
            createdBy: {},
            definitionName: "UnknownActorTask",
            id: { value: "55555555-6666-7777-8888-999999999999" },
            nextRunAt: "2099-01-01T12:00:00Z",
          }),
        ],
      });
    }

    if (call.input === `/api/workable/systems/Ops/schedules/${activeScheduleId}/occurrences?take=50`) {
      return Response.json({
        occurrences: [
          {
            attemptedAt: "2098-12-01T11:00:01Z",
            expiresAt: "2098-12-08T11:00:01Z",
            messages: [],
            occurrenceId: "12121212-3434-5656-7878-909090909090",
            queueStatus: "Accepted",
            scheduledAt: "2098-12-01T11:00:00Z",
            scheduleId: { value: activeScheduleId },
            status: "Accepted",
            workerId: { value: workerId },
          },
          {
            attemptedAt: "2098-12-01T10:00:01Z",
            expiresAt: "2098-12-08T10:00:01Z",
            messages: [],
            occurrenceId: "13131313-3434-5656-7878-909090909090",
            queueStatus: null,
            scheduledAt: "2098-12-01T10:00:00Z",
            scheduleId: { value: activeScheduleId },
            status: "Skipped",
            workerId: null,
          },
          {
            attemptedAt: "2098-12-01T09:00:01Z",
            expiresAt: "2098-12-08T09:00:01Z",
            messages: [],
            occurrenceId: "14141414-3434-5656-7878-909090909090",
            queueStatus: "Rejected",
            scheduledAt: "2098-12-01T09:00:00Z",
            scheduleId: { value: activeScheduleId },
            status: "Rejected",
            workerId: null,
          },
        ],
      });
    }

    if (call.input.includes("/schedules/77777777-8888-9999-aaaa-bbbbbbbbbbbb/occurrences")) {
      return Response.json({ occurrences: [] });
    }

    if (call.input === `/api/workable/systems/Ops/schedules/${activeScheduleId}/cancel`) {
      isCanceled = true;
      return Response.json({
        messages: [],
        schedule: schedule({ status: "Canceled", nextRunAt: null }),
        scheduleId: { value: activeScheduleId },
        status: "Accepted",
      });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      onOpenWorker={(id) => openedWorkers.push(id)}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    result.getByText("Upcoming work");
    result.getByText("Recurring schedules");
    result.getByText("Recent dispatches");
    await result.waitFor(() => result.getByRole("button", { name: workerId }));
    await result.click(result.getByRole("button", { name: workerId }));
    assert.deepEqual(openedWorkers, [workerId]);
    result.getByText("nightly@example.test");
    result.getByText("operator-2");
    result.getByText("Unknown");

    await result.click(result.getByText("NightlyReport"));
    await result.waitFor(() => result.getByText("This schedule has no retained dispatches yet."));
    result.getByText("No");

    await result.click(result.getByRole("button", { name: "Cancel schedule for ImportOrders" }));
    result.getByText("Cancel this schedule?");
    const cancelActions = Array.from(result.dom.window.document.querySelectorAll("button"))
      .filter((button) => button.textContent?.trim() === "Cancel schedule");
    assert.ok(cancelActions.length > 0);
    await result.click(cancelActions.at(-1)!);

    await result.waitFor(() => assert.equal(isCanceled, true));
    await result.waitFor(() => assert.equal(
      fetchMock.calls.filter((call) => call.input.endsWith("/schedules?take=1000")).length,
      2
    ));
    assert.equal(
      fetchMock.calls.some((call) =>
        call.input.endsWith(`/${activeScheduleId}/cancel`) && call.init?.method === "POST"
      ),
      true
    );
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedules screen fails closed for unavailable lists and occurrence history", async () => {
  let rejectList: ((reason: unknown) => void) | undefined;
  const listPromise = new Promise<Response>((_resolve, reject) => {
    rejectList = reject;
  });
  const fetchMock = installFetch((call) => {
    if (call.input.endsWith("/schedules?take=1000")) {
      return listPromise;
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const disabledConnection = { ...connection, schedulingAvailable: false };
  const result = await renderDom(
    <SchedulesView
      connection={disabledConnection}
      isLoadingTarget={false}
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    assert.equal(fetchMock.calls.length, 0);
    assert.equal((result.getByRole("button", { name: "New schedule" }) as HTMLButtonElement).disabled, true);
    await result.rerender(
      <SchedulesView
        connection={disabledConnection}
        isLoadingTarget
        onOpenWorker={() => undefined}
        onReady={() => undefined}
        refreshToken={0}
      />
    );
    await result.waitFor(() => assert.equal(fetchMock.calls.length, 1));
    assert.ok(result.dom.window.document.querySelectorAll(".animate-pulse").length > 0);
    rejectList?.("offline");
    await result.waitFor(() => result.getByText("Schedules could not be loaded."));
  } finally {
    fetchMock.restore();
    await result.restore();
  }

  const historyFetch = installFetch((call) => {
    if (call.input.endsWith("/schedules?take=1000")) {
      return Response.json({ schedules: [schedule()] });
    }
    if (call.input.includes("/occurrences?take=50")) {
      throw "history offline";
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const historyResult = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    await historyResult.waitFor(() => historyResult.getByText("Schedule history could not be loaded."));
  } finally {
    historyFetch.restore();
    await historyResult.restore();
  }
});

test("schedules screen surfaces a rejected cancellation", async () => {
  const fetchMock = installFetch((call) => {
    if (call.input.endsWith("/schedules?take=1000")) {
      return Response.json({ schedules: [schedule()] });
    }
    if (call.input.includes("/occurrences?take=50")) {
      return Response.json({ occurrences: [] });
    }
    if (call.input.endsWith(`/${activeScheduleId}/cancel`)) {
      return Response.json({
        messages: [{ text: "The schedule already completed." }],
        scheduleId: { value: activeScheduleId },
        status: "Conflict",
      });
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Cancel schedule for ImportOrders" }));
    const cancelActions = Array.from(result.dom.window.document.querySelectorAll("button"))
      .filter((button) => button.textContent?.trim() === "Cancel schedule");
    await result.click(cancelActions.at(-1)!);
    await result.waitFor(() => result.getByText("The schedule already completed."));
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedules screen handles empty, failed, and snapshot-free cancellation outcomes", async () => {
  let cancelCalls = 0;
  let listCalls = 0;
  const fetchMock = installFetch((call) => {
    if (call.input.endsWith("/schedules?take=1000")) {
      listCalls += 1;
      return Response.json({ schedules: [schedule()] });
    }
    if (call.input.includes("/occurrences?take=50")) {
      return Response.json({ occurrences: [] });
    }
    if (call.input.endsWith(`/${activeScheduleId}/cancel`)) {
      cancelCalls += 1;
      if (cancelCalls === 1) {
        return Response.json({
          messages: [],
          scheduleId: { value: activeScheduleId },
          status: "Conflict",
        });
      }
      if (cancelCalls === 2) {
        throw "cancel offline";
      }
      if (cancelCalls === 3) {
        throw new Error("Cancellation transport failed.");
      }
      return Response.json({
        messages: [],
        scheduleId: { value: activeScheduleId },
        status: "Accepted",
      });
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    await result.waitFor(() => result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Cancel schedule for ImportOrders" }));
    const cancelAction = () => Array.from(result.dom.window.document.querySelectorAll("button"))
      .filter((button) => button.textContent?.trim() === "Cancel schedule")
      .at(-1)!;

    await result.click(cancelAction());
    await result.waitFor(() => result.getByText("Cancellation returned Conflict."));
    await result.click(cancelAction());
    await result.waitFor(() => result.getByText("Schedule could not be canceled."));
    await result.click(cancelAction());
    await result.waitFor(() => result.getByText("Cancellation transport failed."));
    await result.click(cancelAction());
    await result.waitFor(() => assert.equal(cancelCalls, 4));
    await result.waitFor(() => assert.equal(listCalls, 2));
    assert.equal(result.queryByText("Cancel this schedule?"), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedules screen keeps rows during refresh and ignores history from the prior selection", async () => {
  const secondScheduleId = "22222222-3333-4444-5555-666666666666";
  const staleWorkerId = "bbbbbbbb-cccc-dddd-eeee-ffffffffffff";
  let listCalls = 0;
  let resolveRefreshList: ((response: Response) => void) | undefined;
  let resolveStaleHistory: ((response: Response) => void) | undefined;
  const refreshList = new Promise<Response>((resolve) => {
    resolveRefreshList = resolve;
  });
  const staleHistory = new Promise<Response>((resolve) => {
    resolveStaleHistory = resolve;
  });
  const schedules = [
    schedule(),
    schedule({
      definitionName: "SecondTask",
      id: { value: secondScheduleId },
      nextRunAt: "2099-01-01T11:00:00Z",
    }),
  ];
  const fetchMock = installFetch((call) => {
    if (call.input.endsWith("/schedules?take=1000")) {
      listCalls += 1;
      return listCalls === 1 ? Response.json({ schedules }) : refreshList;
    }
    if (call.input.endsWith(`/${activeScheduleId}/occurrences?take=50`)) {
      return staleHistory;
    }
    if (call.input.endsWith(`/${secondScheduleId}/occurrences?take=50`)) {
      return Response.json({ occurrences: [] });
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const onReady = () => undefined;
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      onOpenWorker={() => undefined}
      onReady={onReady}
      refreshToken={0}
    />
  );

  try {
    await result.waitFor(() => assert.equal(
      fetchMock.calls.some((call) => call.input.endsWith(`/${activeScheduleId}/occurrences?take=50`)),
      true
    ));
    await result.click(result.getByText("SecondTask"));
    await result.waitFor(() => result.getByText("This schedule has no retained dispatches yet."));

    resolveStaleHistory?.(Response.json({
      occurrences: [{
        attemptedAt: "2098-12-01T11:00:01Z",
        expiresAt: "2098-12-08T11:00:01Z",
        messages: [],
        occurrenceId: "stale-occurrence",
        queueStatus: "Accepted",
        scheduledAt: "2098-12-01T11:00:00Z",
        scheduleId: { value: activeScheduleId },
        status: "Accepted",
        workerId: { value: staleWorkerId },
      }],
    }));
    await result.rerender(
      <SchedulesView
        connection={connection}
        isLoadingTarget
        onOpenWorker={() => undefined}
        onReady={onReady}
        refreshToken={1}
      />
    );
    await result.waitFor(() => assert.equal(listCalls, 2));
    result.getByText("SecondTask");
    assert.equal(result.queryByText(staleWorkerId), null);

    resolveRefreshList?.(Response.json({ schedules }));
    await result.rerender(
      <SchedulesView
        connection={connection}
        isLoadingTarget
        onOpenWorker={() => undefined}
        onReady={onReady}
        refreshToken={1}
      />
    );
    await result.waitFor(() => result.getByText("This schedule has no retained dispatches yet."));
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("new schedule surfaces definition loading failures", async () => {
  clearDefinitionCatalogLevelCache();
  const fetchMock = installFetch((call) => {
    if (call.input.endsWith("/schedules?take=1000")) {
      return Response.json({ schedules: [] });
    }
    if (call.input.endsWith("/definitions?level=true")) {
      return Response.json({ categories: [], definitions: [{ category: "Operations", name: "ImportOrders" }] });
    }
    if (call.input.endsWith("/definitions/ImportOrders/info")) {
      throw "definition offline";
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    await result.waitFor(() => result.getByText("No schedules have been created for this work system."));
    await result.click(result.getByRole("button", { name: "New schedule" }));
    await result.waitFor(() => result.getByRole("button", { name: "ImportOrders" }));
    await result.click(result.getByRole("button", { name: "ImportOrders" }));
    await result.waitFor(() => result.getByText("Definition could not be loaded."));
  } finally {
    fetchMock.restore();
    await result.restore();
    clearDefinitionCatalogLevelCache();
  }
});

test("new schedule chooses a definition and opens the queue dialog in schedule mode", async () => {
  clearDefinitionCatalogLevelCache();
  let scheduleCreated = false;
  let listCalls = 0;
  const fetchMock = installFetch((call) => {
    if (call.input === "/api/workable/systems/Ops/schedules?take=1000") {
      listCalls += 1;
      return Response.json({ schedules: [] });
    }

    if (call.input === "/api/workable/systems/Ops/definitions?level=true") {
      return Response.json({ categories: [], definitions: [{ category: "Operations", name: "ImportOrders" }] });
    }

    if (call.input === "/api/workable/systems/Ops/definitions/ImportOrders/info") {
      return Response.json({
        definition: {
          category: "Operations",
          id: { value: "definition-1" },
          name: "ImportOrders",
          revision: 1,
        },
        queueRequestSchema: { schema: { jsonSchema: "{}" }, tabs: [] },
        status: "Registered",
        workers: {
          active: 0,
          canceled: 0,
          completed: 0,
          failed: 0,
          paused: 0,
          queued: 0,
          running: 0,
          total: 0,
          waiting: 0,
        },
      });
    }

    if (call.input === "/api/workable/systems/Ops/work/ImportOrders/schedules") {
      scheduleCreated = true;
      return Response.json({ messages: [], status: "Accepted" });
    }

    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    await result.waitFor(() => result.getByText("No schedules have been created for this work system."));
    await result.click(result.getByRole("button", { name: "New schedule" }));
    result.getByText("Choose work to schedule");
    await result.waitFor(() => result.getByRole("button", { name: "ImportOrders" }));
    await result.click(result.getByRole("button", { name: "ImportOrders" }));
    await result.waitFor(() => result.getByText("Schedule this work"));
    result.getByText("The latest definition will be used each time this schedule runs.");
    await result.click(result.getByRole("button", { name: "Create schedule" }));

    await result.waitFor(() => assert.equal(scheduleCreated, true));
    await result.waitFor(() => assert.equal(listCalls, 2));
    assert.ok(fetchMock.calls.some((call) => call.input.endsWith("/work/ImportOrders/schedules")));
  } finally {
    fetchMock.restore();
    await result.restore();
    clearDefinitionCatalogLevelCache();
  }
});

function schedule(overrides: Partial<WorkScheduleSnapshot> = {}): WorkScheduleSnapshot {
  return {
    createdAt: "2098-12-01T10:00:00Z",
    createdBy: { id: "operator-1", name: "Sample Operator" },
    definitionName: "ImportOrders",
    id: { value: activeScheduleId },
    lastRunAt: null,
    nextRunAt: "2099-01-01T10:00:00Z",
    status: "Active",
    timing: {
      firstRunAt: "2099-01-01T10:00:00Z",
      interval: null,
      runMissedExecution: true,
    },
    ...overrides,
  };
}

type FetchCall = { input: string; init?: RequestInit };

function installFetch(handler: (call: FetchCall) => Response | Promise<Response>) {
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
