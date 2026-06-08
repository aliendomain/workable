import assert from "node:assert/strict";
import test from "node:test";
import {
  createWorkComponentRequest,
  getWorkComponentData,
  getWorkComponentErrors,
} from "./component-results.ts";
import type { WorkComponentQueryResult } from "@/lib/workable";

test("work component request helper omits optional fields until they are needed", () => {
  assert.deepEqual(createWorkComponentRequest("workers"), {
    id: "workers",
    type: "workers",
  });
  assert.deepEqual(createWorkComponentRequest("grid", "workerGrid", "detailed", { take: 50 }), {
    id: "grid",
    options: { take: 50 },
    shape: "detailed",
    type: "workerGrid",
  });
});

test("work component result helpers read successful components and describe failures", () => {
  const result: WorkComponentQueryResult = {
    generatedAt: "2026-05-30T12:00:00Z",
    components: {
      ok: {
        status: "OK",
        data: { count: 3 },
      },
      failed: {
        status: "Error",
        error: "No access.",
      },
      missingError: {
        status: "Error",
      },
    },
  };

  assert.deepEqual(getWorkComponentData<{ count: number }>(result, "ok"), { count: 3 });
  assert.equal(getWorkComponentData(result, "failed"), undefined);
  assert.deepEqual(getWorkComponentErrors(result), [
    "No access.",
    "missingError failed to load.",
  ]);
});
