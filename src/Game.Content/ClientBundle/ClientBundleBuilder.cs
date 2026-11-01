using System.Globalization;
using Game.Content.Registry;
using Game.Shared.ClientContent;
using Game.Shared.Definitions;
using Game.Shared.Vocabulary;
using Game.Shared.World;

namespace Game.Content.ClientExport;

/// <summary>
/// Builds the client bundle from the loaded content (ARCHITECTURE §5.4). Every field is copied by hand into a client record: this is the
/// whitelist, so nothing reaches the client unless it is written here. Output is ordered by id, so it does not depend on load order.
/// </summary>
public static class ClientBundleBuilder
{
    /// <summary>Builds the bundle for <paramref name="content"/>.</summary>
    public static ClientBundle Build(ContentDatabase content) => new(
        ClientBundle.CurrentFormatVersion,
        content.Elements.Multipliers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList(),
        Stats.All.Ordered.ToList(),
        content.Items.All.Select(Item).ToList(),
        content.Skills.All.Select(Skill).ToList(),
        content.Jobs.All.Select(Job).ToList(),
        content.Statuses.All.Select(s => new ClientStatus(s.Id, s.Name, s.Description, s.Debuff, s.IconAssetKey, s.VfxAssetKey)).ToList(),
        content.Monsters.All.Select(Monster).ToList(),
        content.Npcs.All.Select(n => new ClientNpc(n.Id, n.Name, n.Title, n.DialogueId, n.ModelAssetKey, n.PortraitAssetKey)).ToList(),
        content.Dialogue.All.Select(Dialogue).ToList(),
        content.Quests.All.Select(q => new ClientQuest(q.Id, q.Name, q.Description, q.GiverNpcId, q.Steps.Select(s => s.Description ?? "").ToList())).ToList(),
        content.Maps.All.Select(m => Map(m, content.GetGrid(m.Id))).ToList());

    private static ClientMonster Monster(MonsterDef monster)
    {
        var s = monster.Sounds;
        var sounds = s is null || (s.Aggro is null && s.Hit is null && s.Die is null) ? null : new ClientMonsterSounds(s.Aggro, s.Hit, s.Die);
        return new ClientMonster(monster.Id, monster.Name, monster.ModelAssetKey, sounds);
    }

    private static ClientItem Item(ItemDef item)
    {
        // The kind enum's names are the vocabulary terms in PascalCase (single words), so lower-casing gives the ItemKinds term.
        var kind = item.Kind.ToString().ToLowerInvariant();
        var slots = item.Equip?.Slots ?? item.Card?.Slots ?? [];
        return new ClientItem(item.Id, item.Name, item.Description, kind, item.IconAssetKey, item.GroundAssetKey,
            slots.ToList(), item.Equip?.WeaponType, item.Equip?.VisualAssetKey, item.UseSfx, item.EquipSfx);
    }

    private static ClientSkill Skill(SkillDef skill)
    {
        var area = FindArea(skill.Effects);
        // A cast time that is a plain whole number is display data; anything else is a formula and stays on the server.
        int? cast = int.TryParse(skill.CastTimeMs.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var ms) ? ms : null;
        return new ClientSkill(skill.Id, skill.Name, skill.Description, skill.MaxLevel, skill.Passive,
            skill.Target.Range, skill.Target.Ground, skill.Target.Friendly,
            area is null ? null : ClientAreaShapes.Square, area?.Radius ?? 0, cast,
            skill.AnimationId, skill.IconAssetKey, skill.VfxAssetKey, skill.SfxAssetKey);
    }

    /// <summary>The first top-level <c>area</c> effect (the sample content wraps its effects in one).</summary>
    private static AreaEffect? FindArea(IReadOnlyList<EffectDef> effects)
    {
        foreach (var effect in effects)
        {
            if (effect is AreaEffect area) return area;
        }
        return null;
    }

    private static ClientJob Job(JobDef job) => new(job.Id, job.Name, job.Description, job.ParentJobId, job.WeaponTypes.ToList(),
        job.ModelAssetKey, job.IconAssetKey,
        job.Skills.Select(s => new ClientJobSkill(s.SkillId, s.MaxLevel,
            s.Requires.Select(r => new ClientSkillRequirement(r.SkillId, r.Level)).ToList())).ToList());

    private static ClientDialogue Dialogue(DialogueDef dialogue) => new(dialogue.Id, dialogue.Start,
        dialogue.Nodes.OrderBy(n => n.Key, StringComparer.Ordinal).Select(n => new ClientDialogueNode(
            n.Key, n.Value.SpeakerNpcId, n.Value.PortraitAssetKey, n.Value.Text, n.Value.Next,
            n.Value.Choices.Select(c => new ClientDialogueChoice(c.Text, c.Next, c.StartQuestId)).ToList())).ToList());

    private static ClientMap Map(MapDef map, MapGrid grid)
    {
        var cells = new byte[grid.Width * grid.Height];
        for (var y = 0; y < grid.Height; y++)
        {
            for (var x = 0; x < grid.Width; x++)
            {
                var flags = (byte)0;
                if (grid.IsWalkable(x, y)) flags |= CellFlagBits.Walkable;
                if (grid.BlocksLos(x, y)) flags |= CellFlagBits.BlocksLos;
                cells[y * grid.Width + x] = flags;
            }
        }
        return new ClientMap(map.Id, map.Name, map.SceneAssetKey, map.MusicAssetKey, map.AmbienceAssetKey, grid.Width, grid.Height, Convert.ToBase64String(cells),
            grid.Warps.Select(w => new ClientWarp(w.Cell, w.TargetMapId, w.TargetCell)).ToList(),
            grid.Npcs.Select(n => new ClientNpcPlacement(n.NpcId, n.Cell, n.Facing)).ToList(),
            grid.Zones.Select(z => new ClientZone(z.Kind, z.Area)).ToList());
    }
}
