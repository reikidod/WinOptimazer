using System.Text.RegularExpressions;

namespace WinOptimizer.Services;

public enum GameClass { Light, Medium, Heavy, VeryHeavy }

public enum GpuVendor { Unknown, Nvidia, Amd, Intel }

/// <summary>Характеристики ПК, влияющие на настройки игр.</summary>
public sealed record HardwareProfile(
    string CpuName, int CpuCores, int CpuThreads, double CpuMaxGhz,
    double RamGb,
    string GpuName, double VramGb,
    int ScreenWidth, int ScreenHeight, int RefreshHz)
{
    public GpuVendor GpuVendor => GpuInfo.Vendor(GpuName);
    public bool GpuIsIntegrated => GpuInfo.IsIntegrated(GpuName);
}

/// <summary>Оценки производительности комплектующих и откуда они взяты.</summary>
public sealed record HardwareScores(double? Gpu, string GpuSource, double? Cpu, string CpuSource);

/// <summary>Что известно об игре: тяжесть и какие технологии масштабирования она содержит.</summary>
public sealed record GameTraits(
    string Name, GameClass Class, string ClassSource,
    bool Dlss, bool DlssFrameGen, bool Fsr, bool FsrFrameGen, bool Xess,
    string? Engine, string? Note);

public sealed record SettingAdvice(string Name, string Value, string Reason);

public sealed record OptimizationResult(
    string Headline,
    string Preset,
    string Upscaler,
    bool FrameGeneration,
    int TargetFps,
    IReadOnlyList<SettingAdvice> Settings,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> Notes);

/// <summary>Распознавание видеокарт по названию.</summary>
public static class GpuInfo
{
    public static GpuVendor Vendor(string name)
    {
        var n = name.ToLowerInvariant();
        if (n.Contains("nvidia") || n.Contains("geforce") || n.Contains("quadro")) return GpuVendor.Nvidia;
        if (n.Contains("radeon") || n.Contains("amd")) return GpuVendor.Amd;
        if (n.Contains("intel") || n.Contains("arc ")) return GpuVendor.Intel;
        return GpuVendor.Unknown;
    }

    public static bool IsIntegrated(string name)
    {
        var n = name.ToLowerInvariant();
        if (Regex.IsMatch(n, @"\b(rtx|gtx|rx)\s*\d") || Regex.IsMatch(n, @"\barc\s*[ab]\d") || n.Contains("geforce")) return false;
        return n.Contains("uhd") || n.Contains("iris") || n.Contains("hd graphics") || n.Contains("radeon graphics")
               || n.Contains("radeon(tm) graphics") || n.Contains("vega") || n.Contains("intel");
    }

    public static bool SupportsDlss(string name) => Regex.IsMatch(name, @"\bRTX\b", RegexOptions.IgnoreCase);

    /// <summary>Генерация кадров DLSS 3 доступна на RTX 40 и 50.</summary>
    public static bool SupportsDlssFrameGen(string name) =>
        Regex.IsMatch(name, @"\bRTX\s*[45]\d{3}", RegexOptions.IgnoreCase);

    public static bool SupportsRayTracing(string name) =>
        Regex.IsMatch(name, @"\bRTX\b|\bRX\s*[679]\d{3}|\bArc\b", RegexOptions.IgnoreCase);
}

/// <summary>
/// Подбор настроек игры по железу. Чистая функция без обращения к системе: легко проверяется тестами.
/// Числа — оценки по баллам PassMark: «сколько баллов видеокарты нужно на 1080p/60 FPS на низких настройках»
/// для каждого класса игр, дальше умножаем на разрешение, пресет и целевой FPS.
/// </summary>
public static class GameOptimizer
{
    private static readonly double[] GpuBase = { 2500, 4500, 7500, 11000 };
    private static readonly double[] CpuBase = { 7000, 9000, 12000, 15000 };

    private static readonly (string Name, double Cost, double Quality)[] Presets =
    {
        ("Ультра", 2.7, 1.0), ("Высокие", 1.9, 0.9), ("Средние", 1.4, 0.75), ("Низкие", 1.0, 0.55),
    };

    // Во сколько раз масштабирование разрешения «увеличивает» возможности видеокарты, и потеря качества картинки.
    private static readonly (string Label, double Boost, double Quality)[] Modes =
    {
        ("Выкл", 1.0, 1.0), ("Качество", 1.35, 0.95), ("Баланс", 1.55, 0.9), ("Производительность", 2.0, 0.8),
    };

    public static int AutoTargetFps(int refreshHz) =>
        refreshHz >= 140 ? 144 : refreshHz >= 115 ? 120 : refreshHz >= 85 ? 90 : 60;

    public static OptimizationResult Recommend(GameTraits game, HardwareProfile hw, HardwareScores scores, int targetFps = 0)
    {
        var target = targetFps > 0 ? targetFps : AutoTargetFps(hw.RefreshHz);
        var warnings = new List<string>();
        var notes = new List<string>();
        var cls = (int)game.Class;

        var width = Math.Max(640, hw.ScreenWidth);
        var height = Math.Max(480, hw.ScreenHeight);
        var resFactor = Math.Max(0.5, width * (double)height / (1920.0 * 1080.0));

        // ── Видеокарта ──
        var gpu = scores.Gpu;
        if (gpu is null)
        {
            gpu = hw.GpuIsIntegrated ? 2000 : 6000;
            warnings.Add("Не удалось найти оценку производительности видеокарты — подбор грубый, ориентируйтесь на FPS в игре.");
        }

        if (hw.GpuIsIntegrated)
            warnings.Add("Игра будет работать на встроенной графике: возможны только низкие настройки и сниженное разрешение.");

        var upscaler = ChooseUpscaler(game, hw);
        var dlssFg = game.DlssFrameGen && GpuInfo.SupportsDlssFrameGen(hw.GpuName);
        var fsrFg = game.FsrFrameGen;
        var fgAvailable = dlssFg || fsrFg;

        var plan = Pick(gpu.Value, GpuBase[cls], resFactor, target / 60.0);
        var frameGen = false;

        // Если без генерации кадров получаются только средние настройки, а цель высокая, пробуем её.
        if (plan.Quality < 0.75 && target >= 90 && fgAvailable)
        {
            var withFg = Pick(gpu.Value, GpuBase[cls], resFactor, target / 2.0 / 60.0);
            if (withFg.Feasible && withFg.Quality > plan.Quality)
            {
                plan = withFg;
                frameGen = true;
            }
        }

        // ── Процессор ──
        var cpuLimited = false;
        var cpuFps = target;
        if (scores.Cpu is { } cpu)
        {
            var need = CpuBase[cls] * target / 60.0;
            if (cpu < need)
            {
                cpuLimited = true;
                cpuFps = Math.Max(30, (int)Math.Floor(60.0 * cpu / CpuBase[cls] / 5.0) * 5);
                warnings.Add($"Процессор слабоват для {target} FPS в этой игре: реально ждать около {cpuFps} FPS независимо от настроек графики.");
            }
        }

        // ── Видеопамять и ОЗУ ──
        var vramNeed = new[] { 2.0, 4.0, 6.0, 8.0 }[cls] * Math.Sqrt(resFactor);
        var textures = TextureLevel(hw.VramGb, vramNeed, hw.GpuIsIntegrated);
        if (hw.VramGb > 0 && !hw.GpuIsIntegrated && hw.VramGb < vramNeed * 0.8)
            warnings.Add($"Видеопамяти ({hw.VramGb:0.#} ГБ) может не хватить: игре на этом разрешении нужно около {vramNeed:0.#} ГБ — возможны фризы. Снизьте текстуры.");

        if (hw.RamGb > 0 && hw.RamGb < 8)
            warnings.Add($"Оперативной памяти мало ({hw.RamGb:0.#} ГБ): закройте браузер и фоновые программы перед игрой.");
        else if (hw.RamGb > 0 && hw.RamGb < 16 && game.Class >= GameClass.Heavy)
            warnings.Add($"Для тяжёлых игр лучше 16 ГБ ОЗУ, сейчас {hw.RamGb:0.#} ГБ.");

        if (!plan.Feasible)
            warnings.Add($"Видеокарта ниже требований для {target} FPS даже на низких настройках с масштабированием. " +
                         $"Снизьте цель (30–45 FPS) или уменьшите разрешение.");

        // ── Итоговые настройки ──
        var preset = Presets[plan.Preset];
        var mode = Modes[plan.Mode];
        var scalerText = plan.Mode == 0 ? "Выкл" : $"{upscaler.Name}: {mode.Label}";
        var upscaleReason = plan.Mode == 0
            ? "Видеокарте хватает мощности на родное разрешение."
            : upscaler.IsNative
                ? $"Рендер в меньшем разрешении с восстановлением до {width}×{height}: +{(mode.Boost - 1) * 100:0}% к производительности при небольшой потере чёткости."
                : "В файлах игры не найдено DLSS/FSR/XeSS. Включите «масштаб разрешения» ~" +
                  $"{Math.Round(100 / Math.Sqrt(mode.Boost)):0}% в настройках игры или поставьте OptiScaler (кнопка рядом).";

        var settings = new List<SettingAdvice>
        {
            new("Разрешение", $"{width}×{height} (родное)",
                "Меньшее разрешение мылит картинку на мониторе, лучше снижать масштаб рендера."),
            new("Общий пресет", preset.Name,
                $"Подобран под ваши {gpuLabel(hw)} и цель {target} FPS" + (frameGen ? " (с генерацией кадров)" : "") + "."),
            new("Масштабирование (DLSS/FSR/XeSS)", scalerText, upscaleReason),
        };

        if (fgAvailable)
        {
            settings.Add(new SettingAdvice("Генерация кадров", frameGen ? (dlssFg ? "DLSS 3 — Вкл" : "FSR 3 — Вкл") : "Выкл",
                frameGen
                    ? "Без неё до цели не дотянуть; задержка немного выше, для шутеров лучше без неё."
                    : "Для вашей цели хватает обычных кадров — генерация добавила бы задержку."));
        }

        var rt = GpuInfo.SupportsRayTracing(hw.GpuName) && gpu.Value >= GpuBase[cls] * 2.7 * resFactor * target / 60.0 * 2.2;
        settings.Add(new SettingAdvice("Трассировка лучей", rt ? "Можно включить (низкое/среднее)" : "Выкл",
            rt ? "Запас мощности большой, но трассировка всё равно сильно режет FPS: начните с низкого уровня."
               : "Съедает 30–50% FPS; на вашем железе лучше отдать эту мощность пресету и разрешению."));

        settings.Add(new SettingAdvice("Текстуры", textures.Level, textures.Reason));
        settings.Add(new SettingAdvice("Тени", ShadowLevel(plan.Preset),
            "Тени — самая «дорогая» настройка; на ступень ниже общего пресета разницу почти не видно."));
        settings.Add(new SettingAdvice("Дальность прорисовки / плотность объектов",
            cpuLimited ? "Низкая — Средняя" : preset.Name,
            cpuLimited ? "Упираетесь в процессор: эта настройка нагружает именно его."
                       : "Влияет на нагрузку и процессора, и видеокарты."));
        settings.Add(new SettingAdvice("Сглаживание", plan.Mode == 0 ? "TAA / SMAA" : "Задаёт апскейлер",
            plan.Mode == 0 ? "Хороший баланс чёткости и цены." : "Отдельное сглаживание при DLSS/FSR/XeSS не нужно."));
        settings.Add(new SettingAdvice("Анизотропная фильтрация", "16x", "Практически бесплатна, чёткие текстуры под углом."));
        settings.Add(new SettingAdvice("Размытие движения, зернистость, хроматическая аберрация", "Выкл",
            "Отнимают FPS и мешают целиться; на субъективное качество влияют мало."));

        var cap = Math.Min(target, Math.Max(30, hw.RefreshHz - 3));
        settings.Add(new SettingAdvice("Вертикальная синхронизация и лимит FPS",
            $"V-Sync выкл в игре, лимит {cap} FPS",
            "Если монитор поддерживает G-Sync/FreeSync — включите его в драйвере, а лимит поставьте чуть ниже частоты экрана."));

        settings.Add(new SettingAdvice("Режим экрана", "Полноэкранный (эксклюзивный) или без рамки",
            "Без «окна»: меньше задержка и меньше влияния фоновых приложений."));

        var lowLatency = hw.GpuVendor switch
        {
            GpuVendor.Nvidia => ("NVIDIA Reflex", "Вкл (если есть в игре)"),
            GpuVendor.Amd => ("Radeon Anti-Lag", "Вкл в драйвере"),
            _ => ("Режим низкой задержки", "Вкл, если есть"),
        };
        settings.Add(new SettingAdvice(lowLatency.Item1, lowLatency.Item2, "Снижает задержку ввода, особенно когда видеокарта загружена на 100%."));

        if (game.Note is { Length: > 0 }) notes.Add(game.Note);
        notes.Add($"Класс игры: {ClassLabel(game.Class)} ({game.ClassSource}).");
        notes.Add($"Видеокарта: {scores.GpuSource}. Процессор: {scores.CpuSource}.");
        notes.Add("Это расчётная рекомендация по баллам комплектующих, а не замер FPS: проверьте в игре и сдвиньте пресет на ступень вверх или вниз.");

        var headline = $"{preset.Name}, масштабирование: {scalerText}" + (frameGen ? ", генерация кадров" : "") + $" — цель {target} FPS";
        return new OptimizationResult(headline, preset.Name, scalerText, frameGen, target, settings, warnings, notes);

        static string gpuLabel(HardwareProfile h) => string.IsNullOrWhiteSpace(h.GpuName) ? "железо" : h.GpuName.Trim();
    }

    private readonly record struct Plan(int Preset, int Mode, bool Feasible, double Quality);

    private static Plan Pick(double gpuScore, double baseNeed, double resFactor, double fpsMul)
    {
        var best = new Plan(Presets.Length - 1, Modes.Length - 1, false, 0);
        for (var p = 0; p < Presets.Length; p++)
        {
            for (var m = 0; m < Modes.Length; m++)
            {
                var need = baseNeed * Presets[p].Cost * resFactor * fpsMul;
                if (gpuScore * Modes[m].Boost < need) continue;
                var quality = Presets[p].Quality * Modes[m].Quality;
                if (!best.Feasible || quality > best.Quality)
                    best = new Plan(p, m, true, quality);
            }
        }

        return best;
    }

    private readonly record struct Upscaler(string Name, bool IsNative);

    private static Upscaler ChooseUpscaler(GameTraits game, HardwareProfile hw)
    {
        if (game.Dlss && GpuInfo.SupportsDlss(hw.GpuName)) return new("DLSS", true);
        if (game.Fsr) return new("FSR", true);
        if (game.Xess) return new("XeSS", true);
        return new("Масштаб разрешения", false);
    }

    private static (string Level, string Reason) TextureLevel(double vramGb, double need, bool integrated)
    {
        if (integrated) return ("Низкие", "Встроенная графика делит память с процессором.");
        if (vramGb <= 0) return ("Средние", "Объём видеопамяти определить не удалось.");
        var ratio = vramGb / need;
        return ratio switch
        {
            >= 1.5 => ("Ультра", $"Видеопамяти ({vramGb:0.#} ГБ) с запасом: текстуры почти не влияют на FPS."),
            >= 1.1 => ("Высокие", $"Видеопамяти ({vramGb:0.#} ГБ) хватает с небольшим запасом."),
            >= 0.8 => ("Средние", $"Видеопамяти ({vramGb:0.#} ГБ) впритык, высокие дадут фризы."),
            _ => ("Низкие", $"Видеопамяти ({vramGb:0.#} ГБ) мало для этой игры и разрешения."),
        };
    }

    private static string ShadowLevel(int presetIndex) => Presets[Math.Min(Presets.Length - 1, presetIndex + 1)].Name;

    public static string ClassLabel(GameClass c) => c switch
    {
        GameClass.Light => "лёгкая / соревновательная",
        GameClass.Medium => "средней тяжести",
        GameClass.Heavy => "тяжёлая",
        _ => "очень тяжёлая",
    };
}
