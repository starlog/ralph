using System.Reflection;

namespace Ralph.Commands;

internal static class SchemaLoader
{
    public static string Load()
    {
        var assembly = Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("ralph-schema.json")
                           ?? throw new FileNotFoundException("Embedded ralph-schema.json not found");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
