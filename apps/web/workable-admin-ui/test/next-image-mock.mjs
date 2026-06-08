import { createElement } from "react";

export default function Image({ alt, priority, src, ...props }) {
  void priority;
  return createElement("img", {
    alt,
    src: typeof src === "string" ? src : src?.src,
    ...props,
  });
}
