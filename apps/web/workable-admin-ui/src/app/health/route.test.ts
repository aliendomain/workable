import assert from "node:assert/strict";
import test from "node:test";
import { GET, HEAD } from "./route.ts";

test("health route returns deploy probe payload", async () => {
  const response = await GET();

  assert.equal(response.status, 200);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(response.headers.get("x-content-type-options"), "nosniff");

  const payload = await response.json();
  assert.equal(payload.status, "ok");
  assert.equal(payload.service, "workable-admin-ui");
  assert.equal(typeof payload.timestamp, "string");
  assert.ok(!Number.isNaN(Date.parse(payload.timestamp)));
});

test("health route supports HEAD probes", async () => {
  const response = await HEAD();

  assert.equal(response.status, 200);
  assert.equal(response.headers.get("cache-control"), "no-store");
  assert.equal(response.headers.get("x-content-type-options"), "nosniff");
  assert.equal(await response.text(), "");
});
