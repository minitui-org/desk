using System.Globalization;

namespace MiniTui.Desk.Tests;

/// <summary>Where the socket goes, and who may use it (spec §8.2, §13.4).</summary>
public sealed class ServerTests : IDisposable
{
    private const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Scripts _files = new();

    public void Dispose() => _files.Dispose();

    private string Socket(string directory) => Path.Combine(directory, Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + ".sock");

    [Fact]
    public void The_directory_is_made_for_the_owner_only()
    {
        var directory = _files.Path("a/b/tuidesk");
        using var server = DeskServer.Start(directory, c => c.Dispose());
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(directory));
        Assert.Equal(Socket(directory), server.SocketPath);
        Assert.True(File.Exists(server.SocketPath));
    }

    [Fact]
    public void An_existing_directory_of_ours_is_closed_up()
    {
        var directory = Directory.CreateDirectory(_files.Path("open")).FullName;
        File.SetUnixFileMode(directory, OwnerOnly | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        DeskServer.PrepareDirectory(directory);
        Assert.Equal(OwnerOnly, File.GetUnixFileMode(directory));
    }

    [Fact]
    public void Another_users_directory_is_refused_and_left_alone()
    {
        // Root owns everything it would be given here, and refusing nothing would mean changing /tmp
        if (Libc.EffectiveUserId == 0) return;
        var before = File.GetUnixFileMode("/tmp");
        var error = Assert.Throws<IOException>(() => DeskServer.PrepareDirectory("/tmp"));
        Assert.Contains("belongs to another user", error.Message, StringComparison.Ordinal);
        Assert.Equal(before, File.GetUnixFileMode("/tmp"));
    }

    [Fact]
    public void A_link_is_refused_even_to_a_directory_of_ours()
    {
        var target = Directory.CreateDirectory(_files.Path("real")).FullName;
        var link = _files.Path("link");
        Directory.CreateSymbolicLink(link, target);
        var error = Assert.Throws<IOException>(() => DeskServer.PrepareDirectory(link));
        Assert.Contains("isn't a directory", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_stale_socket_is_replaced_and_the_socket_goes_on_dispose()
    {
        var directory = _files.Path("run");
        DeskServer.PrepareDirectory(directory);
        File.WriteAllText(Socket(directory), "left by a shell that died");

        var server = DeskServer.Start(directory, c => c.Dispose());
        using (var client = new RawClient(server.SocketPath)) Assert.False(client.Closed);
        server.Dispose();
        Assert.False(File.Exists(Socket(directory)));
    }

    [Fact]
    public void The_default_is_under_the_runtime_directory_or_tmp_by_uid()
    {
        var saved = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        try
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", "/run/user/7");
            Assert.Equal("/run/user/7/tuidesk", DeskServer.DefaultDirectory());
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", null);
            Assert.Equal($"/tmp/tuidesk-{Libc.EffectiveUserId}", DeskServer.DefaultDirectory());
        }
        finally
        {
            Environment.SetEnvironmentVariable("XDG_RUNTIME_DIR", saved);
        }
    }
}
