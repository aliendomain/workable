import assert from "node:assert/strict";
import { createRef } from "react";
import test from "node:test";
import {
  PanelInfiniteFooter,
  PanelScrollViewport,
  PanelShell,
} from "@/components/features/console/panel-shell";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("panel shell renders title, description, actions, and default body without controls", () => {
  const markup = renderMarkup(
    <PanelShell
      actions={<button>Act</button>}
      description="Panel description"
      title="Workers"
    >
      <div>Panel content</div>
    </PanelShell>
  );

  assertMarkupIncludes(markup, "Workers");
  assertMarkupIncludes(markup, "Panel description");
  assertMarkupIncludes(markup, "Act");
  assertMarkupIncludes(markup, "space-y-4");
  assertMarkupIncludes(markup, "Panel content");
  assertMarkupExcludes(markup, "Panel options");
});

test("panel shell exposes view cycling, menu, close, and synthetic compact states", () => {
  const markup = renderMarkup(
    <PanelShell
      centerActions
      filterControl={{
        activeCount: 2,
        content: <div>Filters</div>,
        label: "Worker filters",
      }}
      hideTitle
      leadingActions={<span>Lead</span>}
      menuLabel="Worker panel options"
      onClose={() => undefined}
      onViewStateChange={() => undefined}
      supportedViewStates={["standard", "detailed"]}
      title="Hidden title"
      viewState="compact"
    >
      <div>Hidden compact content</div>
    </PanelShell>
  );

  assertMarkupIncludes(markup, "grid grid-cols-[minmax(0,1fr)_auto_minmax(0,1fr)]");
  assertMarkupIncludes(markup, "Lead");
  assertMarkupExcludes(markup, "Hidden title");
  assertMarkupIncludes(markup, "aria-label=\"Worker filters, 2 active\"");
  assertMarkupIncludes(markup, ">2<");
  assertMarkupIncludes(markup, "aria-label=\"Next view: Standard\"");
  assertMarkupIncludes(markup, "aria-label=\"Worker panel options\"");
  assertMarkupIncludes(markup, "hidden");
  assertMarkupIncludes(markup, "Hidden compact content");
});

test("panel shell chooses the next supported view state from the active state", () => {
  const markup = renderMarkup(
    <PanelShell
      onViewStateChange={() => undefined}
      supportedViewStates={["compact", "standard", "detailed"]}
      title="Iterations"
      viewState="standard"
    >
      <div>Iteration content</div>
    </PanelShell>
  );

  assertMarkupIncludes(markup, "aria-label=\"Next view: Detailed\"");
  assertMarkupIncludes(markup, "aria-label=\"Panel options\"");
});

test("panel infinite footer covers loading, has-more, count, and hidden pathways", () => {
  const ref = createRef<HTMLDivElement>();
  assertMarkupIncludes(
    renderMarkup(
      <PanelInfiniteFooter
        hasMore={false}
        loadedCount={4}
        loading
        loadingMore={false}
        noun="worker"
        sentinelRef={ref}
      />
    ),
    "Refreshing..."
  );
  assertMarkupIncludes(
    renderMarkup(
      <PanelInfiniteFooter
        hasMore={false}
        loadedCount={4}
        loading={false}
        loadingMore
        noun="worker"
        sentinelRef={ref}
      />
    ),
    "Loading more..."
  );
  assertMarkupIncludes(
    renderMarkup(
      <PanelInfiniteFooter
        hasMore
        loadedCount={4}
        loading={false}
        loadingMore={false}
        noun="worker"
        sentinelRef={ref}
      />
    ),
    "Scroll to load more"
  );
  assertMarkupIncludes(
    renderMarkup(
      <PanelInfiniteFooter
        hasMore={false}
        loadedCount={1}
        loading={false}
        loadingMore={false}
        noun="worker"
        sentinelRef={ref}
      />
    ),
    "Showing 1 worker"
  );
  assertMarkupIncludes(
    renderMarkup(
      <PanelInfiniteFooter
        hasMore={false}
        loadedCount={2}
        loading={false}
        loadingMore={false}
        noun="worker"
        sentinelRef={ref}
      />
    ),
    "Showing 2 workers"
  );

  assert.equal(
    renderMarkup(
      <PanelInfiniteFooter
        hasMore={false}
        loadedCount={2}
        loading={false}
        loadingMore={false}
        noun="worker"
        sentinelRef={ref}
        showLoadedCount={false}
      />
    ),
    ""
  );
});

test("panel scroll viewport applies overscroll modes and optional footer rendering", () => {
  const withFooter = renderMarkup(
    <PanelScrollViewport
      autoLoadMore={false}
      hasMore
      loadedCount={5}
      loading={false}
      loadingMore={false}
      noun="item"
      onLoadMore={() => undefined}
    >
      <div>Rows</div>
    </PanelScrollViewport>
  );
  assertMarkupIncludes(withFooter, "workable-grid-scrollbar");
  assertMarkupIncludes(withFooter, "overscroll-contain");
  assertMarkupIncludes(withFooter, "Rows");
  assertMarkupIncludes(withFooter, "Scroll to load more");

  const withoutFooter = renderMarkup(
    <PanelScrollViewport
      hasMore={false}
      loadedCount={0}
      loading={false}
      loadingMore={false}
      noun="item"
      onLoadMore={() => undefined}
      showLoadedCount={false}
    >
      <div>Rows</div>
    </PanelScrollViewport>
  );
  assertMarkupIncludes(withoutFooter, "overscroll-auto");
  assertMarkupExcludes(withoutFooter, "Showing");
});
