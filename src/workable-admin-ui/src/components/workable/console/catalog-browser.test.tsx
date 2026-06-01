import test from "node:test";
import {
  CatalogBrowser,
  defaultCatalogBrowserBackButtonClassName,
  defaultCatalogBrowserBodyClassName,
  defaultCatalogBrowserHeaderClassName,
  defaultCatalogBrowserTitleClassName,
} from "@/components/workable/console/catalog-browser";
import {
  assertMarkupExcludes,
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

type TestDefinition = {
  id: string;
  name: string;
};

const noDefinitions: TestDefinition[] = [];

test("catalog browser renders loading, root, empty, category, and definition states", () => {
  const loading = renderMarkup(
    <CatalogBrowser
      categories={[]}
      definitions={noDefinitions}
      emptyState={<span>Empty</span>}
      getDefinitionKey={(definition) => definition.id}
      loading
      loadingState={<span>Loading catalog</span>}
      onNavigate={() => undefined}
      path=""
      renderCategory={(category) => <span>{category.label}</span>}
      renderDefinition={(definition) => <span>{definition.name}</span>}
    />
  );
  assertMarkupIncludes(loading, "Catalog root");
  assertMarkupIncludes(loading, "disabled=\"\"");
  assertMarkupIncludes(loading, "All categories");
  assertMarkupIncludes(loading, "Loading catalog");
  assertMarkupExcludes(loading, "Empty");

  const empty = renderMarkup(
    <CatalogBrowser
      categories={[]}
      definitions={noDefinitions}
      emptyState={<span>Nothing found</span>}
      getDefinitionKey={(definition) => definition.id}
      loading={false}
      loadingState={<span>Loading catalog</span>}
      onNavigate={() => undefined}
      path="Billing:Invoices"
      renderCategory={(category) => <span>{category.label}</span>}
      renderDefinition={(definition) => <span>{definition.name}</span>}
      rootLabel="Root"
    />
  );
  assertMarkupIncludes(empty, "Back to parent category");
  assertMarkupIncludes(empty, "Invoices");
  assertMarkupIncludes(empty, "Nothing found");
  assertMarkupExcludes(empty, "disabled=\"\"");

  const populated = renderMarkup(
    <CatalogBrowser
      bodyClassName="body-extra"
      categories={[{ count: 3, label: "Payments", path: "Billing:Payments" }]}
      definitions={[{ id: "send", name: "SendInvoice" }]}
      emptyState={<span>Nothing found</span>}
      getDefinitionKey={(definition) => definition.id}
      headerClassName="header-extra"
      headerRight={<button>New</button>}
      loading={false}
      loadingState={<span>Loading catalog</span>}
      onNavigate={() => undefined}
      path="Billing"
      renderCategory={(category) => <span>Category: {category.label}</span>}
      renderDefinition={(definition) => <span>Definition: {definition.name}</span>}
      titleClassName="title-extra"
      wrapperClassName="wrapper-extra"
    />
  );
  assertMarkupIncludes(populated, "wrapper-extra");
  assertMarkupIncludes(populated, "header-extra");
  assertMarkupIncludes(populated, "title-extra");
  assertMarkupIncludes(populated, "body-extra");
  assertMarkupIncludes(populated, "New");
  assertMarkupIncludes(populated, "Category: Payments");
  assertMarkupIncludes(populated, "Definition: SendInvoice");
  assertMarkupExcludes(populated, "Nothing found");
});

test("catalog browser default class helpers compose custom class names", () => {
  assertMarkupIncludes(defaultCatalogBrowserHeaderClassName("extra-header"), "extra-header");
  assertMarkupIncludes(defaultCatalogBrowserBackButtonClassName("extra-back"), "extra-back");
  assertMarkupIncludes(defaultCatalogBrowserBodyClassName("extra-body"), "extra-body");
  assertMarkupIncludes(defaultCatalogBrowserBodyClassName(), "pb-8");
  assertMarkupIncludes(defaultCatalogBrowserTitleClassName("extra-title"), "extra-title");
});
