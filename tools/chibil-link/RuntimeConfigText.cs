namespace ChibilLink;

/// <summary>
/// The .runtimeconfig.json emitted next to every chibil-linked executable.
/// </summary>
/// <remarks>
/// configProperties: a chibil image is a C program compiled to IL — it never uses
/// .NET globalization. Worse, in a single-file image the culture/resource
/// infrastructure (satellite assemblies, ICU) is absent, so the moment the BCL
/// tries to format ANY exception message it recurses
/// SR.GetResourceString -> CultureInfo.GetCultureInfo -> resource grovel ->
/// (re-fault) -> ... -> fatal StackOverflow, turning every benign managed exception
/// into a process crash. Invariant globalization disables that whole path;
/// UseSystemResourceKeys makes exception text resource-free as a second line of
/// defense. Both are the standard remedy for native/trimmed/single-file images.
/// </remarks>
public static class RuntimeConfigText
{
    public const string Json =
        "{\n" +
        "  \"runtimeOptions\": {\n" +
        "    \"tfm\": \"net10.0\",\n" +
        "    \"rollForward\": \"Major\",\n" +
        "    \"framework\": {\n" +
        "      \"name\": \"Microsoft.NETCore.App\",\n" +
        "      \"version\": \"10.0.0\"\n" +
        "    },\n" +
        "    \"configProperties\": {\n" +
        "      \"System.Globalization.Invariant\": true,\n" +
        "      \"System.Resources.UseSystemResourceKeys\": true\n" +
        "    }\n" +
        "  }\n" +
        "}\n";
}
