using System.Runtime.InteropServices;

namespace MiniTui.Desk;

/// <summary>What .NET doesn't say about a file: who owns it.</summary>
internal static partial class Libc
{
    private const int AtFdCwd = -100;
    private const int AtSymlinkNoFollow = 0x100;
    private const uint StatxType = 0x1;
    private const uint StatxMode = 0x2;
    private const uint StatxUid = 0x8;

    // struct statx has the same layout on every architecture, unlike struct stat
    private const int StatxSize = 256;
    private const int UidOffset = 20;
    private const int ModeOffset = 28;
    private const int TypeMask = 0xF000;
    private const int Directory = 0x4000;

    public static uint EffectiveUserId => geteuid();

    /// <summary>The owner and permission bits of <paramref name="path"/> itself, if it's a directory and not a link to one.</summary>
    public static bool TryDirectoryOwner(string path, out uint owner, out UnixFileMode mode)
    {
        owner = 0;
        mode = 0;
        var buffer = new byte[StatxSize];
        if (statx(AtFdCwd, path, AtSymlinkNoFollow, StatxType | StatxMode | StatxUid, ref buffer[0]) != 0) return false;

        var bits = BitConverter.ToUInt16(buffer, ModeOffset);
        if ((bits & TypeMask) != Directory) return false;
        owner = BitConverter.ToUInt32(buffer, UidOffset);
        mode = (UnixFileMode)(bits & 0xFFF);
        return true;
    }

    [LibraryImport("libc")]
    private static partial uint geteuid();

    [LibraryImport("libc", SetLastError = true, StringMarshalling = StringMarshalling.Utf8)]
    private static partial int statx(int dirfd, string path, int flags, uint mask, ref byte buffer);
}
