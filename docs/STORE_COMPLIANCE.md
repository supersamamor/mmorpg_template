# Store compliance: accounts, deletion and privacy (T-703)

What the template does today, and what a fork must add before it publishes. **This is not legal advice**: have a lawyer read the privacy policy and check the laws of the countries you publish in.
Store rules change. Everything marked "read" was read on **2026-11-25**; read the pages again before you submit.

## 1. What the rules ask, and what is built

| Rule | Where it says so | The template |
|------|------------------|--------------|
| Apps with account creation must let people **start deletion in the app**, easy to find; deactivating is not enough; they must be told how long it takes | [Apple: offering account deletion (5.1.1(v))](https://developer.apple.com/help/app-review/guideline-reference/5-1-1-account-deletion/) (search summary read) | Built: character list ▸ **Delete account**; the answer says when the data is erased (14 days by default) |
| The same inside the app **and on a web page** that works without the app, whose link goes into the Play Console; the data goes too | [Google Play: app account deletion](https://support.google.com/googleplay/android-developer/answer/13327111?hl=en) (read in full) | In the app: built. **The web page is the fork's** (§5) |
| Another login service must be offered **when a third-party login is** (Facebook, Google); apps that only use their own accounts are excepted | [App Review Guidelines 4.8](https://developer.apple.com/app-store/review/guidelines/) (a summary of the page was read, not its exact words) | Nothing needed: the template has only its own username and password. **Adding a third-party login to a fork brings the rule in**: build Sign in with Apple (or another service that meets 4.8) then, and revoke its tokens on deletion |
| A privacy manifest with reasons for some system functions and the data collected | Apple, "Describing use of required reason API" (forum posts and engine documentation read; Apple's page itself was not) | Set in the iOS export preset (§4); **not checked on a Mac** |
| The Data safety form | [Google Play: Data safety](https://support.google.com/googleplay/android-developer/answer/10787469?hl=en) (read in part) | Answers in §3 |

## 2. What is stored, why and for how long

| Data | Where | Why | Until |
|------|-------|-----|-------|
| Username (normalised), password hash (Argon2id), when created, when the password was last changed | `accounts` (`Rows.cs`) | Signing in | The account is erased |
| A hash of each session token, when it signed in and when it ends | `account_sessions` | Staying signed in (24 hours) | Sign-out, password change, deletion; an expired one goes at the next sign-in |
| Characters: name, job, looks, slot, when created or deleted | `characters` | The game. A *character* that is deleted keeps its row and name, so support can undo it | The account is erased |
| Their state: place, levels, stats, skills, quests, currency, items | `character_states`, `character_items` | The game | The account is erased |
| That an account was asked to be deleted, and when it was erased | `account_deletions`: the account's id and two times | Support, and cleaning a restored backup (§6) | Kept. It holds nothing about the person; the id leads nowhere once the account is gone |
| What staff did to an account (T-804): who (staff id), with which role, the action, the account or character **id**, before and after (ids and numbers only: online or not, map and cell, sessions ended), the reason in the staff member's words, the time, the request id | `admin_audit` (append-only) | Accountability for staff actions, support, disputes, security | Kept, also after the account is erased: see §6 "Audit rows" |
| Bans and mutes of an account (T-806): kind, from, until, lifted, and the audit rows that made and lifted them (ids and times; why is in the audit row) | `account_sanctions` | Moderation: enforcing them at sign-in and in the game | The account is erased (a ban of an erased account protects nothing: the id can never sign in again, and keeping it would keep a record about the person) |
| Support requests for deletion or a data copy (T-806): kind, status, times, audit row ids. What the player wrote stays in the support tool | `support_requests` | The store-compliance queue (ARCHITECTURE §12.4) | The account is erased |
| That an item was made by staff (T-806): its permanent id, item id, quantity, audit row | `admin_items` | Dupe audits (`source = admin`, ARCHITECTURE §12.3) | The item is erased with its owner's account; an item it gave away keeps its mark, and the mark names no character |
| Staff members' ids (the identity provider's subject), display names and last role | `admin_staff` | Naming who wrote an audit row | Staff, not players: the fork's own staff policy. Kept while an audit row names them |
| Chat blocks (T-901): which characters an account blocks, and since when | `chat_blocks` | The game: that character's lines no longer reach the account, and its whispers are refused | Either account is erased (the blocker's, or the blocked character's) |
| Chat reports (T-901): a line a player reported, as the server sent it (the text, the channel, when it was said), who said it and who reported it (account and character ids), when, and whether staff closed it (and the audit row) | `chat_reports` | Moderation: staff read a report and decide (ARCHITECTURE §12.8). The only place a line a player wrote is stored | Closed: 30 days after it was closed (`--chat-closed-report-days`). Open: until staff close it. Either way, until either account is erased |
| Chat lines (T-901) | Server **memory** only: every line for 10 minutes (`--chat-report-window-seconds`) so it can be reported; never written to disk or a log | Reports | 10 minutes, or a restart. The client keeps what is on screen in this session, in memory, and forgets it when the player signs out |
| IP address | Server **logs** only (sign-ins and connections), and in memory for the limits on guessing (`AccountGuard.cs`) | Security | The log's own lifetime: **the fork decides** and says so in its policy. The template writes to standard output and keeps no log files |
| Preferences: sound volumes, UI size | On the device only (`user://preferences.json`) | Convenience | The app is removed |

**Not collected:** e-mail, real name, age, location, contacts, photos, microphone, camera, device or advertising identifiers, analytics, crash reports, payments. No third-party SDK is in the client and no tracking happens.
Passwords and tokens are never logged and never written to the device.

## 3. Google Play: Data safety answers for the template as it is

| Question | Answer |
|----------|--------|
| Does the app collect or share data? | Collects: yes. Shares: no |
| Is all of it encrypted in transit? | Yes: a release build refuses anything but `wss://` and `https://` (`TransportPolicy`) |
| Can users ask for deletion? | Yes, in the app; add your web link |
| **Personal info ▸ User IDs** (the username) | Collected, required, not shared. Purposes: app functionality, account management, fraud prevention and security |
| **App activity ▸ Other actions** (gameplay) | Collected, required, not shared. Purpose: app functionality |
| **App activity ▸ Other user-generated content** (character names; T-901: chat lines, which only a report stores) | Collected, required, not shared. Purpose: app functionality (moderation is part of it). Lines a player sends go to the other players who receive them: that is the app's function, not sharing with a third party |
| **Messages ▸ Other in-app messages** (T-901: chat) | Collected (a line is kept in memory for 10 minutes, and stored only when another player reports it), not shared, optional (nobody has to chat). Purpose: app functionality |
| Location | No: the IP address is not used to find out where somebody is. If you start doing that, declare it |
| Anything else | No |

## 4. Apple

**App privacy questions (App Store Connect):** *User ID* (the username) and *Gameplay content* (saved characters): collected, linked to the user, not used for tracking, purpose *App Functionality*. Since T-901 also *Other User Content* (chat lines a player reports, with who said them): collected, linked, not for tracking, *App Functionality*. Nothing else.

**Privacy manifest.** Godot writes `PrivacyInfo.xcprivacy` from the export preset `iOS (Xcode project)` (`client/export_presets.cfg`, `privacy/*`):

| Setting | Value | Means |
|---------|-------|-------|
| `tracking_enabled`, `tracking_domains` | false, none | No tracking |
| `file_timestamp_access_reasons` | 2 | `C617.1`: files in the app's own container |
| `system_boot_time_access_reasons` | 1 | `35F9.1`: measuring time inside the app |
| `disk_space_access_reasons` | 1 | `E174.1`: checking for space before writing |
| `user_defaults_access_reasons` | 4 | `CA92.1`: the app's own defaults |
| `collected_data/user_id`, `collected_data/gameplay_content`, `collected_data/other_user_content` (T-901) | collected, linked, not for tracking, purpose 2 | *App Functionality* |

The numbers are flags in the order the engine lists its reasons; they were read from the pinned engine's own tables, because neither its documentation nor this PC can show the result: **Godot exports C# projects to iOS
only on a Mac.** On the Mac, open the generated `PrivacyInfo.xcprivacy` and compare it with the table before the first upload. `active_keyboard_access_reasons` is left out: the app has no custom keyboard.

## 5. What a fork must add

1. **A privacy policy** on the web, linked in both stores and in the app's store page. Outline (*your lawyer finishes it*): who you are and how to reach you; what is stored, why and for how long (§2, with your log
   lifetime and your hosting company); who else sees it (your hosting company; nobody else in the template); the country it is in; people's rights and how to use them (deletion in the app or through support; a copy of one's data: support
   asks for it with `complete_request` on an `export` request (T-806, docs/ADMIN_API.md) and sends the JSON; the server sends no e-mail); children; changes to the policy.
2. **A web page for deletion** (Google Play). It names the app or developer as in the store, loads without signing in, says what is deleted and what is kept (§2) and how long it takes, and takes a request: a short form
   or an e-mail address is enough. Somebody then checks that the request is the owner's and deletes the account. With no e-mail stored, the proof is the password: the simplest page tells people to install
   the app and delete there, and offers the form to those who cannot.
3. **Support.** A player who forgot the password cannot delete in the app (there is no e-mail to reset it). Say in the policy how you check who somebody is.
4. **Age rating.** The questionnaires ask about fantasy violence (yes, mild in the template), chat between players (**yes since T-901**: players talk on the map and in whispers, unfiltered; answer the
   "users can interact / communicate" questions yes, and say what moderates it: the server's text rules, blocking, reporting a line to staff, staff mutes and bans; Apple's 1.2 (user-generated content)
   asks for exactly those, and for a way to contact you) and purchases (none until T-709). A theme for younger players may need chat off or limited: that is a fork's change. If your theme is made for children,
   other laws and store programmes apply: ask your lawyer first.
5. **Where you publish.** The EU, UK, Brazil, California and others have laws of their own about consent, data copies and answering times.

**When you add something, come back here:** analytics, crash reporting, advertising, push messages, purchases (T-709), chat (done: T-901; a word filter or chat history would come back here), an e-mail address, a third-party login. Each changes the two forms, the privacy manifest and the
policy, and whatever it stores about a person must be erased with the account: add it to `PostgresAccountStore.EraseAsync`. The test `Erasing_leaves_nothing_but_a_record_without_personal_data` reads every table and fails on
names and ids that are left. Data a service of somebody else holds must be deleted there too.

## 6. How deletion works

1. The player types the password again and presses twice. Every session ends, the game with it; the characters leave the world and are saved. From here nobody can sign in.
2. **The wait:** `--account-erase-days` (0 to 90, default 14). A wait protects somebody whose account was taken over, and second thoughts. Signing in says how long is left and offers **Keep my account**; nothing else works.
   The rules ask for a real deletion and to say how long it takes; they allow a wait that is stated. Keep it short, and write it in your policy.
3. **Erasing** (`AccountEraser`): a pass a minute takes what is due, one transaction per account: items, states, characters (deleted ones too), sessions, the account. A character that a live server still holds puts it off until it
   has left. The **username and the character names become free**: keeping them would keep something about the person, and hashing a name hides nothing. Somebody can take a name a minute after it was erased; the wait is what
   keeps that from being used against a player who comes back.
3a. **Through support** (T-806): a deletion request that came in by e-mail or the web page goes into the support queue (`open_request`, once support has checked who asked) and is completed with
   `complete_request`: the same request as the in-app one, with the same wait, record and ended sessions; the owner can keep the account by signing in during the wait. Erasing also takes the
   account's bans, mutes and support requests, and the `admin_items` marks of the items erased with it (`PostgresAccountStore.EraseAsync`; the test `Erasing_the_account_takes_its_bans_mutes_requests_and_item_marks`).
   A banned player cannot sign in to delete in the app: the support path is theirs.
3b. **Chat** (T-901): erasing takes the account's blocks and the blocks of its characters by others, and every chat report it made or that names what it said (whoever made it). Lines in server
   memory go within 10 minutes anyway.
4. What others got from the player (traded items) stays theirs. The app cannot tell the player that it is done, since it has no way to reach them; signing in then answers as if the account had never been.

**Audit rows** (T-804, ARCHITECTURE §12.3). Erasing an account leaves the audit rows about it: nothing may change or delete them (the table's triggers refuse it, the server's own role included), so a staff
action can always be traced. They hold nothing personal of the player: the target is the account or character **id**, which leads nowhere once the account is gone, and before and after hold ids and numbers
only, never a username, a character name, a password hash or a token (the server writes nothing else there; a test reads them after an erasure). What can hold personal data is the **reason**, because staff write it:
the panel (T-805) must tell them to write what happened and never a name, an e-mail address or anything else about a person. A row that holds personal data all the same, and must go, is taken out by whoever owns the
database, in a reviewed maintenance step outside the game (as a migration would: drop the trigger, delete the row, put the trigger back), and that step is recorded where your policy says. Say in the policy that staff
actions are kept, why and for how long; a fork that must not keep them for ever adds a retention step the same way.

**Restoring a backup** brings erased accounts back, so clean it before anybody can sign in:

1. *Before* restoring, save the list from the running database: `COPY (SELECT account_id, requested_at, erased_at FROM account_deletions) TO STDOUT` (keep a copy outside the database with every backup run, for the day the
   database itself is lost).
2. Restore, and start no server yet.
3. Load the list into a table `restored` and run:
   `INSERT INTO account_deletions SELECT * FROM restored ON CONFLICT (account_id) DO NOTHING; UPDATE accounts SET erase_after = now() WHERE id IN (SELECT account_id FROM restored); DELETE FROM account_sessions WHERE account_id IN (SELECT account_id FROM restored);`
   Requests that were still waiting are erased at once this way; give those their own time instead if you prefer (`requested_at` plus your wait).
4. Start the servers: the first pass erases them again. Delete old backups after the time your policy names.
