using System;
using System.Collections.Generic;
using Game.Shared.Definitions;
using Game.Shared.Protocol;
using Game.Shared.Vocabulary;

namespace Game.Shared.Wardrobe
{
    /// <summary>
    /// The outfits of one character (T-708): the ids it owns, each once, and what it chose to show. A plain mutable data holder changed only by <see cref="WardrobeRules"/> (and filled
    /// when the character is loaded). It may hold ids and slots the content does not have: they are kept, count for nothing, and come back with the content. Not thread-safe.
    /// </summary>
    public sealed class WardrobeState
    {
        /// <summary>Owned outfit ids.</summary>
        public HashSet<string> Owned { get; } = new HashSet<string>(StringComparer.Ordinal);

        /// <summary>Slot → the outfit chosen for it. A slot that is not here shows the gear.</summary>
        public Dictionary<string, string> Chosen { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

        /// <summary>The switch that hides every outfit. The choices stay.</summary>
        public bool Hidden { get; set; }
    }

    /// <summary>
    /// What a character may show (T-708, ARCHITECTURE §7.5.2). Pure. Nothing here, and nothing that calls it, may ever read or change anything but looks: an outfit has no part in
    /// stats, combat, speed or any other rule.
    /// </summary>
    public sealed class WardrobeRules
    {
        private readonly Dictionary<string, OutfitDef> _outfits = new Dictionary<string, OutfitDef>(StringComparer.Ordinal);

        /// <summary>Rules over the content's outfits.</summary>
        public WardrobeRules(IEnumerable<OutfitDef> outfits)
        {
            if (outfits == null) throw new ArgumentNullException(nameof(outfits));
            foreach (var outfit in outfits) _outfits[outfit.Id] = outfit;
        }

        /// <summary>
        /// The one way to get an outfit: adds it to what the character owns. True when it is new; owning it already changes nothing, so granting twice is harmless. An id the content
        /// does not have is kept all the same (it was promised), and counts for nothing until the content has it.
        /// </summary>
        public static bool Grant(WardrobeState state, string outfitId)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return !string.IsNullOrEmpty(outfitId) && state.Owned.Add(outfitId);
        }

        /// <summary>True when the content has the outfit.</summary>
        public bool Exists(string outfitId) => _outfits.ContainsKey(outfitId);

        /// <summary>The owned outfits the content has, in id order.</summary>
        public List<string> Owned(WardrobeState state)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var owned = new List<string>(state.Owned.Count);
            foreach (var id in state.Owned)
                if (_outfits.ContainsKey(id)) owned.Add(id);
            owned.Sort(StringComparer.Ordinal);
            return owned;
        }

        /// <summary>
        /// Why <paramref name="outfitId"/> may not show in <paramref name="slot"/> on a character of <paramref name="gender"/> (an <see cref="OutfitReasons"/> term), or null. A null
        /// outfit shows nothing: only the slot must exist.
        /// </summary>
        public string? Check(WardrobeState state, string slot, string? outfitId, string? gender)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            if (!OutfitSlots.All.Contains(slot)) return OutfitReasons.UnknownSlot;
            if (outfitId == null) return null;
            if (!_outfits.TryGetValue(outfitId, out var outfit)) return OutfitReasons.UnknownOutfit;
            if (!state.Owned.Contains(outfitId)) return OutfitReasons.NotOwned;
            var covered = false;
            foreach (var piece in outfit.Pieces)
            {
                if (!string.Equals(piece.Slot, slot, StringComparison.Ordinal)) continue;
                if (piece.Gender == null || string.Equals(piece.Gender, gender, StringComparison.Ordinal)) return null;
                covered = true;
            }
            return covered ? OutfitReasons.WrongGender : OutfitReasons.SlotNotCovered;
        }

        /// <summary>Chooses what shows in a slot (null: nothing) if <see cref="Check"/> allows it; the reason otherwise, with nothing changed.</summary>
        public string? Choose(WardrobeState state, string slot, string? outfitId, string? gender)
        {
            var reason = Check(state, slot, outfitId, gender);
            if (reason != null) return reason;
            if (outfitId == null) state.Chosen.Remove(slot);
            else state.Chosen[slot] = outfitId;
            return null;
        }

        /// <summary>The choices that stand, in slot order, whether or not outfits are hidden: those that are owned, known, and have a piece for the slot and the gender.</summary>
        public List<ShownOutfit> Chosen(WardrobeState state, string? gender)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            var chosen = new List<ShownOutfit>(state.Chosen.Count);
            if (state.Chosen.Count == 0) return chosen;
            foreach (var slot in OutfitSlots.All.Ordered)
                if (state.Chosen.TryGetValue(slot, out var id) && Check(state, slot, id, gender) == null) chosen.Add(new ShownOutfit(slot, id));
            return chosen;
        }

        /// <summary>What others see: <see cref="Chosen"/>, or nothing while outfits are hidden.</summary>
        public List<ShownOutfit> Shown(WardrobeState state, string? gender)
        {
            if (state == null) throw new ArgumentNullException(nameof(state));
            return state.Hidden ? new List<ShownOutfit>() : Chosen(state, gender);
        }
    }
}
