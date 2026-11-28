using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using Game.Content.Validation;
using Game.Tools.Editor;
using Game.Tests.World;

namespace Game.Tests.Tools;

/// <summary>A copy of content/, schemas/ and themes/ in a temporary folder, with an editor over it (T-801).</summary>
public sealed class EditorRig : IDisposable
{
    public readonly string Root = Path.Combine(Path.GetTempPath(), "t801-editor-" + Guid.NewGuid().ToString("N"));
    public readonly ContentEditor Editor;

    public EditorRig(string? dropSchema = null)
    {
        foreach (var folder in new[] { "content", "schemas", "themes" }) Copy(Path.Combine(TestGrids.RepoRoot(), folder), Path.Combine(Root, folder));
        if (dropSchema is not null) File.Delete(Path.Combine(Root, "schemas", dropSchema + ".schema.json"));
        Editor = new ContentEditor(Content, Path.Combine(Root, "schemas"), Path.Combine(Root, "themes"));
    }

    public string Content => Path.Combine(Root, "content");

    public string Read(string path) => File.ReadAllText(Path.Combine(Content, path));

    public byte[] Bytes(string path) => File.ReadAllBytes(Path.Combine(Content, path));

    /// <summary>Every file under content/ with its bytes, to show nothing else changed.</summary>
    public Dictionary<string, string> Snapshot() =>
        Directory.GetFiles(Content, "*", SearchOption.AllDirectories).ToDictionary(f => Path.GetRelativePath(Content, f), f => Convert.ToBase64String(File.ReadAllBytes(f)));

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private static void Copy(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var dir in Directory.GetDirectories(from, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(to, Path.GetRelativePath(from, dir)));
        foreach (var file in Directory.GetFiles(from, "*", SearchOption.AllDirectories)) File.Copy(file, Path.Combine(to, Path.GetRelativePath(from, file)));
    }
}

public sealed class YamlDocumentEditTests
{
    private const string Shop = """
        # yaml-language-server: $schema=../../schemas/shop.schema.json
        id: shop.test
        name: Test Wares   # shown to editors
        # First the potions.
        stock:
          - item_id: item.a
          - { item_id: item.b, price: 12 }
        """;

    [Fact]
    public void A_value_change_is_made_in_place_keeping_comments_order_and_layout()
    {
        var doc = YamlDocumentEdit.ToJson(Shop);
        doc["name"] = "Better Wares";
        doc["stock"]![1]!["price"] = 15;

        var (text, mode) = YamlDocumentEdit.Apply(Shop, doc);

        mode.ShouldBe(YamlWriteMode.InPlace);
        text.ShouldBe(Shop.Replace("name: Test Wares", "name: Better Wares").Replace("price: 12", "price: 15"));
    }

    [Fact]
    public void Line_endings_and_quoting_are_kept()
    {
        var crlf = "id: shop.test\r\nname: \"Quoted Name\"\r\n# note\r\nstock: []\r\n";
        var doc = YamlDocumentEdit.ToJson(crlf);
        doc["name"] = "New: with a colon";

        var (text, mode) = YamlDocumentEdit.Apply(crlf, doc);

        mode.ShouldBe(YamlWriteMode.InPlace);
        text.ShouldBe("id: shop.test\r\nname: \"New: with a colon\"\r\n# note\r\nstock: []\r\n");
        YamlDocumentEdit.ToJson(text)["name"]!.ToString().ShouldBe("New: with a colon");
    }

    [Fact]
    public void A_shape_change_rewrites_the_document_in_the_old_key_order_and_loses_comments_but_the_schema_line()
    {
        var doc = YamlDocumentEdit.ToJson(Shop);
        ((JsonArray)doc["stock"]!).Add(new JsonObject { ["item_id"] = "item.c" });

        var (text, mode) = YamlDocumentEdit.Apply(Shop, doc);

        mode.ShouldBe(YamlWriteMode.Rewritten);
        text.ShouldStartWith("# yaml-language-server: $schema=../../schemas/shop.schema.json\nid: shop.test\nname: Test Wares\nstock:\n");
        text.ShouldNotContain("First the potions");
        YamlDocumentEdit.ToJson(text).ToJsonString().ShouldBe(doc.ToJsonString());
    }

    [Fact]
    public void Plain_scalars_are_typed_and_quoted_ones_stay_strings()
    {
        var doc = YamlDocumentEdit.ToJson("a: 3\nb: \"3\"\nc: true\nd: 1.5\ne: ~\nf: text\n");

        doc.ToJsonString().ShouldBe("{\"a\":3,\"b\":\"3\",\"c\":true,\"d\":1.5,\"e\":null,\"f\":\"text\"}");
    }
}

public sealed class ContentEditorTests : IDisposable
{
    private readonly EditorRig _rig = new();

    public void Dispose() => _rig.Dispose();

    [Fact]
    public void The_kinds_come_from_the_schemas()
    {
        _rig.Editor.Kinds.Select(k => k.Kind).ShouldBe(ContentEditor.FirstKinds);
        _rig.Editor.Kinds.Single(k => k.Kind == "items").ShouldBe(new EditorKind("items", "item", "ItemDef"));

        using var without = new EditorRig(dropSchema: "item");
        without.Editor.Kinds.Select(k => k.Kind).ShouldNotContain("items");   // no schema, no form
    }

    [Fact]
    public void A_valid_save_writes_that_one_file_in_place()
    {
        var before = _rig.Snapshot();
        var (doc, version, _) = _rig.Editor.Load("items/iron_blade.yaml");
        doc!["name"] = "Meadow Iron Blade";

        var result = _rig.Editor.Save("items/iron_blade.yaml", doc, version);

        result.Saved.ShouldBeTrue(string.Join("\n", result.Issues));
        result.Mode.ShouldBe(YamlWriteMode.InPlace);
        var after = _rig.Snapshot();
        after.Keys.ShouldBe(before.Keys, ignoreOrder: true);   // no temporary file left behind
        after.Where(p => before[p.Key] != p.Value).Select(p => p.Key.Replace('\\', '/')).ShouldBe(["items/iron_blade.yaml"]);
        var text = _rig.Read("items/iron_blade.yaml");
        text.ShouldStartWith("# yaml-language-server: $schema=../../schemas/item.schema.json");
        text.ShouldContain("name: Meadow Iron Blade");
        text.Replace("Meadow Iron Blade", "Iron Blade").ShouldBe(File.ReadAllText(Path.Combine(TestGrids.RepoRoot(), "content/items/iron_blade.yaml")));   // nothing else moved
    }

    [Fact]
    public void A_save_that_breaks_a_reference_is_refused_and_writes_nothing()
    {
        var before = _rig.Snapshot();
        var (doc, version, _) = _rig.Editor.Load("shops/meadow_goods.yaml");
        doc!["stock"]![1]!["item_id"] = "item.moonpetal_tonic";

        var result = _rig.Editor.Save("shops/meadow_goods.yaml", doc, version);

        result.Saved.ShouldBeFalse();
        result.Problem.ShouldBeNull();
        result.Issues.ShouldContain(i => i.Severity == ValidationSeverity.Error && i.Code == "ref.missing" && i.Message.Contains("item.moonpetal_tonic") && i.File == "shops/meadow_goods.yaml");
        _rig.Snapshot().ShouldBe(before);
    }

    [Fact]
    public void A_save_against_the_schema_is_refused_too()
    {
        var before = _rig.Snapshot();
        var (doc, version, _) = _rig.Editor.Load("items/iron_blade.yaml");
        doc!["kind"] = "not_a_kind";

        _rig.Editor.Save("items/iron_blade.yaml", doc, version).Saved.ShouldBeFalse();

        _rig.Snapshot().ShouldBe(before);
    }

    [Fact]
    public void The_id_paths_outside_the_kinds_and_stale_versions_are_refused()
    {
        var (doc, version, _) = _rig.Editor.Load("items/iron_blade.yaml");
        var renamed = (JsonObject)doc!.DeepClone();
        renamed["id"] = "item.other_blade";
        _rig.Editor.Save("items/iron_blade.yaml", renamed, version).Problem!.ShouldContain("id cannot be changed");

        _rig.Editor.Save("../balance.yaml", doc, null).Problem.ShouldNotBeNull();
        _rig.Editor.Save("items/../balance.yaml", doc, null).Problem.ShouldNotBeNull();
        _rig.Editor.Save("maps/starter_meadow.yaml", doc, null).Problem.ShouldNotBeNull();
        _rig.Editor.Load("formulas.yaml").Problem.ShouldNotBeNull();

        File.AppendAllText(Path.Combine(_rig.Content, "items/iron_blade.yaml"), "# edited by hand\n");
        _rig.Editor.Save("items/iron_blade.yaml", doc, version).Problem!.ShouldContain("changed on disk");
    }

    [Fact]
    public void A_write_replaces_the_file_whole_and_leaves_no_temporary_file()
    {
        var path = Path.Combine(_rig.Root, "atomic.yaml");
        File.WriteAllText(path, "old: 1\n");

        ContentEditor.WriteAtomically(path, "new: 2\n");

        File.ReadAllText(path).ShouldBe("new: 2\n");
        Directory.GetFiles(_rig.Root, "atomic.yaml*").ShouldBe([path]);
    }

    [Fact]
    public void References_offer_the_ids_that_exist_by_namespace()
    {
        var ids = _rig.Editor.Ids();

        ids["item"].ShouldContain("item.iron_blade");
        ids["monster"].ShouldContain("monster.gel_slime");
        ids["model"].ShouldNotBeEmpty();   // asset keys from the theme's manifest
        ids.Values.SelectMany(v => v).ShouldAllBe(id => id.Contains('.'));
    }
}

public sealed class EditorServerTests
{
    [Fact]
    public void Only_a_loopback_address_is_allowed()
    {
        EditorServer.Problem("127.0.0.1").ShouldBeNull();
        EditorServer.Problem("::1").ShouldBeNull();
        EditorServer.Problem("0.0.0.0").ShouldNotBeNull();
        EditorServer.Problem("::").ShouldNotBeNull();
        EditorServer.Problem("192.168.1.20").ShouldNotBeNull();
        EditorServer.Problem("localhost").ShouldNotBeNull();   // a name could point anywhere
    }

    [Fact]
    public async Task It_refuses_to_start_on_any_other_address()
    {
        using var rig = new EditorRig();

        await Should.ThrowAsync<ArgumentException>(() => EditorServer.StartAsync(rig.Editor, "0.0.0.0", 0));
    }

    [Fact]
    public void The_command_refuses_a_host_that_is_not_loopback()
    {
        var command = Game.Tools.Commands.EditorCommand.Create();

        command.Parse(["--host", "0.0.0.0", "--port", "0"]).Invoke().ShouldBe(2);
    }

    [Fact]
    public async Task It_binds_loopback_and_serves_the_page_and_the_api_to_its_own_page_only()
    {
        using var rig = new EditorRig();
        await using var server = await EditorServer.StartAsync(rig.Editor, "127.0.0.1", 0);
        server.Address.Host.ShouldBe("127.0.0.1");
        using var http = new HttpClient { BaseAddress = server.Address };

        (await http.GetStringAsync("/")).ShouldContain("<title>Content editor</title>");
        (await http.GetStringAsync("/api/kinds")).ShouldContain("\"kind\":\"items\"");

        using var other = new HttpRequestMessage(HttpMethod.Get, "/api/kinds");
        other.Headers.Host = "editor.example.com";
        (await http.SendAsync(other)).StatusCode.ShouldBe(HttpStatusCode.MisdirectedRequest);

        var body = "{\"path\":\"items/iron_blade.yaml\",\"document\":{}}";
        (await http.PostAsync("/api/doc", new StringContent(body, Encoding.UTF8, "application/json"))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);   // no X-Editor header: not the page
        using var form = new HttpRequestMessage(HttpMethod.Post, "/api/doc") { Content = new StringContent(body, Encoding.UTF8, "text/plain") };
        form.Headers.Add("X-Editor", "1");
        (await http.SendAsync(form)).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var load = JsonNode.Parse(await http.GetStringAsync("/api/doc?path=items/iron_blade.yaml"))!;
        load["document"]!["name"] = "Meadow Iron Blade";
        using var save = new HttpRequestMessage(HttpMethod.Post, "/api/doc") { Content = new StringContent(load.ToJsonString(), Encoding.UTF8, "application/json") };
        save.Headers.Add("X-Editor", "1");
        var saved = await http.SendAsync(save);
        saved.StatusCode.ShouldBe(HttpStatusCode.OK, await saved.Content.ReadAsStringAsync());
        rig.Read("items/iron_blade.yaml").ShouldContain("name: Meadow Iron Blade");
    }
}
