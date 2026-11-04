using System.Text.Json.Nodes;
using Middt.Framework.Expressions;
using Middt.Framework.Workflow.Expressions;
using Middt.Framework.Workflow.Schema;

namespace Middt.Framework.Tests.Workflow;

public sealed class JsonSchemaValidatorTests
{
    private static List<string> Validate(string instance, string schema) =>
        JsonSchemaValidator.Validate(JsonNode.Parse(instance), JsonNode.Parse(schema)!.AsObject());

    [Theory]
    [InlineData("""{ "a": 1 }""", "object", true)]
    [InlineData("[1]", "object", false)]
    [InlineData("[1]", "array", true)]
    [InlineData("\"x\"", "string", true)]
    [InlineData("1", "string", false)]
    [InlineData("1.5", "number", true)]
    [InlineData("\"1\"", "number", false)]
    [InlineData("2", "integer", true)]
    [InlineData("2.0", "integer", true)]
    [InlineData("2.5", "integer", false)]
    [InlineData("true", "boolean", true)]
    [InlineData("0", "boolean", false)]
    [InlineData("null", "null", true)]
    [InlineData("1", "unknown-type", false)]
    public void Type_keyword(string instance, string type, bool valid)
    {
        Validate(instance, $$"""{ "type": "{{type}}" }""").Should().HaveCount(valid ? 0 : 1);
    }

    [Fact]
    public void Required_properties_must_be_present_and_not_null()
    {
        const string schema = """{ "type": "object", "required": [ "a", "b" ] }""";

        Validate("""{ "a": 1, "b": 2 }""", schema).Should().BeEmpty();
        Validate("""{ "a": 1, "b": null }""", schema).Should().Equal("$.b: is required.");
        Validate("{}", schema).Should().Equal("$.a: is required.", "$.b: is required.");
    }

    [Fact]
    public void Additional_properties_false_rejects_unknown_members()
    {
        const string schema = """{ "type": "object", "additionalProperties": false, "properties": { "a": { "type": "integer" } } }""";

        Validate("""{ "a": 1 }""", schema).Should().BeEmpty();
        Validate("""{ "a": 1, "isAdmin": true }""", schema).Should().Equal("$.isAdmin: is not allowed.");
        Validate("""{ "a": 1, "extra": 2 }""", """{ "type": "object", "properties": { "a": { "type": "integer" } } }""").Should().BeEmpty();
    }

    [Fact]
    public void Nested_properties_are_validated_with_paths()
    {
        const string schema = """
            { "type": "object", "properties": { "customer": { "type": "object", "required": [ "name" ],
              "properties": { "name": { "type": "string", "minLength": 2 } } } } }
            """;

        Validate("""{ "customer": { "name": "x" } }""", schema).Should().Equal("$.customer.name: must be at least 2 characters.");
        Validate("""{ "customer": { } }""", schema).Should().Equal("$.customer.name: is required.");
        Validate("""{ "customer": "bob" }""", schema).Should().Equal("$.customer: expected object.");
    }

    [Fact]
    public void Null_value_of_an_optional_property_is_accepted()
    {
        Validate("""{ "note": null }""", """{ "type": "object", "properties": { "note": { "type": "string" } } }""").Should().BeEmpty();
    }

    [Fact]
    public void Enum_and_const()
    {
        Validate("\"red\"", """{ "enum": [ "red", "green" ] }""").Should().BeEmpty();
        Validate("\"blue\"", """{ "enum": [ "red", "green" ] }""").Should().ContainSingle().Which.Should().Contain("allowed values");
        Validate("1", """{ "enum": [ 1, 2 ] }""").Should().BeEmpty();
        Validate("\"yes\"", """{ "const": "yes" }""").Should().BeEmpty();
        Validate("\"no\"", """{ "const": "yes" }""").Should().Equal("$: value must be \"yes\".");
    }

    [Theory]
    [InlineData("5", true)]
    [InlineData("1", true)]
    [InlineData("10", true)]
    [InlineData("0.99", false)]
    [InlineData("10.01", false)]
    public void Minimum_and_maximum_are_inclusive(string value, bool valid)
    {
        Validate(value, """{ "type": "number", "minimum": 1, "maximum": 10 }""").Should().HaveCount(valid ? 0 : 1);
    }

    [Theory]
    [InlineData("ab", true)]
    [InlineData("abcd", true)]
    [InlineData("a", false)]
    [InlineData("abcde", false)]
    public void String_length(string value, bool valid)
    {
        Validate($"\"{value}\"", """{ "type": "string", "minLength": 2, "maxLength": 4 }""").Should().HaveCount(valid ? 0 : 1);
    }

    [Theory]
    [InlineData("AB-123", true)]
    [InlineData("ab-123", false)]
    [InlineData("xAB-123", false)]
    public void Pattern(string value, bool valid)
    {
        Validate($"\"{value}\"", """{ "type": "string", "pattern": "^[A-Z]{2}-\\d{3}$" }""").Should().HaveCount(valid ? 0 : 1);
    }

    [Theory]
    [InlineData("2026-01-31T10:00:00Z", "date-time", true)]
    [InlineData("2026-01-31T10:00:00+03:00", "date-time", true)]
    [InlineData("yesterday", "date-time", false)]
    [InlineData("a@b.com", "email", true)]
    [InlineData("not-an-email", "email", false)]
    [InlineData("anything", "uri", true)]
    public void Format(string value, string format, bool valid)
    {
        Validate($"\"{value}\"", $$"""{ "type": "string", "format": "{{format}}" }""").Should().HaveCount(valid ? 0 : 1);
    }

    [Fact]
    public void Array_items_and_counts()
    {
        const string schema = """{ "type": "array", "minItems": 1, "maxItems": 3, "items": { "type": "integer", "minimum": 0 } }""";

        Validate("[1, 2]", schema).Should().BeEmpty();
        Validate("[]", schema).Should().Equal("$: must contain at least 1 items.");
        Validate("[1, 2, 3, 4]", schema).Should().Equal("$: must contain at most 3 items.");
        Validate("[1, -1, \"x\"]", schema).Should().Equal("$[1]: must be ≥ 0.", "$[2]: expected integer.");
    }

    [Fact]
    public void Several_errors_are_reported_together()
    {
        const string schema = """
            { "type": "object", "additionalProperties": false, "required": [ "id" ],
              "properties": { "id": { "type": "integer" }, "name": { "type": "string", "maxLength": 3 } } }
            """;

        Validate("""{ "name": "toolong", "x": 1 }""", schema).Should().HaveCount(3);
    }

    [Fact]
    public void Numbers_outside_the_decimal_range_are_reported_instead_of_throwing()
    {
        Validate("""{ "a": 1e400 }""", """{ "type": "object", "properties": { "a": { "type": "number", "minimum": 0 } } }""")
            .Should().Equal("$.a: number is out of range.");
        Validate("1e400", """{ "type": "integer" }""").Should().Equal("$: expected integer.");
    }

    [Fact]
    public void Numbers_produced_by_expressions_as_double_are_validated()
    {
        var schema = JsonNode.Parse("""{ "type": "object", "properties": { "a": { "type": "integer", "maximum": 10 }, "b": { "type": "number", "minimum": 2 } } }""")!.AsObject();
        var data = new JsonObject { ["a"] = JsonValue.Create(4d), ["b"] = JsonValue.Create(1.5d) };

        JsonSchemaValidator.Validate(data, schema).Should().Equal("$.b: must be ≥ 2.");
        JsonSchemaValidator.Validate(new JsonObject { ["a"] = JsonValue.Create(1.5d) }, schema).Should().Equal("$.a: expected integer.");
    }

    [Fact]
    public void CheckSchema_accepts_every_supported_keyword()
    {
        var schema = JsonNode.Parse("""
            { "$schema": "https://json-schema.org/draft/2020-12/schema", "title": "t", "description": "d", "type": "object",
              "additionalProperties": false, "required": [ "a" ], "default": {},
              "properties": {
                "a": { "type": "string", "minLength": 1, "maxLength": 5, "pattern": "^a", "format": "email", "enum": [ "a" ], "const": "a" },
                "n": { "type": "number", "minimum": 0, "maximum": 1 },
                "l": { "type": "array", "minItems": 0, "maxItems": 2, "items": { "type": "integer" } } } }
            """)!.AsObject();

        JsonSchemaValidator.CheckSchema(schema).Should().BeEmpty();
    }

    [Fact]
    public void CheckSchema_rejects_unsupported_keywords_anywhere_and_bad_regex()
    {
        var schema = JsonNode.Parse("""
            { "type": "object", "oneOf": [],
              "properties": { "a": { "type": "string", "pattern": "([" }, "b": { "$ref": "#/x" } },
              "items": { "if": {} } }
            """)!.AsObject();

        JsonSchemaValidator.CheckSchema(schema).Should().BeEquivalentTo(
            "$: unsupported schema keyword 'oneOf'.",
            "$.a: invalid pattern.",
            "$.b: unsupported schema keyword '$ref'.",
            "$[]: unsupported schema keyword 'if'.");
    }

    private const string LookupSchema = """
        { "type": "object", "required": [ "customerId" ],
          "properties": {
            "customerId": { "type": "integer", "title": "Müşteri",
                            "x-lookup": { "entity": "Customer", "valueField": "customerId", "textField": "name" } } } }
        """;

    [Fact]
    public void CheckSchema_accepts_a_lookup_hint_on_a_property()
    {
        JsonSchemaValidator.CheckSchema(JsonNode.Parse(LookupSchema)!.AsObject()).Should().BeEmpty();
    }

    [Fact]
    public void Lookup_hint_does_not_constrain_the_value()
    {
        Validate("""{ "customerId": 42 }""", LookupSchema).Should().BeEmpty();
        Validate("""{ "customerId": "x" }""", LookupSchema).Should().Equal("$.customerId: expected integer.");
        Validate("{}", LookupSchema).Should().Equal("$.customerId: is required.");
    }

    [Theory]
    [InlineData("\"Customer\"", "$.c.x-lookup: must be an object with entity, valueField and textField.")]
    [InlineData("""{ "valueField": "id", "textField": "name" }""", "$.c.x-lookup.entity: must be a controller name (letters and digits).")]
    [InlineData("""{ "entity": "../admin", "valueField": "id", "textField": "name" }""", "$.c.x-lookup.entity: must be a controller name (letters and digits).")]
    [InlineData("""{ "entity": "Customer", "valueField": "", "textField": "name" }""", "$.c.x-lookup.valueField: must be a property name (camelCase).")]
    [InlineData("""{ "entity": "Customer", "valueField": "id", "textField": 5 }""", "$.c.x-lookup.textField: must be a property name (camelCase).")]
    [InlineData("""{ "entity": "Customer", "valueField": "id", "textField": "name", "filter": {} }""", "$.c.x-lookup: unsupported property 'filter'.")]
    public void CheckSchema_rejects_invalid_lookup_hints(string lookup, string error)
    {
        var schema = JsonNode.Parse($$"""{ "type": "object", "properties": { "c": { "type": "integer", "x-lookup": {{lookup}} } } }""")!.AsObject();

        JsonSchemaValidator.CheckSchema(schema).Should().Equal(error);
    }

    [Fact]
    public void VisibleWhen_requiredWhen_and_disabledWhen_on_schema_properties_are_validated_and_enforced()
    {
        var schema = JsonNode.Parse("""
            {
              "type": "object",
              "required": [ "segment" ],
              "properties": {
                "segment": { "type": "string", "enum": [ "Bireysel", "KOBİ", "Kurumsal" ] },
                "taxOffice": {
                  "type": "string",
                  "minLength": 2,
                  "visibleWhen": "ui.segment != \"Bireysel\"",
                  "requiredWhen": "ui.segment == \"Kurumsal\"",
                  "disabledWhen": "ui.segment == \"Bireysel\""
                }
              }
            }
            """)!.AsObject();

        JsonSchemaValidator.CheckSchema(schema).Should().BeEmpty();
        JsonSchemaValidator.Validate(JsonNode.Parse("""{ "segment": "Bireysel" }"""), schema).Should().BeEmpty();
        JsonSchemaValidator.Validate(JsonNode.Parse("""{ "segment": "KOBİ" }"""), schema).Should().BeEmpty();
        JsonSchemaValidator.Validate(JsonNode.Parse("""{ "segment": "Kurumsal" }"""), schema).Should().Equal("$.taxOffice: is required.");
        JsonSchemaValidator.Validate(JsonNode.Parse("""{ "segment": "Kurumsal", "taxOffice": "Kadıköy" }"""), schema).Should().BeEmpty();

        // Hidden fields (visibleWhen == false) are stripped from the input object so unvalidated values never leak into instance data
        var hiddenPayload = JsonNode.Parse("""{ "segment": "Bireysel", "taxOffice": "X" }""")!.AsObject();
        JsonSchemaValidator.Validate(hiddenPayload, schema).Should().BeEmpty();
        hiddenPayload.ContainsKey("taxOffice").Should().BeFalse();

        // disabledWhen, data.<field>, and user.IsInRole(...) are evaluated with the caller's user and persisted instance data
        var roleAndDataSchema = JsonNode.Parse("""
            {
              "type": "object",
              "required": [ "monthlyIncome" ],
              "properties": {
                "monthlyIncome": { "type": "number", "minimum": 0 },
                "segment": { "type": "string", "default": "Kurumsal" },
                "taxOffice": {
                  "type": "string",
                  "minLength": 2,
                  "visibleWhen": "ui.segment != \"Bireysel\"",
                  "requiredWhen": "ui.segment == \"Kurumsal\" && user.IsInRole(\"manager\")",
                  "disabledWhen": "data.segment == \"Kurumsal\" && !user.IsInRole(\"manager\")"
                }
              }
            }
            """)!.AsObject();
        var persistedData = JsonNode.Parse("""{ "segment": "Kurumsal", "taxOffice": "Büyük Mükellefler V.D." }""")!.AsObject();
        var standardUser = new Middt.Framework.Expressions.ExpressionUser { Name = "owner@test.com", Roles = [] };
        var managerUser = new Middt.Framework.Expressions.ExpressionUser { Name = "alice@test.com", Roles = ["manager"] };

        // Standard user cannot modify disabled taxOffice, but keeping existing value or omitting it succeeds
        JsonSchemaValidator.Validate(
            JsonNode.Parse("""{ "monthlyIncome": 180000, "segment": "Kurumsal", "taxOffice": "Kadıköy V.D." }"""),
            roleAndDataSchema,
            persistedData,
            standardUser).Should().Equal("$.taxOffice: is disabled.");
        JsonSchemaValidator.Validate(
            JsonNode.Parse("""{ "monthlyIncome": 180000, "segment": "Kurumsal", "taxOffice": "Büyük Mükellefler V.D." }"""),
            roleAndDataSchema,
            persistedData,
            standardUser).Should().BeEmpty();
        JsonSchemaValidator.Validate(
            JsonNode.Parse("""{ "monthlyIncome": 180000, "segment": "Kurumsal" }"""),
            roleAndDataSchema,
            persistedData,
            standardUser).Should().BeEmpty();

        // Manager user is not disabled and must provide taxOffice (requiredWhen is true for manager)
        JsonSchemaValidator.Validate(
            JsonNode.Parse("""{ "monthlyIncome": 180000, "segment": "Kurumsal" }"""),
            roleAndDataSchema,
            persistedData,
            managerUser).Should().Equal("$.taxOffice: is required.");
        JsonSchemaValidator.Validate(
            JsonNode.Parse("""{ "monthlyIncome": 180000, "segment": "Kurumsal", "taxOffice": "Kadıköy V.D." }"""),
            roleAndDataSchema,
            persistedData,
            managerUser).Should().BeEmpty();

        // Nested prefillData (e.g. transition with into: "billingContact.emails[0]") allows unchanged disabled field value
        var nestedRootData = JsonNode.Parse("""{ "monthlyIncome": 180000, "billingContact": { "emails": [ { "taxOffice": "Şişli V.D." } ] } }""")!.AsObject();
        var nestedPrefill = JsonPath.Get(nestedRootData, "billingContact.emails[0]") as JsonObject;
        JsonSchemaValidator.Validate(
            JsonNode.Parse("""{ "monthlyIncome": 180000, "segment": "Kurumsal", "taxOffice": "Şişli V.D." }"""),
            roleAndDataSchema,
            nestedRootData,
            standardUser,
            nestedPrefill).Should().BeEmpty();

        var badSchema = JsonNode.Parse("""
            { "type": "object", "properties": { "a": { "type": "string", "requiredWhen": "ui.segment ==" } } }
            """)!.AsObject();
        JsonSchemaValidator.CheckSchema(badSchema).Should().ContainSingle().Which.Should().StartWith("$.a.requiredWhen: ");
    }
}

public sealed class JsonPathTests
{
    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    [Fact]
    public void Get_reads_nested_values_and_returns_null_for_missing_paths()
    {
        var root = Obj("""{ "a": { "b": { "c": 5 } }, "s": "x" }""");

        JsonPath.Get(root, "a.b.c")!.GetValue<int>().Should().Be(5);
        JsonPath.Get(root, null).Should().BeSameAs(root);
        JsonPath.Get(root, "").Should().BeSameAs(root);
        JsonPath.Get(root, "a.x.c").Should().BeNull();
        JsonPath.Get(root, "s.length").Should().BeNull();
    }

    [Fact]
    public void Set_creates_intermediate_objects_and_replaces_non_objects()
    {
        var root = Obj("""{ "a": 1 }""");

        JsonPath.Set(root, "x.y.z", JsonValue.Create(3));
        JsonPath.Set(root, "a.b", JsonValue.Create("v"));

        root.ToJsonString().Should().Be("""{"a":{"b":"v"},"x":{"y":{"z":3}}}""");
    }

    [Fact]
    public void Set_clones_nodes_that_already_have_a_parent()
    {
        var root = Obj("""{ "src": { "v": 1 } }""");

        JsonPath.Set(root, "copy", root["src"]);
        root["copy"]!["v"] = 2;

        root["src"]!["v"]!.GetValue<int>().Should().Be(1);
    }

    [Fact]
    public void Merge_deep_merges_objects_and_replaces_arrays_and_scalars()
    {
        var target = Obj("""{ "a": { "x": 1, "y": 2 }, "list": [ 1, 2, 3 ], "s": "old", "keep": true }""");
        var source = Obj("""{ "a": { "y": 20, "z": 30 }, "list": [ 9 ], "s": "new", "added": null }""");

        JsonPath.Merge(target, source);

        target.ToJsonString().Should().Be("""{"a":{"x":1,"y":20,"z":30},"list":[9],"s":"new","keep":true,"added":null}""");
        source["list"]!.AsArray().Should().HaveCount(1, "the source is cloned, not moved");
    }

    [Fact]
    public void Merge_into_path_creates_and_merges_the_nested_object()
    {
        var target = Obj("""{ "review": { "by": "bob" } }""");

        JsonPath.Merge(target, Obj("""{ "comment": "ok" }"""), "review");
        JsonPath.Merge(target, Obj("""{ "v": 1 }"""), "deep.inner");

        target.ToJsonString().Should().Be("""{"review":{"by":"bob","comment":"ok"},"deep":{"inner":{"v":1}}}""");
    }

    [Fact]
    public void Get_Set_and_Merge_support_fixed_last_and_append_array_indexes()
    {
        var root = Obj("""{ "billingContact": { "emails": [ { "type": "Birincil", "email": "old@firma.com" } ] } }""");

        JsonPath.Get(root, "billingContact.emails[0].email")!.GetValue<string>().Should().Be("old@firma.com");
        JsonPath.Get(root, "billingContact.emails[-1].email")!.GetValue<string>().Should().Be("old@firma.com");
        JsonPath.Get(root, "billingContact.emails[1].email").Should().BeNull();

        JsonPath.Set(root, "billingContact.emails[0].email", JsonValue.Create("new@firma.com"));
        JsonPath.Set(root, "billingContact.emails[]", Obj("""{ "type": "Muhasebe", "email": "acc@firma.com" }"""));
        JsonPath.Set(root, "billingContact.emails[-1].addedBy", JsonValue.Create("alice"));
        JsonPath.Merge(root, Obj("""{ "verified": true }"""), "billingContact.emails[1]");
        JsonPath.Set(root, "billingContact.emails[2].email", JsonValue.Create("fin@firma.com"));

        JsonPath.Get(root, "billingContact.emails[0].email")!.GetValue<string>().Should().Be("new@firma.com");
        JsonPath.Get(root, "billingContact.emails[1].email")!.GetValue<string>().Should().Be("acc@firma.com");
        JsonPath.Get(root, "billingContact.emails[1].addedBy")!.GetValue<string>().Should().Be("alice");
        JsonPath.Get(root, "billingContact.emails[1].verified")!.GetValue<bool>().Should().BeTrue();
        JsonPath.Get(root, "billingContact.emails[2].email")!.GetValue<string>().Should().Be("fin@firma.com");

        // Null on missing fixed index does not create phantom elements, whereas null on existing element clears property
        JsonPath.Set(root, "billingContact.phones[0].number", null);
        JsonPath.Get(root, "billingContact.phones").Should().BeNull();
        JsonPath.Set(root, "billingContact.emails[1].addedBy", null);
        JsonPath.Get(root, "billingContact.emails[1].addedBy").Should().BeNull();
    }

    [Fact]
    public void ToNode_converts_expression_results()
    {
        JsonPath.ToNode(null).Should().BeNull();
        JsonPath.ToNode(1.5m)!.GetValue<decimal>().Should().Be(1.5m);
        JsonPath.ToNode("s")!.GetValue<string>().Should().Be("s");
        JsonPath.ToNode(TimeSpan.FromMinutes(2))!.GetValue<string>().Should().Be("00:02:00");
        JsonPath.ToNode(new List<object?> { 1, "a" })!.ToJsonString().Should().Be("""[1,"a"]""");
        JsonPath.ToNode(new JsonDynamic(Obj("""{ "a": 1 }""")))!.ToJsonString().Should().Be("""{"a":1}""");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("[1]")]
    public void ParseObject_returns_an_empty_object_for_non_objects(string? json)
    {
        JsonPath.ParseObject(json).Should().BeEmpty();
    }
}
