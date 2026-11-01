using System.Collections.Generic;
using Game.Shared.World;

namespace Game.Shared.ClientContent
{
    // The client bundle's records (ARCHITECTURE §5.4). A whitelist: every field here is display data, copied on purpose by
    // Game.Content/ClientBundle/ClientBundleBuilder. A field added to a server definition never appears here by itself.
    // Optional text and keys are null when the content has none; the JSON writer leaves them out.

    /// <summary>An item as the client shows it.</summary>
    /// <param name="Id">Item id.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Description">Display description, or null.</param>
    /// <param name="Kind">An <c>ItemKinds</c> term.</param>
    /// <param name="IconAssetKey">UI icon key, or null.</param>
    /// <param name="GroundAssetKey">Ground visual key, or null.</param>
    /// <param name="Slots">Equipment slots occupied (equip) or accepted (card); empty for other kinds.</param>
    /// <param name="WeaponType">A <c>WeaponTypes</c> term for weapons, else null.</param>
    /// <param name="VisualAssetKey">Worn visual key (equip), or null.</param>
    public sealed record ClientItem(
        string Id, string Name, string? Description, string Kind, string? IconAssetKey, string? GroundAssetKey,
        IReadOnlyList<string> Slots, string? WeaponType, string? VisualAssetKey);

    /// <summary>A skill's display and targeting info.</summary>
    /// <param name="Id">Skill id.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Description">Display description, or null.</param>
    /// <param name="MaxLevel">Highest level.</param>
    /// <param name="Passive">True for a passive skill.</param>
    /// <param name="TargetRange">Range in cells; 0 targets the user.</param>
    /// <param name="TargetGround">True to target a ground cell.</param>
    /// <param name="TargetFriendly">True to target allies.</param>
    /// <param name="AreaShape">A <see cref="ClientAreaShapes"/> term when the skill affects an area, else null.</param>
    /// <param name="AreaRadius">Area radius in cells (0 when <paramref name="AreaShape"/> is null).</param>
    /// <param name="CastTimeMs">Cast time when the content gives a fixed number, else null (a formula is server-only).</param>
    /// <param name="AnimationId">An <c>AnimationIds</c> term, or null.</param>
    /// <param name="IconAssetKey">UI icon key, or null.</param>
    /// <param name="VfxAssetKey">Effect key, or null.</param>
    /// <param name="SfxAssetKey">Sound key, or null.</param>
    public sealed record ClientSkill(
        string Id, string Name, string? Description, int MaxLevel, bool Passive, int TargetRange, bool TargetGround, bool TargetFriendly,
        string? AreaShape, int AreaRadius, int? CastTimeMs, string? AnimationId, string? IconAssetKey, string? VfxAssetKey, string? SfxAssetKey);

    /// <summary>Area shape terms of <see cref="ClientSkill.AreaShape"/>.</summary>
    public static class ClientAreaShapes
    {
        /// <summary>Every cell within the radius by Chebyshev distance.</summary>
        public const string Square = "square";
    }

    /// <summary>One entry of a job's skill tree.</summary>
    /// <param name="SkillId">Skill id.</param>
    /// <param name="MaxLevel">Highest level learnable in the job, or null for the skill's own maximum.</param>
    /// <param name="Requires">Skills to learn first.</param>
    public sealed record ClientJobSkill(string SkillId, int? MaxLevel, IReadOnlyList<ClientSkillRequirement> Requires);

    /// <summary>A skill at a minimum level.</summary>
    /// <param name="SkillId">Skill id.</param>
    /// <param name="Level">Minimum level.</param>
    public sealed record ClientSkillRequirement(string SkillId, int Level);

    /// <summary>A job and its skill tree layout.</summary>
    /// <param name="Id">Job id.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Description">Display description, or null.</param>
    /// <param name="ParentJobId">The job this one changes from, or null.</param>
    /// <param name="WeaponTypes">Usable weapon types (empty means all).</param>
    /// <param name="ModelAssetKey">Body model key, or null.</param>
    /// <param name="IconAssetKey">UI icon key, or null.</param>
    /// <param name="Skills">The skill tree, in content order.</param>
    public sealed record ClientJob(
        string Id, string Name, string? Description, string? ParentJobId, IReadOnlyList<string> WeaponTypes, string? ModelAssetKey,
        string? IconAssetKey, IReadOnlyList<ClientJobSkill> Skills);

    /// <summary>A status as the client shows it.</summary>
    /// <param name="Id">Status id.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Description">Display description, or null.</param>
    /// <param name="Debuff">True for a debuff, false for a buff.</param>
    /// <param name="IconAssetKey">UI icon key, or null.</param>
    /// <param name="VfxAssetKey">Looping effect key, or null.</param>
    public sealed record ClientStatus(string Id, string Name, string? Description, bool Debuff, string? IconAssetKey, string? VfxAssetKey);

    /// <summary>A monster's look and name. No stats, drops or AI.</summary>
    /// <param name="Id">Monster id (the <c>display_key</c> of its view records).</param>
    /// <param name="Name">Display name.</param>
    /// <param name="ModelAssetKey">Model key, or null.</param>
    public sealed record ClientMonster(string Id, string Name, string? ModelAssetKey);

    /// <summary>An NPC's name and look.</summary>
    /// <param name="Id">NPC id.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Title">Title under the name, or null.</param>
    /// <param name="DialogueId">Dialogue started on talk, or null.</param>
    /// <param name="ModelAssetKey">Model key, or null.</param>
    /// <param name="PortraitAssetKey">Portrait key, or null.</param>
    public sealed record ClientNpc(string Id, string Name, string? Title, string? DialogueId, string? ModelAssetKey, string? PortraitAssetKey);

    /// <summary>A dialogue choice.</summary>
    /// <param name="Text">Display text.</param>
    /// <param name="Next">Next node key, or null to end.</param>
    /// <param name="StartQuestId">Quest offered when chosen, or null.</param>
    public sealed record ClientDialogueChoice(string Text, string? Next, string? StartQuestId);

    /// <summary>A dialogue node.</summary>
    /// <param name="Key">Local node id.</param>
    /// <param name="SpeakerNpcId">Speaking NPC, or null for the one talked to.</param>
    /// <param name="PortraitAssetKey">Portrait override, or null.</param>
    /// <param name="Text">Display text.</param>
    /// <param name="Next">Next node key when there are no choices, or null.</param>
    /// <param name="Choices">Player choices.</param>
    public sealed record ClientDialogueNode(
        string Key, string? SpeakerNpcId, string? PortraitAssetKey, string Text, string? Next, IReadOnlyList<ClientDialogueChoice> Choices);

    /// <summary>A dialogue graph. Nodes are ordered by key.</summary>
    /// <param name="Id">Dialogue id.</param>
    /// <param name="Start">Key of the first node.</param>
    /// <param name="Nodes">The nodes.</param>
    public sealed record ClientDialogue(string Id, string Start, IReadOnlyList<ClientDialogueNode> Nodes);

    /// <summary>A quest's display text. Objectives, prerequisites and rewards are server-only.</summary>
    /// <param name="Id">Quest id.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="Description">Display description, or null.</param>
    /// <param name="GiverNpcId">Offering NPC, or null.</param>
    /// <param name="StepTexts">The display text of each step, in order (empty text where the content has none).</param>
    public sealed record ClientQuest(string Id, string Name, string? Description, string? GiverNpcId, IReadOnlyList<string> StepTexts);

    /// <summary>A warp the client can show.</summary>
    /// <param name="Cell">The warp's cell.</param>
    /// <param name="TargetMapId">The map it leads to.</param>
    /// <param name="TargetCell">The arrival cell.</param>
    public sealed record ClientWarp(GridCell Cell, string TargetMapId, GridCell TargetCell);

    /// <summary>An NPC placement.</summary>
    /// <param name="NpcId">NPC id.</param>
    /// <param name="Cell">Its cell.</param>
    /// <param name="Facing">A <c>Directions</c> term.</param>
    public sealed record ClientNpcPlacement(string NpcId, GridCell Cell, string Facing);

    /// <summary>A rule zone.</summary>
    /// <param name="Kind">A <c>ZoneKinds</c> term.</param>
    /// <param name="Area">The zone's cells.</param>
    public sealed record ClientZone(string Kind, GridRect Area);

    /// <summary>A map's display data and layout. No spawns and no scene hash.</summary>
    /// <param name="Id">Map id.</param>
    /// <param name="Name">Display name.</param>
    /// <param name="SceneAssetKey">Scene key.</param>
    /// <param name="MusicAssetKey">Music key, or null.</param>
    /// <param name="Width">Width in cells.</param>
    /// <param name="Height">Height in cells.</param>
    /// <param name="Cells">Base64 of <c>Width * Height</c> bytes, row-major; bit 0 walkable, bit 1 blocks line of sight (as in the grid file).</param>
    /// <param name="Warps">Warps.</param>
    /// <param name="Npcs">NPC placements.</param>
    /// <param name="Zones">Zones.</param>
    public sealed record ClientMap(
        string Id, string Name, string SceneAssetKey, string? MusicAssetKey, int Width, int Height, string Cells,
        IReadOnlyList<ClientWarp> Warps, IReadOnlyList<ClientNpcPlacement> Npcs, IReadOnlyList<ClientZone> Zones);

    /// <summary>The whole client bundle (without its hash; the hash belongs to the file, see <c>ClientBundleWriter</c>).</summary>
    /// <param name="FormatVersion">The bundle format; the client refuses versions it does not know.</param>
    /// <param name="Elements">Element ids for labels, sorted.</param>
    /// <param name="Stats">Stat ids for labels: primary stats, then derived stats.</param>
    /// <param name="Items">Items, by id.</param>
    /// <param name="Skills">Skills, by id.</param>
    /// <param name="Jobs">Jobs, by id.</param>
    /// <param name="Statuses">Statuses, by id.</param>
    /// <param name="Monsters">Monsters, by id.</param>
    /// <param name="Npcs">NPCs, by id.</param>
    /// <param name="Dialogues">Dialogues, by id.</param>
    /// <param name="Quests">Quests, by id.</param>
    /// <param name="Maps">Maps, by id.</param>
    public sealed record ClientBundle(
        int FormatVersion, IReadOnlyList<string> Elements, IReadOnlyList<string> Stats, IReadOnlyList<ClientItem> Items,
        IReadOnlyList<ClientSkill> Skills, IReadOnlyList<ClientJob> Jobs, IReadOnlyList<ClientStatus> Statuses,
        IReadOnlyList<ClientMonster> Monsters, IReadOnlyList<ClientNpc> Npcs, IReadOnlyList<ClientDialogue> Dialogues,
        IReadOnlyList<ClientQuest> Quests, IReadOnlyList<ClientMap> Maps)
    {
        /// <summary>The format this build writes and reads.</summary>
        public const int CurrentFormatVersion = 1;
    }
}
