import assert from "node:assert/strict";
import test from "node:test";
import { act } from "react";
import {
  SchedulesView,
  calculateSchedulePollDelay,
  compareSqlServerUniqueIdentifiers,
  createScheduleOverviewPath,
  createSchedulePagePath,
  createUpcomingSchedulePagePath,
  formatScheduleDateTime,
  formatScheduleTiming,
  getUpcomingSchedules,
  loadScheduleOverview,
  mergeSchedulePages,
  schedulePageSize,
  schedulePollMaximumIntervalMs,
} from "@/components/workable/console/schedules-screen";
import { clearDefinitionCatalogLevelCache } from "@/components/workable/console/catalog-browser-data";
import { renderDom } from "@/test/dom";
import type {
  WorkScheduleOccurrence,
  WorkScheduleOverviewResult,
  WorkScheduleSnapshot,
  WorkScheduleSummary,
  WorkableConnection,
} from "@/lib/workable";

const connection: WorkableConnection = {
  apiUrl: "https://console.example.com/workable",
  schedulingAvailable: true,
  systemName: "Ops",
};
const activeScheduleId = "11111111-2222-3333-4444-555555555555";
const workerId = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

function getScheduleViewport(
  result: Awaited<ReturnType<typeof renderDom>>,
  kind: "recent" | "upcoming"
) {
  const viewport = result.container.querySelector<HTMLElement>(`.schedule-${kind}-viewport`);
  assert.ok(viewport);
  return viewport;
}

function scrollToScheduleViewportEnd(
  result: Awaited<ReturnType<typeof renderDom>>,
  kind: "recent" | "upcoming"
) {
  return result.scroll(getScheduleViewport(result, kind), {
    clientHeight: 200,
    scrollHeight: 1_000,
    scrollTop: 800,
  });
}

function deferredResponse() {
  let resolve!: (response: Response) => void;
  let reject!: (reason?: unknown) => void;
  const promise = new Promise<Response>((complete, fail) => {
    resolve = complete;
    reject = fail;
  });
  return { promise, reject, resolve };
}

function scheduleOverview(
  schedules: WorkScheduleSummary[],
  selectedSchedule: WorkScheduleSummary | null = schedules[0] ?? null,
  occurrences: WorkScheduleOccurrence[] = [],
  options: {
    recentCursor?: WorkScheduleOverviewResult["recent"]["cursor"];
    upcomingCursor?: WorkScheduleOverviewResult["upcoming"]["cursor"];
    upcomingSchedules?: WorkScheduleSummary[];
  } = {}
): WorkScheduleOverviewResult {
  const upcoming = options.upcomingSchedules ?? getUpcomingSchedules(schedules);
  return {
    occurrences,
    recent: {
      cursor: options.recentCursor ?? null,
      schedules,
    },
    selectedSchedule,
    upcoming: {
      activeScheduleCount: schedules.filter((item) => item.status === "Active").length,
      cursor: options.upcomingCursor ?? null,
      recurringScheduleCount: schedules.filter((item) =>
        item.status === "Active" && Boolean(item.timing.interval || item.timing.cronExpression)
      ).length,
      schedules: upcoming,
      upcomingScheduleCount: upcoming.length,
    },
  };
}

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

test("schedule page merging caps both traversal edges", () => {
  const newest = schedule({
    createdAt: "2098-12-03T10:00:00Z",
    id: { value: "newest" },
  });
  const middle = schedule({
    createdAt: "2098-12-02T10:00:00Z",
    id: { value: "middle" },
  });
  const oldest = schedule({
    createdAt: "2098-12-01T10:00:00Z",
    id: { value: "oldest" },
  });
  const compare = (left: WorkScheduleSummary, right: WorkScheduleSummary) =>
    Date.parse(right.createdAt) - Date.parse(left.createdAt);

  const start = mergeSchedulePages([middle], [oldest, newest], compare, 2, "start");
  const end = mergeSchedulePages([middle], [oldest, newest], compare, 2, "end");
  const untrimmed = mergeSchedulePages([newest], [middle], compare, 2);

  assert.equal(start.trimmed, true);
  assert.deepEqual(start.schedules.map((item) => item.id.value), ["newest", "middle"]);
  assert.equal(end.trimmed, true);
  assert.deepEqual(end.schedules.map((item) => item.id.value), ["middle", "oldest"]);
  assert.equal(untrimmed.trimmed, false);
  assert.deepEqual(untrimmed.schedules.map((item) => item.id.value), ["newest", "middle"]);
});

test("schedule tie ordering matches SQL Server uniqueidentifier ordering", () => {
  const lexicallyFirst = "00000000-0000-0000-0000-000000000002";
  const sqlServerFirst = "ffffffff-ffff-ffff-ffff-000000000001";

  assert.equal(lexicallyFirst.localeCompare(sqlServerFirst) < 0, true);
  assert.equal(compareSqlServerUniqueIdentifiers(lexicallyFirst, sqlServerFirst) > 0, true);
  assert.equal(compareSqlServerUniqueIdentifiers(sqlServerFirst, lexicallyFirst) < 0, true);
  assert.equal(compareSqlServerUniqueIdentifiers(sqlServerFirst, sqlServerFirst), 0);
  assert.equal(compareSqlServerUniqueIdentifiers("first", "second") < 0, true);
});

test("schedule overview uses one request for the index, selection, and occurrences", async () => {
  const hiddenActive = schedule({
    definitionName: "LongRunningSchedule",
    id: { value: "hidden-active" },
    nextRunAt: "2099-01-01T10:00:00Z",
  });
  const fetchMock = installFetch((call) => {
    return Response.json(scheduleOverview([hiddenActive], hiddenActive));
  });
  const controller = new AbortController();

  try {
    const loaded = await loadScheduleOverview(
      connection,
      hiddenActive.id.value,
      controller.signal
    );

    assert.equal(loaded.selectedSchedule?.id.value, hiddenActive.id.value);
    assert.equal(fetchMock.calls.length, 1);
    assert.equal(fetchMock.calls[0]?.init?.signal, controller.signal);
    assert.equal(
      fetchMock.calls[0]?.input,
      `/api/workable/systems/Ops/${createScheduleOverviewPath(hiddenActive.id.value)}`
    );
  } finally {
    fetchMock.restore();
  }
});

test("concurrent abortable overview loads do not share an in-flight GET", async () => {
  const firstResponse = deferredResponse();
  const secondResponse = deferredResponse();
  const responses = [firstResponse, secondResponse];
  const fetchMock = installFetch(() => responses.shift()!.promise);
  const firstController = new AbortController();
  const secondController = new AbortController();

  try {
    const firstLoad = loadScheduleOverview(connection, null, firstController.signal);
    const secondLoad = loadScheduleOverview(connection, null, secondController.signal);

    assert.equal(fetchMock.calls.length, 2);
    assert.equal(fetchMock.calls[0]?.init?.signal, firstController.signal);
    assert.equal(fetchMock.calls[1]?.init?.signal, secondController.signal);

    firstResponse.resolve(Response.json(scheduleOverview([])));
    secondResponse.resolve(Response.json(scheduleOverview([])));
    await Promise.all([firstLoad, secondLoad]);
  } finally {
    fetchMock.restore();
  }
});

test("schedule overview path omits an empty selection", () => {
  assert.equal(
    createScheduleOverviewPath(),
    "schedules/overview?occurrenceTake=50&recentTake=25&upcomingTake=25"
  );
  assert.equal(schedulePageSize, 25);
  assert.equal(
    createSchedulePagePath({
      createdAt: "2099-01-01T00:00:00Z",
      scheduleId: { value: activeScheduleId },
    }),
    `schedules?cursorCreatedAt=2099-01-01T00%3A00%3A00Z&cursorScheduleId=${activeScheduleId}&take=25`
  );
  assert.equal(
    createUpcomingSchedulePagePath({
      nextRunAt: "2099-01-02T00:00:00Z",
      scheduleId: { value: activeScheduleId },
    }),
    `schedules/upcoming?cursorNextRunAt=2099-01-02T00%3A00%3A00Z&cursorScheduleId=${activeScheduleId}&take=25`
  );
});

test("an upcoming-only row can be selected when it is outside the recent page", async () => {
  const recent = schedule({
    definitionName: "RecentOnly",
    id: { value: "12121212-2323-3434-4545-565656565656" },
    nextRunAt: null,
    status: "Completed",
  });
  const upcoming = schedule({
    definitionName: "UpcomingOnly",
    id: { value: "67676767-7878-8989-9090-aaaaaaaaaaaa" },
  });
  const fetchMock = installFetch((call) => Response.json(
    scheduleOverview(
      [recent],
      call.input.includes(`selectedScheduleId=${upcoming.id.value}`) ? upcoming : recent,
      [],
      { upcomingSchedules: [upcoming] }
    )
  ));
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
    await result.waitFor(() => result.getByText("UpcomingOnly"));
    await result.click(result.getByText("UpcomingOnly"));
    await result.waitFor(() => assert.equal(
      fetchMock.calls.some((call) =>
        call.input.includes(`selectedScheduleId=${upcoming.id.value}`)),
      true
    ));
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedule polling jitters its cadence and exponentially backs off failures", () => {
  assert.equal(calculateSchedulePollDelay(10_000, 0, 0), 8_000);
  assert.equal(calculateSchedulePollDelay(10_000, 0, 0.5), 10_000);
  assert.equal(calculateSchedulePollDelay(10_000, 1, 0.5), 20_000);
  assert.equal(calculateSchedulePollDelay(10_000, 2, 1), 48_000);
  assert.equal(calculateSchedulePollDelay(10_000, 20, 1), schedulePollMaximumIntervalMs);
  assert.equal(calculateSchedulePollDelay(0, -1, -1), 1);
});

test("schedule lists use fixed internal infinite-scroll viewports with sticky headings", async () => {
  const currentSchedule = schedule();
  const fetchMock = installFetch(() => Response.json(scheduleOverview([currentSchedule])));
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
    for (const kind of ["upcoming", "recent"] as const) {
      const viewport = getScheduleViewport(result, kind);
      assert.equal(viewport.classList.contains("overflow-auto"), true);
      assert.equal(viewport.classList.contains("h-[28rem]"), true);
      assert.equal(viewport.classList.contains("workable-grid-scrollbar"), true);
      assert.equal(viewport.classList.contains("[&_[data-slot=table-container]]:overflow-visible"), true);
      const header = viewport.querySelector<HTMLElement>("[data-slot='table-header']");
      assert.ok(header);
      assert.equal(header.classList.contains("sticky"), true);
      assert.equal(header.classList.contains("top-0"), true);
    }
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedule paging observes the footer inside its internal viewport", async () => {
  const first = schedule();
  const older = schedule({
    definitionName: "OlderSchedule",
    id: { value: "22222222-3333-4444-5555-666666666666" },
    nextRunAt: null,
    status: "Completed",
  });
  const recentCursor = { createdAt: first.createdAt, scheduleId: first.id };
  let pageCalls = 0;
  const observerRoots: Array<Element | Document | null> = [];
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/schedules/overview?")) {
      return Response.json(scheduleOverview([first], first, [], { recentCursor }));
    }
    if (call.input.includes("/schedules?cursorCreatedAt=")) {
      pageCalls += 1;
      return Response.json({ cursor: null, schedules: [older] });
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
    />,
    {
      setupWindow(window) {
        class PageIntersectionObserver {
          constructor(
            private readonly callback: IntersectionObserverCallback,
            options?: IntersectionObserverInit
          ) {
            observerRoots.push(options?.root ?? null);
          }
          disconnect() {}
          observe(target: Element) {
            this.callback([], this as unknown as IntersectionObserver);
            this.callback([{ isIntersecting: false, target } as IntersectionObserverEntry], this as unknown as IntersectionObserver);
            this.callback([{ isIntersecting: true, target } as IntersectionObserverEntry], this as unknown as IntersectionObserver);
          }
          takeRecords(): IntersectionObserverEntry[] {
            return [];
          }
          unobserve() {}
        }
        Object.defineProperty(window, "IntersectionObserver", {
          configurable: true,
          value: PageIntersectionObserver,
        });
        Object.defineProperty(globalThis, "IntersectionObserver", {
          configurable: true,
          value: PageIntersectionObserver,
        });
      },
    }
  );

  try {
    await result.waitFor(() => result.getByText("OlderSchedule"));
    assert.equal(pageCalls, 1);
    assert.equal(observerRoots.includes(getScheduleViewport(result, "recent")), true);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedules screen stays idle while its target is inactive", async () => {
  const fetchMock = installFetch(() => {
    throw new Error("Inactive schedules should not request data.");
  });
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget={false}
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    assert.equal(fetchMock.calls.length, 0);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedules screen pages recent and upcoming schedules with independent cursors", async () => {
  let overviewCalls = 0;
  let upcomingPageCalls = 0;
  const recentPage = deferredResponse();
  const first = schedule();
  const older = schedule({
    definitionName: "OlderSchedule",
    id: { value: "22222222-3333-4444-5555-666666666666" },
    nextRunAt: null,
    status: "Completed",
  });
  const later = schedule({
    definitionName: "LaterSchedule",
    id: { value: "33333333-4444-5555-6666-777777777777" },
    nextRunAt: "2099-01-02T10:00:00Z",
  });
  const recentCursor = {
    createdAt: first.createdAt,
    scheduleId: first.id,
  };
  const upcomingCursor = {
    nextRunAt: first.nextRunAt!,
    scheduleId: first.id,
  };
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/schedules/overview?")) {
      overviewCalls += 1;
      if (overviewCalls > 1) {
        return Response.json(scheduleOverview([
          { ...first, definitionName: "RefreshedSchedule" },
          schedule({
            definitionName: "NewSchedule",
            id: { value: "55555555-6666-7777-8888-999999999999" },
            nextRunAt: "2098-12-31T10:00:00Z",
          }),
        ]));
      }
      return Response.json(scheduleOverview([first], first, [], { recentCursor, upcomingCursor }));
    }
    if (call.input.includes("/schedules?cursorCreatedAt=")) {
      return recentPage.promise;
    }
    if (call.input.includes("/schedules/upcoming?cursorNextRunAt=")) {
      upcomingPageCalls += 1;
      return Response.json({
        activeScheduleCount: 2,
        cursor: null,
        recurringScheduleCount: 0,
        schedules: [later],
        upcomingScheduleCount: 2,
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
    await result.waitFor(() => getScheduleViewport(result, "recent"));
    await scrollToScheduleViewportEnd(result, "recent");
    await scrollToScheduleViewportEnd(result, "upcoming");
    assert.equal(upcomingPageCalls, 0);
    await act(async () => {
      recentPage.resolve(Response.json({ cursor: null, schedules: [older] }));
      await Promise.resolve();
    });
    await result.waitFor(() => result.getByText("OlderSchedule"));
    result.getByText("Showing 2 schedules");

    await scrollToScheduleViewportEnd(result, "upcoming");
    await result.waitFor(() => result.getByText("LaterSchedule"));
    result.getByText("Showing 2 upcoming schedules");
    assert.equal(upcomingPageCalls, 1);
    assert.equal(fetchMock.calls.some((call) => call.input.endsWith(createSchedulePagePath(recentCursor))), true);
    assert.equal(
      fetchMock.calls.some((call) => call.input.endsWith(createUpcomingSchedulePagePath(upcomingCursor))),
      true
    );
    await result.rerender(
      <SchedulesView
        connection={connection}
        isLoadingTarget
        onOpenWorker={() => undefined}
        onReady={() => undefined}
        refreshToken={1}
      />
    );
    await result.waitFor(() => result.getByText("RefreshedSchedule"));
    result.getByText("NewSchedule");
    result.getByText("OlderSchedule");
    result.getByText("LaterSchedule");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedules screen bounds retained page windows and can return to their first pages", async () => {
  const newest = schedule({
    createdAt: "2098-12-03T10:00:00Z",
    definitionName: "NewestSchedule",
    id: { value: "11111111-1111-1111-1111-111111111111" },
    nextRunAt: null,
    status: "Completed",
  });
  const middle = schedule({
    createdAt: "2098-12-02T10:00:00Z",
    definitionName: "MiddleSchedule",
    id: { value: "22222222-2222-2222-2222-222222222222" },
    nextRunAt: null,
    status: "Completed",
  });
  const oldest = schedule({
    createdAt: "2098-12-01T10:00:00Z",
    definitionName: "OldestSchedule",
    id: { value: "33333333-3333-3333-3333-333333333333" },
    nextRunAt: null,
    status: "Completed",
  });
  const soonest = schedule({
    createdAt: "2098-12-04T10:00:00Z",
    definitionName: "SoonestSchedule",
    id: { value: "44444444-4444-4444-4444-444444444444" },
    nextRunAt: "2099-01-01T10:00:00Z",
  });
  const later = schedule({
    createdAt: "2098-12-05T10:00:00Z",
    definitionName: "LaterSchedule",
    id: { value: "55555555-5555-5555-5555-555555555555" },
    nextRunAt: "2099-01-02T10:00:00Z",
  });
  const latest = schedule({
    createdAt: "2098-12-06T10:00:00Z",
    definitionName: "LatestSchedule",
    id: { value: "66666666-6666-6666-6666-666666666666" },
    nextRunAt: "2099-01-03T10:00:00Z",
  });
  const recentCursor = { createdAt: newest.createdAt, scheduleId: newest.id };
  const upcomingCursor = { nextRunAt: soonest.nextRunAt!, scheduleId: soonest.id };
  let overviewCalls = 0;
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/schedules/overview?")) {
      overviewCalls += 1;
      return Response.json(scheduleOverview([newest], newest, [], {
        recentCursor,
        upcomingCursor,
        upcomingSchedules: [soonest],
      }));
    }
    if (call.input.includes("/schedules?cursorCreatedAt=")) {
      return Response.json({ cursor: null, schedules: [middle, oldest] });
    }
    if (call.input.includes("/schedules/upcoming?cursorNextRunAt=")) {
      return Response.json({
        activeScheduleCount: 3,
        cursor: null,
        recurringScheduleCount: 0,
        schedules: [later, latest],
        upcomingScheduleCount: 3,
      });
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      loadedWindowSize={2}
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      refreshToken={0}
    />
  );

  try {
    const listedDefinitions = (tableIndex: number) => Array.from(
      result.dom.window.document.querySelectorAll("tbody")[tableIndex]?.querySelectorAll("tr") ?? []
    ).map((row) => row.querySelector("td")?.textContent?.trim());
    await result.waitFor(() => getScheduleViewport(result, "recent"));
    await scrollToScheduleViewportEnd(result, "recent");
    await result.waitFor(() => result.getByRole("button", { name: "Return to newest" }));
    await scrollToScheduleViewportEnd(result, "upcoming");
    await result.waitFor(() => result.getByRole("button", { name: "Return to soonest" }));
    assert.deepEqual(listedDefinitions(0), ["LaterSchedule", "LatestSchedule"]);
    assert.deepEqual(listedDefinitions(1), ["MiddleSchedule", "OldestSchedule"]);

    await result.rerender(
      <SchedulesView
        connection={connection}
        isLoadingTarget
        loadedWindowSize={2}
        onOpenWorker={() => undefined}
        onReady={() => undefined}
        refreshToken={1}
      />
    );
    await result.waitFor(() => assert.equal(overviewCalls, 2));
    assert.deepEqual(listedDefinitions(0), ["LaterSchedule", "LatestSchedule"]);
    assert.deepEqual(listedDefinitions(1), ["MiddleSchedule", "OldestSchedule"]);

    await result.click(result.getByRole("button", { name: "Return to soonest" }));
    await result.waitFor(() => assert.equal(overviewCalls, 3));
    await result.waitFor(() => assert.deepEqual(listedDefinitions(0), ["SoonestSchedule"]));
    await result.click(result.getByRole("button", { name: "Return to newest" }));
    await result.waitFor(() => assert.equal(overviewCalls, 4));
    await result.waitFor(() => assert.deepEqual(listedDefinitions(1), ["NewestSchedule"]));
    const buttonLabels = Array.from(result.dom.window.document.querySelectorAll("button"))
      .map((button) => button.textContent?.trim());
    assert.equal(buttonLabels.includes("Return to soonest"), false);
    assert.equal(buttonLabels.includes("Return to newest"), false);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedule page failures remain retryable and preserve specific errors", async () => {
  const first = schedule();
  const recentCursor = { createdAt: first.createdAt, scheduleId: first.id };
  const upcomingCursor = { nextRunAt: first.nextRunAt!, scheduleId: first.id };
  let recentAttempts = 0;
  let upcomingAttempts = 0;
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/schedules/overview?")) {
      return Response.json(scheduleOverview([first], first, [], { recentCursor, upcomingCursor }));
    }
    if (call.input.includes("/schedules?cursorCreatedAt=")) {
      recentAttempts += 1;
      if (recentAttempts === 1) {
        throw new Error("Recent page failed.");
      }
      return Response.json({ cursor: null, schedules: [] });
    }
    if (call.input.includes("/schedules/upcoming?cursorNextRunAt=")) {
      upcomingAttempts += 1;
      if (upcomingAttempts === 1) {
        throw "upcoming offline";
      }
      throw new Error("Upcoming page failed.");
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
    await result.waitFor(() => getScheduleViewport(result, "recent"));
    await scrollToScheduleViewportEnd(result, "recent");
    await result.waitFor(() => result.getByText("Recent page failed."));
    await scrollToScheduleViewportEnd(result, "recent");
    await result.waitFor(() => assert.equal(recentAttempts, 2));

    await scrollToScheduleViewportEnd(result, "upcoming");
    await result.waitFor(() => result.getByText("More upcoming schedules could not be loaded."));
    await scrollToScheduleViewportEnd(result, "upcoming");
    await result.waitFor(() => result.getByText("Upcoming page failed."));
    result.getByText("Scroll to load more");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("a late schedule page cannot leak rows across a connection change", async () => {
  const first = schedule();
  const oldPage = deferredResponse();
  let overviewCalls = 0;
  const recentCursor = { createdAt: first.createdAt, scheduleId: first.id };
  const replacement = schedule({
    definitionName: "ReplacementSchedule",
    id: { value: "44444444-5555-6666-7777-888888888888" },
  });
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/systems/Ops/schedules/overview?")) {
      overviewCalls += 1;
      return Response.json(scheduleOverview([first], first, [], { recentCursor }));
    }
    if (call.input.includes("/systems/Ops/schedules?cursorCreatedAt=")) {
      return oldPage.promise;
    }
    if (call.input.includes("/systems/Ops2/schedules/overview?")) {
      return Response.json(scheduleOverview([replacement], replacement));
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const callbacks = {
    onOpenWorker: () => undefined,
    onReady: () => undefined,
  };
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      {...callbacks}
      pollIntervalMs={1}
      refreshToken={0}
    />
  );

  try {
    await result.waitFor(() => getScheduleViewport(result, "recent"));
    await scrollToScheduleViewportEnd(result, "recent");
    const overviewCallsAfterPagingStarted = overviewCalls;
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 10));
    });
    assert.equal(overviewCalls, overviewCallsAfterPagingStarted);
    await result.rerender(
      <SchedulesView
        connection={{ ...connection, systemName: "Ops2" }}
        isLoadingTarget
        {...callbacks}
        refreshToken={0}
      />
    );
    await result.waitFor(() => result.getByText("ReplacementSchedule"));
    oldPage.resolve(Response.json({
      cursor: null,
      schedules: [schedule({ definitionName: "LeakedSchedule" })],
    }));
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    assert.equal(result.queryByText("LeakedSchedule"), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("a late upcoming page cannot leak rows across a connection change", async () => {
  const first = schedule();
  const oldPage = deferredResponse();
  const upcomingCursor = { nextRunAt: first.nextRunAt!, scheduleId: first.id };
  const replacement = schedule({
    definitionName: "ReplacementUpcoming",
    id: { value: "66666666-7777-8888-9999-aaaaaaaaaaaa" },
  });
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/systems/Ops/schedules/overview?")) {
      return Response.json(scheduleOverview([first], first, [], { upcomingCursor }));
    }
    if (call.input.includes("/systems/Ops/schedules/upcoming?")) {
      return oldPage.promise;
    }
    if (call.input.includes("/systems/Ops2/schedules/overview?")) {
      return Response.json(scheduleOverview([replacement], replacement));
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const callbacks = {
    onOpenWorker: () => undefined,
    onReady: () => undefined,
  };
  const result = await renderDom(
    <SchedulesView connection={connection} isLoadingTarget {...callbacks} refreshToken={0} />
  );

  try {
    await result.waitFor(() => getScheduleViewport(result, "upcoming"));
    await scrollToScheduleViewportEnd(result, "upcoming");
    await result.rerender(
      <SchedulesView
        connection={{ ...connection, systemName: "Ops2" }}
        isLoadingTarget
        {...callbacks}
        refreshToken={0}
      />
    );
    await result.waitFor(() => result.getByText("ReplacementUpcoming"));
    oldPage.resolve(Response.json({
      activeScheduleCount: 2,
      cursor: null,
      recurringScheduleCount: 0,
      schedules: [schedule({ definitionName: "LeakedUpcoming" })],
      upcomingScheduleCount: 2,
    }));
    await act(async () => {
      await new Promise((resolve) => setTimeout(resolve, 0));
    });
    assert.equal(result.queryByText("LeakedUpcoming"), null);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedules screen shows upcoming work and history and cancels an active schedule", async () => {
  let isCanceled = false;
  const openedWorkers: string[] = [];
  let overviewCalls = 0;
  const cancellation = deferredResponse();
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/api/workable/systems/Ops/schedules/overview?")) {
      overviewCalls += 1;
      const schedules = [
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
      ];
      const selectedScheduleId = new URL(call.input, "https://admin.example").searchParams.get("selectedScheduleId");
      const selectedSchedule = schedules.find((item) => item.id.value === selectedScheduleId) ?? schedules[0];
      const occurrences: WorkScheduleOccurrence[] = selectedSchedule?.id.value === activeScheduleId ? [
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
      ] : [];
      return Response.json(scheduleOverview(schedules, selectedSchedule, occurrences));
    }

    if (call.input === `/api/workable/systems/Ops/schedules/${activeScheduleId}/cancel`) {
      return cancellation.promise;
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
    assert.equal(overviewCalls, 1);
    await result.click(result.getByRole("button", { name: workerId }));
    assert.deepEqual(openedWorkers, [workerId]);
    result.getByText("nightly@example.test");
    result.getByText("operator-2");
    result.getByText("Unknown");

    await result.click(result.getByText("NightlyReport"));
    await result.waitFor(() => result.getByText("This schedule has no retained dispatches yet."));
    result.getByText("No");

    assert.equal(
      result.dom.window.document.querySelector('[aria-label="Cancel schedule for ImportOrders"]'),
      null
    );
    await result.click(result.getByText("ImportOrders"));
    await result.click(result.getByRole("button", { name: "Cancel schedule" }));
    result.getByText("Cancel this schedule?");
    const cancelActions = Array.from(result.dom.window.document.querySelectorAll("button"))
      .filter((button) => button.textContent?.trim() === "Cancel schedule");
    assert.ok(cancelActions.length > 0);
    const cancelAction = cancelActions.at(-1)!;
    assert.match(cancelAction.className, /bg-\[var\(--status-danger-solid\)\]/);
    assert.match(cancelAction.className, /text-\[var\(--status-danger-contrast\)\]/);
    act(() => cancelAction.click());
    await result.waitFor(() => assert.equal(cancelAction.disabled, true));
    await act(async () => {
      isCanceled = true;
      cancellation.resolve(Response.json({
        messages: [],
        schedule: schedule({ status: "Canceled", nextRunAt: null }),
        scheduleId: { value: activeScheduleId },
        status: "Accepted",
      }));
      await Promise.resolve();
    });

    await result.waitFor(() => assert.equal(isCanceled, true));
    await result.waitFor(() => assert.equal(overviewCalls, 4));
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

test("a late cancellation result cannot leak schedule state across a connection change", async () => {
  const cancellation = deferredResponse();
  const original = schedule({ definitionName: "OriginalSchedule" });
  const replacement = schedule({
    definitionName: "ReplacementSchedule",
    id: { value: "abababab-cdcd-efef-1212-343434343434" },
  });
  let replacementOverviewCalls = 0;
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/systems/Ops/schedules/overview?")) {
      return Response.json(scheduleOverview([original], original));
    }
    if (call.input.endsWith(`/systems/Ops/schedules/${original.id.value}/cancel`)) {
      return cancellation.promise;
    }
    if (call.input.includes("/systems/Ops2/schedules/overview?")) {
      replacementOverviewCalls += 1;
      return Response.json(scheduleOverview([replacement], replacement));
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const callbacks = {
    onOpenWorker: () => undefined,
    onReady: () => undefined,
  };
  const result = await renderDom(
    <SchedulesView connection={connection} isLoadingTarget {...callbacks} refreshToken={0} />
  );

  try {
    await result.waitFor(() => result.getByText("OriginalSchedule"));
    await result.click(result.getByRole("button", { name: "Cancel schedule" }));
    const cancelAction = Array.from(result.dom.window.document.querySelectorAll("button"))
      .filter((button) => button.textContent?.trim() === "Cancel schedule")
      .at(-1)!;
    act(() => cancelAction.click());
    await result.rerender(
      <SchedulesView
        connection={{ ...connection, systemName: "Ops2" }}
        isLoadingTarget
        {...callbacks}
        refreshToken={0}
      />
    );
    await result.waitFor(() => result.getByText("ReplacementSchedule"));
    assert.equal(result.queryByText("Cancel this schedule?"), null);

    await act(async () => {
      cancellation.resolve(Response.json({
        messages: [],
        schedule: schedule({ definitionName: "LeakedCanceledSchedule", status: "Canceled" }),
        scheduleId: original.id,
        status: "Accepted",
      }));
      await Promise.resolve();
    });
    assert.equal(result.queryByText("LeakedCanceledSchedule"), null);
    assert.equal(replacementOverviewCalls, 1);
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("a late cancellation failure cannot overwrite the new connection error state", async () => {
  const cancellation = deferredResponse();
  const original = schedule({ definitionName: "OriginalFailureSchedule" });
  const replacement = schedule({
    definitionName: "ReplacementAfterFailure",
    id: { value: "bcbcbcbc-dede-fafa-2323-454545454545" },
  });
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/systems/Ops/schedules/overview?")) {
      return Response.json(scheduleOverview([original], original));
    }
    if (call.input.endsWith(`/systems/Ops/schedules/${original.id.value}/cancel`)) {
      return cancellation.promise;
    }
    if (call.input.includes("/systems/Ops2/schedules/overview?")) {
      return Response.json(scheduleOverview([replacement], replacement));
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const callbacks = {
    onOpenWorker: () => undefined,
    onReady: () => undefined,
  };
  const result = await renderDom(
    <SchedulesView connection={connection} isLoadingTarget {...callbacks} refreshToken={0} />
  );

  try {
    await result.waitFor(() => result.getByText("OriginalFailureSchedule"));
    await result.click(result.getByRole("button", { name: "Cancel schedule" }));
    const cancelAction = Array.from(result.dom.window.document.querySelectorAll("button"))
      .filter((button) => button.textContent?.trim() === "Cancel schedule")
      .at(-1)!;
    act(() => cancelAction.click());
    await result.rerender(
      <SchedulesView
        connection={{ ...connection, systemName: "Ops2" }}
        isLoadingTarget
        {...callbacks}
        refreshToken={0}
      />
    );
    await result.waitFor(() => result.getByText("ReplacementAfterFailure"));

    await act(async () => {
      cancellation.reject(new Error("Late cancellation failed."));
      await Promise.resolve();
    });
    assert.equal(result.queryByText("Late cancellation failed."), null);
    result.getByText("ReplacementAfterFailure");
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("schedules screen fails closed when the consolidated overview is unavailable", async () => {
  let rejectList: ((reason: unknown) => void) | undefined;
  const listPromise = new Promise<Response>((_resolve, reject) => {
    rejectList = reject;
  });
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/schedules/overview?")) {
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
});

test("schedules screen surfaces a rejected cancellation", async () => {
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/schedules/overview?")) {
      return Response.json(scheduleOverview([schedule()], schedule()));
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
    await result.click(result.getByRole("button", { name: "Cancel schedule" }));
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
    if (call.input.includes("/schedules/overview?")) {
      listCalls += 1;
      return Response.json(scheduleOverview([schedule()], schedule()));
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
    await result.click(result.getByRole("button", { name: "Cancel schedule" }));
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

test("schedules screen keeps rows during refresh and includes selection in the consolidated request", async () => {
  const secondScheduleId = "22222222-3333-4444-5555-666666666666";
  let overviewCalls = 0;
  let resolveRefresh: ((response: Response) => void) | undefined;
  const refresh = new Promise<Response>((resolve) => {
    resolveRefresh = resolve;
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
    if (call.input.includes("/schedules/overview?")) {
      overviewCalls += 1;
      if (overviewCalls === 3) {
        return refresh;
      }

      const selectedId = new URL(call.input, "https://admin.example").searchParams.get("selectedScheduleId");
      const selectedSchedule = schedules.find((item) => item.id.value === selectedId) ?? schedules[0];
      return Response.json(scheduleOverview(schedules, selectedSchedule));
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
    await result.waitFor(() => assert.equal(overviewCalls, 1));
    await result.click(result.getByText("SecondTask"));
    await result.waitFor(() => result.getByText("This schedule has no retained dispatches yet."));
    assert.equal(
      fetchMock.calls.some((call) => call.input.includes(`selectedScheduleId=${secondScheduleId}`)),
      true
    );
    await result.rerender(
      <SchedulesView
        connection={connection}
        isLoadingTarget
        onOpenWorker={() => undefined}
        onReady={onReady}
        refreshToken={1}
      />
    );
    await result.waitFor(() => assert.equal(overviewCalls, 3));
    result.getByText("SecondTask");

    resolveRefresh?.(Response.json(scheduleOverview(schedules, schedules[1])));
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

test("schedules screen polls only while visible and does not overlap overview requests", async () => {
  let visibilityState: DocumentVisibilityState = "visible";
  let overviewCalls = 0;
  let resolvePoll: ((response: Response) => void) | undefined;
  const pendingPoll = new Promise<Response>((resolve) => {
    resolvePoll = resolve;
  });
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/schedules/overview?")) {
      overviewCalls += 1;
      if (overviewCalls === 2) {
        return pendingPoll;
      }
      if (overviewCalls === 3) {
        visibilityState = "hidden";
      }
      return Response.json(scheduleOverview([schedule()], schedule()));
    }
    return Response.json({ error: "Unhandled" }, { status: 500 });
  });
  const result = await renderDom(
    <SchedulesView
      connection={connection}
      isLoadingTarget
      onOpenWorker={() => undefined}
      onReady={() => undefined}
      pollIntervalMs={20}
      refreshToken={0}
    />,
    {
      setupWindow(window) {
        Object.defineProperty(window.document, "visibilityState", {
          configurable: true,
          get: () => visibilityState,
        });
      },
    }
  );

  try {
    await result.waitFor(() => assert.equal(overviewCalls, 2));
    await act(async () => {
      result.dom.window.document.dispatchEvent(new result.dom.window.Event("visibilitychange"));
      await new Promise((resolve) => setTimeout(resolve, 60));
    });
    assert.equal(overviewCalls, 2);

    visibilityState = "hidden";
    await act(async () => {
      resolvePoll?.(Response.json(scheduleOverview([schedule()], schedule())));
      await new Promise((resolve) => setTimeout(resolve, 60));
    });
    assert.equal(overviewCalls, 2);

    visibilityState = "visible";
    await act(async () => {
      result.dom.window.document.dispatchEvent(new result.dom.window.Event("visibilitychange"));
    });
    await result.waitFor(() => assert.equal(overviewCalls, 3));
  } finally {
    fetchMock.restore();
    await result.restore();
  }
});

test("new schedule surfaces definition loading failures", async () => {
  clearDefinitionCatalogLevelCache();
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/schedules/overview?")) {
      return Response.json(scheduleOverview([], null));
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

test("a late definition response cannot open a schedule dialog across a connection change", async () => {
  clearDefinitionCatalogLevelCache();
  const definitionInfo = deferredResponse();
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/systems/Ops/schedules/overview?")) {
      return Response.json(scheduleOverview([], null));
    }
    if (call.input === "/api/workable/systems/Ops/definitions?level=true") {
      return Response.json({ categories: [], definitions: [{ category: "Operations", name: "ImportOrders" }] });
    }
    if (call.input === "/api/workable/systems/Ops/definitions/ImportOrders/info") {
      return definitionInfo.promise;
    }
    if (call.input.includes("/systems/Ops2/schedules/overview?")) {
      return Response.json(scheduleOverview([], null));
    }
    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const callbacks = {
    onOpenWorker: () => undefined,
    onReady: () => undefined,
  };
  const result = await renderDom(
    <SchedulesView connection={connection} isLoadingTarget {...callbacks} refreshToken={0} />
  );

  try {
    await result.waitFor(() => result.getByText("No schedules have been created for this work system."));
    await result.click(result.getByRole("button", { name: "New schedule" }));
    await result.waitFor(() => result.getByRole("button", { name: "ImportOrders" }));
    await result.click(result.getByRole("button", { name: "ImportOrders" }));
    await result.rerender(
      <SchedulesView
        connection={{ ...connection, systemName: "Ops2" }}
        isLoadingTarget
        {...callbacks}
        refreshToken={0}
      />
    );
    await result.waitFor(() => assert.equal(result.queryByText("Choose work to schedule"), null));

    await act(async () => {
      definitionInfo.resolve(Response.json(definitionInfoResponse()));
      await Promise.resolve();
    });
    assert.equal(result.queryByText("Schedule this work"), null);
    assert.equal(result.queryByText("Definition could not be loaded."), null);
  } finally {
    fetchMock.restore();
    await result.restore();
    clearDefinitionCatalogLevelCache();
  }
});

test("a connection change closes an open schedule creation dialog", async () => {
  clearDefinitionCatalogLevelCache();
  const fetchMock = installFetch((call) => {
    if (call.input.includes("/systems/Ops/schedules/overview?") ||
        call.input.includes("/systems/Ops2/schedules/overview?")) {
      return Response.json(scheduleOverview([], null));
    }
    if (call.input === "/api/workable/systems/Ops/definitions?level=true") {
      return Response.json({ categories: [], definitions: [{ category: "Operations", name: "ImportOrders" }] });
    }
    if (call.input === "/api/workable/systems/Ops/definitions/ImportOrders/info") {
      return Response.json(definitionInfoResponse());
    }
    return Response.json({ error: `Unhandled request: ${call.input}` }, { status: 500 });
  });
  const callbacks = {
    onOpenWorker: () => undefined,
    onReady: () => undefined,
  };
  const result = await renderDom(
    <SchedulesView connection={connection} isLoadingTarget {...callbacks} refreshToken={0} />
  );

  try {
    await result.waitFor(() => result.getByText("No schedules have been created for this work system."));
    await result.click(result.getByRole("button", { name: "New schedule" }));
    await result.waitFor(() => result.getByRole("button", { name: "ImportOrders" }));
    await result.click(result.getByRole("button", { name: "ImportOrders" }));
    await result.waitFor(() => result.getByText("Schedule this work"));
    await result.rerender(
      <SchedulesView
        connection={{ ...connection, systemName: "Ops2" }}
        isLoadingTarget
        {...callbacks}
        refreshToken={0}
      />
    );
    await result.waitFor(() => assert.equal(result.queryByText("Schedule this work"), null));
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
    if (call.input.includes("/api/workable/systems/Ops/schedules/overview?")) {
      listCalls += 1;
      return Response.json(scheduleOverview([], null));
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

function definitionInfoResponse() {
  return {
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
  };
}

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
