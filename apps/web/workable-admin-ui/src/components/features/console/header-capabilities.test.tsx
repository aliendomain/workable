import assert from "node:assert/strict";
import test from "node:test";
import { useMemo } from "react";
import {
  ConsoleHeaderCapabilitiesProvider,
  mergeConsoleHeaderCapabilities,
  mergeConsoleHeaderRefreshCapability,
  useRegisterConsoleHeaderCapabilities,
  useResolvedConsoleHeaderCapabilities,
} from "@/components/features/console/header-capabilities";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";
import { renderDom } from "@/test/dom";

test("header capability merge preserves defaults while active capabilities override specified fields", () => {
  const defaultRefresh = {
    ariaLabel: "Refresh all",
    disabled: false,
    hidden: false,
    onRefresh: () => undefined,
    refreshing: false,
    title: "Refresh",
  };
  const activeRefresh = {
    disabled: true,
    refreshing: true,
  };

  assert.equal(mergeConsoleHeaderCapabilities(null, null), null);
  assert.deepEqual(
    mergeConsoleHeaderRefreshCapability(defaultRefresh, activeRefresh),
    {
      ariaLabel: "Refresh all",
      disabled: true,
      hidden: false,
      onRefresh: defaultRefresh.onRefresh,
      refreshing: true,
      title: "Refresh",
    }
  );

  const merged = mergeConsoleHeaderCapabilities(
    {
      realtime: {
        connectionState: "connected",
        enabled: true,
        title: "Default realtime",
      },
      refresh: defaultRefresh,
    },
    {
      realtime: {
        connectionState: "error",
        enabled: false,
        title: "Active realtime",
      },
      refresh: activeRefresh,
    }
  );

  assert.equal(merged?.realtime?.title, "Active realtime");
  assert.equal(merged?.refresh?.disabled, true);
  assert.equal(merged?.refresh?.title, "Refresh");
});

test("header capabilities provider exposes default capabilities to descendants", () => {
  function Probe() {
    const capabilities = useResolvedConsoleHeaderCapabilities();
    return (
      <span>
        {capabilities?.realtime?.title} / {capabilities?.refresh?.title}
      </span>
    );
  }

  const markup = renderMarkup(
    <ConsoleHeaderCapabilitiesProvider
      defaultCapabilities={{
        realtime: {
          connectionState: "connected",
          enabled: true,
          title: "Realtime online",
        },
        refresh: {
          title: "Refresh page",
        },
      }}
    >
      <Probe />
    </ConsoleHeaderCapabilitiesProvider>
  );

  assertMarkupIncludes(markup, "Realtime online / Refresh page");
});

test("header capabilities provider resolves active effect registrations by recency and unregisters inactive views", async () => {
  function Registration({
    active = true,
    id,
    title,
  }: {
    active?: boolean;
    id: string;
    title: string;
  }) {
    const capabilities = useMemo(
      () => ({
        realtime: {
          connectionState: "connected",
          enabled: true,
          title,
        },
        refresh: {
          title: `Refresh ${title}`,
        },
      }),
      [title]
    );

    useRegisterConsoleHeaderCapabilities({
      active,
      capabilities,
      id,
    });
    return null;
  }

  function Probe() {
    const capabilities = useResolvedConsoleHeaderCapabilities();
    return (
      <output data-testid="resolved-capabilities">
        {capabilities?.realtime?.title ?? "none"} / {capabilities?.refresh?.title ?? "none"}
      </output>
    );
  }

  const render = await renderDom(
    <ConsoleHeaderCapabilitiesProvider
      defaultCapabilities={{
        realtime: {
          connectionState: "disconnected",
          enabled: false,
          title: "Default realtime",
        },
        refresh: {
          title: "Refresh default",
        },
      }}
    >
      <Registration id="first" title="First view" />
      <Probe />
    </ConsoleHeaderCapabilitiesProvider>
  );

  try {
    const readResolved = () =>
      render.container.querySelector("[data-testid='resolved-capabilities']")?.textContent;

    assert.equal(readResolved(), "First view / Refresh First view");

    await render.rerender(
      <ConsoleHeaderCapabilitiesProvider
        defaultCapabilities={{
          realtime: {
            connectionState: "disconnected",
            enabled: false,
            title: "Default realtime",
          },
          refresh: {
            title: "Refresh default",
          },
        }}
      >
        <Registration id="first" title="First view" />
        <Registration id="second" title="Second view" />
        <Probe />
      </ConsoleHeaderCapabilitiesProvider>
    );
    assert.equal(readResolved(), "Second view / Refresh Second view");

    await render.rerender(
      <ConsoleHeaderCapabilitiesProvider
        defaultCapabilities={{
          realtime: {
            connectionState: "disconnected",
            enabled: false,
            title: "Default realtime",
          },
          refresh: {
            title: "Refresh default",
          },
        }}
      >
        <Registration id="first" title="First view" />
        <Registration active={false} id="second" title="Second view" />
        <Probe />
      </ConsoleHeaderCapabilitiesProvider>
    );
    assert.equal(readResolved(), "First view / Refresh First view");

    await render.rerender(
      <ConsoleHeaderCapabilitiesProvider
        defaultCapabilities={{
          realtime: {
            connectionState: "disconnected",
            enabled: false,
            title: "Default realtime",
          },
          refresh: {
            title: "Refresh default",
          },
        }}
      >
        <Probe />
      </ConsoleHeaderCapabilitiesProvider>
    );
    assert.equal(readResolved(), "Default realtime / Refresh default");
  } finally {
    await render.restore();
  }
});
