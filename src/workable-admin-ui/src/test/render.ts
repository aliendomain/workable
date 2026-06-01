import assert from "node:assert/strict";
import { createElement } from "react";
import type { ReactElement } from "react";
import { renderToStaticMarkup } from "react-dom/server";
import { TooltipProvider } from "@/components/ui/tooltip";

export function renderMarkup(element: ReactElement) {
  return renderToStaticMarkup(createElement(TooltipProvider, null, element));
}

export function assertMarkupIncludes(markup: string, expected: string) {
  assert.ok(
    markup.includes(expected),
    `Expected markup to include ${JSON.stringify(expected)}.\nMarkup:\n${markup}`
  );
}

export function assertMarkupExcludes(markup: string, unexpected: string) {
  assert.ok(
    !markup.includes(unexpected),
    `Expected markup not to include ${JSON.stringify(unexpected)}.\nMarkup:\n${markup}`
  );
}

export function countMarkupOccurrences(markup: string, expected: string) {
  return markup.split(expected).length - 1;
}
