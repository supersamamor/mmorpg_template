using Game.Content.Registry;

namespace Game.Content.ClientExport;

/// <summary>Content database in, client bundle file out: the one entry point the tools and the server use, so both compute the same hash.</summary>
public static class ClientBundleExporter
{
    /// <summary>Builds the bundle for <paramref name="content"/> and returns its canonical file bytes and hash.</summary>
    public static ClientBundleFile Export(ContentDatabase content) => ClientBundleWriter.Export(ClientBundleBuilder.Build(content));

    /// <summary>True when <paramref name="file"/> is byte for byte what exporting <paramref name="content"/> now produces (the <c>--check</c> test).</summary>
    public static bool IsCurrent(ContentDatabase content, ReadOnlySpan<byte> file) => file.SequenceEqual(Export(content).FileBytes);

    /// <summary>The content hash of <paramref name="content"/>'s client bundle.</summary>
    public static string Hash(ContentDatabase content) => ClientBundleWriter.ComputeHash(ClientBundleBuilder.Build(content));
}
