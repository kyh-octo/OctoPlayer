using System.Text.Json;
using OctoPlayer.Services;

var root = Path.Combine(Path.GetTempPath(), "OctoPlayer-settings-regression-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var assertions = new List<string>();
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException("FAIL: " + description);
    assertions.Add(description);
    Console.WriteLine("PASS: " + description);
}
string FileFor(string name) => Path.Combine(root, name + ".json");
try
{
    // Persistence and process-restart equivalent: all data is isolated under this temp directory.
    var path = FileFor("restart");
    var first = new AppSettings { SkipSeconds = 37 };
    Check(first.SaveTo(path), "SkipSeconds=37 saves successfully");
    Check(AppSettings.LoadFrom(path).SkipSeconds == 37, "reloaded settings retain SkipSeconds=37 (restart equivalent)");

    // Two windows load before either saves. A stale full-object save used to erase the newer value.
    path = FileFor("stale-instance");
    Check(new AppSettings().SaveTo(path), "stale-instance baseline saves");
    var older = AppSettings.LoadFrom(path);
    var newer = AppSettings.LoadFrom(path);
    newer.SkipSeconds = 37;
    Check(newer.SaveTo(path), "newer instance saves SkipSeconds=37");
    older.Volume = 63;
    Check(older.SaveTo(path), "older instance saves its Volume change");
    Check(AppSettings.LoadFrom(path).SkipSeconds == 37, "older Volume save preserves newer SkipSeconds=37");
    Check(AppSettings.LoadFrom(path).Volume == 63, "older Volume change is persisted");

    // Independent fields from concurrent instances both survive, followed by a further stale save.
    path = FileFor("disjoint");
    Check(new AppSettings().SaveTo(path), "disjoint-change baseline saves");
    var left = AppSettings.LoadFrom(path);
    var right = AppSettings.LoadFrom(path);
    left.SkipSeconds = 37;
    Check(left.SaveTo(path), "first disjoint change saves SkipSeconds");
    right.Volume = 41;
    Check(right.SaveTo(path), "second disjoint change saves Volume");
    var combined = AppSettings.LoadFrom(path);
    Check(combined.SkipSeconds == 37 && combined.Volume == 41, "disjoint SkipSeconds and Volume changes both survive");
    left.RepeatCount = 4;
    Check(left.SaveTo(path), "previously stale instance performs a subsequent save");
    combined = AppSettings.LoadFrom(path);
    Check(combined.SkipSeconds == 37 && combined.Volume == 41 && combined.RepeatCount == 4,
        "subsequent stale save preserves newer SkipSeconds and Volume");

    // An existing directory at the destination forces the atomic replacement path to fail.
    var blocked = Path.Combine(root, "readonly-destination.json");
    Directory.CreateDirectory(blocked);
    File.WriteAllText(Path.Combine(blocked, "sentinel.txt"), "intact");
    var rejected = new AppSettings { SkipSeconds = 37 };
    Check(!rejected.SaveTo(blocked), "SaveTo returns false when destination cannot be replaced");
    Check(File.ReadAllText(Path.Combine(blocked, "sentinel.txt")) == "intact", "failed save leaves destination contents intact");

    path = FileFor("readonly-file");
    var readonlyWriter = new AppSettings { SkipSeconds = 23 };
    Check(readonlyWriter.SaveTo(path), "read-only fixture initial save");
    File.SetAttributes(path, FileAttributes.ReadOnly);
    try
    {
        readonlyWriter.SkipSeconds = 37;
        Check(!readonlyWriter.SaveTo(path), "read-only file save reports failure");
        Check(AppSettings.LoadFrom(path).SkipSeconds == 23, "read-only failure preserves original settings");
    }
    finally { File.SetAttributes(path, FileAttributes.Normal); }
    Check(readonlyWriter.SaveTo(path) && AppSettings.LoadFrom(path).SkipSeconds == 37,
        "failed save can be retried after write access is restored");

    // Successful second save establishes .bak; a corrupt primary must recover that previous state.
    path = FileFor("backup-fallback");
    var backupWriter = new AppSettings { SkipSeconds = 29 };
    Check(backupWriter.SaveTo(path), "backup fixture initial state saves");
    backupWriter.Volume = 52;
    Check(backupWriter.SaveTo(path), "backup fixture newer state saves and rotates backup");
    Check(AppSettings.LoadFrom(path).SkipSeconds == 29 && AppSettings.LoadFrom(path).Volume == 52,
        "primary contains latest settings before corruption");
    File.WriteAllText(path, "{ corrupted json");
    var recovered = AppSettings.LoadFrom(path);
    Check(recovered.SkipSeconds == 29 && recovered.Volume == 100,
        "corrupt primary falls back to previous successful settings in .bak");

    // Reproduce HEAD's old whole-object JSON algorithm directly, without building or invoking the player.
    path = FileFor("old-algorithm-repro");
    var baseline = new AppSettings();
    File.WriteAllText(path, JsonSerializer.Serialize(baseline, new JsonSerializerOptions { WriteIndented = true }));
    var oldLoaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!;
    var intervening = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!;
    intervening.SkipSeconds = 37;
    File.WriteAllText(path, JsonSerializer.Serialize(intervening, new JsonSerializerOptions { WriteIndented = true }));
    oldLoaded.Volume = 63;
    File.WriteAllText(path, JsonSerializer.Serialize(oldLoaded, new JsonSerializerOptions { WriteIndented = true }));
    Check(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path))!.SkipSeconds == 10,
        "OLD algorithm reproduction: stale full-object JSON save overwrites newer SkipSeconds=37 with 10");
    Console.WriteLine("OLD BUG: reproduced using the original HEAD Save algorithm (whole-object JsonSerializer.Serialize + File.WriteAllText) against isolated fixture files; no player or real settings path used.");

    Console.WriteLine($"RESULT: {assertions.Count} assertions passed.");
}
finally
{
    try { Directory.Delete(root, recursive: true); }
    catch (IOException) { Console.Error.WriteLine("Warning: could not remove isolated temp fixture: " + root); }
}
