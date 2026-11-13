using System.IO;
using System.Text.Json.Nodes;

namespace Hairpin.App.Tests;

/// <summary>Keeps <c>appsettings.schema.json</c>, which gives editors completion and checks, in step with <c>appsettings.json</c>.</summary>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class SettingsSchemaTests
{
    [TestMethod]
    public void The_schema_describes_every_setting_with_its_default_and_nothing_else()
    {
        var settings = Load("appsettings.json");
        var schema = Load("appsettings.schema.json");
        var problems = new List<string>();

        Compare(settings, schema, string.Empty, problems);

        Assert.IsEmpty(problems, string.Join(Environment.NewLine, problems));
    }

    private static void Compare(JsonObject settings, JsonNode schema, string path, List<string> problems)
    {
        var properties = schema["properties"]?.AsObject();
        foreach (var (key, value) in settings)
        {
            var described = properties?[key] ?? schema["additionalProperties"] as JsonObject;
            if (described is null)
            {
                problems.Add($"{path}{key} isn't in the schema.");
            }
            else if (value is JsonObject child)
            {
                Compare(child, described, $"{path}{key}:", problems);
            }
            else if (described["default"] is { } expected && !JsonNode.DeepEquals(expected, value))
            {
                problems.Add($"{path}{key} defaults to {value?.ToJsonString()}, but the schema says {expected.ToJsonString()}.");
            }
        }

        // Only the closed sections, Relay and App, must list exactly what appsettings.json holds.
        if (schema["additionalProperties"] is JsonValue && properties is not null)
        {
            problems.AddRange(properties.Where(p => !settings.ContainsKey(p.Key)).Select(p => $"{path}{p.Key} is in the schema but not in appsettings.json."));
        }
    }

    private static JsonObject Load(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, name)))!.AsObject();
}
