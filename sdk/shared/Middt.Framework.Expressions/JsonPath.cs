using System.Collections;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Middt.Framework.Expressions;

/// <summary>Dot-path access (<c>customer.address.city</c>, <c>items[0].name</c>, <c>items[-1]</c>, <c>items[]</c>) and deep merge for instance data.</summary>
public static class JsonPath
{
    /// <summary>Upper bound for fixed array index segments (<c>[0]</c> .. <c>[1000]</c>) to prevent excessive array padding allocations.</summary>
    public const int MaxArrayIndex = 1000;

    private static bool TryParseArraySegment(string segment, out string arrayKey, out int? index)
    {
        arrayKey = string.Empty;
        index = null;
        if (!segment.EndsWith(']'))
        {
            return false;
        }

        var open = segment.LastIndexOf('[');
        if (open <= 0)
        {
            return false;
        }

        var idxSpan = segment.AsSpan(open + 1, segment.Length - open - 2);
        if (idxSpan.IsEmpty)
        {
            arrayKey = segment[..open];
            return true;
        }

        if (idxSpan.SequenceEqual("-1"))
        {
            arrayKey = segment[..open];
            index = -1;
            return true;
        }

        if (int.TryParse(idxSpan, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed)
            && parsed >= 0
            && parsed <= MaxArrayIndex)
        {
            arrayKey = segment[..open];
            index = parsed;
            return true;
        }

        return false;
    }

    public static JsonNode? Get(JsonObject root, string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return root;
        }

        JsonNode? current = root;
        foreach (var segment in path.Split('.'))
        {
            if (TryParseArraySegment(segment, out var arrayKey, out var index) && index is { } idx)
            {
                if (current is JsonObject arrParent
                    && arrParent.TryGetPropertyValue(arrayKey, out var arrNode)
                    && arrNode is JsonArray arr)
                {
                    current = idx == -1
                        ? (arr.Count > 0 ? arr[^1] : null)
                        : (idx < arr.Count ? arr[idx] : null);
                }
                else
                {
                    current = null;
                }
            }
            else
            {
                current = current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next) ? next : null;
            }

            if (current is null)
            {
                return null;
            }
        }

        return current;
    }

    /// <summary>Sets a value at <paramref name="path"/>, creating intermediate objects (or appending/indexing into a <see cref="JsonArray"/> when a segment ends with <c>[]</c>, <c>[-1]</c>, or <c>[N]</c>).</summary>
    public static void Set(JsonObject root, string path, JsonNode? value)
    {
        ArgumentException.ThrowIfNullOrEmpty(path);
        if (value is null && (path.Contains("[]", StringComparison.Ordinal) || path.Contains("[-1]", StringComparison.Ordinal)))
        {
            return;
        }

        var segments = path.Split('.');
        var current = root;
        foreach (var segment in segments[..^1])
        {
            if (TryParseArraySegment(segment, out var arrayKey, out var index))
            {
                if (current[arrayKey] is not JsonArray arr)
                {
                    if (value is null)
                    {
                        return;
                    }

                    arr = [];
                    current[arrayKey] = arr;
                }

                if (index is null)
                {
                    var created = new JsonObject();
                    arr.Add(created);
                    current = created;
                    continue;
                }

                if (index == -1)
                {
                    if (arr.Count > 0 && arr[^1] is JsonObject lastObj)
                    {
                        current = lastObj;
                        continue;
                    }

                    var created = new JsonObject();
                    if (arr.Count > 0)
                    {
                        arr[^1] = created;
                    }
                    else
                    {
                        arr.Add(created);
                    }

                    current = created;
                    continue;
                }

                var targetIndex = index.Value;
                if (value is null && (arr.Count <= targetIndex || arr[targetIndex] is not JsonObject))
                {
                    return;
                }

                while (arr.Count <= targetIndex)
                {
                    arr.Add((JsonNode?)null);
                }

                if (arr[targetIndex] is not JsonObject indexedObj)
                {
                    indexedObj = [];
                    arr[targetIndex] = indexedObj;
                }

                current = indexedObj;
                continue;
            }

            if (current[segment] is not JsonObject child)
            {
                child = [];
                current[segment] = child;
            }

            current = child;
        }

        var leaf = segments[^1];
        var cloned = value?.Parent is null ? value : value.DeepClone();
        if (TryParseArraySegment(leaf, out var leafArrayKey, out var leafIndex))
        {
            if (leafIndex is null)
            {
                if (cloned is null || (cloned is JsonArray emptyArr && emptyArr.Count == 0))
                {
                    return;
                }

                if (current[leafArrayKey] is not JsonArray arr)
                {
                    arr = [];
                    current[leafArrayKey] = arr;
                }

                if (cloned is JsonArray itemsToAppend)
                {
                    foreach (var item in itemsToAppend.ToList())
                    {
                        itemsToAppend.Remove(item);
                        arr.Add(item);
                    }
                }
                else
                {
                    arr.Add(cloned);
                }
            }
            else if (leafIndex == -1)
            {
                if (current[leafArrayKey] is JsonArray { Count: > 0 } arr)
                {
                    arr[^1] = cloned;
                }
                else
                {
                    current[leafArrayKey] = new JsonArray { cloned };
                }
            }
            else
            {
                var targetIndex = leafIndex.Value;
                if (cloned is null && (current[leafArrayKey] is not JsonArray existingArr || existingArr.Count <= targetIndex))
                {
                    return;
                }

                if (current[leafArrayKey] is not JsonArray arr)
                {
                    arr = [];
                    current[leafArrayKey] = arr;
                }

                while (arr.Count <= targetIndex)
                {
                    arr.Add((JsonNode?)null);
                }

                arr[targetIndex] = cloned;
            }
        }
        else
        {
            current[leaf] = cloned;
        }
    }

    /// <summary>
    /// Merges <paramref name="source"/> into <paramref name="target"/> (or into the object at <paramref name="path"/>;
    /// when <paramref name="path"/> ends with <c>[]</c>, appends <paramref name="source"/> to the array at that path):
    /// objects merge key by key recursively, everything else (including arrays) is replaced.
    /// </summary>
    public static void Merge(JsonObject target, JsonObject source, string? path = null)
    {
        if (!string.IsNullOrEmpty(path))
        {
            if (path.EndsWith("[]", StringComparison.Ordinal))
            {
                Set(target, path, source.DeepClone());
                return;
            }

            if (Get(target, path) is not JsonObject nested)
            {
                nested = [];
                Set(target, path, nested);
                nested = (JsonObject)Get(target, path)!;
            }

            target = nested;
        }

        foreach (var (key, value) in source)
        {
            if (value is JsonObject sourceChild && target[key] is JsonObject targetChild)
            {
                Merge(targetChild, sourceChild);
            }
            else
            {
                target[key] = value?.DeepClone();
            }
        }
    }

    /// <summary>Converts expression results (primitives, <see cref="JsonDynamic"/>, lists) back to JSON.</summary>
    public static JsonNode? ToNode(object? value) => value switch
    {
        null => null,
        JsonNode node => node.DeepClone(),
        JsonDynamic dyn => dyn.Node?.DeepClone(),
        JsonDynamicArray arr => CloneDynamicArray(arr),
        string s => JsonValue.Create(s),
        bool b => JsonValue.Create(b),
        decimal d => JsonValue.Create(d),
        double d => JsonValue.Create(d),
        float f => JsonValue.Create(f),
        int i => JsonValue.Create(i),
        long l => JsonValue.Create(l),
        DateTimeOffset dto => JsonValue.Create(dto),
        DateTime dt => JsonValue.Create(dt),
        Guid g => JsonValue.Create(g),
        TimeSpan ts => JsonValue.Create(ts.ToString("c", CultureInfo.InvariantCulture)),
        IEnumerable sequence => new JsonArray([.. sequence.Cast<object?>().Select(ToNode)]),
        _ => JsonSerializer.SerializeToNode(value, value.GetType()),
    };

    private static JsonArray CloneDynamicArray(JsonDynamicArray arr)
    {
        var clone = (JsonArray)arr.Node.DeepClone();
        if (JsonDynamic.GetRemovedItems(arr.Node) is { } removed)
        {
            JsonDynamic.RegisterRemovedItems(clone, removed);
        }

        return clone;
    }

    public static JsonObject ParseObject(string? json) =>
        string.IsNullOrWhiteSpace(json) ? [] : JsonNode.Parse(json) as JsonObject ?? [];
}
