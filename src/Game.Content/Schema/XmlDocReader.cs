using System.Text;
using System.Xml.Linq;

namespace Game.Content.Schema;

/// <summary>Reads <c>&lt;summary&gt;</c> text from a compiler-generated XML documentation file.</summary>
public sealed class XmlDocReader
{
    private readonly Dictionary<string, string> _summaries = new(StringComparer.Ordinal);

    private XmlDocReader()
    {
    }

    /// <summary>A reader with no documentation (descriptions are omitted).</summary>
    public static XmlDocReader Empty { get; } = new();

    /// <summary>Loads the <c>.xml</c> file that sits next to <paramref name="assembly"/>; throws if it is missing.</summary>
    public static XmlDocReader ForAssembly(System.Reflection.Assembly assembly)
    {
        var path = Path.ChangeExtension(assembly.Location, ".xml");
        if (!File.Exists(path))
            throw new FileNotFoundException($"XML documentation file for {assembly.GetName().Name} not found; enable GenerateDocumentationFile.", path);
        return Load(path);
    }

    /// <summary>Loads an XML documentation file.</summary>
    public static XmlDocReader Load(string path)
    {
        var reader = new XmlDocReader();
        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            var name = (string?)member.Attribute("name");
            var summary = member.Element("summary");
            if (name is null || summary is null) continue;
            var text = Render(summary);
            if (text.Length > 0) reader._summaries[name] = text;
        }
        return reader;
    }

    /// <summary>Summary of a type, or null.</summary>
    public string? ForType(Type type) => _summaries.GetValueOrDefault("T:" + type.FullName);

    /// <summary>Summary of a property, or null.</summary>
    public string? ForProperty(Type declaringType, string propertyName) =>
        _summaries.GetValueOrDefault("P:" + declaringType.FullName + "." + propertyName);

    private static string Render(XElement element)
    {
        var sb = new StringBuilder();
        Append(element, sb);
        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static void Append(XElement element, StringBuilder sb)
    {
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement { Name.LocalName: "see" or "seealso" } see:
                    var cref = (string?)see.Attribute("cref");
                    if (cref is not null) sb.Append(cref[(cref.LastIndexOf('.') + 1)..]);
                    else sb.Append((string?)see.Attribute("langword"));
                    break;
                case XElement { Name.LocalName: "paramref" or "typeparamref" } p:
                    sb.Append((string?)p.Attribute("name"));
                    break;
                case XElement child:
                    Append(child, sb);
                    break;
            }
        }
    }
}
