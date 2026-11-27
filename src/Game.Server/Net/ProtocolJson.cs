using System.Buffers;
using System.Text.Json;
using Game.Shared.Protocol;
using Game.Shared.World;

namespace Game.Server.Net;

/// <summary>Outcome of reading one message: the message, or the <see cref="ByeReasons"/> term that explains why it could not be read.</summary>
public readonly record struct ReadResult<T>(T? Message, string? Error) where T : class
{
    /// <summary>A message was read.</summary>
    public bool Ok => Message is not null;
}

/// <summary>
/// The JSON mapping of every <see cref="Game.Shared.Protocol"/> message (ARCHITECTURE §10), both directions, written by hand on
/// <see cref="Utf8JsonReader"/>/<see cref="Utf8JsonWriter"/>: no reflection and no source generator, so it is AOT-safe as is. It depends only
/// on <c>Game.Shared</c> and <c>System.Text.Json</c>, so the Godot client (T-206) can compile this same file (linked, with the
/// System.Text.Json package on netstandard2.1) instead of keeping a second copy. Field names are snake_case VOCABULARY terms; cells are
/// <c>[x,y]</c>; speeds are rounded to 0.01. Unknown fields are ignored, so a newer peer can add fields within a protocol version.
/// The gateway writes snapshots and deltas straight from the AOI's objects (<see cref="ViewMessageWriter"/>); the golden tests check
/// that both writers give the same bytes.
/// </summary>
public static class ProtocolJson
{
    /// <summary>Deepest nesting a client message may have (objects or arrays inside unknown fields included).</summary>
    public const int MaxClientDepth = 8;

    internal static readonly JsonWriterOptions WriterOptions = new() { SkipValidation = true };

    // ---- client → server ----

    /// <summary>Reads a client message. Errors: <see cref="ByeReasons.Malformed"/> or <see cref="ByeReasons.UnknownType"/>.</summary>
    public static ReadResult<ClientMessage> ReadClient(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = MaxClientDepth });
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return Fail<ClientMessage>(ByeReasons.Malformed);
            string? type = null, name = null, content = null, resume = null, session = null, character = null, slot = null, skill = null, job = null, stat = null, quest = null, itemId = null, outfit = null;
            string? channel = null, to = null, text = null;   // chat (T-901)
            long? line = null;
            bool? hidden = null, accept = null;
            int? version = null, quantity = null, level = null, amount = null, index = null;
            long? seq = null, target = null, entry = null, card = null;
            long? currency = null;   // trade_currency (T-903)
            string? emblem = null;   // guilds (T-904)
            long? member = null;
            int? rank = null;
            Utf8JsonReader ranksAt = default;
            bool hasRanks = false;
            GridCell? cell = null;
            // start_autopilot's lists (T-604): where they begin; they are read once the type is known, so in any other message they stay unknown fields, ignored whatever they hold
            Utf8JsonReader questsAt = default, skillsAt = default;
            bool hasQuests = false, hasSkills = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "type": type = String(ref reader); break;
                    case "quests":
                        questsAt = reader;
                        hasQuests = true;
                        reader.Skip();
                        break;
                    case "skills":
                        skillsAt = reader;
                        hasSkills = true;
                        reader.Skip();
                        break;
                    case "card": card = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var cd) ? cd : throw Bad(); break;
                    case "version": version = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var v) ? v : throw Bad(); break;
                    case "name": name = String(ref reader); break;
                    case "content": content = String(ref reader); break;
                    case "resume": resume = String(ref reader); break;
                    case "session": session = String(ref reader); break;
                    case "character": character = String(ref reader); break;
                    case "seq": seq = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var s) ? s : throw Bad(); break;
                    case "target": target = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var t) ? t : throw Bad(); break;
                    case "cell": cell = Cell(ref reader); break;
                    case "entry": entry = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var en) ? en : throw Bad(); break;
                    case "slot": slot = String(ref reader); break;
                    case "skill": skill = String(ref reader); break;
                    case "job": job = String(ref reader); break;
                    case "stat": stat = String(ref reader); break;
                    case "quest": quest = String(ref reader); break;
                    case "item": itemId = String(ref reader); break;
                    case "outfit": outfit = String(ref reader); break;
                    case "hidden": hidden = reader.TokenType is JsonTokenType.True or JsonTokenType.False ? reader.GetBoolean() : throw Bad(); break;
                    case "accept": accept = reader.TokenType is JsonTokenType.True or JsonTokenType.False ? reader.GetBoolean() : throw Bad(); break;
                    case "amount": amount = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var am) ? am : throw Bad(); break;
                    case "index": index = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var ix) ? ix : throw Bad(); break;
                    case "level": level = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var lv) ? lv : throw Bad(); break;
                    case "quantity": quantity = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var q) ? q : throw Bad(); break;
                    case "channel": channel = String(ref reader); break;
                    case "to": to = String(ref reader); break;
                    case "text": text = String(ref reader); break;
                    case "line": line = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var ln) ? ln : throw Bad(); break;
                    case "currency": currency = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var cu) ? cu : throw Bad(); break;
                    case "emblem": emblem = String(ref reader); break;
                    case "member": member = reader.TokenType == JsonTokenType.Number && reader.TryGetInt64(out var mb) ? mb : throw Bad(); break;
                    case "rank": rank = reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var rk) ? rk : throw Bad(); break;
                    case "ranks":
                        ranksAt = reader;
                        hasRanks = true;
                        reader.Skip();
                        break;
                    default: reader.Skip(); break;
                }
            }
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return Fail<ClientMessage>(ByeReasons.Malformed);

            ClientMessage? message = type switch
            {
                // exactly one of name (the dev login), resume (a resume token), and session with character (an account's login)
                MessageTypes.Hello => version is { } ver && (session is null) == (character is null)
                                      && (name is null ? 0 : 1) + (resume is null ? 0 : 1) + (session is null ? 0 : 1) == 1
                    ? new HelloMessage(ver, name, content, resume, session, character) : null,
                MessageTypes.Move => cell is { } c ? new MoveMessage(c) : null,
                MessageTypes.Stop => new StopMessage(),
                MessageTypes.Attack => target is { } id ? new AttackMessage(id) : null,
                MessageTypes.StopAttack => new StopAttackMessage(),
                MessageTypes.Respawn => new RespawnMessage(),
                MessageTypes.Logout => new LogoutMessage(),
                MessageTypes.Ping => seq is { } n ? new PingMessage(n) : null,
                MessageTypes.PickUp => target is { } item ? new PickUpMessage(item) : null,
                MessageTypes.UseItem => entry is { } used ? new UseItemMessage(used) : null,
                MessageTypes.Equip => entry is { } worn ? new EquipMessage(worn, slot) : null,
                MessageTypes.Unequip => slot is { } off ? new UnequipMessage(off) : null,
                MessageTypes.DropItem => entry is { } dropped && quantity is { } count ? new DropItemMessage(dropped, count) : null,
                MessageTypes.InsertCard => entry is { } socketed && card is { } inserted ? new InsertCardMessage(socketed, inserted) : null,
                MessageTypes.Refine => entry is { } refined ? new RefineMessage(refined) : null,
                MessageTypes.UseSkill => skill is { } cast ? new UseSkillMessage(cast, level, target, cell) : null,
                MessageTypes.LearnSkill => skill is { } learned ? new LearnSkillMessage(learned) : null,
                MessageTypes.ChangeJob => job is { } changed ? new ChangeJobMessage(changed) : null,
                MessageTypes.RaiseStat => stat is { } raised ? new RaiseStatMessage(raised, amount ?? 1) : null,
                MessageTypes.Talk => target is { } npc ? new TalkMessage(npc) : null,
                MessageTypes.DialogueChoice => index is { } picked ? new DialogueChoiceMessage(picked) : null,
                MessageTypes.DialogueClose => new DialogueCloseMessage(),
                MessageTypes.AbandonQuest => quest is { } dropped ? new AbandonQuestMessage(dropped) : null,
                MessageTypes.ShopBuy => itemId is { } bought && quantity is { } units ? new ShopBuyMessage(bought, units) : null,
                MessageTypes.ShopSell => entry is { } sold && quantity is { } amountSold ? new ShopSellMessage(sold, amountSold) : null,
                MessageTypes.StartAutopilot => hasQuests
                    ? new StartAutopilotMessage(ReadStrings(ref questsAt), hasSkills ? ReadLevels(ref skillsAt) : []) : null,
                MessageTypes.StopAutopilot => new StopAutopilotMessage(),
                // exactly one of slot (with an outfit, or nothing) and hidden
                MessageTypes.SetOutfit => (slot is null) != (hidden is null) && (outfit is null || slot is not null) ? new SetOutfitMessage(slot, outfit, hidden) : null,
                // T-901: the shape only; the chat service decides about the channel, the name and the text
                MessageTypes.Chat => channel is { } said && text is { } words ? new ChatMessage(said, to, words) : null,
                MessageTypes.Block => name is { } blocked ? new BlockMessage(blocked) : null,
                MessageTypes.Unblock => name is { } unblocked ? new UnblockMessage(unblocked) : null,
                MessageTypes.Report => line is { } reported ? new ReportMessage(reported) : null,
                // T-902: exactly one of name and target; the party service decides the rest
                MessageTypes.PartyInvite => (name is null) != (target is null) ? new PartyInviteMessage(name, target) : null,
                MessageTypes.PartyAnswer => accept is { } yes ? new PartyAnswerMessage(yes) : null,
                MessageTypes.PartyLeave => new PartyLeaveMessage(),
                MessageTypes.PartyKick => target is { } kicked ? new PartyKickMessage(kicked) : null,
                MessageTypes.PartyLead => target is { } led ? new PartyLeadMessage(led) : null,
                // T-903: the shape only; the trade system decides the rest
                MessageTypes.TradeAsk => target is { } asked ? new TradeAskMessage(asked) : null,
                MessageTypes.TradeAnswer => accept is { } agreed ? new TradeAnswerMessage(agreed) : null,
                MessageTypes.TradeOffer => entry is { } offered && quantity is { } units2 ? new TradeOfferMessage(offered, units2) : null,
                MessageTypes.TradeCurrency => currency is { } coins ? new TradeCurrencyMessage(coins) : null,
                MessageTypes.TradeLock => new TradeLockMessage(),
                MessageTypes.TradeUnlock => new TradeUnlockMessage(),
                MessageTypes.TradeConfirm => version is { } seen ? new TradeConfirmMessage(seen) : null,
                MessageTypes.TradeCancel => new TradeCancelMessage(),
                // T-904: the shape only; the guild service and the database decide the rest
                MessageTypes.GuildFound => name is { } title && emblem is { } badge ? new GuildFoundMessage(title, badge) : null,
                MessageTypes.GuildInvite => (name is null) != (target is null) ? new GuildInviteMessage(name, target) : null,
                MessageTypes.GuildAnswer => accept is { } joins ? new GuildAnswerMessage(joins) : null,
                MessageTypes.GuildLeave => new GuildLeaveMessage(),
                MessageTypes.GuildKick => member is { } kickedMember ? new GuildKickMessage(kickedMember) : null,
                MessageTypes.GuildRank => member is { } ranked && rank is { } place ? new GuildRankMessage(ranked, place) : null,
                MessageTypes.GuildRanks => hasRanks ? new GuildRanksMessage(ReadGuildRanks(ref ranksAt)) : null,
                MessageTypes.GuildLead => member is { } newLeader ? new GuildLeadMessage(newLeader) : null,
                MessageTypes.GuildNotice => text is { } noticeText ? new GuildNoticeMessage(noticeText) : null,
                null => null,
                _ => throw new UnknownTypeException(),
            };
            return message is null ? Fail<ClientMessage>(ByeReasons.Malformed) : new ReadResult<ClientMessage>(message, null);
        }
        catch (UnknownTypeException)
        {
            return Fail<ClientMessage>(ByeReasons.UnknownType);
        }
        catch (JsonException)
        {
            return Fail<ClientMessage>(ByeReasons.Malformed);
        }
        catch (InvalidOperationException)
        {
            return Fail<ClientMessage>(ByeReasons.Malformed); // a token of the wrong kind for its field
        }
    }

    /// <summary>Writes a client message (what the client sends).</summary>
    public static void Write(IBufferWriter<byte> output, ClientMessage message)
    {
        using var w = new Utf8JsonWriter(output, WriterOptions);
        w.WriteStartObject();
        w.WriteString("type", message.Type);
        switch (message)
        {
            case HelloMessage hello:
                w.WriteNumber("version", hello.Version);
                if (hello.Name is not null) w.WriteString("name", hello.Name);
                if (hello.Resume is not null) w.WriteString("resume", hello.Resume);
                if (hello.Session is not null) w.WriteString("session", hello.Session);
                if (hello.Character is not null) w.WriteString("character", hello.Character);
                if (hello.Content is not null) w.WriteString("content", hello.Content);
                break;
            case MoveMessage move:
                WriteCell(w, "cell", move.Cell);
                break;
            case AttackMessage attack:
                w.WriteNumber("target", attack.Target);
                break;
            case PingMessage ping:
                w.WriteNumber("seq", ping.Seq);
                break;
            case PickUpMessage pick:
                w.WriteNumber("target", pick.Target);
                break;
            case UseItemMessage use:
                w.WriteNumber("entry", use.Entry);
                break;
            case EquipMessage equip:
                w.WriteNumber("entry", equip.Entry);
                if (equip.Slot is not null) w.WriteString("slot", equip.Slot);
                break;
            case UnequipMessage unequip:
                w.WriteString("slot", unequip.Slot);
                break;
            case DropItemMessage drop:
                w.WriteNumber("entry", drop.Entry);
                w.WriteNumber("quantity", drop.Quantity);
                break;
            case InsertCardMessage insert:
                w.WriteNumber("entry", insert.Entry);
                w.WriteNumber("card", insert.Card);
                break;
            case RefineMessage refine:
                w.WriteNumber("entry", refine.Entry);
                break;
            case UseSkillMessage use:
                w.WriteString("skill", use.Skill);
                if (use.Level is { } level) w.WriteNumber("level", level);
                if (use.Target is { } target) w.WriteNumber("target", target);
                if (use.Cell is { } cell) WriteCell(w, "cell", cell);
                break;
            case LearnSkillMessage learn:
                w.WriteString("skill", learn.Skill);
                break;
            case ChangeJobMessage change:
                w.WriteString("job", change.Job);
                break;
            case TalkMessage talk:
                w.WriteNumber("target", talk.Target);
                break;
            case DialogueChoiceMessage choice:
                w.WriteNumber("index", choice.Index);
                break;
            case RaiseStatMessage raise:
                w.WriteString("stat", raise.Stat);
                w.WriteNumber("amount", raise.Amount);
                break;
            case AbandonQuestMessage abandon:
                w.WriteString("quest", abandon.Quest);
                break;
            case ShopBuyMessage buy:
                w.WriteString("item", buy.Item);
                w.WriteNumber("quantity", buy.Quantity);
                break;
            case ShopSellMessage sell:
                w.WriteNumber("entry", sell.Entry);
                w.WriteNumber("quantity", sell.Quantity);
                break;
            case SetOutfitMessage set:
                if (set.Slot is not null) w.WriteString("slot", set.Slot);
                if (set.Outfit is not null) w.WriteString("outfit", set.Outfit);
                if (set.Hidden is { } hide) w.WriteBoolean("hidden", hide);
                break;
            case StartAutopilotMessage start:
                WriteStrings(w, "quests", start.Quests);
                WriteLevels(w, "skills", start.Skills);
                break;
            case ChatMessage chat:
                w.WriteString("channel", chat.Channel);
                if (chat.To is not null) w.WriteString("to", chat.To);
                w.WriteString("text", chat.Text);
                break;
            case BlockMessage block:
                w.WriteString("name", block.Name);
                break;
            case UnblockMessage unblock:
                w.WriteString("name", unblock.Name);
                break;
            case ReportMessage report:
                w.WriteNumber("line", report.Line);
                break;
            case PartyInviteMessage invite:
                if (invite.Name is not null) w.WriteString("name", invite.Name);
                if (invite.Target is { } invited) w.WriteNumber("target", invited);
                break;
            case PartyAnswerMessage answer:
                w.WriteBoolean("accept", answer.Accept);
                break;
            case PartyKickMessage kick:
                w.WriteNumber("target", kick.Target);
                break;
            case PartyLeadMessage lead:
                w.WriteNumber("target", lead.Target);
                break;
            case TradeAskMessage tradeAsk:
                w.WriteNumber("target", tradeAsk.Target);
                break;
            case TradeAnswerMessage tradeAnswer:
                w.WriteBoolean("accept", tradeAnswer.Accept);
                break;
            case TradeOfferMessage tradeOffer:
                w.WriteNumber("entry", tradeOffer.Entry);
                w.WriteNumber("quantity", tradeOffer.Quantity);
                break;
            case TradeCurrencyMessage tradeCurrency:
                w.WriteNumber("currency", tradeCurrency.Currency);
                break;
            case TradeConfirmMessage tradeConfirm:
                w.WriteNumber("version", tradeConfirm.Version);
                break;
            case GuildFoundMessage guildFound:
                w.WriteString("name", guildFound.Name);
                w.WriteString("emblem", guildFound.Emblem);
                break;
            case GuildInviteMessage guildInvite:
                if (guildInvite.Name is not null) w.WriteString("name", guildInvite.Name);
                if (guildInvite.Target is { } guildInvited) w.WriteNumber("target", guildInvited);
                break;
            case GuildAnswerMessage guildAnswer:
                w.WriteBoolean("accept", guildAnswer.Accept);
                break;
            case GuildKickMessage guildKick:
                w.WriteNumber("member", guildKick.Member);
                break;
            case GuildRankMessage guildRank:
                w.WriteNumber("member", guildRank.Member);
                w.WriteNumber("rank", guildRank.Rank);
                break;
            case GuildRanksMessage guildRanks:
                WriteGuildRanks(w, guildRanks.Ranks);
                break;
            case GuildLeadMessage guildLead:
                w.WriteNumber("member", guildLead.Member);
                break;
            case GuildNoticeMessage guildNotice:
                w.WriteString("text", guildNotice.Text);
                break;
        }
        w.WriteEndObject();
    }

    // ---- server → client ----

    /// <summary>Writes a server message.</summary>
    public static void Write(IBufferWriter<byte> output, ServerMessage message)
    {
        using var w = new Utf8JsonWriter(output, WriterOptions);
        w.WriteStartObject();
        w.WriteString("type", message.Type);
        switch (message)
        {
            case WelcomeMessage welcome:
                w.WriteNumber("version", welcome.Version);
                w.WriteNumber("id", welcome.Id);
                w.WriteString("map", welcome.MapId);
                w.WriteNumber("tick_rate", welcome.TickRate);
                if (welcome.Content is not null) w.WriteString("content", welcome.Content);
                if (welcome.Token is not null) w.WriteString("token", welcome.Token);
                break;
            case SnapshotMessage snapshot:
                w.WriteNumber("tick", snapshot.Tick);
                w.WriteString("map", snapshot.MapId);
                w.WriteNumber("id", snapshot.Id);
                w.WriteStartArray("entities");
                foreach (var e in snapshot.Entities) WriteRecord(w, e);
                w.WriteEndArray();
                if (snapshot.Vitals is { } own) WriteVitals(w, own.Hp, own.MaxHp, own.Sp, own.MaxSp);
                break;
            case DeltaMessage delta:
                w.WriteNumber("tick", delta.Tick);
                if (delta.Appeared.Count > 0)
                {
                    w.WriteStartArray("appeared");
                    foreach (var e in delta.Appeared) WriteRecord(w, e);
                    w.WriteEndArray();
                }
                if (delta.Changed.Count > 0)
                {
                    w.WriteStartArray("changed");
                    foreach (var c in delta.Changed) WriteChange(w, c.Id, c.Fields, c.Cell, c.Facing, c.Next, c.Speed, c.DisplayKey, c.Hp, c.Dead, c.Mine, c.Weapon, c.Worn, c.Statuses, c.Outfit);
                    w.WriteEndArray();
                }
                if (delta.Disappeared.Count > 0)
                {
                    w.WriteStartArray("disappeared");
                    foreach (var id in delta.Disappeared) w.WriteNumberValue(id);
                    w.WriteEndArray();
                }
                if (delta.Hits is { Count: > 0 } hits)
                {
                    w.WriteStartArray("hits");
                    foreach (var h in hits) WriteHit(w, h.Attacker, h.Target, h.Amount, h.Result);
                    w.WriteEndArray();
                }
                if (delta.Casts is { Count: > 0 } casts)
                {
                    w.WriteStartArray("casts");
                    foreach (var c in casts) WriteCast(w, c.Caster, c.Skill, c.Ms);
                    w.WriteEndArray();
                }
                if (delta.CastEnds is { Count: > 0 } ends)
                {
                    w.WriteStartArray("cast_ends");
                    foreach (var c in ends) WriteCastEnd(w, c.Caster, c.Skill, c.Result);
                    w.WriteEndArray();
                }
                if (delta.Vitals is { } vitals) WriteVitals(w, vitals.Hp, vitals.MaxHp, vitals.Sp, vitals.MaxSp);
                break;
            case SkillsMessage skills:
                w.WriteString("job", skills.Job);
                w.WriteNumber("points", skills.Points);
                WriteLevels(w, "levels", skills.Levels);
                w.WriteStartArray("cooldowns");
                foreach (var c in skills.Cooldowns)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(c.Skill);
                    w.WriteNumberValue(c.Ms);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteStartArray("jobs");
                foreach (var j in skills.Jobs) w.WriteStringValue(j);
                w.WriteEndArray();
                break;
            case StatsMessage stats:
                w.WriteNumber("points", stats.Points);
                w.WriteStartArray("stats");
                foreach (var s in stats.Stats)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(s.Stat);
                    w.WriteNumberValue(s.Value);
                    w.WriteNumberValue(s.NextCost);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                w.WriteStartArray("derived");
                foreach (var d in stats.Derived)
                {
                    w.WriteStartArray();
                    w.WriteStringValue(d.Stat);
                    w.WriteNumberValue(d.Value);
                    w.WriteEndArray();
                }
                w.WriteEndArray();
                break;
            case CooldownMessage cooldown:
                w.WriteString("skill", cooldown.Skill);
                w.WriteNumber("ms", cooldown.Ms);
                break;
            case RejectedMessage rejected:
                w.WriteString("intent", rejected.Intent);
                w.WriteString("reason", rejected.Reason);
                if (rejected.Cell is { } cell) WriteCell(w, "cell", cell);
                if (rejected.Target is { } target) w.WriteNumber("target", target);
                if (rejected.Entry is { } entry) w.WriteNumber("entry", entry);
                if (rejected.Slot is not null) w.WriteString("slot", rejected.Slot);
                if (rejected.Skill is not null) w.WriteString("skill", rejected.Skill);
                if (rejected.Stat is not null) w.WriteString("stat", rejected.Stat);
                if (rejected.Quest is not null) w.WriteString("quest", rejected.Quest);
                if (rejected.Item is not null) w.WriteString("item", rejected.Item);
                break;
            case AttackStoppedMessage stopped:
                w.WriteNumber("target", stopped.Target);
                w.WriteString("reason", stopped.Reason);
                break;
            case ExpMessage exp:
                w.WriteNumber("base_level", exp.BaseLevel);
                w.WriteNumber("base_exp", exp.BaseExp);
                w.WriteNumber("base_next", exp.BaseNext);
                w.WriteNumber("job_level", exp.JobLevel);
                w.WriteNumber("job_exp", exp.JobExp);
                w.WriteNumber("job_next", exp.JobNext);
                break;
            case LevelUpMessage level:
                if (level.BaseLevel is { } baseLevel) w.WriteNumber("base_level", baseLevel);
                if (level.JobLevel is { } jobLevel) w.WriteNumber("job_level", jobLevel);
                break;
            case YouDiedMessage died:
                if (died.Killer is { } killer) w.WriteNumber("killer", killer);
                break;
            case RespawnedMessage respawned:
                w.WriteString("map", respawned.MapId);
                WriteCell(w, "cell", respawned.Cell);
                w.WriteNumber("hp", respawned.Hp);
                break;
            case InventoryMessage inventory:
                w.WriteNumber("rev", inventory.Rev);
                w.WriteNumber("slots", inventory.Slots);
                w.WriteNumber("weight", inventory.Weight);
                w.WriteNumber("weight_limit", inventory.WeightLimit);
                w.WriteString("load", inventory.Load);
                w.WriteNumber("currency", inventory.Currency);
                w.WriteStartArray("entries");
                foreach (var e in inventory.Entries) WriteEntry(w, e);
                w.WriteEndArray();
                break;
            case InventoryChangedMessage change:
                w.WriteNumber("rev", change.Rev);
                w.WriteString("cause", change.Cause);
                if (change.Item is not null) w.WriteString("item", change.Item);
                w.WriteNumber("weight", change.Weight);
                w.WriteNumber("weight_limit", change.WeightLimit);
                w.WriteString("load", change.Load);
                w.WriteNumber("currency", change.Currency);
                if (change.Entries.Count > 0)
                {
                    w.WriteStartArray("entries");
                    foreach (var e in change.Entries) WriteEntry(w, e);
                    w.WriteEndArray();
                }
                if (change.Removed.Count > 0)
                {
                    w.WriteStartArray("removed");
                    foreach (var id in change.Removed) w.WriteNumberValue(id);
                    w.WriteEndArray();
                }
                break;
            case RefineResultMessage refined:
                w.WriteNumber("entry", refined.Entry);
                w.WriteString("item", refined.Item);
                w.WriteString("result", refined.Result);
                w.WriteNumber("refine", refined.Refine);
                break;
            case DialogueMessage dialogue:
                w.WriteNumber("npc", dialogue.Npc);
                w.WriteString("speaker", dialogue.Speaker);
                if (dialogue.Portrait is not null) w.WriteString("portrait", dialogue.Portrait);
                w.WriteString("text", dialogue.Text);
                w.WriteStartArray("choices");
                foreach (var c in dialogue.Choices)
                {
                    w.WriteStartObject();
                    w.WriteNumber("index", c.Index);
                    w.WriteString("text", c.Text);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                if (dialogue.Choices.Count == 0) w.WriteBoolean("more", dialogue.More);
                break;
            case DialogueEndedMessage ended:
                w.WriteString("reason", ended.Reason);
                break;
            case ServiceOpenedMessage opened:
                w.WriteString("service", opened.Service);
                w.WriteNumber("npc", opened.Npc);
                break;
            case ShopMessage shop:
                w.WriteNumber("npc", shop.Npc);
                w.WriteString("shop", shop.Shop);
                w.WriteNumber("max_quantity", shop.MaxQuantity);
                WriteStock(w, "stock", shop.Stock);
                WriteStock(w, "sell", shop.Sell);
                break;
            case QuestLogMessage log:
                w.WriteStartArray("active");
                foreach (var e in log.Active) WriteQuestEntry(w, e);
                w.WriteEndArray();
                WriteStrings(w, "completed", log.Completed);
                WriteStrings(w, "offer", log.Offer);
                WriteStrings(w, "turn_in", log.TurnIn);
                break;
            case QuestMessage quest:
                w.WriteString("change", quest.Change);
                w.WriteString("quest", quest.Quest);
                if (quest.Entry is { } state)
                {
                    w.WritePropertyName("state");
                    WriteQuestEntry(w, state);
                }
                break;
            case AutopilotMessage autopilot:
                w.WriteString("mode", autopilot.Mode);
                if (autopilot.Quest is not null) w.WriteString("quest", autopilot.Quest);
                if (autopilot.Doing is not null) w.WriteString("doing", autopilot.Doing);
                if (autopilot.Reason is not null) w.WriteString("reason", autopilot.Reason);
                break;
            case WardrobeMessage wardrobe:
                WriteStrings(w, "owned", wardrobe.Owned);
                WriteOutfit(w, "chosen", wardrobe.Chosen);
                if (wardrobe.Hidden) w.WriteBoolean("hidden", true);
                break;
            case PongMessage pong:
                w.WriteNumber("seq", pong.Seq);
                break;
            case AnnouncementMessage announcement:
                w.WriteString("kind", announcement.Kind);
                if (announcement.Text is { } said) w.WriteString("text", said);
                if (announcement.InSeconds is { } inSeconds) w.WriteNumber("in", inSeconds);
                break;
            case ChatLineMessage chatLine:
                w.WriteNumber("line", chatLine.Line);
                w.WriteString("channel", chatLine.Channel);
                w.WriteNumber("from", chatLine.From);
                w.WriteString("name", chatLine.Name);
                if (chatLine.To is not null) w.WriteString("to", chatLine.To);
                w.WriteString("text", chatLine.Text);
                break;
            case ChatRefusedMessage chatRefused:
                w.WriteString("intent", chatRefused.Intent);
                w.WriteString("reason", chatRefused.Reason);
                if (chatRefused.RetryAfterSeconds is { } wait) w.WriteNumber("retry_after", wait);
                if (chatRefused.Channel is not null) w.WriteString("channel", chatRefused.Channel);
                if (chatRefused.To is not null) w.WriteString("to", chatRefused.To);
                if (chatRefused.Line is { } refusedLine) w.WriteNumber("line", refusedLine);
                break;
            case ChatStateMessage chatState:
                w.WriteNumber("max_length", chatState.MaxLength);
                WriteStrings(w, "blocked", chatState.Blocked);
                break;
            case ChatReportedMessage reported:
                w.WriteNumber("line", reported.Line);
                break;
            case PartyMessage party:
                if (party.Party is { } partyId) w.WriteNumber("party", partyId);
                if (party.Party is not null) w.WriteNumber("leader", party.Leader);
                w.WriteStartArray("members");
                foreach (var member in party.Members)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", member.Id);
                    w.WriteString("name", member.Name);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
            case PartyMembersMessage members:
                w.WriteStartArray("status");
                foreach (var m in members.Status)
                {
                    w.WriteStartObject();
                    w.WriteNumber("id", m.Id);
                    if (m.Away) w.WriteBoolean("away", true);
                    else
                    {
                        w.WriteNumber("hp", m.Hp);
                        w.WriteNumber("sp", m.Sp);
                        if (m.Map is not null) w.WriteString("map", m.Map);
                        if (m.Cell is { } at) WriteCell(w, "cell", at);
                        if (m.Dead) w.WriteBoolean("dead", true);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
            case PartyInvitedMessage invited:
                w.WriteNumber("from", invited.From);
                w.WriteString("name", invited.Name);
                w.WriteNumber("expires_in", invited.ExpiresInSeconds);
                break;
            case PartyNoticeMessage notice:
                w.WriteString("notice", notice.Notice);
                if (notice.Name is not null) w.WriteString("name", notice.Name);
                break;
            case PartyRefusedMessage partyRefused:
                w.WriteString("intent", partyRefused.Intent);
                w.WriteString("reason", partyRefused.Reason);
                if (partyRefused.RetryAfterSeconds is { } partyWait) w.WriteNumber("retry_after", partyWait);
                if (partyRefused.Name is not null) w.WriteString("name", partyRefused.Name);
                break;
            case TradeAskedMessage tradeAsked:
                w.WriteNumber("from", tradeAsked.From);
                w.WriteString("name", tradeAsked.Name);
                w.WriteNumber("expires_in", tradeAsked.ExpiresInSeconds);
                break;
            case TradeMessage trade:
                w.WriteNumber("partner", trade.Partner);
                w.WriteString("name", trade.Name);
                w.WriteNumber("version", trade.Version);
                WriteTradeSide(w, "mine", trade.Mine);
                WriteTradeSide(w, "theirs", trade.Theirs);
                break;
            case TradeNoticeMessage tradeNotice:
                w.WriteString("notice", tradeNotice.Notice);
                if (tradeNotice.Name is not null) w.WriteString("name", tradeNotice.Name);
                break;
            case TradeClosedMessage tradeClosed:
                w.WriteString("reason", tradeClosed.Reason);
                if (tradeClosed.Name is not null) w.WriteString("name", tradeClosed.Name);
                if (tradeClosed.Own) w.WriteBoolean("own", true);
                break;
            case TradeRefusedMessage tradeRefused:
                w.WriteString("intent", tradeRefused.Intent);
                w.WriteString("reason", tradeRefused.Reason);
                if (tradeRefused.RetryAfterSeconds is { } tradeWait) w.WriteNumber("retry_after", tradeWait);
                break;
            case GuildMessage guild:
                if (guild.Guild is { } guildId)
                {
                    w.WriteStartObject("guild");
                    w.WriteNumber("id", guildId);
                    w.WriteString("name", guild.Name);
                    w.WriteString("emblem", guild.Emblem);
                    w.WriteString("notice", guild.Notice);
                    w.WriteNumber("own", guild.Own);
                    WriteGuildRanks(w, guild.Ranks);
                    w.WriteStartArray("members");
                    foreach (var m in guild.Members)
                    {
                        w.WriteStartObject();
                        w.WriteNumber("member", m.Member);
                        w.WriteString("name", m.Name);
                        w.WriteNumber("rank", m.Rank);
                        w.WriteNumber("level", m.Level);
                        if (m.Online) w.WriteBoolean("online", true);
                        w.WriteEndObject();
                    }
                    w.WriteEndArray();
                    w.WriteEndObject();
                }
                break;
            case GuildInvitedMessage guildInvited:
                w.WriteString("name", guildInvited.Name);
                w.WriteString("title", guildInvited.Title);
                w.WriteString("emblem", guildInvited.Emblem);
                w.WriteNumber("expires_in", guildInvited.ExpiresInSeconds);
                break;
            case GuildNewsMessage guildNews:
                w.WriteString("news", guildNews.News);
                if (guildNews.Name is not null) w.WriteString("name", guildNews.Name);
                break;
            case GuildRefusedMessage guildRefused:
                w.WriteString("intent", guildRefused.Intent);
                w.WriteString("reason", guildRefused.Reason);
                if (guildRefused.RetryAfterSeconds is { } guildWait) w.WriteNumber("retry_after", guildWait);
                if (guildRefused.Name is not null) w.WriteString("name", guildRefused.Name);
                break;
            case GuildTagsMessage guildTags:
                w.WriteStartArray("tags");
                foreach (var tag in guildTags.Tags)
                {
                    w.WriteStartObject();
                    w.WriteNumber("entity", tag.Entity);
                    w.WriteString("emblem", tag.Emblem);
                    w.WriteEndObject();
                }
                w.WriteEndArray();
                break;
            case ByeMessage bye:
                w.WriteString("reason", bye.Reason);
                if (bye.RetryAfterSeconds is { } retry) w.WriteNumber("retry_after", retry);
                break;
        }
        w.WriteEndObject();
    }

    /// <summary>One side of a <c>trade</c> message (T-903): its entries (cards and refine only when it has them), currency, and the flags only when true.</summary>
    private static void WriteTradeSide(Utf8JsonWriter w, string name, TradeSideRecord side)
    {
        w.WriteStartObject(name);
        w.WriteStartArray("items");
        foreach (var item in side.Items)
        {
            w.WriteStartObject();
            w.WriteNumber("entry", item.Entry);
            w.WriteString("item", item.Item);
            w.WriteNumber("quantity", item.Quantity);
            if (item.Cards is { Count: > 0 } cards) WriteStrings(w, "cards", cards);
            if (item.Refine > 0) w.WriteNumber("refine", item.Refine);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        w.WriteNumber("currency", side.Currency);
        if (side.Locked) w.WriteBoolean("locked", true);
        if (side.Confirmed) w.WriteBoolean("confirmed", true);
        w.WriteEndObject();
    }

    /// <summary>Reads one side of a <c>trade</c> message (T-903).</summary>
    private static TradeSideRecord ReadTradeSide(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw Bad();
        var items = new List<TradeItemRecord>();
        long currency = 0;
        bool locked = false, confirmed = false;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString();
            reader.Read();
            switch (key)
            {
                case "items":
                    if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
                    while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        long entry = 0;
                        string? item = null;
                        int quantity = 0, refine = 0;
                        List<string>? cards = null;
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var field = reader.GetString();
                            reader.Read();
                            switch (field)
                            {
                                case "entry": entry = reader.GetInt64(); break;
                                case "item": item = String(ref reader); break;
                                case "quantity": quantity = reader.GetInt32(); break;
                                case "refine": refine = reader.GetInt32(); break;
                                case "cards": cards = ReadStrings(ref reader); break;
                                default: reader.Skip(); break;
                            }
                        }
                        items.Add(new TradeItemRecord(entry, item ?? throw Bad(), quantity, cards, refine));
                    }
                    break;
                case "currency": currency = reader.GetInt64(); break;
                case "locked": locked = reader.GetBoolean(); break;
                case "confirmed": confirmed = reader.GetBoolean(); break;
                default: reader.Skip(); break;
            }
        }
        return new TradeSideRecord(items, currency, locked, confirmed);
    }

    /// <summary>A server message as a new byte array.</summary>
    public static byte[] ToBytes(ServerMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        Write(buffer, message);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// The close frame's reason for <paramref name="bye"/>: the reason, then <c>retry_after</c> after a space when it has one (<c>banned 3600</c>, T-806). A client may lose the
    /// <c>bye</c> packet that came with the close frame (Godot's WebSocketPeer drops it), so the frame carries everything the bye says.
    /// </summary>
    public static string CloseReasonOf(ByeMessage bye) =>
        bye.RetryAfterSeconds is { } retry ? bye.Reason + " " + retry.ToString(System.Globalization.CultureInfo.InvariantCulture) : bye.Reason;

    /// <summary>The <c>bye</c> a close frame's reason stands for (<see cref="CloseReasonOf"/>), or null when it is none.</summary>
    public static ByeMessage? ByeFromCloseReason(string? closeReason)
    {
        if (closeReason is null) return null;
        var space = closeReason.IndexOf(' ');
        var reason = space < 0 ? closeReason : closeReason.Substring(0, space);
        if (!ByeReasons.All.Contains(reason)) return null;
        if (space < 0) return new ByeMessage(reason);
        return long.TryParse(closeReason.Substring(space + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var retry) && retry > 0
            ? new ByeMessage(reason, retry) : new ByeMessage(reason);
    }

    /// <summary>A client message as a new byte array.</summary>
    public static byte[] ToBytes(ClientMessage message)
    {
        var buffer = new ArrayBufferWriter<byte>(64);
        Write(buffer, message);
        return buffer.WrittenSpan.ToArray();
    }

    /// <summary>Reads a server message (what the client receives). Error: <see cref="ByeReasons.Malformed"/> or <see cref="ByeReasons.UnknownType"/>.</summary>
    public static ReadResult<ServerMessage> ReadServer(ReadOnlySpan<byte> utf8)
    {
        try
        {
            var reader = new Utf8JsonReader(utf8);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return Fail<ServerMessage>(ByeReasons.Malformed);
            string? type = null, map = null, intent = null, reason = null, content = null, token = null, slot = null, cause = null, item = null, load = null, result = null, skill = null, job = null;
            long tick = 0, id = 0, seq = 0, baseExp = 0, baseNext = 0, jobExp = 0, jobNext = 0, rev = 0;
            long? target = null, killer = null, entry = null;
            int version = 0, tickRate = 0, hp = 0, slots = 0, weight = 0, weightLimit = 0, refine = 0;
            List<InventoryEntryRecord>? inventoryEntries = null;
            List<long>? removed = null;
            int? baseLevel = null, jobLevel = null;
            GridCell? cell = null;
            VitalsRecord? vitals = null;
            var entities = new List<EntityRecord>();
            var changed = new List<ChangeRecord>();
            var gone = new List<long>();
            List<HitRecord>? hits = null;
            List<CastRecord>? casts = null;
            List<CastEndRecord>? castEnds = null;
            List<(string, int)>? levels = null, cooldowns = null, derived = null;
            List<StatRecord>? statRows = null;
            string? stat = null;
            List<string>? jobs = null;
            int points = 0, ms = 0;
            string? speaker = null, portrait = null, text = null, service = null;
            long? npc = null;
            bool more = false;
            List<DialogueChoiceRecord>? choices = null;
            string? quest = null, change = null, mode = null, doing = null;
            long currency = 0;
            string? shopId = null;
            int maxQuantity = 0;
            List<ShopStockRecord>? shopStock = null, shopSell = null;
            List<QuestEntryRecord>? questsActive = null;
            QuestEntryRecord? questState = null;
            List<string>? completed = null, offer = null, turnIn = null, owned = null;
            List<ShownOutfit>? chosen = null;
            bool hidden = false;
            string? kind = null;
            long? inSeconds = null, retryAfter = null;
            string? channel = null, name = null, to = null;   // chat (T-901)
            long? line = null, from = null;
            int maxLength = 0;
            List<string>? blocked = null;
            long? partyId = null, leader = null, expiresIn = null;   // party (T-902)
            string? notice = null;
            List<PartyMemberRecord>? partyMembers = null;
            List<PartyMemberStatusRecord>? partyStatus = null;
            GuildMessage? guildState = null;   // guilds (T-904)
            string? title = null, emblem = null, news = null;
            List<GuildTagRecord>? tags = null;
            long? partner = null;   // trade (T-903)
            TradeSideRecord? mine = null, theirs = null;
            bool own = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "type": type = String(ref reader); break;
                    case "version": version = reader.GetInt32(); break;
                    case "id": id = reader.GetInt64(); break;
                    case "map": map = String(ref reader); break;
                    case "tick_rate": tickRate = reader.GetInt32(); break;
                    case "content": content = String(ref reader); break;
                    case "token": token = String(ref reader); break;
                    case "tick": tick = reader.GetInt64(); break;
                    case "seq": seq = reader.GetInt64(); break;
                    case "intent": intent = String(ref reader); break;
                    case "reason": reason = String(ref reader); break;
                    case "cell": cell = Cell(ref reader); break;
                    case "entities":
                    case "appeared":
                        ReadArray(ref reader, entities);
                        break;
                    case "changed": ReadChanges(ref reader, changed); break;
                    case "disappeared":
                        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) gone.Add(reader.GetInt64());
                        break;
                    case "hits": hits = ReadHits(ref reader); break;
                    case "casts": casts = ReadCasts(ref reader); break;
                    case "cast_ends": castEnds = ReadCastEnds(ref reader); break;
                    case "skill": skill = String(ref reader); break;
                    case "job": job = String(ref reader); break;
                    case "points": points = reader.GetInt32(); break;
                    case "ms": ms = reader.GetInt32(); break;
                    case "npc": npc = reader.GetInt64(); break;
                    case "speaker": speaker = String(ref reader); break;
                    case "portrait": portrait = String(ref reader); break;
                    case "text": text = String(ref reader); break;
                    case "service": service = String(ref reader); break;
                    case "more": more = reader.GetBoolean(); break;
                    case "choices": choices = ReadChoices(ref reader); break;
                    case "levels": levels = ReadPairs(ref reader); break;
                    case "cooldowns": cooldowns = ReadPairs(ref reader); break;
                    case "jobs": jobs = ReadStrings(ref reader); break;
                    case "stat": stat = String(ref reader); break;
                    case "stats": statRows = ReadStatRows(ref reader); break;
                    case "derived": derived = ReadPairs(ref reader); break;
                    case "vitals": vitals = ReadVitals(ref reader); break;
                    case "target": target = reader.GetInt64(); break;
                    case "killer": killer = reader.GetInt64(); break;
                    case "hp": hp = reader.GetInt32(); break;
                    case "base_level": baseLevel = reader.GetInt32(); break;
                    case "base_exp": baseExp = reader.GetInt64(); break;
                    case "base_next": baseNext = reader.GetInt64(); break;
                    case "job_level": jobLevel = reader.GetInt32(); break;
                    case "job_exp": jobExp = reader.GetInt64(); break;
                    case "job_next": jobNext = reader.GetInt64(); break;
                    case "entry": entry = reader.GetInt64(); break;
                    case "slot": slot = String(ref reader); break;
                    case "rev": rev = reader.GetInt64(); break;
                    case "slots": slots = reader.GetInt32(); break;
                    case "weight": weight = reader.GetInt32(); break;
                    case "weight_limit": weightLimit = reader.GetInt32(); break;
                    case "load": load = String(ref reader); break;
                    case "cause": cause = String(ref reader); break;
                    case "item": item = String(ref reader); break;
                    case "entries": inventoryEntries = ReadEntries(ref reader); break;
                    case "result": result = String(ref reader); break;
                    case "refine": refine = reader.GetInt32(); break;
                    case "currency": currency = reader.GetInt64(); break;
                    case "shop": shopId = String(ref reader); break;
                    case "max_quantity": maxQuantity = reader.GetInt32(); break;
                    case "stock": shopStock = ReadStock(ref reader); break;
                    case "sell": shopSell = ReadStock(ref reader); break;
                    case "quest": quest = String(ref reader); break;
                    case "change": change = String(ref reader); break;
                    case "mode": mode = String(ref reader); break;
                    case "doing": doing = String(ref reader); break;
                    case "state": questState = ReadQuestEntry(ref reader); break;
                    case "active":
                        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
                        questsActive = new List<QuestEntryRecord>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) questsActive.Add(ReadQuestEntry(ref reader));
                        break;
                    case "completed": completed = ReadStrings(ref reader); break;
                    case "offer": offer = ReadStrings(ref reader); break;
                    case "turn_in": turnIn = ReadStrings(ref reader); break;
                    case "owned": owned = ReadStrings(ref reader); break;
                    case "chosen": chosen = ReadOutfit(ref reader); break;
                    case "kind": kind = String(ref reader); break;
                    case "in": inSeconds = reader.GetInt64(); break;
                    case "retry_after": retryAfter = reader.GetInt64(); break;
                    case "channel": channel = String(ref reader); break;
                    case "name": name = String(ref reader); break;
                    case "to": to = String(ref reader); break;
                    case "line": line = reader.GetInt64(); break;
                    case "from": from = reader.GetInt64(); break;
                    case "max_length": maxLength = reader.GetInt32(); break;
                    case "blocked": blocked = ReadStrings(ref reader); break;
                    case "party": partyId = reader.GetInt64(); break;
                    case "leader": leader = reader.GetInt64(); break;
                    case "expires_in": expiresIn = reader.GetInt64(); break;
                    case "notice": notice = String(ref reader); break;
                    case "members": partyMembers = ReadPartyMembers(ref reader); break;
                    case "status": partyStatus = ReadPartyStatus(ref reader); break;
                    case "partner": partner = reader.GetInt64(); break;
                    case "guild": guildState = ReadGuild(ref reader); break;
                    case "title": title = String(ref reader); break;
                    case "emblem": emblem = String(ref reader); break;
                    case "news": news = String(ref reader); break;
                    case "tags": tags = ReadGuildTags(ref reader); break;
                    case "mine": mine = ReadTradeSide(ref reader); break;
                    case "theirs": theirs = ReadTradeSide(ref reader); break;
                    case "own": own = reader.GetBoolean(); break;
                    case "hidden": hidden = reader.GetBoolean(); break;
                    case "removed":
                        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
                        removed = new List<long>();
                        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) removed.Add(reader.GetInt64());
                        break;
                    default: reader.Skip(); break;
                }
            }
            ServerMessage? message = type switch
            {
                MessageTypes.Welcome => map is null ? null : new WelcomeMessage(version, id, map, tickRate, content, token),
                MessageTypes.Snapshot => map is null ? null : new SnapshotMessage(tick, map, id, entities, vitals),
                MessageTypes.Delta => new DeltaMessage(tick, entities, changed, gone, hits, vitals, casts, castEnds),
                MessageTypes.Rejected => intent is null || reason is null ? null : new RejectedMessage(intent, reason, cell, target, entry, slot, skill, stat, quest, item),
                MessageTypes.AttackStopped => target is null || reason is null ? null : new AttackStoppedMessage(target.Value, reason),
                MessageTypes.Exp => baseLevel is null || jobLevel is null ? null : new ExpMessage(baseLevel.Value, baseExp, baseNext, jobLevel.Value, jobExp, jobNext),
                MessageTypes.LevelUp => (baseLevel is null) == (jobLevel is null) ? null : new LevelUpMessage(baseLevel, jobLevel),
                MessageTypes.YouDied => new YouDiedMessage(killer),
                MessageTypes.Respawned => map is null || cell is null ? null : new RespawnedMessage(map, cell.Value, hp),
                MessageTypes.Inventory => load is null ? null
                    : new InventoryMessage(rev, slots, weight, weightLimit, load, (IReadOnlyList<InventoryEntryRecord>?)inventoryEntries ?? Array.Empty<InventoryEntryRecord>(), currency),
                MessageTypes.InventoryChanged => load is null || cause is null ? null
                    : new InventoryChangedMessage(rev, cause, item, weight, weightLimit, load,
                        (IReadOnlyList<InventoryEntryRecord>?)inventoryEntries ?? Array.Empty<InventoryEntryRecord>(), (IReadOnlyList<long>?)removed ?? Array.Empty<long>(), currency),
                MessageTypes.RefineResult => entry is null || item is null || result is null ? null : new RefineResultMessage(entry.Value, item, result, refine),
                MessageTypes.Skills => job is null ? null
                    : new SkillsMessage(job, points, (levels ?? []).ConvertAll(l => new SkillLevelRecord(l.Item1, l.Item2)),
                        (cooldowns ?? []).ConvertAll(c => new SkillCooldownRecord(c.Item1, c.Item2)), (IReadOnlyList<string>?)jobs ?? Array.Empty<string>()),
                MessageTypes.Stats => new StatsMessage(points, statRows ?? [], (derived ?? []).ConvertAll(d => new DerivedRecord(d.Item1, d.Item2))),
                MessageTypes.Cooldown => skill is null ? null : new CooldownMessage(skill, ms),
                MessageTypes.Dialogue => npc is null || speaker is null || text is null ? null : new DialogueMessage(npc.Value, speaker, portrait, text, (IReadOnlyList<DialogueChoiceRecord>?)choices ?? Array.Empty<DialogueChoiceRecord>(), more),
                MessageTypes.DialogueEnded => reason is null ? null : new DialogueEndedMessage(reason),
                MessageTypes.ServiceOpened => service is null || npc is null ? null : new ServiceOpenedMessage(service, npc.Value),
                MessageTypes.Shop => npc is null || shopId is null ? null : new ShopMessage(npc.Value, shopId, maxQuantity, (IReadOnlyList<ShopStockRecord>?)shopStock ?? Array.Empty<ShopStockRecord>(),
                    (IReadOnlyList<ShopStockRecord>?)shopSell ?? Array.Empty<ShopStockRecord>()),
                MessageTypes.QuestLog => new QuestLogMessage((IReadOnlyList<QuestEntryRecord>?)questsActive ?? Array.Empty<QuestEntryRecord>(),
                    (IReadOnlyList<string>?)completed ?? Array.Empty<string>(), (IReadOnlyList<string>?)offer ?? Array.Empty<string>(), (IReadOnlyList<string>?)turnIn ?? Array.Empty<string>()),
                MessageTypes.Quest => change is null || quest is null ? null : new QuestMessage(change, quest, questState),
                MessageTypes.Autopilot => mode is null ? null : new AutopilotMessage(mode, quest, doing, reason),
                MessageTypes.Wardrobe => new WardrobeMessage((IReadOnlyList<string>?)owned ?? Array.Empty<string>(), (IReadOnlyList<ShownOutfit>?)chosen ?? Array.Empty<ShownOutfit>(), hidden),
                MessageTypes.Pong => new PongMessage(seq),
                MessageTypes.Announcement => kind is null ? null : new AnnouncementMessage(kind, text, inSeconds),
                MessageTypes.ChatLine => line is null || channel is null || from is null || name is null || text is null ? null
                    : new ChatLineMessage(line.Value, channel, from.Value, name, text, to),
                MessageTypes.ChatRefused => intent is null || reason is null ? null : new ChatRefusedMessage(intent, reason, retryAfter, channel, to, line),
                MessageTypes.ChatState => new ChatStateMessage(maxLength, (IReadOnlyList<string>?)blocked ?? Array.Empty<string>()),
                MessageTypes.ChatReported => line is null ? null : new ChatReportedMessage(line.Value),
                MessageTypes.Party => new PartyMessage(partyId, leader ?? 0, (IReadOnlyList<PartyMemberRecord>?)partyMembers ?? Array.Empty<PartyMemberRecord>()),
                MessageTypes.PartyMembers => new PartyMembersMessage((IReadOnlyList<PartyMemberStatusRecord>?)partyStatus ?? Array.Empty<PartyMemberStatusRecord>()),
                MessageTypes.PartyInvited => from is null || name is null || expiresIn is null ? null : new PartyInvitedMessage(from.Value, name, expiresIn.Value),
                MessageTypes.PartyNotice => notice is null ? null : new PartyNoticeMessage(notice, name),
                MessageTypes.PartyRefused => intent is null || reason is null ? null : new PartyRefusedMessage(intent, reason, retryAfter, name),
                MessageTypes.TradeAsked => from is null || name is null || expiresIn is null ? null : new TradeAskedMessage(from.Value, name, expiresIn.Value),
                MessageTypes.Trade => partner is null || name is null || mine is null || theirs is null ? null : new TradeMessage(partner.Value, name, version, mine, theirs),
                MessageTypes.TradeNotice => notice is null ? null : new TradeNoticeMessage(notice, name),
                MessageTypes.TradeClosed => reason is null ? null : new TradeClosedMessage(reason, name, own),
                MessageTypes.TradeRefused => intent is null || reason is null ? null : new TradeRefusedMessage(intent, reason, retryAfter),
                MessageTypes.Guild => guildState ?? GuildMessage.None,
                MessageTypes.GuildInvited => name is null || title is null || emblem is null || expiresIn is null ? null : new GuildInvitedMessage(name, title, emblem, expiresIn.Value),
                MessageTypes.GuildNews => news is null ? null : new GuildNewsMessage(news, name),
                MessageTypes.GuildRefused => intent is null || reason is null ? null : new GuildRefusedMessage(intent, reason, retryAfter, name),
                MessageTypes.GuildTags => new GuildTagsMessage((IReadOnlyList<GuildTagRecord>?)tags ?? Array.Empty<GuildTagRecord>()),
                MessageTypes.Bye => reason is null ? null : new ByeMessage(reason, retryAfter),
                null => null,
                _ => throw new UnknownTypeException(),
            };
            return message is null ? Fail<ServerMessage>(ByeReasons.Malformed) : new ReadResult<ServerMessage>(message, null);
        }
        catch (UnknownTypeException)
        {
            return Fail<ServerMessage>(ByeReasons.UnknownType);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            return Fail<ServerMessage>(ByeReasons.Malformed);
        }
    }

    // ---- shared pieces (also used by ViewMessageWriter) ----

    private static void WriteRecord(Utf8JsonWriter w, EntityRecord e) =>
        WriteRecord(w, e.Id, e.Kind, e.DisplayKey, e.Cell, e.Facing, e.Next, e.Speed, e.Hp, e.Dead, e.Quantity, e.Mine, e.Weapon, e.Worn, e.Statuses, e.Appearance, e.Outfit);

    internal static void WriteRecord(Utf8JsonWriter w, long id, string kind, string? displayKey, GridCell cell, string facing, GridCell? next, double speed,
        double? hp, bool dead, int quantity, bool? mine, string? weapon = null, IReadOnlyList<string>? worn = null, IReadOnlyList<StatusRecord>? statuses = null, AppearanceRecord? appearance = null,
        IReadOnlyList<ShownOutfit>? outfit = null)
    {
        w.WriteStartObject();
        w.WriteNumber("id", id);
        w.WriteString("kind", kind);
        if (displayKey is not null) w.WriteString("display_key", displayKey);
        WriteCell(w, "cell", cell);
        w.WriteString("facing", facing);
        if (next is { } n) WriteCell(w, "next", n);
        if (speed != 0) w.WriteNumber("speed", Round(speed));
        if (hp is { } fraction) w.WriteNumber("hp", Round(fraction));
        if (dead) w.WriteBoolean("dead", true);
        if (quantity != 0) w.WriteNumber("quantity", quantity);
        if (mine is { } m) w.WriteBoolean("mine", m);
        if (weapon is not null) w.WriteString("weapon", weapon);
        if (worn is { Count: > 0 }) WriteStrings(w, "worn", worn);
        if (statuses is { Count: > 0 }) WriteStatuses(w, statuses);
        if (appearance is not null) WriteAppearance(w, "appearance", appearance);
        if (outfit is { Count: > 0 }) WriteOutfit(w, "outfit", outfit);
        w.WriteEndObject();
    }

    /// <summary><c>"appearance":{"gender":..,"skin":..,"face":..,"hair":..,"hair_color":..}</c> (T-705): ids only. Also the shape in the account endpoints.</summary>
    internal static void WriteAppearance(Utf8JsonWriter w, string name, AppearanceRecord appearance)
    {
        w.WriteStartObject(name);
        w.WriteString("gender", appearance.Gender);
        w.WriteString("skin", appearance.Skin);
        w.WriteString("face", appearance.Face);
        w.WriteString("hair", appearance.Hair);
        w.WriteString("hair_color", appearance.HairColor);
        w.WriteEndObject();
    }

    /// <summary>Reads an appearance object; null when it is not one, or lacks a part (the client then draws the defaults).</summary>
    private static AppearanceRecord? ReadAppearance(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            reader.Skip();
            return null;
        }
        string? gender = null, skin = null, face = null, hair = null, hairColor = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "gender": gender = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; break;
                case "skin": skin = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; break;
                case "face": face = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; break;
                case "hair": hair = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; break;
                case "hair_color": hairColor = reader.TokenType == JsonTokenType.String ? reader.GetString() : null; break;
                default: reader.Skip(); break;
            }
        }
        return gender is null || skin is null || face is null || hair is null || hairColor is null ? null : new AppearanceRecord(gender, skin, face, hair, hairColor);
    }

    /// <summary><c>"outfit":{"head":"outfit.x","body":"outfit.y"}</c> (T-708): ids by slot; <c>{}</c> when none shows.</summary>
    internal static void WriteOutfit(Utf8JsonWriter w, string name, IReadOnlyList<ShownOutfit> outfit)
    {
        w.WriteStartObject(name);
        for (var i = 0; i < outfit.Count; i++) w.WriteString(outfit[i].Slot, outfit[i].Outfit);
        w.WriteEndObject();
    }

    /// <summary><c>"members":[{"id":42,"name":"Ada"},...]</c> (T-902).</summary>
    private static List<PartyMemberRecord> ReadPartyMembers(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<PartyMemberRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long? id = null;
            string? name = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "id": id = reader.GetInt64(); break;
                    case "name": name = String(ref reader); break;
                    default: reader.Skip(); break;
                }
            }
            if (id is null || name is null) throw Bad();
            list.Add(new PartyMemberRecord(id.Value, name));
        }
        return list;
    }

    /// <summary><c>"status":[{"id":43,"hp":80,"sp":100,"map":..,"cell":[x,y],"dead":true,"away":true},...]</c> (T-902).</summary>
    private static List<PartyMemberStatusRecord> ReadPartyStatus(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<PartyMemberStatusRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long? id = null;
            int hp = 0, sp = 0;
            string? map = null;
            GridCell? cell = null;
            bool dead = false, away = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "id": id = reader.GetInt64(); break;
                    case "hp": hp = reader.GetInt32(); break;
                    case "sp": sp = reader.GetInt32(); break;
                    case "map": map = String(ref reader); break;
                    case "cell": cell = Cell(ref reader); break;
                    case "dead": dead = reader.GetBoolean(); break;
                    case "away": away = reader.GetBoolean(); break;
                    default: reader.Skip(); break;
                }
            }
            if (id is null) throw Bad();
            list.Add(new PartyMemberStatusRecord(id.Value, hp, sp, map, cell, dead, away));
        }
        return list;
    }

    /// <summary>What <see cref="WriteOutfit"/> writes; a value that is not a string is left out (the client then draws the gear there).</summary>
    private static List<ShownOutfit> ReadOutfit(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw Bad();
        var outfit = new List<ShownOutfit>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var slot = reader.GetString()!;
            reader.Read();
            if (reader.TokenType == JsonTokenType.String) outfit.Add(new ShownOutfit(slot, reader.GetString()!));
            else reader.Skip();
        }
        return outfit;
    }

    /// <summary><c>"statuses":[{"id":..,"stacks":..,"left_ms":..,"debuff":true},...]</c> (T-501); <c>debuff</c> only when true.</summary>
    internal static void WriteStatuses(Utf8JsonWriter w, IReadOnlyList<StatusRecord> statuses)
    {
        w.WriteStartArray("statuses");
        for (var i = 0; i < statuses.Count; i++)
        {
            var s = statuses[i];
            w.WriteStartObject();
            w.WriteString("id", s.Id);
            w.WriteNumber("stacks", s.Stacks);
            w.WriteNumber("left_ms", s.LeftMs);
            if (s.Debuff) w.WriteBoolean("debuff", true);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static List<StatusRecord> ReadStatuses(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<StatusRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            string? id = null;
            int stacks = 1, left = 0;
            var debuff = false;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = String(ref reader); break;
                    case "stacks": stacks = reader.GetInt32(); break;
                    case "left_ms": left = reader.GetInt32(); break;
                    case "debuff": debuff = reader.GetBoolean(); break;
                    default: reader.Skip(); break;
                }
            }
            list.Add(new StatusRecord(id ?? throw Bad(), stacks, left, debuff));
        }
        return list;
    }

    private static void WriteStrings(Utf8JsonWriter w, string name, IReadOnlyList<string> values)
    {
        w.WriteStartArray(name);
        for (var i = 0; i < values.Count; i++) w.WriteStringValue(values[i]);
        w.WriteEndArray();
    }

    private static void WriteEntry(Utf8JsonWriter w, InventoryEntryRecord e)
    {
        w.WriteStartObject();
        w.WriteNumber("id", e.Id);
        w.WriteString("item", e.Item);
        w.WriteNumber("quantity", e.Quantity);
        if (e.Slot is not null) w.WriteString("slot", e.Slot);
        if (e.Cards is { Count: > 0 } cards) WriteStrings(w, "cards", cards);
        if (e.Refine != 0) w.WriteNumber("refine", e.Refine);
        w.WriteEndObject();
    }

    private static List<InventoryEntryRecord> ReadEntries(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<InventoryEntryRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long id = 0;
            string? item = null, slot = null;
            var quantity = 0;
            var refine = 0;
            List<string>? cards = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = reader.GetInt64(); break;
                    case "item": item = String(ref reader); break;
                    case "quantity": quantity = reader.GetInt32(); break;
                    case "slot": slot = String(ref reader); break;
                    case "cards": cards = ReadStrings(ref reader); break;
                    case "refine": refine = reader.GetInt32(); break;
                    default: reader.Skip(); break;
                }
            }
            list.Add(new InventoryEntryRecord(id, item ?? throw Bad(), quantity, slot, cards, refine));
        }
        return list;
    }

    private static void WriteQuestEntry(Utf8JsonWriter w, QuestEntryRecord e)
    {
        w.WriteStartObject();
        w.WriteString("quest", e.Quest);
        w.WriteNumber("step", e.Step);
        w.WriteStartArray("objectives");
        foreach (var o in e.Objectives)
        {
            w.WriteStartObject();
            w.WriteString("type", o.Type);
            if (o.Target is not null) w.WriteString("target", o.Target);
            w.WriteNumber("have", o.Have);
            w.WriteNumber("need", o.Need);
            w.WriteEndObject();
        }
        w.WriteEndArray();
        if (e.Held is not null) w.WriteString("held", e.Held);
        w.WriteEndObject();
    }

    private static QuestEntryRecord ReadQuestEntry(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw Bad();
        string? quest = null, held = null;
        var step = 0;
        var objectives = new List<QuestObjectiveRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString();
            reader.Read();
            switch (key)
            {
                case "quest": quest = String(ref reader); break;
                case "step": step = reader.GetInt32(); break;
                case "held": held = String(ref reader); break;
                case "objectives":
                    if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
                    while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
                    {
                        if (reader.TokenType != JsonTokenType.StartObject) throw Bad();
                        string? type = null, target = null;
                        int have = 0, need = 0;
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var field = reader.GetString();
                            reader.Read();
                            switch (field)
                            {
                                case "type": type = String(ref reader); break;
                                case "target": target = String(ref reader); break;
                                case "have": have = reader.GetInt32(); break;
                                case "need": need = reader.GetInt32(); break;
                                default: reader.Skip(); break;
                            }
                        }
                        objectives.Add(new QuestObjectiveRecord(type ?? throw Bad(), target, have, need));
                    }
                    break;
                default: reader.Skip(); break;
            }
        }
        return new QuestEntryRecord(quest ?? throw Bad(), step, objectives, held);
    }

    private static List<DialogueChoiceRecord> ReadChoices(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<DialogueChoiceRecord>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw Bad();
            int? index = null;
            string? text = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "index": index = reader.GetInt32(); break;
                    case "text": text = String(ref reader); break;
                    default: reader.Skip(); break;
                }
            }
            list.Add(new DialogueChoiceRecord(index ?? throw Bad(), text ?? throw Bad()));
        }
        return list;
    }

    private static void WriteStock(Utf8JsonWriter w, string name, IReadOnlyList<ShopStockRecord> rows)
    {
        w.WriteStartArray(name);
        foreach (var row in rows)
        {
            w.WriteStartObject();
            w.WriteString("item", row.Item);
            w.WriteNumber("price", row.Price);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static List<ShopStockRecord> ReadStock(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<ShopStockRecord>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.StartObject) throw Bad();
            string? item = null;
            long? price = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "item": item = String(ref reader); break;
                    case "price": price = reader.GetInt64(); break;
                    default: reader.Skip(); break;
                }
            }
            list.Add(new ShopStockRecord(item ?? throw Bad(), price ?? throw Bad()));
        }
        return list;
    }

    /// <summary><c>"ranks":[{"name":"Officer","permissions":["invite"]},...]</c> (T-904).</summary>
    private static void WriteGuildRanks(Utf8JsonWriter w, IReadOnlyList<GuildRankRecord> ranks)
    {
        w.WriteStartArray("ranks");
        foreach (var r in ranks)
        {
            w.WriteStartObject();
            w.WriteString("name", r.Name);
            WriteStrings(w, "permissions", r.Permissions);
            w.WriteEndObject();
        }
        w.WriteEndArray();
    }

    private static List<GuildRankRecord> ReadGuildRanks(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<GuildRankRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            string? name = null;
            List<string>? permissions = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "name": name = String(ref reader); break;
                    case "permissions": permissions = ReadStrings(ref reader); break;
                    default: reader.Skip(); break;
                }
            }
            if (name is null) throw Bad();
            list.Add(new GuildRankRecord(name, (IReadOnlyList<string>?)permissions ?? Array.Empty<string>()));
        }
        if (reader.TokenType != JsonTokenType.EndArray) throw Bad();
        return list;
    }

    /// <summary>The <c>guild</c> object of a <c>guild</c> message (T-904); a JSON null reads as no guild.</summary>
    private static GuildMessage ReadGuild(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return GuildMessage.None;
        if (reader.TokenType != JsonTokenType.StartObject) throw Bad();
        long? id = null;
        long own = 0;
        string? name = null, emblem = null, notice = null;
        List<GuildRankRecord>? ranks = null;
        var members = new List<GuildMemberRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var key = reader.GetString();
            reader.Read();
            switch (key)
            {
                case "id": id = reader.GetInt64(); break;
                case "name": name = String(ref reader); break;
                case "emblem": emblem = String(ref reader); break;
                case "notice": notice = String(ref reader); break;
                case "own": own = reader.GetInt64(); break;
                case "ranks": ranks = ReadGuildRanks(ref reader); break;
                case "members":
                    if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
                    while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
                    {
                        long? number = null;
                        string? who = null;
                        int rank = 0, level = 0;
                        var online = false;
                        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                        {
                            var field = reader.GetString();
                            reader.Read();
                            switch (field)
                            {
                                case "member": number = reader.GetInt64(); break;
                                case "name": who = String(ref reader); break;
                                case "rank": rank = reader.GetInt32(); break;
                                case "level": level = reader.GetInt32(); break;
                                case "online": online = reader.GetBoolean(); break;
                                default: reader.Skip(); break;
                            }
                        }
                        if (number is null || who is null) throw Bad();
                        members.Add(new GuildMemberRecord(number.Value, who, rank, level, online));
                    }
                    break;
                default: reader.Skip(); break;
            }
        }
        if (id is null || name is null || emblem is null) throw Bad();
        return new GuildMessage(id, name, emblem, notice ?? string.Empty, own, (IReadOnlyList<GuildRankRecord>?)ranks ?? Array.Empty<GuildRankRecord>(), members);
    }

    private static List<GuildTagRecord> ReadGuildTags(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<GuildTagRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long? entity = null;
            string? emblem = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var key = reader.GetString();
                reader.Read();
                switch (key)
                {
                    case "entity": entity = reader.GetInt64(); break;
                    case "emblem": emblem = String(ref reader); break;
                    default: reader.Skip(); break;
                }
            }
            if (entity is null || emblem is null) throw Bad();
            list.Add(new GuildTagRecord(entity.Value, emblem));
        }
        return list;
    }

    private static List<string> ReadStrings(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray) list.Add(String(ref reader) ?? throw Bad());
        return list;
    }

    internal static void WriteHit(Utf8JsonWriter w, long attacker, long target, int amount, string result)
    {
        w.WriteStartArray();
        w.WriteNumberValue(attacker);
        w.WriteNumberValue(target);
        w.WriteNumberValue(amount);
        w.WriteStringValue(result);
        w.WriteEndArray();
    }

    internal static void WriteCast(Utf8JsonWriter w, long caster, string skill, int ms)
    {
        w.WriteStartArray();
        w.WriteNumberValue(caster);
        w.WriteStringValue(skill);
        w.WriteNumberValue(ms);
        w.WriteEndArray();
    }

    internal static void WriteCastEnd(Utf8JsonWriter w, long caster, string skill, string result)
    {
        w.WriteStartArray();
        w.WriteNumberValue(caster);
        w.WriteStringValue(skill);
        w.WriteStringValue(result);
        w.WriteEndArray();
    }

    internal static void WriteVitals(Utf8JsonWriter w, int hp, int maxHp, int sp, int maxSp)
    {
        w.WriteStartObject("vitals");
        w.WriteNumber("hp", hp);
        w.WriteNumber("max_hp", maxHp);
        w.WriteNumber("sp", sp);
        w.WriteNumber("max_sp", maxSp);
        w.WriteEndObject();
    }

    internal static void WriteChange(Utf8JsonWriter w, long id, ChangedFields fields, GridCell cell, string? facing, GridCell? next, double speed, string? displayKey,
        double hp, bool dead, bool mine, string? weapon = null, IReadOnlyList<string>? worn = null, IReadOnlyList<StatusRecord>? statuses = null, IReadOnlyList<ShownOutfit>? outfit = null)
    {
        w.WriteStartObject();
        w.WriteNumber("id", id);
        if ((fields & ChangedFields.Cell) != 0) WriteCell(w, "cell", cell);
        if ((fields & ChangedFields.Facing) != 0) w.WriteString("facing", facing);
        if ((fields & ChangedFields.Next) != 0)
        {
            if (next is { } n) WriteCell(w, "next", n);
            else w.WriteNull("next");
        }
        if ((fields & ChangedFields.Speed) != 0) w.WriteNumber("speed", Round(speed));
        if ((fields & ChangedFields.DisplayKey) != 0)
        {
            if (displayKey is null) w.WriteNull("display_key");
            else w.WriteString("display_key", displayKey);
        }
        if ((fields & ChangedFields.Hp) != 0) w.WriteNumber("hp", Round(hp));
        if ((fields & ChangedFields.Dead) != 0) w.WriteBoolean("dead", dead);
        if ((fields & ChangedFields.Mine) != 0) w.WriteBoolean("mine", mine);
        if ((fields & ChangedFields.Look) != 0)
        {
            w.WriteString("weapon", weapon);
            WriteStrings(w, "worn", worn ?? Array.Empty<string>());
        }
        if ((fields & ChangedFields.Statuses) != 0) WriteStatuses(w, statuses ?? Array.Empty<StatusRecord>());
        if ((fields & ChangedFields.Outfit) != 0) WriteOutfit(w, "outfit", outfit ?? Array.Empty<ShownOutfit>());
        w.WriteEndObject();
    }

    internal static void WriteCell(Utf8JsonWriter w, string name, GridCell cell)
    {
        w.WriteStartArray(name);
        w.WriteNumberValue(cell.X);
        w.WriteNumberValue(cell.Y);
        w.WriteEndArray();
    }

    /// <summary>Speeds and HP fractions go out with two decimals: enough to time a step or draw a bar, and short.</summary>
    internal static double Round(double speed) => Math.Round(speed, 2, MidpointRounding.AwayFromZero);

    private static void ReadArray(ref Utf8JsonReader reader, List<EntityRecord> into)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long id = 0;
            string? kind = null, key = null, facing = null;
            GridCell cell = default;
            GridCell? next = null;
            double speed = 0;
            double? hp = null;
            var dead = false;
            var quantity = 0;
            bool? mine = null;
            string? weapon = null;
            List<string>? worn = null;
            List<StatusRecord>? statuses = null;
            AppearanceRecord? appearance = null;
            List<ShownOutfit>? outfit = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = reader.GetInt64(); break;
                    case "kind": kind = String(ref reader); break;
                    case "display_key": key = String(ref reader); break;
                    case "cell": cell = Cell(ref reader) ?? throw Bad(); break;
                    case "facing": facing = String(ref reader); break;
                    case "next": next = Cell(ref reader); break;
                    case "speed": speed = reader.GetDouble(); break;
                    case "hp": hp = reader.GetDouble(); break;
                    case "dead": dead = reader.GetBoolean(); break;
                    case "quantity": quantity = reader.GetInt32(); break;
                    case "mine": mine = reader.GetBoolean(); break;
                    case "weapon": weapon = String(ref reader); break;
                    case "worn": worn = ReadStrings(ref reader); break;
                    case "statuses": statuses = ReadStatuses(ref reader); break;
                    case "appearance": appearance = ReadAppearance(ref reader); break;
                    case "outfit": outfit = ReadOutfit(ref reader); break;
                    default: reader.Skip(); break;
                }
            }
            into.Add(new EntityRecord(id, kind ?? throw Bad(), key, cell, facing ?? throw Bad(), next, speed, hp, dead, quantity, mine, weapon, worn, statuses, appearance, outfit));
        }
    }

    private static void ReadChanges(ref Utf8JsonReader reader, List<ChangeRecord> into)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartObject)
        {
            long id = 0;
            var fields = ChangedFields.None;
            string? facing = null, key = null;
            GridCell cell = default;
            GridCell? next = null;
            double speed = 0, hp = 0;
            bool dead = false, mine = false;
            string? weapon = null;
            List<string>? worn = null;
            List<StatusRecord>? statuses = null;
            List<ShownOutfit>? outfit = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                reader.Read();
                switch (name)
                {
                    case "id": id = reader.GetInt64(); break;
                    case "cell": cell = Cell(ref reader) ?? throw Bad(); fields |= ChangedFields.Cell; break;
                    case "facing": facing = String(ref reader); fields |= ChangedFields.Facing; break;
                    case "next": next = Cell(ref reader); fields |= ChangedFields.Next; break;
                    case "speed": speed = reader.GetDouble(); fields |= ChangedFields.Speed; break;
                    case "display_key": key = String(ref reader); fields |= ChangedFields.DisplayKey; break;
                    case "hp": hp = reader.GetDouble(); fields |= ChangedFields.Hp; break;
                    case "dead": dead = reader.GetBoolean(); fields |= ChangedFields.Dead; break;
                    case "mine": mine = reader.GetBoolean(); fields |= ChangedFields.Mine; break;
                    case "weapon": weapon = String(ref reader); fields |= ChangedFields.Look; break;
                    case "worn": worn = ReadStrings(ref reader); fields |= ChangedFields.Look; break;
                    case "statuses": statuses = ReadStatuses(ref reader); fields |= ChangedFields.Statuses; break;
                    case "outfit": outfit = ReadOutfit(ref reader); fields |= ChangedFields.Outfit; break;
                    default: reader.Skip(); break;
                }
            }
            into.Add(new ChangeRecord(id, fields, cell, facing, next, speed, key, hp, dead, mine, weapon, worn, statuses, outfit));
        }
    }

    /// <summary><c>[[attacker,target,amount,"result"],...]</c>.</summary>
    private static List<HitRecord> ReadHits(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var hits = new List<HitRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var attacker = reader.GetInt64();
            reader.Read();
            var target = reader.GetInt64();
            reader.Read();
            var amount = reader.GetInt32();
            reader.Read();
            var result = String(ref reader) ?? throw Bad();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
            hits.Add(new HitRecord(attacker, target, amount, result));
        }
        return hits;
    }

    /// <summary><c>[[caster,"skill",ms],...]</c>.</summary>
    private static List<CastRecord> ReadCasts(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<CastRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var caster = reader.GetInt64();
            reader.Read();
            var skill = String(ref reader) ?? throw Bad();
            reader.Read();
            var ms = reader.GetInt32();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
            list.Add(new CastRecord(caster, skill, ms));
        }
        return list;
    }

    /// <summary><c>[[caster,"skill","result"],...]</c>.</summary>
    private static List<CastEndRecord> ReadCastEnds(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<CastEndRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var caster = reader.GetInt64();
            reader.Read();
            var skill = String(ref reader) ?? throw Bad();
            reader.Read();
            var result = String(ref reader) ?? throw Bad();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
            list.Add(new CastEndRecord(caster, skill, result));
        }
        return list;
    }

    /// <summary>Skill levels as <c>[["skill.x",n],...]</c>.</summary>
    private static void WriteLevels(Utf8JsonWriter w, string name, IReadOnlyList<SkillLevelRecord> levels)
    {
        w.WriteStartArray(name);
        foreach (var l in levels)
        {
            w.WriteStartArray();
            w.WriteStringValue(l.Skill);
            w.WriteNumberValue(l.Level);
            w.WriteEndArray();
        }
        w.WriteEndArray();
    }

    /// <summary>What <see cref="WriteLevels"/> writes; anything else is malformed.</summary>
    private static List<SkillLevelRecord> ReadLevels(ref Utf8JsonReader reader)
    {
        var pairs = ReadPairs(ref reader);
        if (reader.TokenType != JsonTokenType.EndArray) throw Bad();
        return pairs.ConvertAll(l => new SkillLevelRecord(l.Item1, l.Item2));
    }

    /// <summary><c>[["name",n],...]</c> as (name, number) pairs.</summary>
    private static List<(string, int)> ReadPairs(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<(string, int)>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var name = String(ref reader) ?? throw Bad();
            reader.Read();
            if (reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var n)) throw Bad();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
            list.Add((name, n));
        }
        return list;
    }

    private static List<StatRecord> ReadStatRows(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        var list = new List<StatRecord>();
        while (reader.Read() && reader.TokenType == JsonTokenType.StartArray)
        {
            reader.Read();
            var name = String(ref reader) ?? throw Bad();
            reader.Read();
            var value = reader.GetInt32();
            reader.Read();
            var cost = reader.GetInt32();
            if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
            list.Add(new StatRecord(name, value, cost));
        }
        return list;
    }

    private static VitalsRecord ReadVitals(ref Utf8JsonReader reader)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw Bad();
        int hp = 0, maxHp = 0, sp = 0, maxSp = 0;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var name = reader.GetString();
            reader.Read();
            switch (name)
            {
                case "hp": hp = reader.GetInt32(); break;
                case "max_hp": maxHp = reader.GetInt32(); break;
                case "sp": sp = reader.GetInt32(); break;
                case "max_sp": maxSp = reader.GetInt32(); break;
                default: reader.Skip(); break;
            }
        }
        return new VitalsRecord(hp, maxHp, sp, maxSp);
    }

    /// <summary>A string, or null for a JSON null; anything else is malformed.</summary>
    private static string? String(ref Utf8JsonReader reader) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Null => null,
            _ => throw Bad(),
        };

    /// <summary><c>[x,y]</c> as a cell, or null for a JSON null; anything else (other lengths, non-integers) is malformed.</summary>
    private static GridCell? Cell(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType != JsonTokenType.StartArray) throw Bad();
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var x)) throw Bad();
        if (!reader.Read() || reader.TokenType != JsonTokenType.Number || !reader.TryGetInt32(out var y)) throw Bad();
        if (!reader.Read() || reader.TokenType != JsonTokenType.EndArray) throw Bad();
        return new GridCell(x, y);
    }

    private static JsonException Bad() => new("malformed");

    private static ReadResult<T> Fail<T>(string reason) where T : class => new(null, reason);

    private sealed class UnknownTypeException : Exception;
}
