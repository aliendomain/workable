import assert from "node:assert/strict";
import test from "node:test";
import { Circle } from "lucide-react";
import { ConsoleHeaderCapabilityControls } from "@/components/features/console/header-capability-controls";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";
import { renderDom } from "@/test/dom";

test("header capability controls hide when no realtime or refresh action is available", () => {
  assert.equal(renderMarkup(<ConsoleHeaderCapabilityControls capabilities={null} />), "");
  assert.equal(
    renderMarkup(
      <ConsoleHeaderCapabilityControls
        capabilities={{
          refresh: {
            hidden: true,
            onRefresh: () => undefined,
            title: "Refresh",
          },
        }}
      />
    ),
    ""
  );
});

test("header refresh control supports aria label, disabled state, and refreshing spin", () => {
  const markup = renderMarkup(
    <ConsoleHeaderCapabilityControls
      capabilities={{
        refresh: {
          ariaLabel: "Refresh everything",
          disabled: true,
          onRefresh: () => undefined,
          refreshing: true,
          title: "Refresh data",
        },
      }}
    />
  );

  assertMarkupIncludes(markup, "aria-label=\"Refresh everything\"");
  assertMarkupIncludes(markup, "disabled=\"\"");
  assertMarkupIncludes(markup, "animate-spin");
});

test("header realtime control renders status-only, single-action, and menu-trigger variants", () => {
  const statusOnly = renderMarkup(
    <ConsoleHeaderCapabilityControls
      capabilities={{
        realtime: {
          connectionState: "disconnected",
          enabled: true,
          title: "Realtime disconnected",
        },
      }}
    />
  );
  assertMarkupIncludes(statusOnly, "role=\"img\"");
  assertMarkupIncludes(statusOnly, "aria-label=\"Realtime disconnected\"");
  assertMarkupIncludes(statusOnly, "text-[var(--status-danger-text)]");
  assertMarkupIncludes(statusOnly, ">!<");

  const singleAction = renderMarkup(
    <ConsoleHeaderCapabilityControls
      capabilities={{
        realtime: {
          connectionState: "connected",
          enabled: true,
          menuItems: [{
            active: true,
            id: "payloads",
            label: "Payloads",
            onSelect: () => undefined,
          }],
        },
      }}
    />
  );
  assertMarkupIncludes(singleAction, "aria-label=\"Realtime enabled. Hide payloads\"");
  assertMarkupIncludes(singleAction, "text-foreground");

  const menuTrigger = renderMarkup(
    <ConsoleHeaderCapabilityControls
      capabilities={{
        realtime: {
          connectionState: "connecting",
          enabled: true,
          menuItems: [
            {
              icon: <Circle className="size-3" />,
              id: "payloads",
              label: "Payloads",
              onSelect: () => undefined,
            },
            {
              disabled: true,
              id: "events",
              label: "Events",
              onSelect: () => undefined,
            },
          ],
        },
        refresh: {
          onRefresh: () => undefined,
          refreshing: true,
          title: "Refresh data",
        },
      }}
    />
  );
  assertMarkupIncludes(menuTrigger, "aria-label=\"Realtime connecting\"");
  assertMarkupIncludes(menuTrigger, "text-[var(--status-info-text)]");
  assertMarkupIncludes(menuTrigger, "aria-label=\"Refresh data\"");
  assertMarkupExcludes(menuTrigger, "animate-spin");
});

test("header controls invoke refresh and single realtime action clicks in the DOM", async () => {
  let refreshCount = 0;
  let realtimeSelectCount = 0;
  const render = await renderDom(
    <ConsoleHeaderCapabilityControls
      capabilities={{
        realtime: {
          connectionState: "connected",
          enabled: true,
          menuItems: [{
            active: false,
            id: "payloads",
            label: "Payloads",
            onSelect: () => {
              realtimeSelectCount += 1;
            },
          }],
        },
        refresh: {
          ariaLabel: "Refresh everything",
          onRefresh: () => {
            refreshCount += 1;
          },
          title: "Refresh data",
        },
      }}
    />
  );

  try {
    const realtimeButton = render.container.querySelector(
      "button[aria-label='Realtime enabled. Show payloads']"
    );
    const refreshButton = render.container.querySelector(
      "button[aria-label='Refresh everything']"
    );

    assert.ok(realtimeButton);
    assert.ok(refreshButton);
    await render.click(realtimeButton);
    await render.click(refreshButton);

    assert.equal(realtimeSelectCount, 1);
    assert.equal(refreshCount, 1);
  } finally {
    await render.restore();
  }
});
