import { JSDOM } from "jsdom";
import { act, createElement, type ReactElement } from "react";
import type { Root } from "react-dom/client";
import { TooltipProvider } from "@/components/ui/tooltip";

type GlobalKey =
  | "cancelAnimationFrame"
  | "CustomEvent"
  | "document"
  | "DOMRect"
  | "Element"
  | "Event"
  | "getComputedStyle"
  | "HTMLElement"
  | "HTMLInputElement"
  | "HTMLTextAreaElement"
  | "KeyboardEvent"
  | "MouseEvent"
  | "MutationObserver"
  | "navigator"
  | "Node"
  | "PointerEvent"
  | "requestAnimationFrame"
  | "window";

const globalKeys: GlobalKey[] = [
  "cancelAnimationFrame",
  "CustomEvent",
  "document",
  "DOMRect",
  "Element",
  "Event",
  "getComputedStyle",
  "HTMLElement",
  "HTMLInputElement",
  "HTMLTextAreaElement",
  "KeyboardEvent",
  "MouseEvent",
  "MutationObserver",
  "navigator",
  "Node",
  "PointerEvent",
  "requestAnimationFrame",
  "window",
];

const mutableGlobal = globalThis as typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
  [key: string]: unknown;
};
const previousActEnvironment = mutableGlobal.IS_REACT_ACT_ENVIRONMENT;

export type DomRenderResult = {
  click: (element: Element) => Promise<void>;
  container: HTMLElement;
  dom: JSDOM;
  getByText: (text: string) => HTMLElement;
  input: (element: HTMLInputElement | HTMLTextAreaElement, value: string) => Promise<void>;
  queryByText: (text: string) => HTMLElement | null;
  rerender: (element: ReactElement) => Promise<void>;
  restore: () => Promise<void>;
  root: Root;
};

export async function renderDom(element: ReactElement): Promise<DomRenderResult> {
  const dom = new JSDOM("<!doctype html><html><body><div id=\"root\"></div></body></html>", {
    pretendToBeVisual: true,
    url: "http://localhost/",
  });
  const previousGlobals = new Map<GlobalKey, unknown>();

  for (const key of globalKeys) {
    previousGlobals.set(key, mutableGlobal[key]);
  }

  installDomGlobals(dom);
  const { createRoot } = await import("react-dom/client");

  const container = dom.window.document.getElementById("root");
  if (!(container instanceof dom.window.HTMLElement)) {
    throw new Error("Test DOM root was not created.");
  }

  const root = createRoot(container);

  await act(async () => {
    root.render(createElement(TooltipProvider, null, element));
  });
  await flushDomEffects();

  const result: DomRenderResult = {
    click: async (target) => {
      await act(async () => {
        target.dispatchEvent(new dom.window.MouseEvent("click", {
          bubbles: true,
          cancelable: true,
        }));
      });
      await flushDomEffects();
    },
    container,
    dom,
    getByText: (text) => {
      const match = findElementByText(dom.window.document.body, text);
      if (!match) {
        throw new Error(`Unable to find text: ${text}`);
      }

      return match;
    },
    input: async (target, value) => {
      await act(async () => {
        const valueSetter = Object.getOwnPropertyDescriptor(
          Object.getPrototypeOf(target),
          "value"
        )?.set;
        valueSetter?.call(target, value);
        target.dispatchEvent(new dom.window.Event("input", {
          bubbles: true,
          cancelable: true,
        }));
        target.dispatchEvent(new dom.window.Event("change", {
          bubbles: true,
          cancelable: true,
        }));
      });
      await flushDomEffects();
    },
    queryByText: (text) => findElementByText(dom.window.document.body, text),
    rerender: async (nextElement) => {
      await act(async () => {
        root.render(createElement(TooltipProvider, null, nextElement));
      });
      await flushDomEffects();
    },
    restore: async () => {
      await act(async () => {
        root.unmount();
      });
      dom.window.close();
      restoreDomGlobals(previousGlobals);
    },
    root,
  };

  return result;
}

async function flushDomEffects() {
  await Promise.resolve();
  await new Promise((resolve) => setTimeout(resolve, 0));
}

function installDomGlobals(dom: JSDOM) {
  const { window } = dom;
  mutableGlobal.IS_REACT_ACT_ENVIRONMENT = true;
  setGlobal("window", window);
  setGlobal("document", window.document);
  setGlobal("navigator", window.navigator);
  setGlobal("Node", window.Node);
  setGlobal("Element", window.Element);
  setGlobal("HTMLElement", window.HTMLElement);
  setGlobal("HTMLInputElement", window.HTMLInputElement);
  setGlobal("HTMLTextAreaElement", window.HTMLTextAreaElement);
  setGlobal("Event", window.Event);
  setGlobal("CustomEvent", window.CustomEvent);
  setGlobal("KeyboardEvent", window.KeyboardEvent);
  setGlobal("MouseEvent", window.MouseEvent);
  setGlobal("PointerEvent", window.PointerEvent ?? window.MouseEvent);
  setGlobal("MutationObserver", window.MutationObserver);
  setGlobal("DOMRect", window.DOMRect);
  setGlobal("getComputedStyle", window.getComputedStyle.bind(window));
  setGlobal("requestAnimationFrame", window.requestAnimationFrame.bind(window));
  setGlobal("cancelAnimationFrame", window.cancelAnimationFrame.bind(window));

  window.HTMLElement.prototype.scrollIntoView ??= () => undefined;
  window.HTMLElement.prototype.releasePointerCapture ??= () => undefined;
  window.HTMLElement.prototype.setPointerCapture ??= () => undefined;
}

function restoreDomGlobals(previousGlobals: Map<GlobalKey, unknown>) {
  for (const [key, value] of previousGlobals) {
    if (value === undefined) {
      delete mutableGlobal[key];
    } else {
      setGlobal(key, value);
    }
  }

  mutableGlobal.IS_REACT_ACT_ENVIRONMENT = previousActEnvironment;
}

function setGlobal(key: GlobalKey, value: unknown) {
  Object.defineProperty(globalThis, key, {
    configurable: true,
    value,
    writable: true,
  });
}

function findElementByText(root: Element, text: string) {
  const elements = [root, ...Array.from(root.querySelectorAll("*"))];
  return elements.find((element) =>
    Array.from(element.childNodes).some((child) =>
      child.nodeType === 3 &&
      child.textContent?.trim() === text
    )
  ) as HTMLElement | undefined ?? null;
}
