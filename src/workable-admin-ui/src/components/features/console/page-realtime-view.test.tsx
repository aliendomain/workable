import assert from "node:assert/strict";
import test from "node:test";
import { useMemo } from "react";
import {
  ConsolePageRealtimeViewProvider,
  createDisabledConsolePageRealtimeView,
  useConsolePageRealtimeView,
  useRegisterConsolePageRealtimeView,
  useResolvedConsolePageRealtimeViewDescriptorId,
} from "@/components/features/console/page-realtime-view";
import { renderDom } from "@/test/dom";

test("disabled page realtime view exposes the inert loadable shape used for inactive views", () => {
  const view = createDisabledConsolePageRealtimeView<{ count: number }>();

  assert.equal(view.connectionState, "disabled");
  assert.equal(view.enabled, false);
  assert.equal(view.hubUrl, null);
  assert.equal(view.loading, false);
  assert.deepEqual(view.messages, []);
  assert.equal(view.clearMessages(), undefined);
});

test("page realtime provider resolves active registrations by recency and disables inactive consumers", async () => {
  function Registration({
    active = true,
    id,
    viewName,
  }: {
    active?: boolean;
    id: string;
    viewName: string;
  }) {
    const descriptor = useMemo(
      () => ({
        body: { viewName },
        captureEnabled: false,
        connection: null,
        enabled: false,
        maxMessages: 25,
        viewName,
      }),
      [viewName]
    );

    useRegisterConsolePageRealtimeView({
      active,
      descriptor,
      id,
    });
    return null;
  }

  function Probe({ id }: { id: string }) {
    const descriptorId = useResolvedConsolePageRealtimeViewDescriptorId();
    const view = useConsolePageRealtimeView(id);
    return (
      <output data-testid={id}>
        {descriptorId ?? "none"} / {view.enabled ? "enabled" : "disabled"} / {view.connectionState}
      </output>
    );
  }

  const render = await renderDom(
    <ConsolePageRealtimeViewProvider>
      <Registration id="first" viewName="First" />
      <Probe id="first" />
      <Probe id="second" />
    </ConsolePageRealtimeViewProvider>
  );

  try {
    const read = (id: string) =>
      render.container.querySelector(`[data-testid='${id}']`)?.textContent;

    assert.equal(read("first"), "first / disabled / disabled");
    assert.equal(read("second"), "first / disabled / disabled");

    await render.rerender(
      <ConsolePageRealtimeViewProvider>
        <Registration id="first" viewName="First" />
        <Registration id="second" viewName="Second" />
        <Probe id="first" />
        <Probe id="second" />
      </ConsolePageRealtimeViewProvider>
    );
    assert.equal(read("first"), "second / disabled / disabled");
    assert.equal(read("second"), "second / disabled / disabled");

    await render.rerender(
      <ConsolePageRealtimeViewProvider>
        <Registration id="first" viewName="First" />
        <Registration active={false} id="second" viewName="Second" />
        <Probe id="first" />
        <Probe id="second" />
      </ConsolePageRealtimeViewProvider>
    );
    assert.equal(read("first"), "first / disabled / disabled");
    assert.equal(read("second"), "first / disabled / disabled");

    await render.rerender(
      <ConsolePageRealtimeViewProvider>
        <Probe id="first" />
      </ConsolePageRealtimeViewProvider>
    );
    assert.equal(read("first"), "none / disabled / disabled");
  } finally {
    await render.restore();
  }
});
