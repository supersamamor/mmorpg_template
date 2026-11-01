using Game.Content.Registry;
using Game.Shared.Definitions;

namespace Game.Content.Validation.Rules;

/// <summary>The kind of definition an id is expected to point at.</summary>
internal enum RefKind { Job, Skill, Item, Monster, Status, Quest, Npc, Dialogue, Drop, Map, ExpTable }

/// <summary>An id used inside a definition. <see cref="Path"/> is in YAML key naming.</summary>
internal readonly record struct IdRef(string Owner, string Path, string Id, RefKind Kind);

/// <summary>An asset key used inside a definition. <see cref="Required"/> keys must not be empty.</summary>
internal readonly record struct AssetRef(string Owner, string Path, string? Key, bool Required);

/// <summary>A stat modifier used inside a definition.</summary>
internal readonly record struct ModifierRef(string Owner, string Path, ModifierDef Modifier);

/// <summary>An effect found inside a definition (nested area effects are flattened).</summary>
internal readonly record struct EffectRef(string Owner, string Path, EffectDef Effect);

/// <summary>
/// Single traversal of every definition, so each rule asks "what ids / assets / modifiers / effects are there"
/// instead of re-walking the records. Paths use YAML key naming. Adding a field to a definition that holds an
/// id, asset key, modifier or effect list means adding it here once.
/// </summary>
internal static class ContentWalker
{
    public static IEnumerable<EffectRef> Effects(ContentDatabase db)
    {
        foreach (var skill in db.Skills.All)
            foreach (var e in Flatten(skill.Id, "effects", skill.Effects)) yield return e;
        foreach (var item in db.Items.All)
            if (item.Consumable is { } c)
                foreach (var e in Flatten(item.Id, "consumable.effects", c.Effects)) yield return e;
        foreach (var status in db.Statuses.All)
        {
            foreach (var e in Flatten(status.Id, "on_apply", status.OnApply)) yield return e;
            foreach (var e in Flatten(status.Id, "on_tick", status.OnTick)) yield return e;
        }
    }

    private static IEnumerable<EffectRef> Flatten(string owner, string path, IReadOnlyList<EffectDef> effects)
    {
        for (var i = 0; i < effects.Count; i++)
        {
            var p = $"{path}[{i}]";
            yield return new EffectRef(owner, p, effects[i]);
            if (effects[i] is AreaEffect area)
                foreach (var nested in Flatten(owner, p + ".effects", area.Effects)) yield return nested;
        }
    }

    public static IEnumerable<ModifierRef> Modifiers(ContentDatabase db)
    {
        foreach (var job in db.Jobs.All)
            for (var i = 0; i < job.StatBonuses.Count; i++)
                yield return new ModifierRef(job.Id, $"stat_bonuses[{i}]", job.StatBonuses[i]);
        foreach (var skill in db.Skills.All)
            for (var i = 0; i < skill.PassiveModifiers.Count; i++)
                yield return new ModifierRef(skill.Id, $"passive_modifiers[{i}]", skill.PassiveModifiers[i]);
        foreach (var item in db.Items.All)
        {
            if (item.Equip is { } eq)
                for (var i = 0; i < eq.Modifiers.Count; i++)
                    yield return new ModifierRef(item.Id, $"equip.modifiers[{i}]", eq.Modifiers[i]);
            if (item.Card is { } card)
                for (var i = 0; i < card.Modifiers.Count; i++)
                    yield return new ModifierRef(item.Id, $"card.modifiers[{i}]", card.Modifiers[i]);
        }
        foreach (var e in Effects(db))
            if (e.Effect is ModifyStatEffect m) yield return new ModifierRef(e.Owner, e.Path + ".modifier", m.Modifier);
    }

    public static IEnumerable<IdRef> Refs(ContentDatabase db)
    {
        foreach (var job in db.Jobs.All)
        {
            if (!string.IsNullOrEmpty(job.ParentJobId)) yield return new(job.Id, "parent_job_id", job.ParentJobId!, RefKind.Job);
            yield return new(job.Id, "job_exp_table_id", job.JobExpTableId, RefKind.ExpTable);
            for (var i = 0; i < job.Requirements.QuestIds.Count; i++)
                yield return new(job.Id, $"requirements.quest_ids[{i}]", job.Requirements.QuestIds[i], RefKind.Quest);
            for (var i = 0; i < job.Skills.Count; i++)
            {
                yield return new(job.Id, $"skills[{i}].skill_id", job.Skills[i].SkillId, RefKind.Skill);
                for (var j = 0; j < job.Skills[i].Requires.Count; j++)
                    yield return new(job.Id, $"skills[{i}].requires[{j}].skill_id", job.Skills[i].Requires[j].SkillId, RefKind.Skill);
            }
        }

        foreach (var e in Effects(db))
        {
            switch (e.Effect)
            {
                case ApplyStatusEffect a: yield return new(e.Owner, e.Path + ".status_id", a.StatusId, RefKind.Status); break;
                case RemoveStatusEffect r:
                    for (var i = 0; i < r.StatusIds.Count; i++)
                        yield return new(e.Owner, $"{e.Path}.status_ids[{i}]", r.StatusIds[i], RefKind.Status);
                    break;
                case SummonEffect s: yield return new(e.Owner, e.Path + ".monster_id", s.MonsterId, RefKind.Monster); break;
                case TeleportEffect t when !string.IsNullOrEmpty(t.MapId): yield return new(e.Owner, e.Path + ".map_id", t.MapId!, RefKind.Map); break;
            }
        }

        foreach (var item in db.Items.All)
            if (item.Equip is { } eq)
                for (var i = 0; i < eq.JobIds.Count; i++)
                    yield return new(item.Id, $"equip.job_ids[{i}]", eq.JobIds[i], RefKind.Job);

        foreach (var m in db.Monsters.All)
        {
            for (var i = 0; i < m.DropTableIds.Count; i++)
                yield return new(m.Id, $"drop_table_ids[{i}]", m.DropTableIds[i], RefKind.Drop);
            for (var i = 0; i < m.Ai.Skills.Count; i++)
                yield return new(m.Id, $"ai.skills[{i}].skill_id", m.Ai.Skills[i].SkillId, RefKind.Skill);
        }

        foreach (var d in db.Drops.All)
            for (var i = 0; i < d.Entries.Count; i++)
                yield return new(d.Id, $"entries[{i}].item_id", d.Entries[i].ItemId, RefKind.Item);

        foreach (var q in db.Quests.All)
        {
            if (!string.IsNullOrEmpty(q.GiverNpcId)) yield return new(q.Id, "giver_npc_id", q.GiverNpcId!, RefKind.Npc);
            for (var i = 0; i < q.Prerequisites.JobIds.Count; i++)
                yield return new(q.Id, $"prerequisites.job_ids[{i}]", q.Prerequisites.JobIds[i], RefKind.Job);
            for (var i = 0; i < q.Prerequisites.QuestIds.Count; i++)
                yield return new(q.Id, $"prerequisites.quest_ids[{i}]", q.Prerequisites.QuestIds[i], RefKind.Quest);
            for (var s = 0; s < q.Steps.Count; s++)
                for (var o = 0; o < q.Steps[s].Objectives.Count; o++)
                {
                    var p = $"steps[{s}].objectives[{o}]";
                    switch (q.Steps[s].Objectives[o])
                    {
                        case KillObjective k: yield return new(q.Id, p + ".monster_id", k.MonsterId, RefKind.Monster); break;
                        case CollectObjective c: yield return new(q.Id, p + ".item_id", c.ItemId, RefKind.Item); break;
                        case TalkObjective t: yield return new(q.Id, p + ".npc_id", t.NpcId, RefKind.Npc); break;
                        case ReachObjective r: yield return new(q.Id, p + ".map_id", r.MapId, RefKind.Map); break;
                        case JobObjective j: yield return new(q.Id, p + ".job_id", j.JobId, RefKind.Job); break;
                        case UseItemObjective u: yield return new(q.Id, p + ".item_id", u.ItemId, RefKind.Item); break;
                        case EquipObjective eq: yield return new(q.Id, p + ".item_id", eq.ItemId, RefKind.Item); break;
                    }
                }
            for (var i = 0; i < q.Rewards.Count; i++)
            {
                var p = $"rewards[{i}]";
                switch (q.Rewards[i])
                {
                    case ItemReward r: yield return new(q.Id, p + ".item_id", r.ItemId, RefKind.Item); break;
                    case UnlockJobReward u: yield return new(q.Id, p + ".job_id", u.JobId, RefKind.Job); break;
                    case StatusReward s: yield return new(q.Id, p + ".status_id", s.StatusId, RefKind.Status); break;
                }
            }
        }

        foreach (var n in db.Npcs.All)
            if (!string.IsNullOrEmpty(n.DialogueId)) yield return new(n.Id, "dialogue_id", n.DialogueId!, RefKind.Dialogue);

        foreach (var d in db.Dialogue.All)
            foreach (var (key, node) in d.Nodes)
            {
                if (!string.IsNullOrEmpty(node.SpeakerNpcId)) yield return new(d.Id, $"nodes.{key}.speaker_npc_id", node.SpeakerNpcId!, RefKind.Npc);
                for (var i = 0; i < node.Choices.Count; i++)
                    if (!string.IsNullOrEmpty(node.Choices[i].StartQuestId))
                        yield return new(d.Id, $"nodes.{key}.choices[{i}].start_quest_id", node.Choices[i].StartQuestId!, RefKind.Quest);
            }
    }

    public static IEnumerable<AssetRef> Assets(ContentDatabase db)
    {
        foreach (var j in db.Jobs.All)
        {
            yield return new(j.Id, "model_asset_key", j.ModelAssetKey, false);
            yield return new(j.Id, "icon_asset_key", j.IconAssetKey, false);
        }
        foreach (var s in db.Skills.All)
        {
            yield return new(s.Id, "icon_asset_key", s.IconAssetKey, false);
            yield return new(s.Id, "vfx_asset_key", s.VfxAssetKey, false);
            yield return new(s.Id, "sfx_asset_key", s.SfxAssetKey, false);
        }
        foreach (var i in db.Items.All)
        {
            yield return new(i.Id, "icon_asset_key", i.IconAssetKey, false);
            yield return new(i.Id, "ground_asset_key", i.GroundAssetKey, false);
            yield return new(i.Id, "use_sfx", i.UseSfx, false);
            yield return new(i.Id, "equip_sfx", i.EquipSfx, false);
            if (i.Equip is { } eq) yield return new(i.Id, "equip.visual_asset_key", eq.VisualAssetKey, false);
        }
        foreach (var m in db.Monsters.All)
        {
            yield return new(m.Id, "model_asset_key", m.ModelAssetKey, false);
            yield return new(m.Id, "sounds.aggro", m.Sounds?.Aggro, false);
            yield return new(m.Id, "sounds.hit", m.Sounds?.Hit, false);
            yield return new(m.Id, "sounds.die", m.Sounds?.Die, false);
        }
        foreach (var s in db.Statuses.All)
        {
            yield return new(s.Id, "icon_asset_key", s.IconAssetKey, false);
            yield return new(s.Id, "vfx_asset_key", s.VfxAssetKey, false);
        }
        foreach (var n in db.Npcs.All)
        {
            yield return new(n.Id, "model_asset_key", n.ModelAssetKey, false);
            yield return new(n.Id, "portrait_asset_key", n.PortraitAssetKey, false);
        }
        foreach (var m in db.Maps.All)
        {
            yield return new(m.Id, "scene_asset_key", m.SceneAssetKey, true);
            yield return new(m.Id, "music_asset_key", m.MusicAssetKey, false);
            yield return new(m.Id, "ambience_asset_key", m.AmbienceAssetKey, false);
        }
        foreach (var d in db.Dialogue.All)
            foreach (var (key, node) in d.Nodes)
                yield return new(d.Id, $"nodes.{key}.portrait_asset_key", node.PortraitAssetKey, false);
    }
}
