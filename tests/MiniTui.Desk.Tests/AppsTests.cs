namespace MiniTui.Desk.Tests;

/// <summary>Manifests, <c>tuidesk add</c>, <c>remove</c> and <c>apps</c> (spec §16.4, §16.5).</summary>
public sealed class AppsTests : IDisposable
{
    private readonly Scripts _files = new();
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();

    public void Dispose()
    {
        _files.Dispose();
        _output.Dispose();
        _error.Dispose();
    }

    private string Config => Directory.CreateDirectory(_files.Path("config")).FullName;
    private string Added => _files.Path("added");
    private string Bin => Directory.CreateDirectory(_files.Path("bin")).FullName;

    private DeskConfig Load() => DeskConfig.Load(Config, Added, Bin);

    private int Run(params string[] args) =>
        new DeskCommands(Config, Added, Bin, _output, _error) { AnswerTimeout = TimeSpan.FromMilliseconds(500) }.Run(args);

    // A program that says this manifest when asked, and otherwise waits
    private string Answering(string json) => _files.Script($"""
        if [ "$MINITUI_DESK_MANIFEST" = 1 ]; then echo '{json}'; exit 0; fi
        sleep 10
        """);

    // A program on Bin with this name, as a first-party app would be
    private void OnPath(string command) => File.WriteAllText(Path.Combine(Bin, command), "");

    // ---- Loading ----

    [Fact]
    public void The_launcher_has_apps_json_then_the_added_apps_then_the_first_party_ones_on_path()
    {
        File.WriteAllText(Path.Combine(Config, "apps.json"), """[{ "id": "notes", "name": "My notes", "command": "vim" }, { "name": "top", "command": "top" }]""");
        Directory.CreateDirectory(Added);
        File.WriteAllText(DeskApps.PathOf(Added, "k9s"), """{ "id": "k9s", "name": "Kubernetes", "command": "k9s" }""");
        File.WriteAllText(DeskApps.PathOf(Added, "tasks"), """{ "id": "tasks", "name": "My tasks", "command": "todo" }""");
        OnPath("tuinotes");
        OnPath("tuitasks");
        OnPath("tuireader");

        var config = Load();
        Assert.Empty(config.Errors);
        // apps.json's notes and the added tasks replace the first-party ones
        Assert.Equal(["My notes", "top", "Kubernetes", "My tasks", "Reader"], config.Apps.Select(a => a.DisplayName));
        Assert.Equal([AppSource.AppsJson, AppSource.AppsJson, AppSource.Added, AppSource.Added, AppSource.Found], config.Apps.Select(a => a.Source));
        Assert.Equal("tuireader", config.Apps[^1].Command);
    }

    [Fact]
    public void Without_the_added_folder_or_a_path_only_the_two_files_count()
    {
        OnPath("tuinotes");
        Assert.Empty(DeskConfig.Load(Config).Apps);
    }

    [Theory]
    [InlineData("k9s.json", "{ nope", "k9s.json ignored")]
    [InlineData("k9s.json", """{ "id": "other", "command": "k9s" }""", "its id should be \"k9s\"")]
    [InlineData("K9S.json", """{ "id": "K9S", "command": "k9s" }""", "its id should be \"K9S\"")]
    [InlineData("shell.json", """{ "id": "shell", "command": "bash" }""", "shell.json ignored")]
    [InlineData("k9s.json", """{ "id": "k9s" }""", "it has no command")]
    public void A_broken_manifest_is_skipped_and_reported(string file, string json, string said)
    {
        Directory.CreateDirectory(Added);
        File.WriteAllText(Path.Combine(Added, file), json);
        File.WriteAllText(DeskApps.PathOf(Added, "good"), """{ "id": "good", "command": "true" }""");

        var config = Load();
        Assert.Equal("good", Assert.Single(config.Apps).Id);
        Assert.Contains(said, Assert.Single(config.Errors), StringComparison.Ordinal);
    }

    [Fact]
    public void An_answer_is_found_after_whatever_the_terminal_was_sent_first()
    {
        var answer = DeskApps.ParseAnswer("\u001b[?1h\u001b={\"id\":\"notes\",\"name\":\"Notes\"}\r\n");
        Assert.Equal("notes", answer?.Id);
        Assert.Equal("Notes", answer?.Name);
        Assert.Null(DeskApps.ParseAnswer("top - 10:00:00 up 1 day\r\n"));
        Assert.Null(DeskApps.ParseAnswer("{ not json\r\n"));
    }

    // ---- add ----

    [Fact]
    public void Add_saves_what_the_app_says_with_the_command_as_typed()
    {
        var app = Answering("""{"id":"weather","name":"Weather","description":"The forecast","args":["--city","Oslo"]}""");
        Assert.Equal(0, Run("add", app));
        Assert.StartsWith("Added weather: Weather", _output.ToString(), StringComparison.Ordinal);

        var entry = Assert.Single(Load().Apps);
        Assert.Equal(("weather", "Weather", app, "The forecast"), (entry.Id, entry.Name, entry.Command, entry.Description));
        Assert.Equal(["--city", "Oslo"], entry.Args!);
        Assert.Equal(AppSource.Added, entry.Source);
    }

    [Fact]
    public void The_options_and_the_args_after_dashes_win_over_the_manifest()
    {
        var app = Answering("""{"id":"weather","name":"Weather","args":["--city","Oslo"]}""");
        Assert.Equal(0, Run("add", app, "--id", "rain", "--name", "Rain", "--", "--city", "Bergen"));
        var entry = Assert.Single(Load().Apps);
        Assert.Equal(("rain", "Rain"), (entry.Id, entry.Name));
        Assert.Equal(["--city", "Bergen"], entry.Args!);
    }

    [Fact]
    public void A_program_that_does_not_answer_needs_an_id_and_a_name()
    {
        var app = _files.Script("sleep 10");
        Assert.Equal(1, Run("add", app));
        Assert.Contains("didn't say its manifest, so it needs --id and --name", _error.ToString(), StringComparison.Ordinal);
        Assert.False(Directory.Exists(Added));

        Assert.Equal(0, Run("add", app, "--id", "nap", "--name", "Nap"));
        Assert.Equal("Nap", Assert.Single(Load().Apps).Name);
    }

    [Fact]
    public void A_manifest_without_an_id_needs_one()
    {
        Assert.Equal(1, Run("add", Answering("""{"name":"Weather"}""")));
        Assert.Contains("has no id in its manifest, so it needs --id", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void A_program_that_fails_has_not_answered()
    {
        Assert.Equal(1, Run("add", _files.Script("""echo '{"id":"x","name":"X"}'; exit 3""")));
        Assert.Contains("didn't say its manifest", _error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Weather")]
    [InlineData("shell")]
    [InlineData("my app")]
    public void An_id_must_be_lowercase_letters_digits_and_dashes(string id)
    {
        Assert.Equal(1, Run("add", Answering($$"""{"id":"{{id}}","name":"X"}""")));
        Assert.Contains("can't be an id", _error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Adding_an_id_again_replaces_it()
    {
        Run("add", Answering("""{"id":"weather","name":"Weather"}"""));
        Assert.Equal(0, Run("add", Answering("""{"id":"weather","name":"Forecast"}""")));
        Assert.Contains("Replaced weather: Forecast", _output.ToString(), StringComparison.Ordinal);
        Assert.Equal("Forecast", Assert.Single(Load().Apps).Name);
    }

    [Fact]
    public void A_program_that_is_not_there_is_said_so()
    {
        Assert.Equal(1, Run("add", "no-such-program-here"));
        Assert.StartsWith("Can't run no-such-program-here", _error.ToString(), StringComparison.Ordinal);
    }

    // ---- remove ----

    [Fact]
    public void Remove_deletes_an_added_app()
    {
        Run("add", Answering("""{"id":"weather","name":"Weather"}"""));
        Assert.Equal(0, Run("remove", "weather"));
        Assert.Empty(Load().Apps);
    }

    [Fact]
    public void Remove_leaves_apps_json_and_the_first_party_apps_alone_and_says_why()
    {
        File.WriteAllText(Path.Combine(Config, "apps.json"), """[{ "id": "k9s", "command": "k9s" }]""");
        OnPath("tuinotes");

        Assert.Equal(1, Run("remove", "k9s"));
        Assert.Contains("is in", _error.ToString(), StringComparison.Ordinal);
        Assert.Contains("apps.json: edit that", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Run("remove", "notes"));
        Assert.Contains("found on PATH", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, Run("remove", "nope"));
        Assert.Contains("No app nope", _error.ToString(), StringComparison.Ordinal);
        Assert.Equal(2, Load().Apps.Count);
    }

    // ---- apps, and the rest ----

    [Fact]
    public void Apps_lists_each_with_where_it_came_from()
    {
        File.WriteAllText(Path.Combine(Config, "apps.json"), """[{ "id": "k9s", "name": "Kubernetes", "command": "k9s", "args": ["-A"] }]""");
        Run("add", Answering("""{"id":"weather","name":"Weather"}"""));
        OnPath("tuireader");
        _output.GetStringBuilder().Clear();

        Assert.Equal(0, Run("apps"));
        var lines = _output.ToString().TrimEnd().Split('\n');
        Assert.StartsWith("ID", lines[0], StringComparison.Ordinal);
        Assert.Matches(@"^k9s +Kubernetes +k9s -A +apps\.json$", lines[1]);
        Assert.Matches(@"^weather +Weather +\S+ +added$", lines[2]);
        Assert.Matches(@"^reader +Reader +tuireader +found on PATH$", lines[3]);
    }

    [Fact]
    public void Apps_with_none_says_how_to_add_one()
    {
        Assert.Equal(0, Run("apps"));
        Assert.Contains("tuidesk add <command>", _output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("add")]
    [InlineData("add", "--id", "x")]
    [InlineData("remove")]
    [InlineData("apps", "now")]
    public void Anything_else_shows_the_usage(params string[] args)
    {
        Assert.Equal(2, Run(args));
        Assert.StartsWith("Usage: tuidesk", _error.ToString(), StringComparison.Ordinal);
    }
}
