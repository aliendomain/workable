import assert from "node:assert/strict";
import test from "node:test";
import {
  SchemaForm,
  SchemaPathField,
  SchemaPresetButton,
  compactJson,
  createDefaultValue,
  parseJsonSchema,
} from "@/components/workable/schema-form";
import {
  assertMarkupIncludes,
  renderMarkup,
} from "@/test/render";
import type { JsonSchemaNode } from "@/lib/workable";

test("schema parsing and default values cover invalid, explicit, enum, object, array, formatted, and primitive schemas", () => {
  assert.equal(parseJsonSchema(null), null);
  assert.equal(parseJsonSchema(" "), null);
  assert.equal(parseJsonSchema("1"), null);
  assert.deepEqual(parseJsonSchema("[]"), []);
  assert.equal(parseJsonSchema("{bad"), null);
  assert.deepEqual(parseJsonSchema("{\"type\":\"string\"}"), { type: "string" });

  assert.equal(createDefaultValue(null), undefined);
  assert.equal(createDefaultValue({ type: "string", default: "given" }), "given");
  assert.equal(createDefaultValue({ enum: ["first", "second"] }), "first");
  assert.deepEqual(
    createDefaultValue({
      properties: {
        count: { type: "integer" },
        enabled: { type: "boolean" },
        tags: { items: { type: "string" }, type: "array" },
      },
      type: "object",
    }),
    {
      count: 0,
      enabled: false,
      tags: [],
    }
  );
  assert.deepEqual(createDefaultValue({ items: { type: "string" }, type: "array" }), []);
  assert.match(String(createDefaultValue({ format: "date", type: "string" })), /^\d{4}-\d{2}-\d{2}$/);
  assert.match(String(createDefaultValue({ format: "date-time", type: "string" })), /^\d{4}-\d{2}-\d{2}T/);
  assert.equal(createDefaultValue({ type: "number" }), 0);
  assert.equal(createDefaultValue({ type: "string" }), "");
});

test("schema form renders empty, object, enum, boolean, number, and formatted string pathways", () => {
  assertMarkupIncludes(
    renderMarkup(<SchemaForm schema={null} value={undefined} onChange={() => undefined} />),
    "This definition does not expose a typed input schema."
  );

  const schema: JsonSchemaNode = {
    properties: {
      enabled: {
        description: "Whether to run.",
        title: "Enabled",
        type: "boolean",
      },
      mode: {
        enum: ["fast", "safe"],
        title: "Mode",
      },
      retries: {
        title: "Retries",
        type: "integer",
      },
      callbackUrl: {
        format: "uri",
        title: "Callback URL",
        type: "string",
      },
      runAt: {
        format: "date-time",
        title: "Run at",
        type: "string",
      },
    },
    required: ["enabled", "mode"],
    type: "object",
  };
  const markup = renderMarkup(
    <SchemaForm
      onChange={() => undefined}
      schema={schema}
      value={{
        callbackUrl: "https://example.test/callback",
        enabled: true,
        mode: "fast",
        retries: 3,
        runAt: "2026-05-30T12:34:56Z",
      }}
    />
  );

  assertMarkupIncludes(markup, "Enabled");
  assertMarkupIncludes(markup, "True");
  assertMarkupIncludes(markup, "False");
  assertMarkupIncludes(markup, "Mode");
  assertMarkupIncludes(markup, "role=\"combobox\"");
  assertMarkupIncludes(markup, "type=\"number\"");
  assertMarkupIncludes(markup, "step=\"1\"");
  assertMarkupIncludes(markup, "type=\"url\"");
  assertMarkupIncludes(markup, "https://example.com/data.csv");
  assertMarkupIncludes(markup, "type=\"datetime-local\"");
  assertMarkupIncludes(markup, "2026-05-30T12:34");
});

test("schema path fields render missing paths and nested required/optional field details", () => {
  const schema: JsonSchemaNode = {
    properties: {
      options: {
        properties: {
          batchSize: {
            description: "Items per batch.",
            type: "integer",
          },
          name: {
            type: ["string", "null"],
          },
        },
        required: ["batchSize"],
        type: "object",
      },
    },
    required: ["options"],
    type: "object",
  };

  const missing = renderMarkup(
    <SchemaPathField
      onChange={() => undefined}
      path="options.missing"
      schema={schema}
      value={{ options: {} }}
    />
  );
  assertMarkupIncludes(missing, "options.missing");

  const nested = renderMarkup(
    <SchemaPathField
      description="Override description"
      label="Batch size"
      onChange={() => undefined}
      path="options.batchSize"
      schema={schema}
      value={{ options: { batchSize: 10 } }}
    />
  );
  assertMarkupIncludes(nested, "Batch size");
  assertMarkupIncludes(nested, "aria-label=\"Batch size field details\"");
  assertMarkupIncludes(nested, "type=\"number\"");
  assertMarkupIncludes(nested, "value=\"10\"");
});

test("schema form renders array and dictionary empty and populated pathways", () => {
  const arraySchema: JsonSchemaNode = {
    items: { type: "string" },
    title: "Recipients",
    type: "array",
  };
  const emptyArray = renderMarkup(
    <SchemaForm schema={arraySchema} value={[]} onChange={() => undefined} />
  );
  assertMarkupIncludes(emptyArray, "Recipients");
  assertMarkupIncludes(emptyArray, "Add");
  assertMarkupIncludes(emptyArray, "No items.");

  const populatedArray = renderMarkup(
    <SchemaForm schema={arraySchema} value={["one"]} onChange={() => undefined} />
  );
  assertMarkupIncludes(populatedArray, "Item 1");
  assertMarkupIncludes(populatedArray, "value=\"one\"");

  const dictionarySchema: JsonSchemaNode = {
    additionalProperties: { type: "number" },
    title: "Headers",
    type: "object",
  };
  const emptyDictionary = renderMarkup(
    <SchemaForm schema={dictionarySchema} value={{}} onChange={() => undefined} />
  );
  assertMarkupIncludes(emptyDictionary, "Headers");
  assertMarkupIncludes(emptyDictionary, "No key-value pairs.");

  const populatedDictionary = renderMarkup(
    <SchemaForm
      schema={dictionarySchema}
      value={{ retryAfter: 30 }}
      onChange={() => undefined}
    />
  );
  assertMarkupIncludes(populatedDictionary, "aria-label=\"Dictionary key\"");
  assertMarkupIncludes(populatedDictionary, "retryAfter");
  assertMarkupIncludes(populatedDictionary, "value=\"30\"");
});

test("schema preset button and compact JSON expose disabled, enabled, and undefined-stripping pathways", () => {
  const disabled = renderMarkup(
    <SchemaPresetButton schema={null} onApply={() => undefined} />
  );
  assertMarkupIncludes(disabled, "disabled=\"\"");
  assertMarkupIncludes(disabled, "Use input defaults");

  const enabled = renderMarkup(
    <SchemaPresetButton schema={{ type: "boolean" }} onApply={() => undefined} />
  );
  assertMarkupIncludes(enabled, "Use input defaults");
  assert.doesNotMatch(enabled, /disabled=""/);

  assert.equal(
    compactJson({
      nested: {
        kept: true,
        skipped: undefined,
      },
      omitted: undefined,
      values: [1, undefined, 3],
    }),
    "{\n  \"nested\": {\n    \"kept\": true\n  },\n  \"values\": [\n    1,\n    null,\n    3\n  ]\n}"
  );
});
