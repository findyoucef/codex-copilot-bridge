using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexCopilotBridge.Protocol;

public sealed record CodexToolDeclaration(
    string CopilotName,
    string CodexName,
    string? CodexNamespace,
    string Description,
    JsonElement Parameters);

public sealed record CodexToolReaderOptions
{
    public bool CompactSchemas { get; init; }

    public IReadOnlySet<string> ExcludedToolNames { get; init; } = new HashSet<string>(StringComparer.Ordinal);

    public IReadOnlyList<string> ExcludedToolNamePrefixes { get; init; } = [];
}

public static class CodexToolReader
{
    public static IReadOnlyList<CodexToolDeclaration> ReadToolDeclarations(
        ResponsesApiRequest request,
        CodexToolReaderOptions? options = null)
    {
        if (request.Tools is not { Count: > 0 })
        {
            return [];
        }

        options ??= new CodexToolReaderOptions();
        var declarations = new List<CodexToolDeclaration>();
        var seenCopilotNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tool in request.Tools)
        {
            if (!tool.TryGetProperty("type", out var typeElement))
            {
                continue;
            }

            switch (typeElement.GetString())
            {
                case "function":
                    AddFunctionDeclaration(tool, codexNamespace: null, namespaceDescription: null, declarations, seenCopilotNames, options);
                    break;
                case "namespace":
                    AddNamespaceDeclarations(tool, declarations, seenCopilotNames, options);
                    break;
            }
        }

        return declarations;
    }

    private static void AddNamespaceDeclarations(
        JsonElement namespaceTool,
        List<CodexToolDeclaration> declarations,
        HashSet<string> seenCopilotNames,
        CodexToolReaderOptions options)
    {
        if (!TryGetString(namespaceTool, "name", out var namespaceName) ||
            !namespaceTool.TryGetProperty("tools", out var tools) ||
            tools.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var namespaceDescription = TryGetString(namespaceTool, "description", out var description)
            ? description
            : null;

        foreach (var tool in tools.EnumerateArray())
        {
            if (!tool.TryGetProperty("type", out var typeElement) ||
                typeElement.GetString() != "function")
            {
                continue;
            }

            AddFunctionDeclaration(tool, namespaceName, namespaceDescription, declarations, seenCopilotNames, options);
        }
    }

    private static void AddFunctionDeclaration(
        JsonElement tool,
        string? codexNamespace,
        string? namespaceDescription,
        List<CodexToolDeclaration> declarations,
        HashSet<string> seenCopilotNames,
        CodexToolReaderOptions options)
    {
        if (!TryGetString(tool, "name", out var name))
        {
            return;
        }

        var copilotName = FlattenToolName(codexNamespace, name);
        if (IsExcluded(copilotName, name, options))
        {
            return;
        }

        if (!seenCopilotNames.Add(copilotName))
        {
            return;
        }

        var descriptionParts = new[]
            {
                namespaceDescription,
                TryGetString(tool, "description", out var description) ? description : null
            }
            .Where(part => !string.IsNullOrWhiteSpace(part));

        var parameters = tool.TryGetProperty("parameters", out var parameterElement)
            ? ReadParametersSchema(parameterElement, options.CompactSchemas)
            : EmptyParametersSchema();

        declarations.Add(new CodexToolDeclaration(
            copilotName,
            name,
            codexNamespace,
            string.Join(Environment.NewLine, descriptionParts),
            parameters));
    }

    private static bool IsExcluded(string copilotName, string codexName, CodexToolReaderOptions options)
    {
        if (options.ExcludedToolNames.Contains(copilotName) ||
            options.ExcludedToolNames.Contains(codexName))
        {
            return true;
        }

        return options.ExcludedToolNamePrefixes.Any(prefix =>
            !string.IsNullOrWhiteSpace(prefix) &&
            copilotName.StartsWith(prefix, StringComparison.Ordinal));
    }

    private static bool TryGetString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        value = property.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(value);
    }

    private static string FlattenToolName(string? codexNamespace, string name)
    {
        if (string.IsNullOrWhiteSpace(codexNamespace))
        {
            return name;
        }

        return codexNamespace.EndsWith("__", StringComparison.Ordinal)
            ? $"{codexNamespace}{name}"
            : $"{codexNamespace}__{name}";
    }

    private static JsonElement ReadParametersSchema(JsonElement parameters, bool compactSchemas)
    {
        if (!compactSchemas)
        {
            return parameters.Clone();
        }

        var node = JsonNode.Parse(parameters.GetRawText());
        if (node is null)
        {
            return parameters.Clone();
        }

        RemoveSchemaAnnotations(node);
        return JsonSerializer.SerializeToElement(node, ProtocolJson.SerializerOptions);
    }

    private static void RemoveSchemaAnnotations(JsonNode? node, bool isSchemaMap = false)
    {
        switch (node)
        {
            case JsonObject schema:
                if (!isSchemaMap)
                {
                    schema.Remove("description");
                    schema.Remove("title");
                    schema.Remove("examples");
                }

                foreach (var property in schema.ToArray())
                {
                    RemoveSchemaAnnotations(property.Value, IsSchemaMapProperty(property.Key));
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    RemoveSchemaAnnotations(item);
                }
                break;
        }
    }

    private static bool IsSchemaMapProperty(string propertyName) =>
        propertyName is "properties" or "patternProperties" or "$defs" or "definitions" or "dependentSchemas";

    private static JsonElement EmptyParametersSchema() =>
        JsonSerializer.Deserialize<JsonElement>(
            """
            {
              "type": "object",
              "properties": {},
              "additionalProperties": false
            }
            """,
            ProtocolJson.SerializerOptions);
}
