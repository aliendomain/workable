import assert from "node:assert/strict";
import test from "node:test";
import nextConfig from "../next.config.ts";

test("next config applies baseline browser security headers", async () => {
  const configuredHeaders = await nextConfig.headers?.();

  assert.ok(configuredHeaders);
  const allHeaders = configuredHeaders.flatMap((entry) => entry.headers);
  const headerValue = (name: string) =>
    allHeaders.find((header) => header.key.toLowerCase() === name.toLowerCase())?.value;

  assert.equal(headerValue("X-Frame-Options"), "DENY");
  assert.equal(headerValue("X-Content-Type-Options"), "nosniff");
  assert.equal(headerValue("Referrer-Policy"), "no-referrer");
  assert.equal(headerValue("Cross-Origin-Opener-Policy"), "same-origin");
  assert.match(headerValue("Permissions-Policy") ?? "", /camera=\(\)/);

  const csp = headerValue("Content-Security-Policy") ?? "";
  assert.match(csp, /base-uri 'self'/);
  assert.match(csp, /form-action 'self'/);
  assert.match(csp, /frame-ancestors 'none'/);
  assert.match(csp, /object-src 'none'/);
  assert.doesNotMatch(csp, /default-src/);
  assert.doesNotMatch(csp, /script-src/);
  assert.doesNotMatch(csp, /style-src/);
  assert.doesNotMatch(csp, /connect-src/);
});
