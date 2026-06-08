import test from "node:test";
import {
  ConsolePageLayout,
  ConsolePanelBody,
  ConsolePanelDescription,
  ConsolePanelHeader,
  ConsolePanelSurface,
  ConsolePanelTitle,
  ConsoleViewFrame,
  ConsoleViewMount,
  ConsoleViewport,
  ConsoleViewportContent,
  ViewActionFrame,
  ViewActionLane,
} from "@/components/features/console/console-primitives";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("console layout primitives expose toolbar, fill, scroll, and padding options", () => {
  const page = renderMarkup(
    <ConsolePageLayout
      className="page-extra"
      fill
      reserveToolbar
      scrollMode="panel"
      toolbar={<button>Refresh</button>}
    >
      <main>Body</main>
    </ConsolePageLayout>
  );
  assertMarkupIncludes(page, "flex min-h-0 flex-col gap-6");
  assertMarkupIncludes(page, "flex-1");
  assertMarkupIncludes(page, "overflow-hidden");
  assertMarkupIncludes(page, "page-extra");
  assertMarkupIncludes(page, "Refresh");
  assertMarkupIncludes(page, "Body");

  const reserved = renderMarkup(
    <ConsolePageLayout reserveToolbar>
      <main>Reserved only</main>
    </ConsolePageLayout>
  );
  assertMarkupIncludes(reserved, "aria-hidden=\"true\"");

  const frame = renderMarkup(
    <ConsoleViewFrame className="frame-extra" id="frame" padding="tightTop">
      Content
    </ConsoleViewFrame>
  );
  assertMarkupIncludes(frame, "rounded-2xl border");
  assertMarkupIncludes(frame, "px-4 pb-4 pt-1.5");
  assertMarkupIncludes(frame, "frame-extra");
  assertMarkupIncludes(frame, "id=\"frame\"");
});

test("console mount and viewport primitives switch visibility and overflow by options", () => {
  const hiddenMount = renderMarkup(
    <ConsoleViewMount active={false} fill scrollMode="panel">
      Hidden panel
    </ConsoleViewMount>
  );
  assertMarkupIncludes(hiddenMount, "hidden");
  assertMarkupIncludes(hiddenMount, "flex-1");
  assertMarkupIncludes(hiddenMount, "overflow-hidden");

  const activeMount = renderMarkup(
    <ConsoleViewMount>
      Active panel
    </ConsoleViewMount>
  );
  assertMarkupIncludes(activeMount, "flex min-h-0 flex-col");
  assertMarkupExcludes(activeMount, "hidden");

  const viewport = renderMarkup(
    <ConsoleViewport className="viewport-extra" scrollMode="panel">
      Viewport
    </ConsoleViewport>
  );
  assertMarkupIncludes(viewport, "flex min-h-0 flex-1 flex-col");
  assertMarkupIncludes(viewport, "overflow-hidden");
  assertMarkupIncludes(viewport, "viewport-extra");

  const content = renderMarkup(
    <ConsoleViewportContent scrollMode="browser">
      Content
    </ConsoleViewportContent>
  );
  assertMarkupIncludes(content, "overflow-visible");
});

test("panel surface primitives preserve semantic wrappers and class extension points", () => {
  const markup = renderMarkup(
    <ConsolePanelSurface className="surface-extra">
      <ConsolePanelHeader className="header-extra">
        <ConsolePanelTitle className="title-extra">Panel title</ConsolePanelTitle>
      </ConsolePanelHeader>
      <ConsolePanelDescription className="description-extra">
        Panel description
      </ConsolePanelDescription>
      <ConsolePanelBody className="body-extra">
        Panel body
      </ConsolePanelBody>
    </ConsolePanelSurface>
  );

  assertMarkupIncludes(markup, "<section");
  assertMarkupIncludes(markup, "rounded-xl bg-card");
  assertMarkupIncludes(markup, "surface-extra");
  assertMarkupIncludes(markup, "header-extra");
  assertMarkupIncludes(markup, "truncate font-semibold");
  assertMarkupIncludes(markup, "title-extra");
  assertMarkupIncludes(markup, "description-extra");
  assertMarkupIncludes(markup, "body-extra");
});

test("view action primitives handle empty and populated toolbar slots", () => {
  const emptyLane = renderMarkup(<ViewActionLane />);
  assertMarkupIncludes(emptyLane, "aria-hidden=\"true\"");

  const populatedLane = renderMarkup(
    <ViewActionLane>
      <ViewActionFrame className="frame-extra">
        Action
      </ViewActionFrame>
    </ViewActionLane>
  );
  assertMarkupExcludes(populatedLane, "aria-hidden=\"true\"");
  assertMarkupIncludes(populatedLane, "inline-flex min-h-9");
  assertMarkupIncludes(populatedLane, "frame-extra");
  assertMarkupIncludes(populatedLane, "Action");
});
