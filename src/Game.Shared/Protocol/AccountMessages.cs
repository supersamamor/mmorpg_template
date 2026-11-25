using System.Collections.Generic;

namespace Game.Shared.Protocol
{
    /// <summary>The paths of the account endpoints (ARCHITECTURE §10 "Accounts"), next to the gateway's WebSocket path on the same host and port.</summary>
    public static class AccountPaths
    {
        /// <summary>The prefix of them all.</summary>
        public const string Root = "/account";

        /// <summary><c>POST</c> a <see cref="CredentialsRequest"/>: creates an account and signs in. 201 with a <see cref="SessionAnswer"/>.</summary>
        public const string Register = Root + "/register";

        /// <summary><c>POST</c> a <see cref="CredentialsRequest"/>: signs in. 200 with a <see cref="SessionAnswer"/>.</summary>
        public const string Login = Root + "/login";

        /// <summary><c>POST</c> with the session token, no body: ends the session. 204.</summary>
        public const string Logout = Root + "/logout";

        /// <summary><c>POST</c> a <see cref="ChangePasswordRequest"/> with the session token: changes the password and ends every session of the account (T-710). 200 with the caller's new <see cref="SessionAnswer"/>.</summary>
        public const string Password = Root + "/password";

        /// <summary><c>GET</c>: 200 with a <see cref="CharactersAnswer"/>. <c>POST</c> a <see cref="CreateCharacterRequest"/>: 201 with a <see cref="CharacterRecord"/>. <c>DELETE</c> <c>/{id}</c>: 204.</summary>
        public const string Characters = Root + "/characters";

        /// <summary>The scheme of the <c>Authorization</c> header that carries the session token.</summary>
        public const string TokenScheme = "Bearer ";
    }

    /// <summary>The body of <see cref="AccountPaths.Register"/> and <see cref="AccountPaths.Login"/>. Never log one.</summary>
    /// <param name="Username">The username as typed.</param>
    /// <param name="Password">The password.</param>
    public sealed record CredentialsRequest(string Username, string Password)
    {
        /// <summary>No password in a log line or an exception.</summary>
        public override string ToString() => "CredentialsRequest { *** }";
    }

    /// <summary>The body of <see cref="AccountPaths.Password"/>: <c>{"current":"...","new":"..."}</c>. Never log one.</summary>
    /// <param name="Current">The password the account has.</param>
    /// <param name="New">The one it should have.</param>
    public sealed record ChangePasswordRequest(string Current, string New)
    {
        /// <summary>No password in a log line or an exception.</summary>
        public override string ToString() => "ChangePasswordRequest { *** }";
    }

    /// <summary>A new session: <c>{"token":"...","expires_in":86400}</c>. Never log one.</summary>
    /// <param name="Token">The session token (256 random bits, base64url).</param>
    /// <param name="ExpiresInSeconds">Seconds until it stops working.</param>
    public sealed record SessionAnswer(string Token, long ExpiresInSeconds)
    {
        /// <summary>No token in a log line or an exception.</summary>
        public override string ToString() => "SessionAnswer { *** }";
    }

    /// <summary>
    /// The body of a character creation: <c>{"name":"Ada"}</c>, optionally with <c>"appearance":{"gender":"female","skin":"skin.tan","face":"face.female_calm","hair":"hair.female_bob","hair_color":"haircolor.golden"}</c>
    /// (T-705). Unknown fields are ignored.
    /// </summary>
    /// <param name="Name">The name as typed.</param>
    /// <param name="Appearance">The looks chosen, or null for the defaults.</param>
    public sealed record CreateCharacterRequest(string Name, AppearanceChoice? Appearance = null);

    /// <summary>
    /// What a creation request chooses of the looks (T-705, ARCHITECTURE §7.5.1). Every part is optional; one left out gets the default of the gender chosen (or of the default
    /// gender, the first term of <c>Genders</c>, when no gender is given): the face and hairstyle by that gender's <c>default</c>, the colours by the file's defaults.
    /// </summary>
    /// <param name="Gender">A "Genders" term.</param>
    /// <param name="Skin">A skin tone id (<c>skin.*</c>).</param>
    /// <param name="Face">A face id (<c>face.*</c>) of the gender.</param>
    /// <param name="Hair">A hairstyle id (<c>hair.*</c>) of the gender.</param>
    /// <param name="HairColor">A hair colour id (<c>haircolor.*</c>).</param>
    public sealed record AppearanceChoice(string? Gender = null, string? Skin = null, string? Face = null, string? Hair = null, string? HairColor = null);

    /// <summary>
    /// A character's looks as ids (T-705): <c>{"gender":"male","skin":"skin.light","face":"face.male_calm","hair":"hair.male_crop","hair_color":"haircolor.chestnut"}</c>.
    /// Cosmetic only; the client finds models and colours in its bundle and ignores an id the bundle does not have.
    /// </summary>
    /// <param name="Gender">A "Genders" term.</param>
    /// <param name="Skin">Skin tone id.</param>
    /// <param name="Face">Face id.</param>
    /// <param name="Hair">Hairstyle id.</param>
    /// <param name="HairColor">Hair colour id.</param>
    public sealed record AppearanceRecord(string Gender, string Skin, string Face, string Hair, string HairColor);

    /// <summary>One character of the account: <c>{"id":"...","slot":0,"name":"Ada","job":"job.x"}</c>.</summary>
    /// <param name="Id">Its permanent id (a GUID): what <c>hello</c> names. Not the runtime entity id.</param>
    /// <param name="Slot">Its slot, from 0.</param>
    /// <param name="Name">Its name.</param>
    /// <param name="Job">Its job id (the name is in the client's bundle).</param>
    /// <param name="Appearance">Its looks (T-705), so a select screen can draw it; null when the answer has none.</param>
    public sealed record CharacterRecord(string Id, int Slot, string Name, string Job, AppearanceRecord? Appearance = null);

    /// <summary>The account's characters and the rules for a new one: <c>{"slots":3,"name_min":3,"name_max":16,"characters":[...]}</c>.</summary>
    /// <param name="Slots">How many slots an account has.</param>
    /// <param name="NameMin">Shortest name.</param>
    /// <param name="NameMax">Longest name.</param>
    /// <param name="Characters">The characters, by slot.</param>
    public sealed record CharactersAnswer(int Slots, int NameMin, int NameMax, IReadOnlyList<CharacterRecord> Characters);

    /// <summary>A refusal: <c>{"error":"name_taken"}</c>, with <c>retry_after</c> (seconds) for <c>rate_limited</c> and <c>unavailable</c>.</summary>
    /// <param name="Error">An "Account errors" term.</param>
    /// <param name="RetryAfterSeconds">When to try again, or 0.</param>
    public sealed record AccountErrorAnswer(string Error, long RetryAfterSeconds = 0);
}
