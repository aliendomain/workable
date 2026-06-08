import assert from "node:assert/strict";
import test from "node:test";
import {
  WorkableApiError,
  getWorkableRealtimeAccessToken,
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

test("getWorkableRealtimeAccessToken fetches, caches, and reuses hosted API tokens", async () => {
  const apiUrl = "https://token-cache.example.com/workable";
  const calls: string[] = [];
  const restoreFetch = mockFetch(async (input) => {
    calls.push(String(input));
    return jsonResponse({ accessToken: "token-123" });
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
