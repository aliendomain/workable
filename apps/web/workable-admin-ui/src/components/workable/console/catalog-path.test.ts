import assert from "node:assert/strict";
import test from "node:test";
import {
  createOverviewComponentScope,
  createQueryCatalogScope,
  formatOverviewScopeLabel,
  normalizeCategoryFilter,
  normalizeOverviewScope,
  overviewScopesEqual,
  splitCatalogPath,
} from "./catalog-path.ts";

test("catalog paths are trimmed, normalized, and displayed consistently", () => {
  assert.deepEqual(splitCatalogPath(" Billing : Invoices : "), ["Billing", "Invoices"]);
  assert.deepEqual(splitCatalogPath(null), []);
  assert.equal(normalizeCategoryFilter(" Billing :: Invoices "), "Billing:Invoices");
  assert.equal(
    formatOverviewScopeLabel({
      category: "Billing:Invoices",
      definitionName: "SendInvoice",
      includeSubcategories: true,
    }),
    "Billing / Invoices / SendInvoice"
  );
});

test("overview scopes drop empty filters and preserve category subcategory behavior", () => {
  assert.equal(normalizeOverviewScope(null), null);
  assert.equal(normalizeOverviewScope({ category: " ", definitionName: " " }), null);
  assert.deepEqual(normalizeOverviewScope({ category: "Ops", includeSubcategories: undefined }), {
    category: "Ops",
    definitionName: undefined,
    includeSubcategories: true,
  });
  assert.deepEqual(normalizeOverviewScope({ category: "Ops", definitionName: "Import" }), {
    category: "Ops",
    definitionName: "Import",
    includeSubcategories: undefined,
  });
});

test("component overview scopes can match overview and query request shapes", () => {
  assert.equal(createOverviewComponentScope(null), null);
  assert.equal(createOverviewComponentScope(null, { emptyValue: undefined }), undefined);
  assert.deepEqual(createOverviewComponentScope({ category: "Ops", includeSubcategories: false }), {
    category: "Ops",
    definitionName: undefined,
    includeSubcategories: false,
  });
  assert.deepEqual(
    createOverviewComponentScope(
      { category: "Ops", definitionName: "Import", includeSubcategories: true },
      { emptyValue: undefined, includeSubcategoriesForDefinition: true }
    ),
    {
      category: "Ops",
      definitionName: "Import",
      includeSubcategories: true,
    }
  );
});

test("query catalog scopes and scope equality normalize equivalent input", () => {
  assert.equal(createQueryCatalogScope(" ", " "), null);
  assert.deepEqual(createQueryCatalogScope(" Ops : Import ", ""), {
    category: "Ops:Import",
    definitionName: undefined,
    includeSubcategories: true,
  });
  assert.equal(
    overviewScopesEqual(
      { category: "Ops:Import", includeSubcategories: true },
      { category: " Ops : Import " }
    ),
    true
  );
});
