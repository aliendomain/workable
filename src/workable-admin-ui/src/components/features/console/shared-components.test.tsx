import assert from "node:assert/strict";
import test from "node:test";
import { RefreshCw } from "lucide-react";
import {
  ConsoleEmptyState,
  ConsolePlaceholder,
} from "@/components/features/console/empty-state";
import {
  FormEmptyState,
  FormField,
  FormFieldHeader,
  ReadonlyFormValue,
} from "@/components/features/console/form-controls";
import { StackedSkeleton } from "@/components/features/console/stacked-skeleton";
import { ToolbarIconButton } from "@/components/features/console/toolbar-icon-button";
import {
  RealtimeCollapsedRail,
  RealtimeMessageLimitField,
  RealtimePanelFrame,
  RealtimePanelHeader,
  RealtimeToolbar,
  RealtimeToolbarSearchInput,
  RealtimeToolbarSurface,
  normalizeRealtimeMessageLimit,
} from "@/components/features/console/realtime-message-controls";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  countMarkupOccurrences,
  renderMarkup,
} from "@/test/render";
import { renderDom } from "@/test/dom";

test("console empty and placeholder components apply padding, fill, and custom classes", () => {
  const compact = renderMarkup(
    <ConsoleEmptyState className="custom-empty" fill padding="compact">
      Nothing here
    </ConsoleEmptyState>
  );
  assertMarkupIncludes(compact, "rounded-lg border border-dashed");
  assertMarkupIncludes(compact, "flex min-h-0 flex-1 items-center justify-center");
  assertMarkupIncludes(compact, "p-4");
  assertMarkupIncludes(compact, "custom-empty");
  assertMarkupIncludes(compact, "Nothing here");

  const spacious = renderMarkup(
    <ConsoleEmptyState padding="spacious">A wider message</ConsoleEmptyState>
  );
  assertMarkupIncludes(spacious, "p-8");

  const placeholder = renderMarkup(
    <ConsolePlaceholder className="custom-placeholder" fill />
  );
  assertMarkupIncludes(placeholder, "rounded-lg border border-dashed");
  assertMarkupIncludes(placeholder, "flex min-h-0 flex-1");
  assertMarkupIncludes(placeholder, "custom-placeholder");
});

test("form field components cover label, tooltip, sizing, and readonly pathways", () => {
  const defaultField = renderMarkup(
    <FormField htmlFor="user" label="User name">
      <input id="user" />
    </FormField>
  );
  assertMarkupIncludes(defaultField, "grid gap-2");
  assertMarkupIncludes(defaultField, "w-full max-w-md");
  assertMarkupIncludes(defaultField, "for=\"user\"");
  assertMarkupIncludes(defaultField, "User name");

  const unboundedField = renderMarkup(
    <FormField className="wide-field" label="Host" maxWidth="none">
      <input />
    </FormField>
  );
  assertMarkupIncludes(unboundedField, "wide-field");
  assertMarkupExcludes(unboundedField, "max-w-md");

  const unlabeledField = renderMarkup(
    <FormField>
      <input aria-label="Raw value" />
    </FormField>
  );
  assertMarkupIncludes(unlabeledField, "aria-label=\"Raw value\"");
  assertMarkupExcludes(unlabeledField, "<label");

  const tooltipHeader = renderMarkup(
    <FormFieldHeader
      description="Shown in tooltip"
      details={<span>Extra details</span>}
      label="Timeout"
      labelClassName="important-label"
    />
  );
  assertMarkupIncludes(tooltipHeader, "aria-label=\"Timeout field details\"");
  assertMarkupIncludes(tooltipHeader, "important-label");
  assertMarkupIncludes(tooltipHeader, "Timeout");

  const fallbackTooltipLabel = renderMarkup(
    <FormFieldHeader description="Details" label={<span>Complex label</span>} />
  );
  assertMarkupIncludes(fallbackTooltipLabel, "aria-label=\"Form field details\"");
  assertMarkupIncludes(fallbackTooltipLabel, "Complex label");

  const emptyState = renderMarkup(
    <FormEmptyState className="form-empty" padding="compact">
      No schema
    </FormEmptyState>
  );
  assertMarkupIncludes(emptyState, "rounded-lg border border-dashed");
  assertMarkupIncludes(emptyState, "p-4");
  assertMarkupIncludes(emptyState, "form-empty");

  const readonlyValue = renderMarkup(
    <ReadonlyFormValue className="readonly-extra">42</ReadonlyFormValue>
  );
  assertMarkupIncludes(readonlyValue, "bg-muted/30");
  assertMarkupIncludes(readonlyValue, "font-mono");
  assertMarkupIncludes(readonlyValue, "readonly-extra");
});

test("stacked skeleton renders the requested number of stable skeleton rows", () => {
  const markup = renderMarkup(
    <StackedSkeleton className="outer-stack" count={3} itemClassName="custom-row" />
  );

  assertMarkupIncludes(markup, "space-y-3");
  assertMarkupIncludes(markup, "outer-stack");
  assert.equal(countMarkupOccurrences(markup, "custom-row"), 3);
  assert.equal(countMarkupOccurrences(markup, "h-10 w-full"), 3);
});

test("toolbar icon button exposes accessible label and button options", () => {
  const markup = renderMarkup(
    <ToolbarIconButton
      className="extra-button"
      disabled
      label="Refresh data"
      side="bottom"
      tooltip="Reload the current panel"
    >
      <RefreshCw className="size-4" />
    </ToolbarIconButton>
  );

  assertMarkupIncludes(markup, "aria-label=\"Refresh data\"");
  assertMarkupIncludes(markup, "extra-button");
  assertMarkupIncludes(markup, "disabled=\"\"");
  assertMarkupIncludes(markup, "size-4");
});

test("realtime message controls centralize panel, toolbar, rail, search, and limit markup", () => {
  assert.equal(normalizeRealtimeMessageLimit("bad"), 100);
  assert.equal(normalizeRealtimeMessageLimit("0"), 1);
  assert.equal(normalizeRealtimeMessageLimit("2500"), 1000);
  assert.equal(normalizeRealtimeMessageLimit("75"), 75);

  const markup = renderMarkup(
    <RealtimePanelFrame className="custom-panel">
      <RealtimePanelHeader>
        <RealtimeToolbar>
          <RealtimeToolbarSearchInput
            onChange={() => undefined}
            placeholder="Filter events"
            value="worker"
          />
          <RealtimeMessageLimitField onChange={() => undefined} value={250} />
        </RealtimeToolbar>
      </RealtimePanelHeader>
      <RealtimeCollapsedRail>12 events</RealtimeCollapsedRail>
    </RealtimePanelFrame>
  );

  assertMarkupIncludes(markup, "custom-panel");
  assertMarkupIncludes(markup, "grid min-h-0 overflow-hidden rounded-md border");
  assertMarkupIncludes(markup, "grid gap-2 border-b bg-muted/30 px-2 py-2");
  assertMarkupIncludes(markup, "flex min-w-0 flex-wrap items-center gap-1");
  assertMarkupIncludes(markup, "placeholder=\"Filter events\"");
  assertMarkupIncludes(markup, "value=\"worker\"");
  assertMarkupIncludes(markup, "Max");
  assertMarkupIncludes(markup, "value=\"250\"");
  assertMarkupIncludes(markup, "[writing-mode:vertical-rl]");

  const compactHeader = renderMarkup(
    <RealtimePanelHeader variant="compact-title">
      <span>Inspector</span>
    </RealtimePanelHeader>
  );
  assertMarkupIncludes(compactHeader, "px-2 py-1.5");

  const customRows = renderMarkup(
    <RealtimePanelFrame defaultRows={false}>
      <span>No default rows</span>
    </RealtimePanelFrame>
  );
  assertMarkupExcludes(customRows, "grid-rows-[auto_minmax(0,1fr)]");

  const toolbarSurface = renderMarkup(
    <RealtimeToolbarSurface className="custom-toolbar">
      <span>Controls</span>
    </RealtimeToolbarSurface>
  );
  assertMarkupIncludes(toolbarSurface, "rounded-md border bg-muted/30");
  assertMarkupIncludes(toolbarSurface, "custom-toolbar");
});

test("realtime message controls dispatch search text and normalized limits in the DOM", async () => {
  let searchText = "";
  let limit = 0;
  const render = await renderDom(
    <>
      <RealtimeToolbarSearchInput
        onChange={(nextSearchText) => {
          searchText = nextSearchText;
        }}
        placeholder="Filter payloads"
        value={searchText}
      />
      <RealtimeMessageLimitField
        onChange={(nextLimit) => {
          limit = nextLimit;
        }}
        value={100}
      />
    </>
  );

  try {
    const searchInput = render.container.querySelector("input[placeholder='Filter payloads']");
    const limitInput = render.container.querySelector("input[type='number']");

    assert.ok(searchInput instanceof render.dom.window.HTMLInputElement);
    assert.ok(limitInput instanceof render.dom.window.HTMLInputElement);

    await render.input(searchInput, "worker.failed");
    await render.input(limitInput, "5000");

    assert.equal(searchText, "worker.failed");
    assert.equal(limit, 1000);
  } finally {
    await render.restore();
  }
});
