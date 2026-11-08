namespace WinOptimizer.Services;

/// <summary>
/// Что мы знаем об игре: известные названия по классам тяжести и поиск DLSS/FSR/XeSS по файлам игры.
/// </summary>
public static class GameKnowledge
{
    private static readonly (GameClass Class, string[] Keys, string? Note)[] Known =
    {
        (GameClass.VeryHeavy, new[] { "alan wake 2", "black myth", "stalker 2", "flight simulator", "indiana jones", "silent hill 2",
            "star wars outlaws", "avatar: frontiers", "crysis", "monster hunter wilds", "ark: survival ascended", "star citizen" }, null),
        (GameClass.Heavy, new[] { "cyberpunk", "red dead redemption", "hogwarts legacy", "starfield", "call of duty", "warzone", "battlefield",
            "forza horizon 5", "assassin", "far cry", "horizon zero dawn", "horizon forbidden west", "god of war", "spider-man",
            "last of us", "resident evil", "dying light 2", "hunt: showdown", "helldivers", "palworld", "lies of p", "remnant",
            "dragon's dogma 2", "the finals", "returnal", "ghost of tsushima" }, null),
        (GameClass.Medium, new[] { "apex legends", "pubg", "battlegrounds", "grand theft auto v", "gta v", "witcher 3", "elden ring", "baldur",
            "forza horizon 4", "genshin", "honkai", "rainbow six", "destiny 2", "path of exile", "dark souls", "sekiro", "doom eternal",
            "death stranding", "fallout", "skyrim", "war thunder", "world of tanks", "escape from tarkov", "rust", "diablo", "lost ark" },
            null),
        (GameClass.Medium, new[] { "fortnite" },
            "Fortnite: для максимума FPS используйте режим «Производительность» (DX11 / Performance Mode)."),
        (GameClass.Light, new[] { "counter-strike", "valorant", "dota 2", "league of legends", "rocket league", "minecraft", "overwatch",
            "team fortress", "terraria", "warframe", "brawlhalla", "left 4 dead", "garry's mod", "stardew", "hades", "among us" },
            null),
    };

    private static readonly string[] UpscalerSkip = { "redist", "_commonredist", "__installer" };

    public static GameTraits Analyze(string name, string installDir)
    {
        var (cls, source, note) = Classify(name);
        var files = ScanFolder(installDir, out var engine);

        if (source == "по умолчанию" && engine == "Unreal Engine") note ??= "Игра на Unreal Engine: обычно очень чувствительна к теням и дальности прорисовки.";

        return new GameTraits(name, cls, source,
            Dlss: files.Contains("nvngx_dlss.dll"),
            DlssFrameGen: files.Contains("nvngx_dlssg.dll"),
            Fsr: files.Any(f => f.StartsWith("amd_fidelityfx_dx12", StringComparison.Ordinal)
                                || f.StartsWith("amd_fidelityfx_vk", StringComparison.Ordinal)
                                || f.StartsWith("ffx_fsr", StringComparison.Ordinal)),
            FsrFrameGen: files.Any(f => f.Contains("frameinterpolation", StringComparison.Ordinal)
                                        || f.Contains("framegeneration", StringComparison.Ordinal)
                                        || f.StartsWith("ffx_fsr3", StringComparison.Ordinal)),
            Xess: files.Contains("libxess.dll"),
            Engine: engine,
            Note: note);
    }

    public static (GameClass Class, string Source, string? Note) Classify(string name)
    {
        var n = name.ToLowerInvariant();
        foreach (var (cls, keys, note) in Known)
            if (keys.Any(k => n.Contains(k, StringComparison.Ordinal)))
                return (cls, "по названию игры", note);

        return (GameClass.Medium, "по умолчанию", null);
    }

    /// <summary>Имена dll в папке игры (глубина до 4, не больше ~30 000 файлов) в нижнем регистре.</summary>
    private static HashSet<string> ScanFolder(string installDir, out string? engine)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        engine = null;
        if (string.IsNullOrWhiteSpace(installDir) || !Directory.Exists(installDir)) return names;

        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((installDir, 0));
        var seen = 0;

        while (queue.Count > 0 && seen < 30_000)
        {
            var (dir, depth) = queue.Dequeue();
            try
            {
                foreach (var file in Directory.EnumerateFiles(dir))
                {
                    seen++;
                    var fileName = Path.GetFileName(file).ToLowerInvariant();
                    if (fileName.EndsWith(".dll", StringComparison.Ordinal)) names.Add(fileName);
                    if (fileName == "unityplayer.dll") engine ??= "Unity";
                    if (fileName.EndsWith("-win64-shipping.exe", StringComparison.Ordinal)) engine ??= "Unreal Engine";
                    if (fileName == "engine2.dll") engine ??= "Source 2";
                }

                if (depth >= 4) continue;
                foreach (var sub in Directory.EnumerateDirectories(dir))
                {
                    var sn = Path.GetFileName(sub).ToLowerInvariant();
                    if (UpscalerSkip.Any(s => sn.Contains(s, StringComparison.Ordinal))) continue;
                    if (sn == "engine") engine ??= "Unreal Engine";
                    queue.Enqueue((sub, depth + 1));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // недоступная папка — пропускаем
            }
        }

        return names;
    }
}
