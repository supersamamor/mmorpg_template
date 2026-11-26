using System;
using System.Collections.Generic;
using Game.Client.Core.Bundle;
using Game.Shared.ClientContent;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Client.Core.Outfits;

/// <summary>One slot of the wardrobe panel: the owned outfits that have something for it on this character, and the chosen one (null: the gear shows).</summary>
/// <param name="Slot">An "Outfit slots" term.</param>
/// <param name="Outfits">Outfits, in id order.</param>
/// <param name="Chosen">The chosen outfit's id, or null.</param>
public sealed record WardrobeRow(string Slot, IReadOnlyList<ClientOutfit> Outfits, string? Chosen);

/// <summary>
/// The player's own outfits as the server last said (T-708, <c>wardrobe</c>): what is owned, what is chosen by slot, and the switch that hides them. It decides nothing: a choice goes to
/// the server and comes back here. <see cref="Version"/> moves on every change, so the panel redraws only then.
/// </summary>
public sealed class WardrobeBook
{
    private readonly DisplayBundle _bundle;
    private readonly HashSet<string> _owned = new HashSet<string>(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _chosen = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Creates an empty book (nothing owned until the server says).</summary>
    public WardrobeBook(DisplayBundle bundle) => _bundle = bundle;

    /// <summary>True once the server has sent the wardrobe.</summary>
    public bool Known { get; private set; }

    /// <summary>Every outfit is hidden.</summary>
    public bool Hidden { get; private set; }

    /// <summary>The owned outfit ids.</summary>
    public IReadOnlyCollection<string> Owned => _owned;

    /// <summary>Counts changes.</summary>
    public int Version { get; private set; }

    /// <summary>The outfit chosen for a slot, or null.</summary>
    public string? Chosen(string slot) => _chosen.TryGetValue(slot, out var outfit) ? outfit : null;

    /// <summary>Takes a <c>wardrobe</c> message. The ids in <paramref name="gained"/> (cleared first) are new since the last one; none for the first of a login.</summary>
    public void Apply(WardrobeMessage message, List<string> gained)
    {
        gained.Clear();
        if (Known)
            foreach (var id in message.Owned)
                if (!_owned.Contains(id)) gained.Add(id);
        _owned.Clear();
        _chosen.Clear();
        foreach (var id in message.Owned) _owned.Add(id);
        foreach (var shown in message.Chosen) _chosen[shown.Slot] = shown.Outfit;
        Hidden = message.Hidden;
        Known = true;
        Version++;
    }

    /// <summary>A new login.</summary>
    public void Reset()
    {
        _owned.Clear();
        _chosen.Clear();
        Hidden = false;
        Known = false;
        Version++;
    }

    /// <summary>The panel's rows for a character of <paramref name="gender"/>, in slot order: only slots that some owned outfit has something for.</summary>
    public IReadOnlyList<WardrobeRow> Rows(string? gender)
    {
        var rows = new List<WardrobeRow>();
        foreach (var slot in OutfitSlots.All.Ordered)
        {
            var outfits = new List<ClientOutfit>();
            foreach (var outfit in _bundle.Outfits)
                if (_owned.Contains(outfit.Id) && OutfitPieces.Find(outfit, slot, gender) is not null) outfits.Add(outfit);
            if (outfits.Count > 0) rows.Add(new WardrobeRow(slot, outfits, Chosen(slot)));
        }
        return rows;
    }
}

/// <summary>Which piece of an outfit a slot draws.</summary>
public static class OutfitPieces
{
    /// <summary>The outfit's piece for a slot on a character of <paramref name="gender"/>, or null when it has none.</summary>
    public static ClientOutfitPiece? Find(ClientOutfit outfit, string slot, string? gender)
    {
        foreach (var piece in outfit.Pieces)
            if (piece.Slot == slot && (piece.Gender is null || piece.Gender == gender)) return piece;
        return null;
    }
}
