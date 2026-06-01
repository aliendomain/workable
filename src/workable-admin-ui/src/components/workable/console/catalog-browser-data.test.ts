import assert from "node:assert/strict";
import test from "node:test";
import { createDefinitionCatalogLevelPath } from "@/components/workable/console/catalog-browser-data";

test("definition catalog level paths include normalized category only when present", () => {
  assert.equal(createDefinitionCatalogLevelPath(""), "definitions?level=true");
  assert.equal(
    createDefinitionCatalogLevelPath(" Billing : Invoices "),
    "definitions?level=true&category=Billing%3AInvoices"
  );
});
