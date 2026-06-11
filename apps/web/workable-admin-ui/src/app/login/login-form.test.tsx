import assert from "node:assert/strict";
import test from "node:test";
import { LoginForm } from "@/app/login/login-form";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";
import { renderDom } from "@/test/dom";
import {
  getNextNavigationRouterCalls,
  resetNextNavigationMock,
} from "@/test/next-navigation";

test("basic login form defers credential inputs until hydration and preserves unauthorized error state", () => {
  const markup = renderMarkup(
    <LoginForm
      authProvider="basic"
      initialError="Unauthorized user."
      initialReason="unauthorized"
      nextPath="/workable?tab=overview"
    />
  );

  assertMarkupIncludes(markup, "Preparing secure sign-in...");
  assertMarkupIncludes(markup, "Unauthorized");
  assertMarkupIncludes(markup, "Unauthorized user.");
  assertMarkupIncludes(markup, "Sign in");
  assertMarkupExcludes(markup, "autoComplete=\"username\"");
  assertMarkupExcludes(markup, "autoComplete=\"current-password\"");
  assertMarkupExcludes(markup, "Sign in with Microsoft");
});

test("entra login form renders Microsoft sign-in link and session-expired error title", () => {
  const markup = renderMarkup(
    <LoginForm
      authProvider="entra"
      initialError="Please sign in again."
      initialReason={null}
      nextPath="/workable/systems?name=Ops"
    />
  );

  assertMarkupIncludes(markup, "Session expired");
  assertMarkupIncludes(markup, "Please sign in again.");
  assertMarkupIncludes(markup, "Sign in with Microsoft");
  assertMarkupIncludes(markup, "/api/auth/entra/login?next=%2Fworkable%2Fsystems%3Fname%3DOps");
  assertMarkupExcludes(markup, "Username");
  assertMarkupExcludes(markup, "Password");
});

test("basic login form submits credentials and navigates on success", async () => {
  resetNextNavigationMock();
  const originalFetch = globalThis.fetch;
  let requestUrl = "";
  let requestMethod = "";
  let requestContentType = "";
  let requestBody = "";

  globalThis.fetch = async (input, init) => {
    requestUrl = String(input);
    requestMethod = init?.method ?? "";
    requestContentType = typeof init?.headers === "object" && init.headers !== null
      ? String((init.headers as Record<string, string>)["content-type"] ?? "")
      : "";
    requestBody = String(init?.body ?? "");
    return Response.json({ userName: "admin" });
  };

  const render = await renderDom(
    <LoginForm
      authProvider="basic"
      initialError={null}
      initialReason={null}
      nextPath="/workable?tab=workers"
    />
  );

  try {
    await render.input(render.getByLabelText("Username") as HTMLInputElement, "admin");
    await render.input(render.getByLabelText("Password") as HTMLInputElement, "secret");
    const form = render.container.querySelector("form");
    assert.ok(form instanceof render.dom.window.HTMLFormElement);

    await render.submit(form);

    assert.equal(requestUrl, "/api/auth/login");
    assert.equal(requestMethod, "POST");
    assert.equal(requestContentType, "application/json");
    assert.equal(requestBody, JSON.stringify({ userName: "admin", password: "secret" }));
    assert.deepEqual(getNextNavigationRouterCalls(), {
      refreshCount: 1,
      replaces: ["/workable?tab=workers"],
    });
    assert.equal(render.queryByText("Sign in failed"), null);
  } finally {
    globalThis.fetch = originalFetch;
    resetNextNavigationMock();
    await render.restore();
  }
});

test("basic login form shows server validation errors without navigating", async () => {
  resetNextNavigationMock();
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () =>
    Response.json({ error: "Invalid credentials." }, { status: 401 });

  const render = await renderDom(
    <LoginForm
      authProvider="basic"
      initialError={null}
      initialReason={null}
      nextPath="/"
    />
  );

  try {
    await render.input(render.getByLabelText("Username") as HTMLInputElement, "admin");
    await render.input(render.getByLabelText("Password") as HTMLInputElement, "wrong");
    const form = render.container.querySelector("form");
    assert.ok(form instanceof render.dom.window.HTMLFormElement);

    await render.submit(form);

    render.getByText("Invalid credentials.");
    assert.deepEqual(getNextNavigationRouterCalls(), {
      refreshCount: 0,
      replaces: [],
    });
    assert.equal((render.getByRole("button", { name: "Sign in" }) as HTMLButtonElement).disabled, false);
  } finally {
    globalThis.fetch = originalFetch;
    resetNextNavigationMock();
    await render.restore();
  }
});

test("basic login form shows a recoverable message when the request fails", async () => {
  resetNextNavigationMock();
  const originalFetch = globalThis.fetch;
  globalThis.fetch = async () => {
    throw new Error("network down");
  };

  const render = await renderDom(
    <LoginForm
      authProvider="basic"
      initialError={null}
      initialReason={null}
      nextPath="/"
    />
  );

  try {
    await render.input(render.getByLabelText("Username") as HTMLInputElement, "admin");
    await render.input(render.getByLabelText("Password") as HTMLInputElement, "secret");
    const form = render.container.querySelector("form");
    assert.ok(form instanceof render.dom.window.HTMLFormElement);

    await render.submit(form);

    render.getByText("Unable to sign in to the Workable admin UI.");
    assert.deepEqual(getNextNavigationRouterCalls(), {
      refreshCount: 0,
      replaces: [],
    });
  } finally {
    globalThis.fetch = originalFetch;
    resetNextNavigationMock();
    await render.restore();
  }
});
