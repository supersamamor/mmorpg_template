using Game.Client.Core.Bundle;
using Game.Client.Core.Theme;
using Game.Content.ClientExport;
using Game.Shared.ClientContent;

namespace Game.Tests.Client;

/// <summary>
/// T-711, the pure part of textured characters in one draw call: which textures are the same image (<see cref="ImageIdentity"/>, what the merge groups by), and which
/// models are preloaded (<see cref="ModelPreload"/>: job models only when a body is missing). The merge itself is checked in the engine by <c>run-e2e.ps1 -Run textured</c>.
/// </summary>
public class TexturedLookTests
{
    private static readonly byte[] Atlas = [1, 2, 3, 4, 5, 6, 7, 8];
    private static readonly byte[] Other = [8, 7, 6, 5, 4, 3, 2, 1];

    [Fact]
    public void The_same_pixels_in_two_files_are_one_image_and_different_pixels_are_not()
    {
        var warnings = new List<string>();
        var images = new ImageIdentity(warnings.Add);
        var body = images.KeyOf(1, "body_0.png", () => ("file", (byte[])Atlas.Clone()));
        var face = images.KeyOf(2, "face_0.png", () => ("file", (byte[])Atlas.Clone())); // another texture object, another copy of the bytes
        var hair = images.KeyOf(3, "hair_0.png", () => ("file", Other));
        body.ShouldNotBeNull();
        face.ShouldBe(body);
        hair.ShouldNotBeNull();
        hair.ShouldNotBe(body);
        (images.Read, images.Distinct).ShouldBe((3, 2));
        warnings.ShouldBeEmpty();
    }

    [Fact]
    public void The_kind_of_content_is_part_of_the_key()
    {
        var images = new ImageIdentity(_ => { });
        images.KeyOf(1, "a", () => ("file", Atlas)).ShouldNotBe(images.KeyOf(2, "b", () => ("pixels:2x4:5:False", Atlas)));
        images.KeyOf(3, "c", () => ("pixels:2x4:5:False", Atlas)).ShouldBe(images.KeyOf(4, "d", () => ("pixels:2x4:5:False", Atlas)));
        ImageIdentity.Key("file", Atlas).ShouldStartWith("file:");
    }

    [Fact]
    public void Each_texture_is_read_once()
    {
        var reads = 0;
        var images = new ImageIdentity(_ => { });
        for (var i = 0; i < 5; i++) images.KeyOf(7, "body_0.png", () => { reads++; return ("file", Atlas); });
        reads.ShouldBe(1);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("throws")]
    public void A_missing_or_broken_texture_has_no_key_and_warns_once(string how)
    {
        var warnings = new List<string>();
        var images = new ImageIdentity(warnings.Add);
        Func<(string, byte[])?> content = how switch
        {
            "missing" => () => null,
            "empty" => () => ("file", Array.Empty<byte>()),
            _ => () => throw new InvalidOperationException("truncated"),
        };
        for (var i = 0; i < 3; i++) images.KeyOf(9, "broken_0.png", content).ShouldBeNull();
        var warning = warnings.ShouldHaveSingleItem();
        warning.ShouldContain("broken_0.png");
        warning.ShouldContain("surface of its own");
        if (how == "throws") warning.ShouldContain("truncated");
        images.Distinct.ShouldBe(0);
        images.KeyOf(10, "fine_0.png", () => ("file", Atlas)).ShouldNotBeNull(); // the others still merge
    }

    [Fact]
    public void Clearing_forgets_the_textures()
    {
        var reads = 0;
        var images = new ImageIdentity(_ => { });
        images.KeyOf(1, "a", () => { reads++; return ("file", Atlas); });
        images.Clear();
        images.KeyOf(1, "a", () => { reads++; return ("file", Atlas); });
        (reads, images.Read, images.Distinct).ShouldBe((2, 1, 1));
    }

    // ---- job models only when needed (ADR-002 row 314) ----

    private static DisplayBundle Bundle(bool genders = true)
    {
        var jobs = new[]
        {
            new ClientJob("job.a", "A", null, null, [], "model.char.job_a", null, []),
            new ClientJob("job.b", "B", null, null, [], "model.char.shared", null, []),
        };
        var monsters = new[] { new ClientMonster("monster.m", "M", "model.monster.m", null) };
        var npcs = new[] { new ClientNpc("npc.n", "N", null, "model.char.shared", null) }; // an NPC drawn like a job: it must stay
        var appearance = genders
            ? new ClientAppearance([new ClientGender("male", "Male", "model.body.m", [new("face.m", "F", "model.face.m")], [new("hair.m", "H", "model.hair.m")], "face.m", "hair.m")],
                [new("skin.a", "A", "#804020")], "skin.a", [new("haircolor.a", "A", "#202020")], "haircolor.a", "job.a")
            : new ClientAppearance([], [], "", [], "", "job.a");
        var bundle = new ClientBundle(ClientBundle.CurrentFormatVersion, [], [], [], [], jobs, [], monsters, npcs, [], [], appearance);
        DisplayBundle.TryRead(ClientBundleWriter.Export(bundle).FileBytes, out var read, out _).ShouldBe(BundleStatus.Ok);
        return read!;
    }

    [Fact]
    public void Job_models_are_not_preloaded_while_every_body_is_there()
    {
        var bundle = Bundle();
        bundle.IsJobModelOnly("model.char.job_a").ShouldBeTrue();
        bundle.IsJobModelOnly("model.char.shared").ShouldBeFalse();
        bundle.IsJobModelOnly("model.monster.m").ShouldBeFalse();
        ModelPreload.JobModelsUnneeded(bundle, true, _ => true).ShouldBeTrue();
        ModelPreload.Keys(bundle, true, _ => true).ShouldBe(["model.char.shared", "model.monster.m"]);
    }

    [Fact]
    public void Job_models_are_preloaded_when_a_body_is_missing_or_looks_are_not_drawn()
    {
        var bundle = Bundle();
        string[] all = ["model.char.job_a", "model.char.shared", "model.monster.m"];
        ModelPreload.Keys(bundle, true, body => body != "model.body.m").ShouldBe(all);
        ModelPreload.Keys(bundle, false, _ => true).ShouldBe(all);
        ModelPreload.Keys(Bundle(genders: false), true, _ => true).ShouldBe(all);
    }
}
