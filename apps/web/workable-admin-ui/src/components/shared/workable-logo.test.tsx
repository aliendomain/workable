import test from "node:test";
import { WorkableLogo } from "@/components/shared/workable-logo";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";

test("workable logo renders the configured image with default and custom options", () => {
  const defaultLogo = renderMarkup(<WorkableLogo />);
  assertMarkupIncludes(defaultLogo, "alt=\"Workable\"");
  assertMarkupIncludes(defaultLogo, "h-14 w-auto object-contain");
  assertMarkupIncludes(defaultLogo, "workable-logo-transparent.png");

  const priorityLogo = renderMarkup(<WorkableLogo className="custom-logo" priority />);
  assertMarkupIncludes(priorityLogo, "custom-logo");
  assertMarkupIncludes(priorityLogo, "workable-logo-transparent.png");
});
