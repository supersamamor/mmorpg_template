using Game.Content.Validation;

namespace Game.Tests.Client;

/// <summary>
/// Reads theme pack files for the tests. A fresh worktree can hold Git LFS pointer files in place of the models until <c>git lfs pull</c> (ADR-002 row 208); a test that
/// parsed one would fail with a confusing parse error, so this stops with the fix instead.
/// </summary>
public static class PackFiles
{
    /// <summary>The bytes of <paramref name="path"/>; fails with "run git lfs pull" when the file is an LFS pointer.</summary>
    public static byte[] ReadAllBytes(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (LfsPointer.Is(bytes)) throw new InvalidOperationException(LfsPointer.Message(path));
        return bytes;
    }
}

public class PackFilesTests
{
    [Fact]
    public void A_git_lfs_pointer_fails_with_a_message_that_says_to_run_git_lfs_pull()
    {
        var path = Path.Combine(Path.GetTempPath(), "pointer-" + Guid.NewGuid().ToString("N") + ".glb");
        File.WriteAllText(path, "version https://git-lfs.github.com/spec/v1\noid sha256:abc\nsize 12\n");
        try
        {
            var failure = Should.Throw<InvalidOperationException>(() => PackFiles.ReadAllBytes(path));
            failure.Message.ShouldContain("LFS pointer");
            failure.Message.ShouldContain("git lfs pull");
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void A_real_file_is_read_as_it_is()
    {
        var path = Path.Combine(Path.GetTempPath(), "real-" + Guid.NewGuid().ToString("N") + ".glb");
        File.WriteAllBytes(path, new byte[] { 0x67, 0x6C, 0x54, 0x46 });
        try { PackFiles.ReadAllBytes(path).Length.ShouldBe(4); }
        finally { File.Delete(path); }
    }
}
