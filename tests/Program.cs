using System.IO.Compression;
using K4GOTV;
using Microsoft.Extensions.Logging.Abstractions;

var root = Path.Combine(Path.GetTempPath(), "k4gotv-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var game = Path.Combine(root, "game");
    var csgo = Path.Combine(game, "csgo");
    Check(DemoPaths.GetCsgoDirectory(game) == csgo, "game root resolves to csgo");
    Check(DemoPaths.GetCsgoDirectory(csgo + Path.DirectorySeparatorChar) == csgo, "csgo root is not duplicated");
    await Task.WhenAll(TestDirectRecording(), TestDelayedRecording(), TestZip(), TestEmpty(), TestCollision());
    Console.WriteLine("All regression checks passed.");
}
finally
{
    Directory.Delete(root, recursive: true);
}

async Task TestDelayedRecording()
{
    var csgo = Path.Combine(root, "delayed", "csgo");
    string[] candidates = [Path.Combine(csgo, "unused.dem"), Path.Combine(csgo, "addons", "metamod", "legacy.dem")];
    Directory.CreateDirectory(Path.GetDirectoryName(candidates[1])!);
    var destination = Path.Combine(csgo, "discord_demos", "demo.dem");
    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
    var finalize = FileManager.FinalizeDemoAsync(candidates, destination, NullLogger.Instance);
    await Task.Delay(600); // Engine creates the file after stop was dispatched.
    using (var writer = new FileStream(candidates[1], FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        writer.Write([1, 2, 3]);
        writer.Flush();
        await Task.Delay(2100);
        Check(!finalize.IsCompleted && !File.Exists(destination), "active writer is not moved");
        writer.Write([4, 5, 6]);
    }
    Check(await finalize, "delayed recording found in Metamod search path");
    Check(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }), "finalized demo includes final writes");
    Check(!File.Exists(candidates[1]), "finalized demo moved out of engine directory");
}

async Task TestDirectRecording()
{
    var csgo = Path.Combine(root, "direct server", "game", "csgo");
    var directory = Path.Combine(csgo, "discord_demos");
    Directory.CreateDirectory(directory);
    var name = "autodemo_de_dust2_2026-09-24_22-39-55.dem";
    var destination = Path.Combine(directory, name);
    var commandPath = DemoPaths.GetRecordingPath(directory, name);
    Check(Path.IsPathFullyQualified(commandPath), "recording command uses an absolute path");
    Check(Path.GetFileName(commandPath) == name, "recording command preserves configured filename");
    var engineDirectory = Path.Combine(csgo, "addons", "metamod");
    Check(Path.GetFullPath(Path.Combine(engineDirectory, commandPath)) == Path.GetFullPath(destination), "Metamod engine directory cannot redirect recording path");
    Check(commandPath.Contains("direct server") && !commandPath.Contains('\\'), "recording path preserves spaces and uses forward slashes");
    File.WriteAllBytes(commandPath, [1, 2, 3, 4]);
    Check(await FileManager.FinalizeDemoAsync([commandPath], destination, NullLogger.Instance), "direct recording finalizes in place");
    Check(File.ReadAllBytes(destination).SequenceEqual(new byte[] { 1, 2, 3, 4 }), "in-place finalization retains original bytes");
    var zip = Path.ChangeExtension(destination, ".zip");
    Check(await FileManager.ZipDemoAsync(destination, zip, NullLogger.Instance), "direct recording compressed");
    using var archive = ZipFile.OpenRead(zip);
    Check(archive.Entries.Single().Name == name, "archive uses configured demo filename");
}

async Task TestZip()
{
    var source = Path.Combine(root, "zip.dem");
    var zip = Path.Combine(root, "zip.zip");
    byte[] bytes = new byte[1024 * 128];
    Random.Shared.NextBytes(bytes);
    using (var writer = new FileStream(source, FileMode.CreateNew, FileAccess.Write, FileShare.None))
    {
        writer.Write(bytes);
        var pending = FileManager.ZipDemoAsync(source, zip, NullLogger.Instance);
        Check(!pending.IsCompleted, "compression waits for writer");
        writer.Dispose();
        Check(await pending, "compression succeeds after writer closes");
    }
    using var archive = ZipFile.OpenRead(zip);
    using var contents = archive.Entries.Single().Open();
    using var output = new MemoryStream();
    await contents.CopyToAsync(output);
    Check(archive.Entries.Single().Name == "zip.dem" && output.ToArray().SequenceEqual(bytes), "zip preserves filename and every byte");
}

async Task TestEmpty()
{
    var source = Path.Combine(root, "empty.dem");
    var zip = Path.Combine(root, "empty.zip");
    File.WriteAllBytes(source, []);
    Check(!await FileManager.ZipDemoAsync(source, zip, NullLogger.Instance), "empty demo rejected");
    Check(!File.Exists(zip) && File.Exists(source), "rejected source retained without archive");
    Check(!await FileManager.FinalizeDemoAsync([Path.Combine(root, "missing.dem")], Path.Combine(root, "missing-final.dem"), NullLogger.Instance), "missing demo times out without success");
}

async Task TestCollision()
{
    var source = Path.Combine(root, "collision.dem");
    var zip = Path.Combine(root, "collision.zip");
    File.WriteAllBytes(source, [1, 2, 3]);
    File.WriteAllBytes(zip, [9, 8, 7]);
    Check(!await FileManager.ZipDemoAsync(source, zip, NullLogger.Instance), "existing archive rejected");
    Check(File.ReadAllBytes(zip).SequenceEqual(new byte[] { 9, 8, 7 }), "existing archive preserved");
    var destination = Path.Combine(root, "collision-final.dem");
    File.WriteAllBytes(destination, [4, 5, 6]);
    Check(!await FileManager.FinalizeDemoAsync([source], destination, NullLogger.Instance), "existing demo destination rejected");
    Check(File.Exists(source) && File.ReadAllBytes(destination).SequenceEqual(new byte[] { 4, 5, 6 }), "move collision preserves both demos");
}

static void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    Console.WriteLine("PASS: " + name);
}
