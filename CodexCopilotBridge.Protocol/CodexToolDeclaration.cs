using System.Text.Json;

namespace CodexCopilotBridge.Protocol;

public sealed record CodexToolDeclaration(
    string CopilotName,
    string CodexName,
    string? CodexNamespace,
    string Description,
    JsonElement Parameters);

public static class CodexToolReader
{
    public static IReadOnlyList<CodexToolDeclaration> ReadToolDeclarations(ResponsesApiRequest request)
    {
        if (request.Tools is not { Count: > 0 })
        {
            return [];
        }

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
                    AddFunctionDeclaration(tool, codexNamespace: null, namespaceDescription: null, declarations, seenCopilotNames);
                    break;
                case "namespace":
                    AddNamespaceDeclarations(tool, declarations, seenCopilotNames);
                    break;
            }
        }

        return declarations;
    }

    private static void AddNamespaceDeclarations(
        JsonElement namespaceTool,
        List<CodexToolDeclaration> declarations,
        HashSet<string> seenCopilotNames)
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

            AddFunctionDeclaration(tool, namespaceName, namespaceDescription, declarations, seenCopilotNames);
        }
    }

    private static void AddFunctionDeclaration(
        JsonElement tool,
        string? codexNamespace,
        string? namespaceDescription,
        List<CodexToolDeclaration> declarations,
        HashSet<string> seenCopilotNames)
    {
        if (!TryGetString(tool, "name", out var name))
        {
            return;
        }

        var copilotName = FlattenToolName(codexNamespace, name);
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
            ? parameterElement.Clone()
            : EmptyParametersSchema();

        declarations.Add(new CodexToolDeclaration(
            copilotName,
            name,
            codexNamespace,
            string.Join(Environment.NewLine, descriptionParts),
            parameters));
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
