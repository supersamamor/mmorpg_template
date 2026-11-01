using System;
using System.Collections.Generic;
using Game.Shared.Vocabulary;

namespace Game.Shared.Audio
{
    /// <summary>
    /// Picks the sound key to play (ARCHITECTURE §7.8). The order is: the key the content names, then the theme's
    /// category default (<c>audio.defaults</c>), then silence (null). Event sounds come only from the theme
    /// (<c>audio.events</c>). Pure lookup; nothing is loaded or played here, so the client and the tests share it.
    /// </summary>
    public sealed class AudioResolver
    {
        private readonly IReadOnlyDictionary<string, string> _defaults;
        private readonly IReadOnlyDictionary<string, string> _events;

        /// <summary>Creates a resolver over the theme's tables.</summary>
        /// <param name="defaults">Category (<see cref="AudioCategories"/>) → sound key.</param>
        /// <param name="events">Event name (<see cref="EventNames"/>) → sound key.</param>
        public AudioResolver(IReadOnlyDictionary<string, string> defaults, IReadOnlyDictionary<string, string> events)
        {
            _defaults = defaults ?? throw new ArgumentNullException(nameof(defaults));
            _events = events ?? throw new ArgumentNullException(nameof(events));
        }

        /// <summary>The key for <paramref name="category"/>: <paramref name="contentKey"/> if set, else the theme default, else null (silence).</summary>
        public string? Resolve(string? contentKey, string category)
        {
            if (!string.IsNullOrEmpty(contentKey)) return contentKey;
            return _defaults.TryGetValue(category, out var key) && key.Length > 0 ? key : null;
        }

        /// <summary>The swing sound of a weapon type (theme default only; weapon types are vocabulary, not content).</summary>
        public string? WeaponSwing(string weaponType) => Resolve(null, AudioCategories.WeaponSwing(weaponType));

        /// <summary>The impact sound of a weapon type (theme default only).</summary>
        public string? WeaponImpact(string weaponType) => Resolve(null, AudioCategories.WeaponImpact(weaponType));

        /// <summary>The sound of a game event, or null when the theme gives none.</summary>
        public string? ForEvent(string eventName) =>
            _events.TryGetValue(eventName, out var key) && key.Length > 0 ? key : null;
    }
}
