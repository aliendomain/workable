import { existsSync, readFileSync, statSync } from "node:fs";
import { registerHooks } from "node:module";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import { JSDOM } from "jsdom";
import ts from "typescript";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceRoot = path.join(projectRoot, "src");
const extensions = ["", ".tsx", ".ts", ".jsx", ".js", ".mjs"];

class TestResizeObserver {
  #callback;

  constructor(callback) {
    this.#callback = callback;
  }

  disconnect() {}

  observe(target) {
    const rect = target.getBoundingClientRect();
    const boxSize = {
      blockSize: rect.height,
      inlineSize: rect.width,
    };
    this.#callback?.([
      {
        borderBoxSize: [boxSize],
        contentBoxSize: [boxSize],
        contentRect: rect,
        devicePixelContentBoxSize: [boxSize],
        target,
      },
    ], this);
  }

  unobserve() {}
}

class TestIntersectionObserver {
  disconnect() {}
  observe() {}
  takeRecords() {
    return [];
  }
  unobserve() {}
}

installBaselineDomForClientModules();

function resolveExistingPath(basePath) {
  for (const extension of extensions) {
    const candidate = `${basePath}${extension}`;
    if (isFile(candidate)) {
      return candidate;
    }
  }

  for (const extension of extensions.slice(1)) {
    const candidate = path.join(basePath, `index${extension}`);
    if (isFile(candidate)) {
      return candidate;
    }
  }

  return null;
}

function isFile(candidate) {
  return existsSync(candidate) && statSync(candidate).isFile();
}

function installBaselineDomForClientModules() {
  if (globalThis.window?.document) {
    installWindowPatches(globalThis.window);
    return;
  }

  const dom = new JSDOM("<!doctype html><html><body></body></html>", {
    pretendToBeVisual: true,
    url: "http://localhost/",
  });
  const { window } = dom;
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
  installWindowPatches(window);
}

function installWindowPatches(window) {
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
  window.HTMLElement.prototype.scrollTo ??= function scrollTo(options, y) {
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

function setGlobal(key, value) {
  Object.defineProperty(globalThis, key, {
    configurable: true,
    value,
    writable: true,
  });
}

function resolveLocalSpecifier(specifier, parentURL) {
  if (specifier.startsWith("@/")) {
    return resolveExistingPath(path.join(sourceRoot, specifier.slice(2)));
  }

  if (specifier.startsWith(".") || specifier.startsWith("/")) {
    const parentPath = parentURL && parentURL.startsWith("file:")
      ? path.dirname(fileURLToPath(parentURL))
      : projectRoot;
    if (parentPath.includes(`${path.sep}node_modules${path.sep}`)) {
      return null;
    }
    return resolveExistingPath(path.resolve(parentPath, specifier));
  }

  return null;
}

registerHooks({
  resolve(specifier, context, nextResolve) {
    if (specifier === "next/image") {
      return {
        format: "module",
        shortCircuit: true,
        url: pathToFileURL(path.join(projectRoot, "test", "next-image-mock.mjs")).href,
      };
    }
    if (specifier === "next/navigation") {
      return {
        format: "module",
        shortCircuit: true,
        url: pathToFileURL(path.join(projectRoot, "test", "next-navigation-mock.mjs")).href,
      };
    }
    if (specifier === "next/server") {
      return {
        format: "commonjs",
        shortCircuit: true,
        url: pathToFileURL(path.join(projectRoot, "node_modules", "next", "server.js")).href,
      };
    }

    const resolvedPath = resolveLocalSpecifier(specifier, context.parentURL);
    if (resolvedPath) {
      return {
        format: "module",
        shortCircuit: true,
        url: pathToFileURL(resolvedPath).href,
      };
    }

    return nextResolve(specifier, context);
  },
  load(url, context, nextLoad) {
    if (!url.startsWith("file:")) {
      return nextLoad(url, context);
    }

    const filePath = fileURLToPath(url);
    if (!/\.[cm]?tsx?$/.test(filePath)) {
      return nextLoad(url, context);
    }

    const source = readFileSync(filePath, "utf8");
    const output = ts.transpileModule(source, {
      compilerOptions: {
        esModuleInterop: true,
        jsx: ts.JsxEmit.ReactJSX,
        module: ts.ModuleKind.ESNext,
        moduleResolution: ts.ModuleResolutionKind.Bundler,
        target: ts.ScriptTarget.ES2022,
      },
      fileName: filePath,
    });

    return {
      format: "module",
      shortCircuit: true,
      source: output.outputText,
    };
  },
});
