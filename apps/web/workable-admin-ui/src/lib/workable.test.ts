import assert from "node:assert/strict";
import test from "node:test";
import {
  WorkableApiError,
  WorkableRequestHeadersTooLargeError,
  WorkableRealtimeAuthenticationError,
  getWorkableRealtimeAccessToken,
  hasWorkableRequestHeadersTooLargeFailure,
  invalidateWorkableRealtimeAccessToken,
  isWorkableRequestHeadersTooLargeError,
  resetWorkableRequestHeadersTooLargeFailureForTests,
  stopWorkableRequestsForOversizedHeaders,
  workableFetch,
  workableQueryFetch,
  type WorkableConnection,
} from "./workable.ts";

const connection: WorkableConnection = {
  apiUrl: "https://workable.example.com/workable",
  systemName: "Main system",
};

test("workableFetch scopes requests to the selected system and sends the target API header", async () => {
  const calls: Array<{ input: string; init?: RequestInit }> = [];
  const restoreFetch = mockFetch(async (input, init) => {
    calls.push({ input: String(input), init });
    return jsonResponse({ ok: true, name: "Overview" });
  });

  try {
    const result = await workableFetch<{ ok: boolean; name: string }>(
      connection,
      "/views/overview"
    );

    assert.deepEqual(result, { ok: true, name: "Overview" });
    assert.equal(calls.length, 1);
    assert.equal(
      calls[0]?.input,
      "/api/workable/systems/Main%20system/views/overview"
    );
    assert.equal(
      getHeaderValue(calls[0]?.init?.headers, "x-workable-api-url"),
      "https://workable.example.com/workable"
    );
    assert.equal(getHeaderValue(calls[0]?.init?.headers, "content-type"), "application/json");
  } finally {
    restoreFetch();
  }
});

test("workableQueryFetch coalesces identical in-flight query requests", async () => {
  let fetchCount = 0;
  let releaseResponse!: () => void;
  const responseGate = new Promise<void>((resolve) => {
    releaseResponse = resolve;
  });
  const restoreFetch = mockFetch(async () => {
    fetchCount += 1;
    await responseGate;
    return jsonResponse({ rows: [1, 2, 3] });
  });

  try {
    const init = {
      body: JSON.stringify({ filter: "running" }),
      method: "POST",
    };
    const first = workableQueryFetch<{ rows: number[] }>(connection, "views/workers/query", init);
    const second = workableQueryFetch<{ rows: number[] }>(connection, "views/workers/query", init);

    await Promise.resolve();
    assert.equal(fetchCount, 1);

    releaseResponse();
    assert.deepEqual(await first, { rows: [1, 2, 3] });
    assert.deepEqual(await second, { rows: [1, 2, 3] });
  } finally {
    restoreFetch();
  }
});

test("workableFetch can bypass an identical in-flight GET for fresh reconciliation", async () => {
  let fetchCount = 0;
  let releaseFirst!: () => void;
  const firstGate = new Promise<void>((resolve) => {
    releaseFirst = resolve;
  });
  const restoreFetch = mockFetch(async () => {
    fetchCount += 1;
    if (fetchCount === 1) {
      await firstGate;
      return jsonResponse({ version: "stale" });
    }
    return jsonResponse({ version: "fresh" });
  });

  try {
    const first = workableFetch<{ version: string }>(connection, "capture-rules");
    await Promise.resolve();
    const fresh = workableFetch<{ version: string }>(
      connection,
      "capture-rules",
      undefined,
      { coalesce: false }
    );

    assert.deepEqual(await fresh, { version: "fresh" });
    assert.equal(fetchCount, 2);
    releaseFirst();
    assert.deepEqual(await first, { version: "stale" });
  } finally {
    releaseFirst();
    restoreFetch();
  }
});

test("workableFetch surfaces API problem details and redirects auth-required responses to login", async () => {
  const replacements: string[] = [];
  const restoreWindow = mockWindowLocation("/workers", "?state=Running", replacements);
  const restoreFetch = mockFetch(async () =>
    jsonResponse(
      { error: "Authentication is required for the Workable admin UI." },
      { status: 401 }
    )
  );

  try {
    await assert.rejects(
      workableFetch(connection, "views/workers"),
      (error) => {
        assert.equal(error instanceof WorkableApiError, true);
        const apiError = error as WorkableApiError;
        assert.equal(apiError.status, 401);
        assert.equal(apiError.message, "Authentication is required for the Workable admin UI.");
        return true;
      }
    );

    assert.deepEqual(replacements, [
      "/login?next=%2Fworkers%3Fstate%3DRunning&error=https%3A%2F%2Fworkable.example.com%2Fworkable&reason=unauthorized",
    ]);
  } finally {
    restoreFetch();
    restoreWindow();
  }
});

test("a 431 opens a page-wide circuit breaker for subsequent Workable requests", async () => {
  let fetchCount = 0;
  const restoreFetch = mockFetch(async () => {
    fetchCount++;
    return new Response("{malformed", {
      headers: { "content-type": "application/json" },
      status: 431,
    });
  });

  resetWorkableRequestHeadersTooLargeFailureForTests();
  try {
    await assert.rejects(
      workableFetch(connection, "views/workers"),
      (error) => {
        assert.equal(error instanceof WorkableRequestHeadersTooLargeError, true);
        assert.match((error as Error).message, /Automatic requests have stopped/);
        return true;
      }
    );
    await assert.rejects(
      workableFetch(connection, "views/overview"),
      WorkableRequestHeadersTooLargeError
    );
    assert.equal(fetchCount, 1);
  } finally {
    resetWorkableRequestHeadersTooLargeFailureForTests();
    restoreFetch();
  }
});

test("oversized-header detection recognizes fetch and SignalR error shapes", () => {
  const terminal = new WorkableRequestHeadersTooLargeError();

  assert.equal(isWorkableRequestHeadersTooLargeError(terminal), true);
  assert.equal(
    isWorkableRequestHeadersTooLargeError(new WorkableApiError("large", 431, null)),
    true
  );
  assert.equal(
    isWorkableRequestHeadersTooLargeError(new WorkableApiError("failed", 500, null)),
    false
  );
  assert.equal(isWorkableRequestHeadersTooLargeError({ status: 431 }), true);
  assert.equal(isWorkableRequestHeadersTooLargeError({ statusCode: "431" }), true);
  assert.equal(
    isWorkableRequestHeadersTooLargeError(new Error("Request Header Fields Too Large")),
    true
  );
  assert.equal(
    isWorkableRequestHeadersTooLargeError(new Error("Negotiation returned status 431")),
    true
  );
  assert.equal(
    isWorkableRequestHeadersTooLargeError(
      new Error("Response status code does not indicate success: 431")
    ),
    true
  );
  assert.equal(isWorkableRequestHeadersTooLargeError("HTTP 431"), true);
  assert.equal(isWorkableRequestHeadersTooLargeError("Disconnected after 431 ms"), false);
  assert.equal(isWorkableRequestHeadersTooLargeError("Worker 431 failed"), false);
  assert.equal(isWorkableRequestHeadersTooLargeError(null), false);

  resetWorkableRequestHeadersTooLargeFailureForTests();
  assert.equal(stopWorkableRequestsForOversizedHeaders(new Error("temporary")), null);
  assert.equal(hasWorkableRequestHeadersTooLargeFailure(), false);
  assert.strictEqual(stopWorkableRequestsForOversizedHeaders(terminal), terminal);
  assert.equal(hasWorkableRequestHeadersTooLargeFailure(), true);
  assert.strictEqual(stopWorkableRequestsForOversizedHeaders(), terminal);
  resetWorkableRequestHeadersTooLargeFailureForTests();
});

test("getWorkableRealtimeAccessToken fetches, caches, and reuses hosted API tokens", async () => {
  const apiUrl = "https://token-cache.example.com/workable";
  const calls: string[] = [];
  const restoreFetch = mockFetch(async (input) => {
    calls.push(String(input));
    return jsonResponse({
      accessToken: "token-123",
      accessTokenExpiresInSeconds: 3600,
    });
  });

  try {
    const first = await getWorkableRealtimeAccessToken(apiUrl);
    const second = await getWorkableRealtimeAccessToken(apiUrl);

    assert.equal(first, "token-123");
    assert.equal(second, "token-123");
    assert.deepEqual(calls, [
      "/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Ftoken-cache.example.com%2Fworkable",
    ]);
  } finally {
    restoreFetch();
  }
});

test("getWorkableRealtimeAccessToken renews according to the token's actual expiration", async () => {
  const apiUrl = "https://token-expiration.example.com/workable";
  const originalNow = Date.now;
  let now = 1_800_000_000_000;
  const calls: string[] = [];
  Date.now = () => now;
  const restoreFetch = mockFetch(async (input) => {
    calls.push(String(input));
    return jsonResponse({
      accessToken: calls.length === 1 ? "nearly-expired-token" : "renewed-token",
      accessTokenExpiresInSeconds: calls.length === 1 ? 120 : 3600,
    });
  });

  try {
    assert.equal(await getWorkableRealtimeAccessToken(apiUrl), "nearly-expired-token");
    assert.equal(await getWorkableRealtimeAccessToken(apiUrl), "nearly-expired-token");

    now += 61_000;

    assert.equal(await getWorkableRealtimeAccessToken(apiUrl), "renewed-token");
    assert.equal(calls.length, 2);
  } finally {
    restoreFetch();
    Date.now = originalNow;
  }
});

test("getWorkableRealtimeAccessToken can force a server-side refresh after a realtime 401", async () => {
  const apiUrl = "https://token-force-refresh.example.com/workable";
  const calls: Array<{ forceRefresh: string | null; input: string }> = [];
  const restoreFetch = mockFetch(async (input, init) => {
    calls.push({
      forceRefresh: new Headers(init?.headers).get("x-workable-force-token-refresh"),
      input: String(input),
    });
    return jsonResponse({
      accessToken: calls.length === 1 ? "rejected-token" : "replacement-token",
      accessTokenExpiresInSeconds: 3600,
    });
  });

  try {
    assert.equal(await getWorkableRealtimeAccessToken(apiUrl), "rejected-token");

    invalidateWorkableRealtimeAccessToken(apiUrl, true);

    assert.equal(await getWorkableRealtimeAccessToken(apiUrl), "replacement-token");
    assert.deepEqual(calls, [
      {
        forceRefresh: null,
        input: "/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Ftoken-force-refresh.example.com%2Fworkable",
      },
      {
        forceRefresh: "true",
        input: "/api/auth/entra/workable-token?apiUrl=https%3A%2F%2Ftoken-force-refresh.example.com%2Fworkable",
      },
    ]);
  } finally {
    restoreFetch();
  }
});

test("getWorkableRealtimeAccessToken throws the server error message when token acquisition fails", async () => {
  const apiUrl = "https://token-error.example.com/workable";
  const restoreFetch = mockFetch(async () =>
    jsonResponse({ error: "Hosted token exchange failed." }, { status: 502 })
  );

  try {
    await assert.rejects(
      getWorkableRealtimeAccessToken(apiUrl),
      /Hosted token exchange failed\./
    );
  } finally {
    restoreFetch();
  }
});

test("a realtime token 431 opens the shared Workable request circuit breaker", async () => {
  const apiUrl = "https://token-headers.example.com/workable";
  let fetchCount = 0;
  const restoreFetch = mockFetch(async () => {
    fetchCount++;
    return new Response("{malformed", {
      headers: { "content-type": "application/json" },
      status: 431,
    });
  });

  resetWorkableRequestHeadersTooLargeFailureForTests();
  try {
    await assert.rejects(
      getWorkableRealtimeAccessToken(apiUrl),
      WorkableRequestHeadersTooLargeError
    );
    await assert.rejects(
      getWorkableRealtimeAccessToken(apiUrl),
      WorkableRequestHeadersTooLargeError
    );
    await assert.rejects(
      workableFetch(connection, "views/workers"),
      WorkableRequestHeadersTooLargeError
    );
    assert.equal(fetchCount, 1);
  } finally {
    resetWorkableRequestHeadersTooLargeFailureForTests();
    restoreFetch();
  }
});

test("getWorkableRealtimeAccessToken classifies interactive sign-in requirements", async () => {
  const apiUrl = "https://token-sign-in.example.com/workable";
  const restoreFetch = mockFetch(async () =>
    jsonResponse(
      { error: "Microsoft Entra ID access has expired. Sign in again." },
      { status: 401 }
    )
  );

  try {
    await assert.rejects(
      getWorkableRealtimeAccessToken(apiUrl),
      (error) => {
        assert.equal(error instanceof WorkableRealtimeAuthenticationError, true);
        assert.match((error as Error).message, /Sign in again/);
        return true;
      }
    );
  } finally {
    restoreFetch();
  }
});

function jsonResponse(body: unknown, init?: ResponseInit) {
  return new Response(JSON.stringify(body), {
    ...init,
    headers: {
      "content-type": "application/json",
      ...init?.headers,
    },
  });
}

function mockFetch(
  handler: (input: RequestInfo | URL, init?: RequestInit) => Promise<Response> | Response
) {
  const previousFetch = globalThis.fetch;
  globalThis.fetch = handler as typeof fetch;
  return () => {
    globalThis.fetch = previousFetch;
  };
}

function getHeaderValue(headers: RequestInit["headers"], name: string) {
  return new Headers(headers).get(name);
}

function mockWindowLocation(pathname: string, search: string, replacements: string[]) {
  const globalWithWindow = globalThis as typeof globalThis & { window?: unknown };
  const previousWindow = globalWithWindow.window;
  Object.defineProperty(globalThis, "window", {
    configurable: true,
    value: {
      location: {
        pathname,
        replace(value: string) {
          replacements.push(value);
        },
        search,
      },
    },
  });

  return () => {
    if (previousWindow === undefined) {
      Reflect.deleteProperty(globalWithWindow, "window");
    } else {
      Object.defineProperty(globalThis, "window", {
        configurable: true,
        value: previousWindow,
      });
    }
  };
}
