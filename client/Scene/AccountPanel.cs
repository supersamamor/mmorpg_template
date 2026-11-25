using System.Collections.Generic;
using Game.Client.Core;
using Game.Client.Core.Accounts;
using Game.Client.Core.Bundle;
using Game.Client.Core.Net;
using Game.Shared.Protocol;
using Godot;

namespace Game.Client.Scene;

/// <summary>
/// The sign-in and character-select screens (T-701, ARCHITECTURE §7.4): code-built controls inside the HUD's safe area, sized in dp times the UI scale (every
/// button and field at least the touch target), words from <see cref="UiText"/>. It only asks (<see cref="AccountFlow"/>); the server decides. It redraws only
/// when the flow's or the connection's version moves. The content scrolls, so it fits a small phone at the largest UI scale.
/// </summary>
public sealed class AccountPanel
{
    private const double PanelWidthDp = 460;

    private readonly ClientSession _session;
    private readonly DisplayBundle _bundle;
    private readonly List<Control> _interactive;
    private readonly Action _tap;
    private readonly bool _devLogin;
    private readonly string _devName;
    private readonly CenterContainer _center = new() { MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };
    private readonly ScrollContainer _scroll = new() { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
    private readonly VBoxContainer _box = new() { MouseFilter = Control.MouseFilterEnum.Ignore, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Label _title = Wrapped();
    private readonly Label _notice = Wrapped();
    private readonly Label _accountNotice = Wrapped();

    private readonly VBoxContainer _signIn = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly LineEdit _username = new() { PlaceholderText = UiText.Username, MaxLength = 24 };
    private readonly LineEdit _password = new() { PlaceholderText = UiText.Password, Secret = true, MaxLength = 128 };
    private readonly HBoxContainer _signInButtons = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly Button _signInButton = new() { Text = UiText.SignIn, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _registerButton = new() { Text = UiText.Register, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _devButton = new() { Text = UiText.DevLogin };

    private readonly VBoxContainer _characters = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly VBoxContainer _rows = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly List<Row> _rowList = new();
    private readonly VBoxContainer _newBox = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
    private readonly LineEdit _name = new() { MaxLength = 32, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
    private readonly Button _createButton = new() { Text = UiText.CreateCharacter };
    private readonly Button _signOutButton = new() { Text = UiText.SignOut };

    private float _target = 48;
    private float _playWidth = 96;
    private int _shownFlow = -1;
    private int _shownConnection = -1;
    private int _shownCharacters;
    private IReadOnlyList<CharacterRecord>? _samples;
    private AccountScreen _sampleScreen;

    /// <summary>Builds the panel (hidden) under <paramref name="parent"/>; every control that takes touches goes into <paramref name="interactive"/>.</summary>
    public AccountPanel(Control parent, ClientSession session, DisplayBundle bundle, ClientConfig config, List<Control> interactive, Action tap)
    {
        _session = session;
        _bundle = bundle;
        _interactive = interactive;
        _tap = tap;
        _devLogin = config.AllowCleartext; // a debug build; the server refuses the dev login outside development anyway
        _devName = config.DevName;
        parent.AddChild(_center);
        _center.AddChild(_scroll);
        _scroll.AddChild(_box);
        _box.AddChild(_title);
        _box.AddChild(_notice);
        _box.AddChild(_accountNotice);

        _box.AddChild(_signIn);
        _signIn.AddChild(_username);
        _signIn.AddChild(_password);
        _signIn.AddChild(_signInButtons);
        _signInButtons.AddChild(_signInButton);
        _signInButtons.AddChild(_registerButton);
        _signIn.AddChild(_devButton);
        _devButton.Visible = _devLogin;
        _signInButton.Pressed += () => Submit(_session.Accounts.SignIn);
        _registerButton.Pressed += () => Submit(_session.Accounts.Register);
        _password.TextSubmitted += _ => Submit(_session.Accounts.SignIn);
        _devButton.Pressed += () =>
        {
            _tap();
            var typed = _username.Text.Trim();
            _session.Login(typed.Length > 0 ? typed : _devName, Clocks.Wall());
        };

        _box.AddChild(_characters);
        _characters.AddChild(_rows);
        _characters.AddChild(_newBox);
        _newBox.AddChild(_name);
        _newBox.AddChild(_createButton);
        _characters.AddChild(_signOutButton);
        _createButton.Pressed += () =>
        {
            _tap();
            _session.Accounts.Create(_name.Text.Trim());
        };
        _signOutButton.Pressed += () =>
        {
            _tap();
            _session.Accounts.SignOut();
        };
        foreach (var control in new Control[] { _username, _password, _signInButton, _registerButton, _devButton, _name, _createButton, _signOutButton }) interactive.Add(control);
    }

    /// <summary>True while it shows.</summary>
    public bool Open => _center.Visible;

    /// <summary>The line the screen shows about the connection, then the account (for the scripted run).</summary>
    public string Text => string.Join(" | ", new[] { _title.Text, _notice.Text, _accountNotice.Text }.Where(t => t.Length > 0));

    /// <summary>The username field (for the scripted run).</summary>
    public LineEdit Username => _username;

    /// <summary>The password field (for the scripted run).</summary>
    public LineEdit Password => _password;

    /// <summary>The sign-in button (for the scripted run).</summary>
    public Button SignInButton => _signInButton;

    /// <summary>The register button (for the scripted run).</summary>
    public Button RegisterButton => _registerButton;

    /// <summary>The new character's name (for the scripted run).</summary>
    public LineEdit NameField => _name;

    /// <summary>The create button (for the scripted run).</summary>
    public Button CreateButton => _createButton;

    /// <summary>The sign-out button (for the scripted run).</summary>
    public Button SignOutButton => _signOutButton;

    /// <summary>The character rows now shown: what each says and its buttons (for the scripted run).</summary>
    public IReadOnlyList<(string Text, Button Play, Button Delete)> Rows => _rowList.Where(r => r.Box.Visible).Select(r => (r.Label.Text, r.Play, r.Delete)).ToList();

    /// <summary>Shows or hides the panel.</summary>
    public void SetOpen(bool open)
    {
        if (open == _center.Visible) return;
        _center.Visible = open;
        _shownFlow = -1;
        if (!open) _password.Text = string.Empty;
        Refresh();
    }

    /// <summary>Lays the panel out for the HUD's safe area (canvas units); <paramref name="dp"/> converts dp to canvas units.</summary>
    public void ApplyMetrics(Func<double, float> dp, float target, Rect2 safe)
    {
        _target = target;
        _playWidth = dp(96);
        var margin = dp(8);
        var width = Mathf.Min(dp(PanelWidthDp), safe.Size.X - 2 * margin);
        _scroll.CustomMinimumSize = new Vector2(width, safe.Size.Y - 2 * margin);
        var inner = width - dp(12); // beside the scroll bar
        var gap = Mathf.RoundToInt(dp(8));
        foreach (var box in new BoxContainer[] { _box, _signIn, _signInButtons, _characters, _rows, _newBox }) box.AddThemeConstantOverride("separation", gap);
        _box.CustomMinimumSize = new Vector2(inner, 0);
        foreach (var label in new[] { _title, _notice, _accountNotice }) label.CustomMinimumSize = new Vector2(inner, 0);
        foreach (var control in new Control[] { _username, _password, _signInButton, _registerButton, _devButton, _name, _createButton, _signOutButton })
            control.CustomMinimumSize = new Vector2(_target, _target);
        foreach (var row in _rowList) Size(row);
        // Last: a control never gets smaller than what its children need, so a smaller screen must have shrunk them first.
        _center.Position = safe.Position;
        _center.Size = safe.Size;
    }

    /// <summary>Redraws what changed.</summary>
    public void Refresh()
    {
        if (!_center.Visible) return;
        var flow = _session.Accounts;
        var connection = _session.Connection;
        if (_shownFlow == flow.Version && _shownConnection == connection.Version) return;
        _shownFlow = flow.Version;
        _shownConnection = connection.Version;
        var screen = _samples is null ? flow.Screen : _sampleScreen;
        var characters = _samples ?? flow.Characters;
        _title.Text = screen == AccountScreen.SignIn ? UiText.SignInTitle : UiText.CharactersTitle;
        Set(_notice, UiText.Notice(connection.Notice));
        if (_samples is null) Set(_accountNotice, flow.Busy ? UiText.AccountBusy : UiText.Account(flow.Notice, flow.Error, flow.RetryAfterSeconds));
        _signIn.Visible = screen == AccountScreen.SignIn;
        _characters.Visible = screen == AccountScreen.Characters;
        foreach (var button in new[] { _signInButton, _registerButton, _createButton, _signOutButton }) button.Disabled = flow.Busy;

        if (characters.Count > _shownCharacters) _name.Text = string.Empty; // the name in the field has become a character
        _shownCharacters = characters.Count;
        while (_rowList.Count < characters.Count) AddRow();
        for (var i = 0; i < _rowList.Count; i++)
        {
            var row = _rowList[i];
            row.Box.Visible = i < characters.Count;
            row.Id = i < characters.Count ? characters[i].Id : null;
            if (row.Id is null) continue;
            row.Label.Text = UiText.CharacterRow(characters[i].Name, _bundle.NameOf(characters[i].Job));
            row.Delete.Text = flow.Deleting == row.Id || _samples is not null ? UiText.ConfirmDelete : UiText.DeleteCharacter;
            row.Play.Disabled = row.Delete.Disabled = flow.Busy;
        }
        _newBox.Visible = characters.Count < (_samples is null ? flow.Slots : characters.Count + 1);
        _name.PlaceholderText = UiText.NewCharacterName(flow.NameMin, flow.NameMax);
    }

    /// <summary>Fills the screens with the longest words they can show, so a layout check sees the worst case; null characters is the sign-in screen.</summary>
    public void ShowSamples(AccountScreen screen, string longName)
    {
        _sampleScreen = screen;
        _samples = screen == AccountScreen.SignIn
            ? []
            : [new CharacterRecord("a", 0, longName, longName), new CharacterRecord("b", 1, longName, longName), new CharacterRecord("c", 2, longName, longName)];
        _shownFlow = -1;
        Set(_accountNotice, UiText.LongestAccount);
        Refresh();
    }

    /// <summary>Back to what the server said.</summary>
    public void ClearSamples()
    {
        _samples = null;
        _shownFlow = -1;
        Refresh();
    }

    private void Submit(Action<string, string> send)
    {
        _tap();
        var password = _password.Text;
        _password.Text = string.Empty; // not kept on screen or in the field, whatever the server answers
        send(_username.Text.Trim(), password);
    }

    private void AddRow()
    {
        var row = new Row();
        row.Label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.Box.AddChild(row.Label);
        row.Box.AddChild(row.Play);
        row.Box.AddChild(row.Delete);
        _rows.AddChild(row.Box);
        _rowList.Add(row);
        row.Play.Pressed += () =>
        {
            _tap();
            if (row.Id is { } id && _samples is null) _session.Play(id, Clocks.Wall());
        };
        row.Delete.Pressed += () =>
        {
            _tap();
            if (row.Id is { } id && _samples is null) _session.Accounts.Delete(id);
        };
        _interactive.Add(row.Play);
        _interactive.Add(row.Delete);
        Size(row);
    }

    private void Size(Row row)
    {
        row.Label.CustomMinimumSize = new Vector2(_playWidth, _target);
        row.Play.CustomMinimumSize = new Vector2(_playWidth * 0.75f, _target);
        row.Delete.CustomMinimumSize = new Vector2(_playWidth * 0.75f, _target);
    }

    private static void Set(Label label, string text)
    {
        label.Text = text;
        label.Visible = text.Length > 0;
    }

    private static Label Wrapped() => new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore, HorizontalAlignment = HorizontalAlignment.Center };

    private sealed class Row
    {
        public readonly HBoxContainer Box = new() { MouseFilter = Control.MouseFilterEnum.Ignore };
        public readonly Label Label = new() { AutowrapMode = TextServer.AutowrapMode.WordSmart, MouseFilter = Control.MouseFilterEnum.Ignore, VerticalAlignment = VerticalAlignment.Center };
        public readonly Button Play = new() { Text = UiText.Play };
        public readonly Button Delete = new() { Text = UiText.DeleteCharacter };
        public string? Id;
    }
}
