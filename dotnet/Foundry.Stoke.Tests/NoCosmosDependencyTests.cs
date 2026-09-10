using System.Text.Json;
using System.Xml.Linq;

namespace Foundry.Stoke.Tests;

/// <summary>Dependency inspection for T039, FR-011, SC-002 and CC-004.</summary>
public sealed class NoCosmosDependencyTests
{
    private static readonly string[] ForbiddenSdkRoots =
    [
        "Microsoft.Azure.Cosmos",
        "Azure.Cosmos",
        "Microsoft.Azure.DocumentDB",
        "Microsoft.Azure.Documents",
        "Microsoft.Azure.CosmosDB.Table",
        "Azure.Data.Tables",
        "WindowsAzure.Storage",
        "Microsoft.WindowsAzure.Storage",
        "StackExchange.Redis",
        "MongoDB.Driver",
    ];

    [Fact]
    public void CoreProjectDoesNotDeclareStoreSdk()
    {
        var project = XDocument.Load(Path.Combine(CoreDirectory(), "Foundry.Stoke.csproj"));

        AssertNoStoreSdk(DeclaredReferences(project));
    }

    [Fact]
    public void CoreRestoreGraphDoesNotDependOnStoreSdk()
    {
        using var assets = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(CoreDirectory(), "obj", "project.assets.json")));

        AssertNoStoreSdk(RestoredReferences(assets.RootElement));
    }

    [Theory]
    [InlineData("Microsoft.Azure.Cosmos")]
    [InlineData("microsoft.azure.cosmos.direct")]
    [InlineData("Microsoft.Azure.DocumentDB.Core")]
    [InlineData("Microsoft.Azure.CosmosDB.Table")]
    [InlineData("Azure.Data.Tables")]
    [InlineData("StackExchange.Redis")]
    public void InspectionRejectsUnusedDeclarationsAndTransitiveStoreSdk(string packageName)
    {
        var project = new XDocument(new XElement("Project",
            new XElement("ItemGroup", new XElement("PackageReference",
                new XAttribute("Include", packageName), new XAttribute("Version", "1.0.0")))));
        using var assets = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            libraries = new Dictionary<string, object>
            {
                [$"{packageName}/1.0.0"] = new { type = "package" },
            },
            project = new { frameworks = new { net8_0 = new { } } },
        }));

        Assert.Throws<Xunit.Sdk.DoesNotContainException>(() => AssertNoStoreSdk(DeclaredReferences(project)));
        Assert.Throws<Xunit.Sdk.DoesNotContainException>(() => AssertNoStoreSdk(RestoredReferences(assets.RootElement)));
    }

    private static IEnumerable<string> DeclaredReferences(XDocument project) =>
        project.Descendants()
            .Where(element => element.Name.LocalName is "PackageReference" or "Reference")
            .Select(element => (string?)element.Attribute("Include") ?? (string?)element.Attribute("Update"))
            .OfType<string>()
            .Select(reference => reference.Split(',')[0].Trim());

    private static IEnumerable<string> RestoredReferences(JsonElement assets)
    {
        foreach (var library in assets.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() == "package")
            {
                yield return library.Name.Split('/')[0];
            }
        }

        foreach (var framework in assets.GetProperty("project").GetProperty("frameworks").EnumerateObject())
        {
            if (framework.Value.TryGetProperty("dependencies", out var dependencies))
            {
                foreach (var dependency in dependencies.EnumerateObject())
                {
                    yield return dependency.Name;
                }
            }
        }
    }

    private static void AssertNoStoreSdk(IEnumerable<string> references) =>
        Assert.DoesNotContain(references, reference => ForbiddenSdkRoots.Any(root =>
            reference.Equals(root, StringComparison.OrdinalIgnoreCase)
            || reference.StartsWith(root + ".", StringComparison.OrdinalIgnoreCase)));

    private static string CoreDirectory()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var core = Path.Combine(directory.FullName, "Foundry.Stoke");
            if (File.Exists(Path.Combine(core, "Foundry.Stoke.csproj")))
            {
                return core;
            }
        }

        throw new DirectoryNotFoundException("Run the dependency inspection from the repository's .NET test output.");
    }
}
