import assert from "node:assert/strict";
import test from "node:test";
import ErrorPage from "@/app/error";
import { renderDom } from "@/test/dom";

test("global error boundary explains the failure and retries on request", async () => {
  const originalConsoleError = console.error;
  let retryCount = 0;
  console.error = () => undefined;

  const render = await renderDom(
    <ErrorPage
      error={new Error("Render failed")}
      unstable_retry={() => {
        retryCount += 1;
      }}
    />
  );

  try {
    render.getByText("Something went wrong");
    render.getByText("Render failed");

    await render.click(render.getByRole("button", { name: "Retry" }));

    assert.equal(retryCount, 1);
  } finally {
    console.error = originalConsoleError;
    await render.restore();
  }
});
