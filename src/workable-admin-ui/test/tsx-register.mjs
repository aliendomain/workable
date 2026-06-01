import { existsSync, readFileSync } from "node:fs";
import { registerHooks } from "node:module";
import path from "node:path";
import { fileURLToPath, pathToFileURL } from "node:url";
import ts from "typescript";

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const sourceRoot = path.join(projectRoot, "src");
const extensions = ["", ".tsx", ".ts", ".jsx", ".js", ".mjs"];

function resolveExistingPath(basePath) {
  for (const extension of extensions) {
    const candidate = `${basePath}${extension}`;
    if (existsSync(candidate)) {
      return candidate;
    }
  }

  for (const extension of extensions.slice(1)) {
    const candidate = path.join(basePath, `index${extension}`);
    if (existsSync(candidate)) {
      return candidate;
    }
  }

  return null;
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
