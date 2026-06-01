import test from "node:test";
import { PanelAggregateFrame } from "@/components/features/console/panel-aggregate-frame";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("panel aggregate frame renders controls, settings trigger, fill, scroll, padding, and content options", () => {
  const markup = renderMarkup(
    <PanelAggregateFrame
      className="aggregate-extra"
      contentClassName="content-extra"
      controls={<button>Refresh</button>}
      fill
      hiddenPanelIds={["logs"]}
      onPanelVisibilityChange={() => undefined}
      onResetUi={() => undefined}
      padding="tightTop"
      panelOptions={[
        { id: "summary", label: "Summary", description: "Overview panel" },
        { id: "logs", label: "Logs", description: "Log panel" },
      ]}
      scrollMode="panel"
      settingsButtonLabel="Worker panel settings"
      settingsDescription="Choose visible worker panels."
      settingsTitle="Worker panels"
    >
      <section>Aggregate content</section>
    </PanelAggregateFrame>
  );

  assertMarkupIncludes(markup, "aggregate-extra");
  assertMarkupIncludes(markup, "content-extra");
  assertMarkupIncludes(markup, "flex min-h-0 flex-1 flex-col");
  assertMarkupIncludes(markup, "overflow-hidden");
  assertMarkupIncludes(markup, "px-4 pb-4 pt-1.5");
  assertMarkupIncludes(markup, "Refresh");
  assertMarkupIncludes(markup, "aria-label=\"Worker panel settings\"");
  assertMarkupIncludes(markup, "Aggregate content");
});
