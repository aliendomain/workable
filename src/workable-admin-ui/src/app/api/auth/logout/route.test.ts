import assert from "node:assert/strict";
import test from "node:test";
import { POST } from "./route.ts";

test("logout route rejects unsafe POST requests without a same-origin Origin", async () => {
  const response = await POST(new Request("https://admin.example.com/api/auth/logout", {
    method: "POST",
  }));

  assert.equal(response.status, 403);
  assert.deepEqual(await response.json(), {
    error: "Unsafe Workable admin UI requests require a same-origin Origin header.",
  });
});

test("logout route clears admin session cookies for same-origin requests", async () => {
  const response = await POST(new Request("https://admin.example.com/api/auth/logout", {
    headers: {
      origin: "https://admin.example.com",
    },
    method: "POST",
  }));

  assert.equal(response.status, 200);
  assert.deepEqual(await response.json(), { ok: true });
  assert.match(response.headers.get("set-cookie") ?? "", /workable_admin_session=;/);
  assert.match(response.headers.get("set-cookie") ?? "", /Max-Age=0/);
});
