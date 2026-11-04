using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Middt.Framework.Expressions;
using Middt.Framework.Expressions.Portable;

namespace Middt.Framework.Workflow.Schema;

/// <summary>
/// Dependency free validator for the JSON Schema subset used by transition inputs:
/// <c>type</c>, <c>properties</c>, <c>required</c>, <c>additionalProperties</c> (bool), <c>enum</c>, <c>const</c>,
/// <c>minimum</c>/<c>maximum</c>, <c>minLength</c>/<c>maxLength</c>, <c>pattern</c>, <c>format</c> (date-time, email),
/// <c>items</c>, <c>minItems</c>/<c>maxItems</c>. Unknown keywords are rejected when the definition is loaded.
/// <para>
/// UI extension: <c>"x-lookup": { "entity": "Customer", "valueField": "customerId", "textField": "name" }</c> on a property
/// tells frontends to render a select whose options come from <c>api/v{version}/{entity}/getitems</c> (same shape as the
/// layout <c>lookup</c>). Its shape is checked at load time; it does not constrain the submitted value.
/// </para>
/// <para>
/// Data projection extensions: <c>"x-roles": ["admin", "auditor"]</c> strips the property when projecting instance data for
/// callers lacking those roles; <c>"x-mask": { "pattern": "last4" | "email" | "***", "exceptRoles": ["auditor"] }</c> masks
/// string properties in the projected response.
/// </para>
/// </summary>
public static partial class JsonSchemaValidator
{
    /// <summary>Schema keyword of the lookup UI hint.</summary>
    public const string LookupKeyword = "x-lookup";

    /// <summary>Schema keyword restricting field visibility by role.</summary>
    public const string RolesKeyword = "x-roles";

    /// <summary>Schema keyword masking string field values unless exempted by role.</summary>
    public const string MaskKeyword = "x-mask";

    /// <summary>Schema keyword controlling whether a property is shown as a column in the workflow instance list (<c>true</c> by default).</summary>
    public const string ListKeyword = "x-list";

    /// <summary>Schema keyword controlling whether a property is shown in the workflow instance list search bar (<c>false</c> by default).</summary>
    public const string SearchKeyword = "x-search";

    /// <summary>Schema keyword controlling whether a partition functional B-Tree index is created for the property.</summary>
    public const string IndexKeyword = "x-index";

    /// <summary>Schema keyword on the root <c>dataSchema</c> controlling whether <c>createdFrom</c>/<c>createdTo</c> date range filters are shown.</summary>
    public const string DateSearchKeyword = "x-date-search";

    /// <summary>Schema keyword on a transition input property populating <c>enum</c> dynamically from an instance data array path (<c>&lt;arrayPath&gt;.&lt;field&gt;</c>).</summary>
    public const string EnumFromKeyword = "x-enumFrom";

    /// <summary>Schema keyword on a property controlling conditional visibility in forms using portable expressions (e.g. <c>ui.segment == "Kurumsal"</c>).</summary>
    public const string VisibleWhenKeyword = "visibleWhen";

    /// <summary>Alias of <see cref="VisibleWhenKeyword"/> with the <c>x-</c> extension prefix.</summary>
    public const string XVisibleWhenKeyword = "x-visibleWhen";

    /// <summary>Schema keyword on a property controlling conditional required validation using portable expressions (e.g. <c>ui.amount > 500000</c>).</summary>
    public const string RequiredWhenKeyword = "requiredWhen";

    /// <summary>Alias of <see cref="RequiredWhenKeyword"/> with the <c>x-</c> extension prefix.</summary>
    public const string XRequiredWhenKeyword = "x-requiredWhen";

    /// <summary>Schema keyword on a property controlling whether the field is disabled in forms using portable expressions (e.g. <c>!user.IsInRole("manager")</c>).</summary>
    public const string DisabledWhenKeyword = "disabledWhen";

    /// <summary>Alias of <see cref="DisabledWhenKeyword"/> with the <c>x-</c> extension prefix.</summary>
    public const string XDisabledWhenKeyword = "x-disabledWhen";

    private static readonly PortableExpression UiEvaluator = new(["data", "query", "row", "record", "instance", "ui"]);

    private static readonly HashSet<string> SupportedSearchModes =
    [
        "text", "select", "range", "min", "max", "gte", "lte", "date-range",
    ];

    private static readonly HashSet<string> SupportedKeywords =
    [
        "$schema", "title", "description", "type", "properties", "required", "additionalProperties", "enum", "const",
        "minimum", "maximum", "minLength", "maxLength", "pattern", "format", "items", "minItems", "maxItems", "default",
    ];

    /// <summary>Errors in the schema itself (unsupported keywords, invalid patterns).</summary>
    public static IEnumerable<string> CheckSchema(JsonObject schema, string path = "$")
    {
        foreach (var (keyword, value) in schema)
        {
            if (keyword == LookupKeyword)
            {
                foreach (var error in CheckLookup(value, $"{path}.{LookupKeyword}"))
                {
                    yield return error;
                }
            }
            else if (keyword == RolesKeyword)
            {
                foreach (var error in CheckRoles(value, $"{path}.{RolesKeyword}"))
                {
                    yield return error;
                }
            }
            else if (keyword == MaskKeyword)
            {
                foreach (var error in CheckMask(value, $"{path}.{MaskKeyword}"))
                {
                    yield return error;
                }
            }
            else if (keyword is ListKeyword or IndexKeyword or DateSearchKeyword)
            {
                if (value is not JsonValue lv || lv.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                {
                    yield return $"{path}.{keyword}: must be a boolean.";
                }
            }
            else if (keyword == SearchKeyword)
            {
                var valid = value is JsonValue sv && (
                    sv.GetValueKind() is JsonValueKind.True or JsonValueKind.False ||
                    (sv.GetValueKind() == JsonValueKind.String && sv.TryGetValue<string>(out var mode) && SupportedSearchModes.Contains(mode)));
                if (!valid)
                {
                    yield return $"{path}.{keyword}: must be a boolean or one of {string.Join(", ", SupportedSearchModes)}.";
                }
            }
            else if (keyword == EnumFromKeyword)
            {
                var validString = value is JsonValue ev && ev.TryGetValue<string>(out var ef) && !string.IsNullOrWhiteSpace(ef);
                var validObject = value is JsonObject eo
                    && eo["source"] is JsonValue sv && sv.TryGetValue<string>(out var src) && !string.IsNullOrWhiteSpace(src)
                    && (eo["format"] is null || (eo["format"] is JsonValue fv && fv.TryGetValue<string>(out var fmt) && !string.IsNullOrWhiteSpace(fmt)))
                    && eo.All(kv => kv.Key is "source" or "format");
                if (!validString && !validObject)
                {
                    yield return $"{path}.{keyword}: must be a non-empty string path ('<arrayPath>.<field>') or an object '{{ \"source\": \"...\", \"format\": \"...\" }}'.";
                }
            }
            else if (keyword is VisibleWhenKeyword or XVisibleWhenKeyword or RequiredWhenKeyword or XRequiredWhenKeyword or DisabledWhenKeyword or XDisabledWhenKeyword)
            {
                if (value is not JsonValue vw || !vw.TryGetValue<string>(out var expr) || string.IsNullOrWhiteSpace(expr))
                {
                    yield return $"{path}.{keyword}: must be a non-empty expression string.";
                }
                else if (UiEvaluator.Validate(expr) is { } exprError)
                {
                    yield return $"{path}.{keyword}: {exprError}";
                }
            }
            else if (!SupportedKeywords.Contains(keyword))
            {
                yield return $"{path}: unsupported schema keyword '{keyword}'.";
            }
            else if (keyword == "pattern" && !IsValidRegex(value?.GetValue<string>()))
            {
                yield return $"{path}: invalid pattern.";
            }
        }

        if (schema["properties"] is JsonObject properties)
        {
            foreach (var (name, child) in properties)
            {
                if (child is JsonObject childSchema)
                {
                    foreach (var error in CheckSchema(childSchema, $"{path}.{name}"))
                    {
                        yield return error;
                    }
                }
            }
        }

        if (schema["items"] is JsonObject items)
        {
            foreach (var error in CheckSchema(items, $"{path}[]"))
            {
                yield return error;
            }
        }
    }

    private static IEnumerable<string> CheckRoles(JsonNode? rolesNode, string path)
    {
        if (rolesNode is not JsonArray arr || arr.Count == 0 ||
            arr.Any(item => item is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)))
        {
            yield return $"{path}: must be a non-empty array of role names.";
        }
    }

    private static IEnumerable<string> CheckMask(JsonNode? maskNode, string path)
    {
        if (maskNode is not JsonObject obj)
        {
            yield return $"{path}: must be an object with 'pattern' and optional 'exceptRoles'.";
            yield break;
        }

        foreach (var (name, _) in obj)
        {
            if (name is not ("pattern" or "exceptRoles"))
            {
                yield return $"{path}: unsupported property '{name}'.";
            }
        }

        var pattern = obj["pattern"] is JsonValue pv && pv.TryGetValue<string>(out var p) ? p : null;
        if (pattern is not ("last4" or "email" or "***"))
        {
            yield return $"{path}.pattern: must be 'last4', 'email' or '***'.";
        }

        if (obj["exceptRoles"] is { } exceptNode)
        {
            if (exceptNode is not JsonArray arr ||
                arr.Any(item => item is not JsonValue v || !v.TryGetValue<string>(out var s) || string.IsNullOrWhiteSpace(s)))
            {
                yield return $"{path}.exceptRoles: must be an array of role names.";
            }
        }
    }

    /// <summary>
    /// Applies <c>x-roles</c> field visibility filtering and <c>x-mask</c> string masking to <paramref name="data"/>
    /// according to <paramref name="schema"/> for <paramref name="user"/>.
    /// </summary>
    public static JsonObject ProjectData(JsonObject data, JsonObject? schema, ExpressionUser user)
    {
        if (schema is null || user.IsSystem)
        {
            return data;
        }

        var projected = (JsonObject)data.DeepClone();
        ApplyProjection(projected, schema, user);
        return projected;
    }

    /// <summary>
    /// Clones <paramref name="schema"/> and removes properties whose <c>x-roles</c> exclude <paramref name="user"/>.
    /// </summary>
    public static JsonObject? ProjectSchema(JsonObject? schema, ExpressionUser user)
    {
        if (schema is null)
        {
            return null;
        }

        var projected = (JsonObject)schema.DeepClone();
        if (user.IsSystem)
        {
            return projected;
        }

        ApplySchemaProjection(projected, user);
        return projected;
    }

    private static void ApplySchemaProjection(JsonObject schemaObj, ExpressionUser user)
    {
        if (schemaObj["properties"] is JsonObject properties)
        {
            var disallowed = properties
                .Where(kv => kv.Value is JsonObject propSchema &&
                             propSchema[RolesKeyword] is JsonArray rolesArr &&
                             rolesArr.Count > 0 &&
                             !rolesArr
                                 .Select(r => r is JsonValue rv && rv.TryGetValue<string>(out var s) ? s : null)
                                 .OfType<string>()
                                 .Any(user.IsInRole))
                .Select(kv => kv.Key)
                .ToList();

            foreach (var name in disallowed)
            {
                properties.Remove(name);
            }

            foreach (var (_, propNode) in properties)
            {
                if (propNode is JsonObject childSchema)
                {
                    ApplySchemaProjection(childSchema, user);
                }
            }
        }

        if (schemaObj["items"] is JsonObject itemsSchema)
        {
            ApplySchemaProjection(itemsSchema, user);
        }
    }

    private static void ApplyProjection(JsonObject obj, JsonObject schema, ExpressionUser user)
    {
        if (schema["properties"] is not JsonObject properties)
        {
            return;
        }

        foreach (var (name, propNode) in properties)
        {
            if (propNode is not JsonObject propSchema || !obj.TryGetPropertyValue(name, out var val))
            {
                continue;
            }

            if (propSchema[RolesKeyword] is JsonArray rolesArr && rolesArr.Count > 0)
            {
                var allowed = rolesArr
                    .Select(r => r is JsonValue rv && rv.TryGetValue<string>(out var s) ? s : null)
                    .OfType<string>()
                    .Any(user.IsInRole);
                if (!allowed)
                {
                    obj.Remove(name);
                    continue;
                }
            }

            if (propSchema[MaskKeyword] is JsonObject maskObj &&
                val is JsonValue strVal && strVal.GetValueKind() == JsonValueKind.String && strVal.TryGetValue<string>(out var raw))
            {
                var exempted = maskObj["exceptRoles"] is JsonArray exceptArr && exceptArr
                    .Select(r => r is JsonValue rv && rv.TryGetValue<string>(out var s) ? s : null)
                    .OfType<string>()
                    .Any(user.IsInRole);

                if (!exempted)
                {
                    var pattern = maskObj["pattern"]?.GetValue<string>() ?? "***";
                    obj[name] = MaskString(raw, pattern);
                    continue;
                }
            }

            if (val is JsonObject childObj)
            {
                ApplyProjection(childObj, propSchema, user);
            }
            else if (val is JsonArray childArr && propSchema["items"] is JsonObject itemSchema)
            {
                foreach (var item in childArr.OfType<JsonObject>())
                {
                    ApplyProjection(item, itemSchema, user);
                }
            }
        }
    }

    private static string MaskString(string value, string pattern) => pattern switch
    {
        "last4" => value.Length <= 4 ? new string('*', value.Length) : new string('*', value.Length - 4) + value[^4..],
        "email" => value.IndexOf('@') is > 0 and var at ? $"{value[0]}***{value[at..]}" : "***",
        _ => "***",
    };

    private static IEnumerable<string> CheckLookup(JsonNode? lookup, string path)
    {
        if (lookup is not JsonObject obj)
        {
            yield return $"{path}: must be an object with entity, valueField and textField.";
            yield break;
        }

        foreach (var (name, _) in obj)
        {
            if (name is not ("entity" or "valueField" or "textField"))
            {
                yield return $"{path}: unsupported property '{name}'.";
            }
        }

        // The entity becomes a URL path segment and the fields JSON property names: restrict them to plain identifiers.
        if (!IsMatch(obj["entity"], EntityRegex()))
        {
            yield return $"{path}.entity: must be a controller name (letters and digits).";
        }

        foreach (var field in (string[])["valueField", "textField"])
        {
            if (!IsMatch(obj[field], FieldRegex()))
            {
                yield return $"{path}.{field}: must be a property name (camelCase).";
            }
        }
    }

    private static bool IsMatch(JsonNode? node, Regex regex) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && regex.IsMatch(text);

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex EntityRegex();

    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex FieldRegex();

    public static List<string> Validate(JsonNode? instance, JsonObject schema, JsonObject? data = null, ExpressionUser? user = null, JsonObject? prefillData = null)
    {
        var errors = new List<string>();
        Validate(instance, schema, "$", errors, data, user, prefillData);
        return errors;
    }

    private static void Validate(JsonNode? node, JsonObject schema, string path, List<string> errors, JsonObject? data, ExpressionUser? user, JsonObject? prefillData = null)
    {
        if (schema["type"] is JsonValue typeValue && typeValue.TryGetValue<string>(out var type) && !IsType(node, type))
        {
            errors.Add($"{path}: expected {type}.");
            return;
        }

        if (schema["enum"] is JsonArray allowed && !allowed.Any(a => JsonNode.DeepEquals(a, node)))
        {
            errors.Add($"{path}: value is not one of the allowed values.");
        }

        if (schema["const"] is { } constant && !JsonNode.DeepEquals(constant, node))
        {
            errors.Add($"{path}: value must be {constant.ToJsonString()}.");
        }

        switch (node)
        {
            case JsonObject obj:
                ValidateObject(obj, schema, path, errors, data, user, prefillData);
                break;
            case JsonArray array:
                ValidateArray(array, schema, path, errors, data, user);
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                ValidateString(value.GetValue<string>(), schema, path, errors);
                break;
            case JsonValue value when value.GetValueKind() == JsonValueKind.Number:
                if (TryGetNumber(value, out var number))
                {
                    ValidateNumber(number, schema, path, errors);
                }
                else
                {
                    errors.Add($"{path}: number is out of range.");
                }

                break;
        }
    }

    private static void ValidateObject(JsonObject obj, JsonObject schema, string path, List<string> errors, JsonObject? data, ExpressionUser? user, JsonObject? prefillData = null)
    {
        var properties = schema["properties"] as JsonObject;
        var visibleProps = GetVisibleProperties(obj, properties, data, user);
        if (visibleProps is not null && properties is not null)
        {
            foreach (var (name, _) in properties)
            {
                if (!visibleProps.Contains(name))
                {
                    obj.Remove(name);
                }
            }
        }

        var evalUi = properties is not null ? BuildVisibleUiObject(obj, properties, visibleProps) : obj;
        var disabledProps = GetDisabledProperties(evalUi, properties, visibleProps, data, user);
        var requiredReported = new HashSet<string>(StringComparer.Ordinal);

        if (schema["required"] is JsonArray required)
        {
            foreach (var name in required.Select(r => r?.GetValue<string>()).OfType<string>())
            {
                if ((visibleProps is not null && !visibleProps.Contains(name))
                    || (disabledProps is not null && disabledProps.Contains(name)))
                {
                    continue;
                }

                if (!obj.TryGetPropertyValue(name, out var v) || v is null)
                {
                    errors.Add($"{path}.{name}: is required.");
                    requiredReported.Add(name);
                }
            }
        }

        if (properties is not null)
        {
            foreach (var (name, propNode) in properties)
            {
                if (requiredReported.Contains(name)
                    || (visibleProps is not null && !visibleProps.Contains(name))
                    || (disabledProps is not null && disabledProps.Contains(name)))
                {
                    continue;
                }

                if (propNode is JsonObject propSchema && GetRequiredWhen(propSchema) is { } reqWhen && EvaluateFormCondition(reqWhen, evalUi, data, user))
                {
                    if (!obj.TryGetPropertyValue(name, out var v) || v is null)
                    {
                        errors.Add($"{path}.{name}: is required.");
                    }
                }
            }
        }

        var allowAdditional = schema["additionalProperties"] is not JsonValue additional || additional.GetValue<bool>();
        foreach (var (name, value) in obj)
        {
            if (properties?[name] is JsonObject propertySchema)
            {
                if (visibleProps is not null && !visibleProps.Contains(name))
                {
                    continue;
                }

                if (disabledProps is not null && disabledProps.Contains(name) && value is not null)
                {
                    JsonNode? expectedValue = null;
                    var hasExpected = false;
                    var sourceObj = prefillData ?? data;
                    if (sourceObj is not null && sourceObj.TryGetPropertyValue(name, out var dataVal) && dataVal is not null)
                    {
                        expectedValue = dataVal;
                        hasExpected = true;
                    }
                    else if (data is not null && !ReferenceEquals(sourceObj, data) && data.TryGetPropertyValue(name, out var rootVal) && rootVal is not null)
                    {
                        expectedValue = rootVal;
                        hasExpected = true;
                    }
                    else if (propertySchema["default"] is { } defaultVal)
                    {
                        expectedValue = defaultVal;
                        hasExpected = true;
                    }

                    if (!hasExpected || !JsonNode.DeepEquals(value, expectedValue))
                    {
                        errors.Add($"{path}.{name}: is disabled.");
                        continue;
                    }
                }

                if (value is not null)
                {
                    var childPrefill = (prefillData ?? data)?[name] as JsonObject;
                    Validate(value, propertySchema, $"{path}.{name}", errors, data, user, childPrefill);
                }
            }
            else if (!allowAdditional)
            {
                errors.Add($"{path}.{name}: is not allowed.");
            }
        }
    }

    private static HashSet<string>? GetVisibleProperties(JsonObject obj, JsonObject? properties, JsonObject? data, ExpressionUser? user)
    {
        if (properties is null || properties.Count == 0)
        {
            return null;
        }

        var hasConditions = false;
        foreach (var (_, propNode) in properties)
        {
            if (propNode is JsonObject propObj && GetVisibleWhen(propObj) is not null)
            {
                hasConditions = true;
                break;
            }
        }

        if (!hasConditions)
        {
            return null;
        }

        var ui = (JsonObject)obj.DeepClone();
        for (var pass = 0; pass < properties.Count; pass++)
        {
            var changed = false;
            foreach (var (name, propNode) in properties)
            {
                if (propNode is not JsonObject propObj)
                {
                    continue;
                }

                var visible = EvaluateVisibleWhen(GetVisibleWhen(propObj), ui, data, user);
                if (!visible && ui.ContainsKey(name))
                {
                    ui.Remove(name);
                    changed = true;
                }
                else if (visible && !ui.ContainsKey(name) && obj.TryGetPropertyValue(name, out var original) && original is not null)
                {
                    ui[name] = original.DeepClone();
                    changed = true;
                }
            }

            if (!changed)
            {
                break;
            }
        }

        var result = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (name, propNode) in properties)
        {
            if (propNode is not JsonObject propObj || EvaluateVisibleWhen(GetVisibleWhen(propObj), ui, data, user))
            {
                result.Add(name);
            }
        }

        return result;
    }

    private static HashSet<string>? GetDisabledProperties(JsonObject evalUi, JsonObject? properties, HashSet<string>? visibleProps, JsonObject? data, ExpressionUser? user)
    {
        if (properties is null || properties.Count == 0)
        {
            return null;
        }

        HashSet<string>? result = null;
        foreach (var (name, propNode) in properties)
        {
            if (visibleProps is not null && !visibleProps.Contains(name))
            {
                continue;
            }

            if (propNode is JsonObject propObj && GetDisabledWhen(propObj) is { } disWhen && EvaluateFormCondition(disWhen, evalUi, data, user))
            {
                result ??= new HashSet<string>(StringComparer.Ordinal);
                result.Add(name);
            }
        }

        return result;
    }

    private static JsonObject BuildVisibleUiObject(JsonObject obj, JsonObject properties, HashSet<string>? visibleProps)
    {
        if (visibleProps is null)
        {
            return obj;
        }

        var ui = new JsonObject();
        foreach (var (name, _) in properties)
        {
            if (visibleProps.Contains(name) && obj.TryGetPropertyValue(name, out var val) && val is not null)
            {
                ui[name] = val.DeepClone();
            }
        }

        return ui;
    }

    private static string? GetVisibleWhen(JsonObject propertySchema) =>
        (propertySchema[VisibleWhenKeyword] is JsonValue v1 && v1.TryGetValue<string>(out var s1) && !string.IsNullOrWhiteSpace(s1) ? s1 : null)
        ?? (propertySchema[XVisibleWhenKeyword] is JsonValue v2 && v2.TryGetValue<string>(out var s2) && !string.IsNullOrWhiteSpace(s2) ? s2 : null);

    private static string? GetRequiredWhen(JsonObject propertySchema) =>
        (propertySchema[RequiredWhenKeyword] is JsonValue v1 && v1.TryGetValue<string>(out var s1) && !string.IsNullOrWhiteSpace(s1) ? s1 : null)
        ?? (propertySchema[XRequiredWhenKeyword] is JsonValue v2 && v2.TryGetValue<string>(out var s2) && !string.IsNullOrWhiteSpace(s2) ? s2 : null);

    private static string? GetDisabledWhen(JsonObject propertySchema) =>
        (propertySchema[DisabledWhenKeyword] is JsonValue v1 && v1.TryGetValue<string>(out var s1) && !string.IsNullOrWhiteSpace(s1) ? s1 : null)
        ?? (propertySchema[XDisabledWhenKeyword] is JsonValue v2 && v2.TryGetValue<string>(out var s2) && !string.IsNullOrWhiteSpace(s2) ? s2 : null);

    private static bool EvaluateVisibleWhen(string? condition, JsonObject ui, JsonObject? data, ExpressionUser? user) =>
        string.IsNullOrWhiteSpace(condition) || EvaluateFormCondition(condition, ui, data, user);

    private static bool EvaluateFormCondition(string condition, JsonObject ui, JsonObject? data, ExpressionUser? user)
    {
        try
        {
            var context = new ExpressionContext { User = user ?? ExpressionUser.Anonymous };
            context.Set("ui", ui);
            context.Set("data", data ?? ui);
            return UiEvaluator.EvaluateCondition(condition, context);
        }
        catch (PortableExpressionException)
        {
            return false;
        }
    }

    private static void ValidateArray(JsonArray array, JsonObject schema, string path, List<string> errors, JsonObject? data, ExpressionUser? user)
    {
        if (Int(schema, "minItems") is { } min && array.Count < min)
        {
            errors.Add($"{path}: must contain at least {min} items.");
        }

        if (Int(schema, "maxItems") is { } max && array.Count > max)
        {
            errors.Add($"{path}: must contain at most {max} items.");
        }

        if (schema["items"] is JsonObject itemSchema)
        {
            for (var i = 0; i < array.Count; i++)
            {
                Validate(array[i], itemSchema, $"{path}[{i}]", errors, data, user);
            }
        }
    }

    private static void ValidateString(string value, JsonObject schema, string path, List<string> errors)
    {
        if (Int(schema, "minLength") is { } min && value.Length < min)
        {
            errors.Add($"{path}: must be at least {min} characters.");
        }

        if (Int(schema, "maxLength") is { } max && value.Length > max)
        {
            errors.Add($"{path}: must be at most {max} characters.");
        }

        if (schema["pattern"]?.GetValue<string>() is { } pattern && !Regex.IsMatch(value, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            errors.Add($"{path}: has an invalid format.");
        }

        var format = schema["format"]?.GetValue<string>();
        if (format == "date-time" && !DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
        {
            errors.Add($"{path}: must be a date-time.");
        }
        else if (format == "email" && !System.Net.Mail.MailAddress.TryCreate(value, out _))
        {
            errors.Add($"{path}: must be an e-mail address.");
        }
    }

    private static void ValidateNumber(decimal value, JsonObject schema, string path, List<string> errors)
    {
        if (schema["minimum"] is JsonValue min && value < min.GetValue<decimal>())
        {
            errors.Add($"{path}: must be ≥ {min}.");
        }

        if (schema["maximum"] is JsonValue max && value > max.GetValue<decimal>())
        {
            errors.Add($"{path}: must be ≤ {max}.");
        }
    }

    private static bool IsType(JsonNode? node, string type) => type switch
    {
        "object" => node is JsonObject,
        "array" => node is JsonArray,
        "string" => node is JsonValue v && v.GetValueKind() == JsonValueKind.String,
        "number" => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number,
        "integer" => node is JsonValue v && v.GetValueKind() == JsonValueKind.Number && TryGetNumber(v, out var n) && decimal.IsInteger(n),
        "boolean" => node is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False,
        "null" => node is null,
        _ => false,
    };

    /// <summary>
    /// Reads any JSON number as decimal without throwing: values out of the decimal range (<c>1e400</c>) or backed by
    /// another CLR type (<c>double</c> from an expression) would otherwise make <c>GetValue&lt;decimal&gt;</c> throw.
    /// </summary>
    private static bool TryGetNumber(JsonValue value, out decimal number) =>
        value.TryGetValue(out number) ||
        decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out number);

    private static int? Int(JsonObject schema, string keyword) => schema[keyword] is JsonValue v ? v.GetValue<int>() : null;

    private static bool IsValidRegex(string? pattern)
    {
        try
        {
            _ = new Regex(pattern ?? string.Empty, RegexOptions.None, TimeSpan.FromMilliseconds(100));
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
