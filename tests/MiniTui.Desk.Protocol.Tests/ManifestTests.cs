namespace MiniTui.Desk.Protocol.Tests;

/// <summary>Manifest mode (spec §16.4): what <c>WithDesk</c> says for <c>tuidesk add</c>.</summary>
public sealed class ManifestTests
{
    private static string Written(string name, string? version, DeskManifest? manifest)
    {
        using var output = new StringWriter();
        DeskAppExtensions.WriteManifest(output, name, version, manifest);
        return output.ToString();
    }

    [Fact]
    public void The_manifest_is_one_line_of_json_with_the_name_and_version()
    {
        var manifest = new DeskManifest { Id = "weather", Description = "The forecast", Args = ["--city", "Oslo"] };
        Assert.Equal(
            """{"id":"weather","name":"Weather","version":"1.2","description":"The forecast","args":["--city","Oslo"]}""" + Environment.NewLine,
            Written("Weather", "1.2", manifest));
    }

    [Fact]
    public void Without_a_manifest_only_the_name_is_said()
    {
        Assert.Equal("""{"name":"Weather"}""" + Environment.NewLine, Written("Weather", null, null));
    }
}
