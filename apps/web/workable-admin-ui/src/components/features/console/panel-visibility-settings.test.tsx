import assert from "node:assert/strict";
import test from "node:test";
import {
  PanelVisibilitySettings,
  isPanelVisibleInSettings,
} from "@/components/features/console/panel-visibility-settings";
import { renderDom } from "@/test/dom";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("panel visibility settings helper reports visible and hidden option state", () => {
  assert.equal(isPanelVisibleInSettings(["logs"], "summary"), true);
  assert.equal(isPanelVisibleInSettings(["logs"], "logs"), false);
});

test("panel visibility settings renders default and custom trigger labels", () => {
  assertMarkupIncludes(
    renderMarkup(
      <PanelVisibilitySettings
        hiddenPanelIds={[]}
        onPanelVisibilityChange={() => undefined}
        panelOptions={[{ id: "summary", label: "Summary", description: "Summary panel" }]}
      />
    ),
    "aria-label=\"Panel settings\""
  );

  assertMarkupIncludes(
    renderMarkup(
      <PanelVisibilitySettings
        buttonLabel="Worker panel settings"
        description="Choose worker panels."
        hiddenPanelIds={["logs"]}
        onPanelVisibilityChange={() => undefined}
        onResetUi={() => undefined}
        panelOptions={[
          { id: "summary", label: "Summary", description: "Summary panel" },
          { id: "logs", label: "Logs", description: "Log panel" },
        ]}
        resetLabel="Restore worker panels"
        title="Worker panels"
      />
    ),
    "aria-label=\"Worker panel settings\""
  );
});

test("panel visibility settings reset button invokes reset without forwarding the click event", async () => {
  const resetCalls: unknown[] = [];
  const result = await renderDom(
    <PanelVisibilitySettings
      buttonLabel="Worker panel settings"
      hiddenPanelIds={["logs"]}
      onPanelVisibilityChange={() => undefined}
      onResetUi={(value?: unknown) => {
        resetCalls.push(value);
      }}
      panelOptions={[
        { id: "summary", label: "Summary", description: "Summary panel" },
        { id: "logs", label: "Logs", description: "Log panel" },
      ]}
    />
  );

  try {
    await result.click(result.getByRole("button", { name: "Worker panel settings" }));
    await result.waitFor(() => result.getByRole("button", { name: "Reset UI to defaults" }));
    await result.click(result.getByRole("button", { name: "Reset UI to defaults" }));
    assert.deepEqual(resetCalls, [undefined]);
  } finally {
    await result.restore();
  }
});
