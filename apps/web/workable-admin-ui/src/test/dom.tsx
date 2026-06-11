import { JSDOM } from "jsdom";
import { act, createElement, type ReactElement } from "react";
import type { Root } from "react-dom/client";
import { TooltipProvider } from "@/components/ui/tooltip";

type GlobalKey =
  | "cancelAnimationFrame"
  | "CustomEvent"
  | "document"
  | "DOMRect"
  | "DocumentFragment"
  | "Element"
  | "Event"
  | "getComputedStyle"
  | "HTMLElement"
  | "HTMLInputElement"
  | "HTMLTextAreaElement"
  | "IntersectionObserver"
  | "KeyboardEvent"
  | "MouseEvent"
  | "MutationObserver"
  | "navigator"
  | "Node"
  | "NodeFilter"
  | "PointerEvent"
  | "ResizeObserver"
  | "requestAnimationFrame"
  | "window";

const globalKeys: GlobalKey[] = [
  "cancelAnimationFrame",
  "CustomEvent",
  "document",
  "DOMRect",
  "DocumentFragment",
  "Element",
  "Event",
  "getComputedStyle",
  "HTMLElement",
  "HTMLInputElement",
  "HTMLTextAreaElement",
  "IntersectionObserver",
  "KeyboardEvent",
  "MouseEvent",
  "MutationObserver",
  "navigator",
  "Node",
  "NodeFilter",
  "PointerEvent",
  "ResizeObserver",
  "requestAnimationFrame",
  "window",
];

const mutableGlobal = globalThis as typeof globalThis & {
  IS_REACT_ACT_ENVIRONMENT?: boolean;
  [key: string]: unknown;
};
const previousActEnvironment = mutableGlobal.IS_REACT_ACT_ENVIRONMENT;

class TestResizeObserver {
  private readonly callback?: ResizeObserverCallback;

  constructor(callback?: ResizeObserverCallback) {
    this.callback = callback;
  }

  disconnect() {}

  observe(target: Element) {
    const rect = target.getBoundingClientRect();
    const boxSize = {
      blockSize: rect.height,
      inlineSize: rect.width,
    };
    this.callback?.([
      {
        borderBoxSize: [boxSize],
        contentBoxSize: [boxSize],
        contentRect: rect,
        devicePixelContentBoxSize: [boxSize],
        target,
      } as ResizeObserverEntry,
    ], this as unknown as ResizeObserver);
  }

  unobserve() {}
}

class TestIntersectionObserver {
  disconnect() {}
  observe() {}
  takeRecords(): IntersectionObserverEntry[] {
    return [];
  }
  unobserve() {}
}

export type DomRenderResult = {
  click: (element: Element) => Promise<void>;
  container: HTMLElement;
  dom: JSDOM;
  focus: (element: Element) => Promise<void>;
  getByText: (text: string) => HTMLElement;
  getByLabelText: (text: string | RegExp) => HTMLElement;
  getByRole: (role: string, options?: { name?: string | RegExp }) => HTMLElement;
  input: (element: HTMLInputElement | HTMLTextAreaElement, value: string) => Promise<void>;
  mouseDown: (element: Element) => Promise<void>;
  mouseUp: (element: Element) => Promise<void>;
  pointerDown: (element: Element) => Promise<void>;
  pointerUp: (element: Element) => Promise<void>;
  queryByText: (text: string) => HTMLElement | null;
  rerender: (element: ReactElement) => Promise<void>;
  restore: () => Promise<void>;
  root: Root;
  scroll: (
    element: HTMLElement,
    options?: { clientHeight?: number; scrollHeight?: number; scrollTop?: number }
  ) => Promise<void>;
  submit: (element: HTMLFormElement) => Promise<void>;
  waitFor: (assertion: () => void, options?: { timeoutMs?: number }) => Promise<void>;
};

export type DomRenderOptions = {
  setupWindow?: (window: JSDOM["window"]) => void;
};

export async function renderDom(
  element: ReactElement,
  options?: DomRenderOptions
): Promise<DomRenderResult> {
  const dom = new JSDOM("<!doctype html><html><body><div id=\"root\"></div></body></html>", {
    pretendToBeVisual: true,
    url: "http://localhost/",
  });
  const previousGlobals = new Map<GlobalKey, unknown>();

  for (const key of globalKeys) {
    previousGlobals.set(key, mutableGlobal[key]);
  }

  installDomGlobals(dom);
  options?.setupWindow?.(dom.window);
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
    focus: async (target) => {
      await act(async () => {
        if (target instanceof dom.window.HTMLElement) {
          target.focus();
          return;
        }

        target.dispatchEvent(new dom.window.Event("focus", {
          bubbles: false,
          cancelable: false,
        }));
      });
      await flushDomEffects();
    },
    getByText: (text) => {
      const match = findElementByText(dom.window.document.body, text);
      if (!match) {
        throw new Error(`Unable to find text: ${text}`);
      }

      return match;
    },
    getByLabelText: (text) => {
      const match = findElementByLabelText(dom.window.document.body, text);
      if (!match) {
        throw new Error(`Unable to find form control with label: ${String(text)}`);
      }

      return match;
    },
    getByRole: (role, options) => {
      const match = findElementByRole(dom.window.document.body, role, options?.name);
      if (!match) {
        const name = options?.name === undefined ? "" : ` and name: ${String(options.name)}`;
        throw new Error(`Unable to find role: ${role}${name}`);
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
    mouseDown: async (target) => {
      await act(async () => {
        target.dispatchEvent(new dom.window.MouseEvent("mousedown", {
          bubbles: true,
          button: 0,
          cancelable: true,
        }));
      });
      await flushDomEffects();
    },
    mouseUp: async (target) => {
      await act(async () => {
        target.dispatchEvent(new dom.window.MouseEvent("mouseup", {
          bubbles: true,
          button: 0,
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
      await flushDomEffects();
      dom.window.close();
      restoreDomGlobals(previousGlobals);
    },
    root,
    scroll: async (target, scrollOptions) => {
      if (scrollOptions?.clientHeight !== undefined) {
        defineNumericElementProperty(target, "clientHeight", scrollOptions.clientHeight);
      }
      if (scrollOptions?.scrollHeight !== undefined) {
        defineNumericElementProperty(target, "scrollHeight", scrollOptions.scrollHeight);
      }
      if (scrollOptions?.scrollTop !== undefined) {
        target.scrollTop = scrollOptions.scrollTop;
      }

      await act(async () => {
        target.dispatchEvent(new dom.window.Event("scroll", {
          bubbles: true,
          cancelable: true,
        }));
      });
      await flushDomEffects();
    },
    submit: async (target) => {
      await act(async () => {
        target.dispatchEvent(new dom.window.Event("submit", {
          bubbles: true,
          cancelable: true,
        }));
      });
      await flushDomEffects();
    },
    waitFor: async (assertion, waitOptions) => {
      const timeoutMs = waitOptions?.timeoutMs ?? 1500;
      const start = Date.now();
      let lastError: unknown;

      while (Date.now() - start < timeoutMs) {
        try {
          assertion();
          return;
        } catch (error) {
          lastError = error;
        }

        await act(async () => {
          await new Promise((resolve) => setTimeout(resolve, 10));
        });
        await flushDomEffects();
      }

      throw lastError;
    },
    pointerDown: async (target) => {
      await act(async () => {
        target.dispatchEvent(new dom.window.PointerEvent("pointerdown", {
          bubbles: true,
          button: 0,
          cancelable: true,
        }));
      });
      await flushDomEffects();
    },
    pointerUp: async (target) => {
      await act(async () => {
        target.dispatchEvent(new dom.window.PointerEvent("pointerup", {
          bubbles: true,
          button: 0,
          cancelable: true,
        }));
      });
      await flushDomEffects();
    },
  };

  return result;
}

async function flushDomEffects() {
  for (let index = 0; index < 2; index += 1) {
    await act(async () => {
      await Promise.resolve();
      await new Promise((resolve) => setTimeout(resolve, 0));
      await new Promise<void>((resolve) => {
        if (typeof requestAnimationFrame === "function") {
          requestAnimationFrame(() => resolve());
          return;
        }

        setTimeout(resolve, 0);
      });
    });
  }
}

function installDomGlobals(dom: JSDOM) {
  const { window } = dom;
  mutableGlobal.IS_REACT_ACT_ENVIRONMENT = true;
  setGlobal("window", window);
  setGlobal("document", window.document);
  setGlobal("navigator", window.navigator);
  setGlobal("Node", window.Node);
  setGlobal("NodeFilter", window.NodeFilter);
  setGlobal("Element", window.Element);
  setGlobal("HTMLElement", window.HTMLElement);
  setGlobal("HTMLInputElement", window.HTMLInputElement);
  setGlobal("HTMLTextAreaElement", window.HTMLTextAreaElement);
  setGlobal("IntersectionObserver", window.IntersectionObserver ?? TestIntersectionObserver);
  setGlobal("Event", window.Event);
  setGlobal("CustomEvent", window.CustomEvent);
  setGlobal("KeyboardEvent", window.KeyboardEvent);
  setGlobal("MouseEvent", window.MouseEvent);
  setGlobal("PointerEvent", window.PointerEvent ?? window.MouseEvent);
  setGlobal("ResizeObserver", window.ResizeObserver ?? TestResizeObserver);
  setGlobal("MutationObserver", window.MutationObserver);
  setGlobal("DOMRect", window.DOMRect);
  setGlobal("DocumentFragment", window.DocumentFragment);
  setGlobal("getComputedStyle", window.getComputedStyle.bind(window));
  setGlobal("requestAnimationFrame", window.requestAnimationFrame.bind(window));
  setGlobal("cancelAnimationFrame", window.cancelAnimationFrame.bind(window));

  window.matchMedia ??= (query) => ({
    addEventListener() {},
    addListener() {},
    dispatchEvent() {
      return false;
    },
    matches: false,
    media: query,
    onchange: null,
    removeEventListener() {},
    removeListener() {},
  });
  window.scrollTo = () => undefined;
  window.HTMLElement.prototype.scrollTo ??= function scrollTo(
    this: HTMLElement,
    options?: ScrollToOptions | number,
    y?: number
  ) {
    if (typeof options === "number") {
      this.scrollLeft = options;
      this.scrollTop = y ?? this.scrollTop;
      return;
    }

    if (options?.left !== undefined) {
      this.scrollLeft = options.left;
    }
    if (options?.top !== undefined) {
      this.scrollTop = options.top;
    }
  };
  window.HTMLElement.prototype.scrollIntoView ??= () => undefined;
  window.HTMLElement.prototype.hasPointerCapture ??= () => false;
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

function defineNumericElementProperty(
  element: HTMLElement,
  key: "clientHeight" | "scrollHeight",
  value: number
) {
  Object.defineProperty(element, key, {
    configurable: true,
    value,
  });
}

function findElementByText(root: Element, text: string) {
  const elements = [root, ...Array.from(root.querySelectorAll("*"))];
  return elements.find((element) =>
    element.textContent?.replace(/\s+/g, " ").trim() === text &&
    !Array.from(element.children).some((child) =>
      child.textContent?.replace(/\s+/g, " ").trim() === text
    )
  ) as HTMLElement | undefined ?? null;
}

function findElementByLabelText(root: Element, text: string | RegExp) {
  const ariaLabelMatch = Array.from(root.querySelectorAll("[aria-label]")).find((element) =>
    matchesText(element.getAttribute("aria-label")?.trim() ?? "", text)
  );
  if (ariaLabelMatch instanceof HTMLElement) {
    return ariaLabelMatch;
  }

  const labels = Array.from(root.querySelectorAll("label"));
  for (const label of labels) {
    if (!matchesText(label.textContent?.trim() ?? "", text)) {
      continue;
    }

    const htmlFor = label.getAttribute("for");
    if (htmlFor) {
      const control = root.ownerDocument.getElementById(htmlFor);
      if (control instanceof HTMLElement) {
        return control;
      }
    }

    const nestedControl = label.querySelector("input, select, textarea, button");
    if (nestedControl instanceof HTMLElement) {
      return nestedControl;
    }
  }

  return null;
}

function findElementByRole(root: Element, role: string, name?: string | RegExp) {
  const candidates = Array.from(root.querySelectorAll("*")).filter((element) =>
    getImplicitOrExplicitRole(element) === role
  );

  const match = name === undefined
    ? candidates[0]
    : candidates.find((element) => matchesText(getAccessibleName(element), name));

  return match instanceof HTMLElement ? match : null;
}

function getImplicitOrExplicitRole(element: Element) {
  const explicitRole = element.getAttribute("role");
  if (explicitRole) {
    return explicitRole;
  }

  const tagName = element.tagName.toLowerCase();
  if (tagName === "button") {
    return "button";
  }
  if (tagName === "a" && element.hasAttribute("href")) {
    return "link";
  }
  if (tagName === "input") {
    const type = element.getAttribute("type") ?? "text";
    if (type === "button" || type === "reset" || type === "submit") {
      return "button";
    }
    if (type === "checkbox") {
      return "checkbox";
    }
    if (type === "radio") {
      return "radio";
    }
    if (type === "number") {
      return "spinbutton";
    }
    return "textbox";
  }
  if (tagName === "textarea") {
    return "textbox";
  }
  if (tagName === "select") {
    return "combobox";
  }

  return null;
}

function getAccessibleName(element: Element) {
  const ariaLabel = element.getAttribute("aria-label");
  if (ariaLabel) {
    return ariaLabel.trim();
  }

  if (element instanceof HTMLInputElement) {
    return element.value || element.getAttribute("placeholder") || "";
  }

  return element.textContent?.replace(/\s+/g, " ").trim() ?? "";
}

function matchesText(value: string, expected: string | RegExp) {
  return typeof expected === "string"
    ? value === expected
    : expected.test(value);
}
