using Microsoft.Win32;

namespace WinOptimizer.Tweaks;

/// <summary>
/// Список твиков по категориям: Игры, Интерфейс, Сеть, Система, Службы.
/// </summary>
public static class TweakCatalog
{
    private const string DeviceGuardKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    private const string MultimediaProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    private static int SvcHostThresholdKb()
    {
        // Порог должен быть больше объёма ОЗУ (в КБ), иначе службы всё равно разделятся.
        var ramKb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024;
        return (int)Math.Min(int.MaxValue, ramKb + 1_024_000);
    }

    private static RegistryTweak Reg(
        string id, string title, string description, EvidenceLevel evidence, string note,
        bool reboot, string category, BackupStore backup, string? warning, params RegistryEntry[] entries) =>
        new(new TweakInfo(id, title, description, evidence, note, reboot, warning, category), entries, backup);

    // Службы, которых нет в системе, не трогаем: запись Start создала бы «пустой» ключ службы.
    private static ITweak[] BrowserUpdaterTweak(BackupStore backup)
    {
        (string Name, int Default)[] services =
        [
            ("MicrosoftEdgeElevationService", 3), ("edgeupdate", 2), ("edgeupdatem", 3),
            ("GoogleChromeElevationService", 3), ("gupdate", 2), ("gupdatem", 3),
        ];

        var entries = new List<RegistryEntry>();
        foreach (var (name, def) in services)
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{name}");
            if (key is not null)
                entries.Add(new RegistryEntry(RegistryHive.LocalMachine, $@"SYSTEM\CurrentControlSet\Services\{name}",
                    "Start", RegistryValueKind.DWord, 4, def));
        }

        if (entries.Count == 0) return [];

        return
        [
            new RegistryTweak(
                new TweakInfo("browser-updaters-off", "Отключить фоновые службы обновления Edge и Chrome",
                    "Найденные в системе службы обновления и повышения прав Edge и Chrome переводятся в режим «Отключена».",
                    EvidenceLevel.Reasoned, "Убирает фоновые процессы обновления; браузеры обновляются при запуске или вручную.",
                    RequiresReboot: true,
                    Warning: "Браузеры перестанут обновляться в фоне, в том числе патчами безопасности. Обновляйте их вручную.",
                    Category: "Система"),
                entries, backup),
        ];
    }

    public static IReadOnlyList<ITweak> Create(BackupStore backup) =>
    [
        // ─── Игры ───
        new RegistryTweak(
            new TweakInfo(
                "game-dvr-off",
                "Отключить фоновую запись игр (Xbox Game DVR)",
                "Выключает фоновый захват видео и клипов Xbox Game Bar.",
                EvidenceLevel.Reasoned,
                "Количественных замеров мало; убирает реальные фоновые задачи захвата.",
                RequiresReboot: false,
                Warning: "Запись через Game Bar (Win+Alt+R) перестанет работать.",
                Category: "Игры"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled", RegistryValueKind.DWord, 0, 1),
                new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "fso-off",
                "Отключить Fullscreen Optimizations (глобально)",
                "GameDVR_FSEBehaviorMode = 2 — попытка отключить FSO для всех игр.",
                EvidenceLevel.NoData,
                "Эффект зависит от игры; измеряйте сами.",
                RequiresReboot: false,
                Category: "Игры"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_FSEBehaviorMode", RegistryValueKind.DWord, 2, 0),
                new RegistryEntry(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_HonorUserFSEBehaviorMode", RegistryValueKind.DWord, 1, 0),
                new RegistryEntry(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DSEBehavior", RegistryValueKind.DWord, 2, 0),
                new RegistryEntry(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_DXGIHonorFSEWindowsCompatible", RegistryValueKind.DWord, 1, 0),
                new RegistryEntry(RegistryHive.CurrentUser, @"System\GameConfigStore", "GameDVR_EFSEFeatureFlags", RegistryValueKind.DWord, 0, 0),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "mouse-accel-off",
                "Отключить ускорение указателя мыши",
                "Движение курсора 1:1 без ускорения Windows.",
                EvidenceLevel.Reasoned,
                "Важно для точности; современные игры с Raw Input почти не зависят от этого.",
                RequiresReboot: false,
                Category: "Игры"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseSpeed", RegistryValueKind.String, "0", "1"),
                new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold1", RegistryValueKind.String, "0", "6"),
                new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\Mouse", "MouseThreshold2", RegistryValueKind.String, "0", "10"),
            ],
            backup,
            MouseSettings.ApplyFromRegistry),

        new RegistryTweak(
            new TweakInfo(
                "mmcss-games",
                "MMCSS: приоритет Games",
                "GPU Priority / Priority / Scheduling Category для задач Games.",
                EvidenceLevel.NoData,
                "Классическая настройка MMCSS; эффект зависит от нагрузки.",
                RequiresReboot: false,
                Category: "Игры"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, MultimediaProfile + @"\Tasks\Games", "GPU Priority", RegistryValueKind.DWord, 8, 8),
                new RegistryEntry(RegistryHive.LocalMachine, MultimediaProfile + @"\Tasks\Games", "Priority", RegistryValueKind.DWord, 6, 2),
            ],
            backup),

        // ─── Интерфейс ───
        new RegistryTweak(
            new TweakInfo(
                "transparency-off",
                "Отключить эффекты прозрачности",
                "Выключает прозрачность панели задач, «Пуск» и окон.",
                EvidenceLevel.Measured,
                "PC-Tuning: меньше циклов CPU у dwm.exe в единичном замере.",
                RequiresReboot: false,
                Category: "Интерфейс"),
            [new RegistryEntry(
                RegistryHive.CurrentUser,
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "EnableTransparency",
                RegistryValueKind.DWord, 0, 1)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "sticky-keys-off",
                "Отключить окно «Залипание клавиш»",
                "Убирает запрос при многократном Shift в играх.",
                EvidenceLevel.Reasoned,
                "Устраняет навязчивое окно, а не нагрузку.",
                RequiresReboot: false,
                Category: "Интерфейс"),
            [new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", RegistryValueKind.String, "506", "510")],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "animations-off",
                "Отключить анимации окон",
                "MinAnimate = 0 — мгновенное свертывание/развертывание.",
                EvidenceLevel.Reasoned,
                "Субъективная отзывчивость UI.",
                RequiresReboot: false,
                Category: "Интерфейс"),
            [new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", RegistryValueKind.String, "0", "1")],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "widgets-off",
                "Отключить виджеты / News and Interests",
                "Убирает кнопку виджетов с панели задач.",
                EvidenceLevel.Reasoned,
                "Меньше фоновых процессов ленты новостей.",
                RequiresReboot: false,
                Category: "Интерфейс"),
            [new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", RegistryValueKind.DWord, 0, 1)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "search-highlights-off",
                "Отключить подсветку поиска / Bing в поиске",
                "Убирает рекламные блоки и новости из поиска Windows.",
                EvidenceLevel.Reasoned,
                "Меньше сетевой активности поиска.",
                RequiresReboot: false,
                Category: "Интерфейс"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Search", "BingSearchEnabled", RegistryValueKind.DWord, 0, 1),
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDynamicSearchBoxEnabled", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        // ─── Сеть ───
        new RegistryTweak(
            new TweakInfo(
                "network-throttling-off",
                "NetworkThrottlingIndex = off",
                "Снимает ограничение MMCSS на сетевой трафик (~10 по умолчанию).",
                EvidenceLevel.NoData,
                "Часто рекомендуют для онлайн-игр; замеры зависят от сети.",
                RequiresReboot: false,
                Category: "Сеть"),
            [new RegistryEntry(RegistryHive.LocalMachine, MultimediaProfile, "NetworkThrottlingIndex", RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF), 10)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "system-responsiveness-10",
                "SystemResponsiveness = 10",
                "Минимальный резерв CPU для low-priority (MMCSS).",
                EvidenceLevel.NoData,
                "Значения ниже 10 Windows может игнорировать.",
                RequiresReboot: false,
                Category: "Сеть"),
            [new RegistryEntry(RegistryHive.LocalMachine, MultimediaProfile, "SystemResponsiveness", RegistryValueKind.DWord, 10, 20)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "tcp-nagle-off",
                "Отключить Nagle (TcpNoDelay / TcpAckFrequency)",
                "Может снизить latency в части онлайн-игр.",
                EvidenceLevel.NoData,
                "Эффект неоднозначен; на части соединений вреден.",
                RequiresReboot: false,
                Warning: "Экспериментально. При проблемах с сетью — откатите.",
                Category: "Сеть"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpAckFrequency", RegistryValueKind.DWord, 1),
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TCPNoDelay", RegistryValueKind.DWord, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "tcp-autotuning-normal",
                "TCP Autotuning = normal",
                "Включает нормальную автонастройку окна приёма TCP (лучше на быстрых каналах).",
                EvidenceLevel.Reasoned,
                "Соответствует netsh interface tcp set global autotuninglevel=normal.",
                RequiresReboot: false,
                Category: "Сеть"),
            [new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpWindowSize", RegistryValueKind.DWord, 0)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "tcp-timestamps-off",
                "Отключить TCP timestamps",
                "Убирает опцию timestamps; иногда чуть снижает overhead, но ломает PAWS.",
                EvidenceLevel.NoData,
                "На современных ОС обычно оставляют включённым.",
                RequiresReboot: true,
                Warning: "Может ухудшить поведение на нестабильных линиях.",
                Category: "Сеть"),
            [new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "Tcp1323Opts", RegistryValueKind.DWord, 1)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "tcp-max-connections",
                "Увеличить MaxUserPort / TcpTimedWaitDelay",
                "Больше исходящих портов и быстрее освобождение TIME_WAIT — полезно при множестве коротких соединений.",
                EvidenceLevel.Reasoned,
                "Часто советуют для P2P и нагруженных клиентов.",
                RequiresReboot: true,
                Category: "Сеть"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "MaxUserPort", RegistryValueKind.DWord, 65534),
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpTimedWaitDelay", RegistryValueKind.DWord, 30),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "dns-cache-positive",
                "DNS-кэш: дольше хранить успешные ответы",
                "Увеличивает MaxCacheTtl — меньше повторных запросов к DNS.",
                EvidenceLevel.NoData,
                "На часто меняющихся CDN может чуть устаревать ответы.",
                RequiresReboot: false,
                Category: "Сеть"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxCacheTtl", RegistryValueKind.DWord, 86400),
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Dnscache\Parameters", "MaxNegativeCacheTtl", RegistryValueKind.DWord, 5),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "disable-nln-probing",
                "Отключить Network Location Awareness probes",
                "Меньше фоновых HTTP-проверок «есть ли интернет» (NCSI).",
                EvidenceLevel.NoData,
                "Иногда ломает индикатор «Подключено / Без интернета» в трее.",
                RequiresReboot: false,
                Category: "Сеть"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\NlaSvc\Parameters\Internet", "EnableActiveProbing", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "disable-wifi-sense-hotspot",
                "Отключить автоматическое подключение к hotspot / Wi‑Fi Sense",
                "Не подключаться к открытым точкам и не раздавать данные о сетях.",
                EvidenceLevel.Reasoned,
                "Снижает лишний фон и риски на публичных Wi‑Fi.",
                RequiresReboot: false,
                Category: "Сеть"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\WcmSvc\wifinetworkmanager\config", "AutoConnectAllowedOEM", RegistryValueKind.DWord, 0),
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\PolicyManager\default\WiFi\AllowAutoConnectToWiFiSenseHotspots", "value", RegistryValueKind.DWord, 0),
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\PolicyManager\default\WiFi\AllowWiFiHotSpotReporting", "value", RegistryValueKind.DWord, 0),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "prefer-ipv4-over-ipv6",
                "Предпочитать IPv4 перед IPv6",
                "Меняет приоритет префиксов: меньше задержек там, где IPv6 «ломаный».",
                EvidenceLevel.NoData,
                "Если IPv6 у провайдера нормальный — твик не нужен.",
                RequiresReboot: true,
                Category: "Сеть"),
            [
                // DisabledComponents bit: prefer IPv4 (0x20) — осторожное значение, не полный disable IPv6
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip6\Parameters", "DisabledComponents", RegistryValueKind.DWord, 0x20, 0),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "lanman-throughput",
                "LanmanWorkstation: крупнее буферы SMB",
                "Чуть быстрее копирование по локальной сети (SMB).",
                EvidenceLevel.NoData,
                "На WAN эффект минимален.",
                RequiresReboot: true,
                Category: "Сеть"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "DisableBandwidthThrottling", RegistryValueKind.DWord, 1),
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "FileInfoCacheEntriesMax", RegistryValueKind.DWord, 32768),
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\LanmanWorkstation\Parameters", "DirectoryCacheEntriesMax", RegistryValueKind.DWord, 4096),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "qos-not-reserve-bandwidth",
                "Не резервировать 20% канала под QoS",
                "NonBestEffortLimit = 0: приложениям доступна вся полоса (если QoS-политики не заданы иначе).",
                EvidenceLevel.Reasoned,
                "Миф «всегда режется 20%» преувеличен, но limit=0 снимает резерв для PSched.",
                RequiresReboot: false,
                Category: "Сеть"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Psched", "NonBestEffortLimit", RegistryValueKind.DWord, 0),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "disable-delivery-optimization",
                "Отключить Delivery Optimization (P2P-обновления)",
                "Windows не раздаёт и не качает обновления с других ПК в интернете.",
                EvidenceLevel.Reasoned,
                "Экономит фон на слабом канале; обновления только с серверов Microsoft.",
                RequiresReboot: false,
                Category: "Сеть"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", RegistryValueKind.DWord, 0),
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization", "DODownloadMode", RegistryValueKind.DWord, 0),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "disable-wifi-power-saving",
                "Отключить энергосбережение Wi‑Fi (через политику)",
                "Снижает обрывы на ноутбуках, когда адаптер «засыпает».",
                EvidenceLevel.NoData,
                "Полностью режим адаптера лучше выставить в диспетчере устройств; здесь — смежные ключи.",
                RequiresReboot: false,
                Category: "Сеть"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "DisableDHCPMediaSense", RegistryValueKind.DWord, 0),
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile", "NetworkThrottlingIndex", RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF)),
            ],
            backup),

        // ─── Система ───
        new RegistryTweak(
            new TweakInfo(
                "vbs-off",
                "Отключить VBS (изоляция на основе виртуализации)",
                "Снижает накладные расходы гипервизора в играх.",
                EvidenceLevel.Measured,
                "Tom's Hardware и др.: до нескольких процентов в зависимости от игры.",
                RequiresReboot: true,
                Warning: "Снижает защиту. Античиты (Vanguard, FACEIT) могут требовать VBS.",
                Category: "Система"),
            [new RegistryEntry(RegistryHive.LocalMachine, DeviceGuardKey, "EnableVirtualizationBasedSecurity", RegistryValueKind.DWord, 0)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "hvci-off",
                "Отключить целостность памяти (HVCI)",
                "Выключает Memory Integrity.",
                EvidenceLevel.Measured,
                "Связано с VBS; прирост в играх в тех же тестах.",
                RequiresReboot: true,
                Warning: "Снижает защиту от вредоносных драйверов.",
                Category: "Система"),
            [new RegistryEntry(
                RegistryHive.LocalMachine,
                DeviceGuardKey + @"\Scenarios\HypervisorEnforcedCodeIntegrity",
                "Enabled", RegistryValueKind.DWord, 0)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "background-apps-off",
                "Запретить фоновую работу приложений Store",
                "Политика LetAppsRunInBackground = 2.",
                EvidenceLevel.Reasoned,
                "Меньше фоновой активности UWP.",
                RequiresReboot: false,
                Category: "Система"),
            [new RegistryEntry(
                RegistryHive.LocalMachine,
                @"SOFTWARE\Policies\Microsoft\Windows\AppPrivacy",
                "LetAppsRunInBackground", RegistryValueKind.DWord, 2)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "fast-startup-off",
                "Отключить быстрый запуск (Fast Startup)",
                "Полное выключение вместо гибернации ядра.",
                EvidenceLevel.Reasoned,
                "Стабильность драйверов между сеансами.",
                RequiresReboot: false,
                Category: "Система"),
            [new RegistryEntry(
                RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Power",
                "HiberbootEnabled", RegistryValueKind.DWord, 0, 1)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "prefetch-off",
                "Отключить Prefetch (для SSD)",
                "EnablePrefetcher = 0.",
                EvidenceLevel.NoData,
                "На SSD выгода Prefetch минимальна.",
                RequiresReboot: false,
                Category: "Система"),
            [new RegistryEntry(
                RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters",
                "EnablePrefetcher", RegistryValueKind.DWord, 0, 3)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "hags-on",
                "Hardware-accelerated GPU Scheduling",
                "HwSchMode = 2. Требует перезагрузки.",
                EvidenceLevel.NoData,
                "На части NVIDIA/AMD может снизить latency.",
                RequiresReboot: true,
                Category: "Система"),
            [new RegistryEntry(
                RegistryHive.LocalMachine,
                @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers",
                "HwSchMode", RegistryValueKind.DWord, 2, 1)],
            backup),

        new PowerPlanTweak(
            new TweakInfo(
                "power-high-performance",
                "Схема питания «Высокая производительность»",
                "Меньше парковки ядер, ниже latency отклика CPU.",
                EvidenceLevel.NoData,
                "Зависит от платформы; на ноутбуке сильнее греет и разряжает батарею.",
                RequiresReboot: false,
                Warning: "На ноутбуках батарея разряжается быстрее.",
                Category: "Система"),
            backup),

        
        // ─── Обновления и брандмауэр ───
        new RegistryTweak(
            new TweakInfo(
                "windows-update-disable",
                "Отключить автоматические обновления Windows",
                "Политика NoAutoUpdate: Центр обновления не ставит патчи сам.",
                EvidenceLevel.Reasoned,
                "Меньше фоновой нагрузки и внезапных перезагрузок. Обновления безопасности придётся ставить вручную.",
                RequiresReboot: false,
                Warning: "Система останется без авто-патчей. Используйте только если понимаете риски.",
                Category: "Система"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", RegistryValueKind.DWord, 1, 0),
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "AUOptions", RegistryValueKind.DWord, 1, 4),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "windows-update-notify-only",
                "Только уведомлять об обновлениях (не ставить)",
                "AUOptions = 2: скачивание/установка только с разрешения пользователя.",
                EvidenceLevel.Reasoned,
                "Компромисс: не отключает обновления полностью, но убирает тихую установку.",
                RequiresReboot: false,
                Category: "Система"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "NoAutoUpdate", RegistryValueKind.DWord, 0, 0),
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate\AU", "AUOptions", RegistryValueKind.DWord, 2, 4),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "driver-update-disable",
                "Не обновлять драйверы через Windows Update",
                "ExcludeWUDriversInQualityUpdate — драйверы не подтягиваются вместе с качеством.",
                EvidenceLevel.Reasoned,
                "Полезно, если стабильные драйверы GPU/чипа уже стоят с сайта вендора.",
                RequiresReboot: false,
                Category: "Система"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\WindowsUpdate", "ExcludeWUDriversInQualityUpdate", RegistryValueKind.DWord, 1, 0),
            ],
            backup),

        // Брандмауэр: заголовок + профили (сворачиваются в UI)
        new RegistryTweak(
            new TweakInfo(
                "firewall-disable-all-profiles",
                "Отключить брандмауэр Windows",
                "Выключает Defender Firewall на всех профилях (Domain, Private, Public).",
                EvidenceLevel.Reasoned,
                "Раскройте стрелкой, чтобы отключить только отдельные профили.",
                RequiresReboot: false,
                Warning: "Снижает сетевую защиту. Не рекомендуется без необходимости.",
                Category: "Система",
                GroupKey: "firewall",
                IsGroupHeader: true),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\DomainProfile", "EnableFirewall", RegistryValueKind.DWord, 0, 1),
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\PrivateProfile", "EnableFirewall", RegistryValueKind.DWord, 0, 1),
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\PublicProfile", "EnableFirewall", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "firewall-disable-domain",
                "Только профиль Domain",
                "EnableFirewall = 0 для доменного профиля.",
                EvidenceLevel.Reasoned,
                "На домашнем ПК почти не используется.",
                RequiresReboot: false,
                Warning: "Снижает защиту в доменной сети.",
                Category: "Система",
                GroupKey: "firewall"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\DomainProfile", "EnableFirewall", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "firewall-disable-private",
                "Только профиль Private (дом / офис)",
                "EnableFirewall = 0 для частной сети.",
                EvidenceLevel.Reasoned,
                "Иногда нужно для локальных игр и серверов.",
                RequiresReboot: false,
                Warning: "Снижает защиту в домашней сети.",
                Category: "Система",
                GroupKey: "firewall"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\PrivateProfile", "EnableFirewall", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "firewall-disable-public",
                "Только профиль Public (кафе, хотспот)",
                "EnableFirewall = 0 для общедоступных сетей.",
                EvidenceLevel.Reasoned,
                "Самый рискованный профиль.",
                RequiresReboot: false,
                Warning: "Сильно снижает защиту в публичных сетях.",
                Category: "Система",
                GroupKey: "firewall"),
            [
                new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsFirewall\PublicProfile", "EnableFirewall", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        // ─── Визуал ───
        new RegistryTweak(
            new TweakInfo(
                "theme-dark-apps",
                "Тёмная тема приложений Windows",
                "AppsUseLightTheme = 0 — тёмный режим для UWP/Настроек.",
                EvidenceLevel.Reasoned,
                "Действует сразу для многих приложений; проводник может потребовать перезапуск.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "theme-dark-system",
                "Тёмная тема панели задач и системы",
                "SystemUsesLightTheme = 0 — тёмный Taskbar/Start.",
                EvidenceLevel.Reasoned,
                "Меняет оформление оболочки Windows.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "theme-light-apps",
                "Светлая тема приложений Windows",
                "AppsUseLightTheme = 1.",
                EvidenceLevel.Reasoned,
                "Возврат светлого оформления приложений.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", RegistryValueKind.DWord, 1, 0),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "show-file-extensions",
                "Показывать расширения файлов (.exe, .dll…)",
                "HideFileExt = 0 в параметрах проводника.",
                EvidenceLevel.Reasoned,
                "Проще отличать опасные файлы вроде document.pdf.exe.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "show-hidden-files",
                "Показывать скрытые файлы и папки",
                "Hidden = 1 в параметрах проводника.",
                EvidenceLevel.Reasoned,
                "Видны файлы со атрибутом Hidden.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", RegistryValueKind.DWord, 1, 2),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "show-superhidden",
                "Показывать защищённые системные файлы",
                "ShowSuperHidden = 1 — видны файлы ОС (осторожно).",
                EvidenceLevel.Reasoned,
                "Нужно редко; случайное удаление системных файлов опасно.",
                RequiresReboot: false,
                Warning: "Не удаляйте незнакомые системные файлы.",
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "ShowSuperHidden", RegistryValueKind.DWord, 1, 0),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "desktop-icon-this-pc",
                "Значок «Этот компьютер» на рабочем столе",
                "Показывает This PC на desktop.",
                EvidenceLevel.Reasoned,
                "Удобный ярлык к дискам без открытия проводника.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{20D04FE0-3AEA-1069-A2D8-08002B30309D}", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "desktop-icon-recycle",
                "Значок «Корзина» на рабочем столе",
                "Показывает Recycle Bin (0 = показать).",
                EvidenceLevel.Reasoned,
                "Стандартный значок корзины.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{645FF040-5081-101B-9F08-00AA002F954E}", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "desktop-icon-network",
                "Значок «Сеть» на рабочем столе",
                "Показывает Network на desktop.",
                EvidenceLevel.Reasoned,
                "Быстрый доступ к сетевому окружению.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{F02C1A0D-BE21-4350-88B0-7367FC96EF3C}", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "desktop-icon-control-panel",
                "Значок «Панель управления» на рабочем столе",
                "Показывает Control Panel на desktop.",
                EvidenceLevel.Reasoned,
                "Классическая панель рядом с «Параметрами».",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\HideDesktopIcons\NewStartPanel", "{5399E694-6CE5-4D6C-8FCE-1D8870FDCBA0}", RegistryValueKind.DWord, 0, 1),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "explorer-nav-all-folders",
                "В панели навигации показывать все папки",
                "NavPaneShowAllFolders = 1 — дерево проводника как «все папки».",
                EvidenceLevel.NoData,
                "Удобнее переходить по дереву дисков и библиотек.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "NavPaneShowAllFolders", RegistryValueKind.DWord, 1, 0),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "explorer-show-full-path",
                "Полный путь в заголовке проводника",
                "Показывать полный путь вместо имени папки.",
                EvidenceLevel.Reasoned,
                "Удобнее ориентироваться в глубоких каталогах.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\CabinetState", "FullPath", RegistryValueKind.DWord, 1, 0),
            ],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "taskbar-endtask",
                "«Завершить задачу» в меню панели задач",
                "Включает End Task по ПКМ на приложении в taskbar (Win 11).",
                EvidenceLevel.Reasoned,
                "Быстрое закрытие зависших окон без диспетчера задач.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarEndTask", RegistryValueKind.DWord, 1, 0),
            ],
            backup),

        // ─── Набор «Registry Tweak» (отобранное из пользовательских .reg/.bat) ───
        Reg("cpu-priority-separation", "Приоритет фоновых задач: Win32PrioritySeparation = 0x26",
            "Короткие фиксированные кванты и приоритет активного окна — типичная настройка для игр.",
            EvidenceLevel.NoData, "Количественных замеров нет; на современных CPU разница обычно в пределах погрешности.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", RegistryValueKind.DWord, 0x26, 2)),

        Reg("csrss-priority", "Повысить приоритет csrss.exe (CPU и ввод-вывод)",
            "Ставит csrss.exe (обработка ввода и окон) в высокий приоритет через PerfOptions.",
            EvidenceLevel.NoData, "Распространённая настройка из гайдов; независимых замеров не найдено.",
            true, "Игры", backup, "Неверные значения PerfOptions могут ухудшить отзывчивость системы. Откат возвращает исходное состояние.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "CpuPriorityClass", RegistryValueKind.DWord, 3),
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "IoPriority", RegistryValueKind.DWord, 3)),

        Reg("svchost-split-off", "Объединить службы в меньшее число svchost.exe",
            "SvcHostSplitThresholdInKB больше объёма ОЗУ: службы не разносятся по отдельным процессам.",
            EvidenceLevel.Reasoned, "Меньше процессов svchost и немного меньше памяти; отдельные сбои службы могут затронуть соседние.",
            true, "Система", backup, "Одна упавшая служба может потянуть за собой остальные в том же svchost.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "SvcHostSplitThresholdInKB", RegistryValueKind.DWord, SvcHostThresholdKb(), 3670016)),

        Reg("spectre-meltdown-off", "Отключить защиту от Spectre/Meltdown",
            "FeatureSettingsOverride/Mask = 3: выключает программные митигации уязвимостей процессора.",
            EvidenceLevel.Measured, "Прирост заметен в основном на старых процессорах без аппаратных исправлений; на новых — почти нулевой.",
            true, "Система", backup, "Снижает защиту от атак на процессор (чтение чужой памяти). Не включайте на ПК, где открываете недоверенные программы и сайты.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettings", RegistryValueKind.DWord, 1),
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride", RegistryValueKind.DWord, 3),
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverrideMask", RegistryValueKind.DWord, 3)),

        Reg("maintenance-off", "Отключить автоматическое обслуживание Windows",
            "MaintenanceDisabled = 1: не запускаются фоновые задачи обслуживания в простое.",
            EvidenceLevel.Reasoned, "Убирает неожиданную фоновую нагрузку; обслуживание придётся запускать вручную.",
            false, "Система", backup, "Дефрагментация/TRIM по расписанию и диагностика перестанут запускаться сами.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled", RegistryValueKind.DWord, 1)),

        Reg("telemetry-off", "Минимальная телеметрия (AllowTelemetry = 0)",
            "Сообщает Windows, что диагностические данные отправлять не нужно.",
            EvidenceLevel.Reasoned, "Полностью работает только в редакциях Pro/Enterprise/Education; в Home действует как «обязательные данные».",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", RegistryValueKind.DWord, 0)),

        Reg("gamedvr-policy-off", "Запретить Game DVR политикой",
            "AllowGameDVR = 0 в политиках: запись игр отключена для всех пользователей.",
            EvidenceLevel.Reasoned, "Дополняет отключение Game DVR на уровне пользователя.",
            false, "Игры", backup, "Запись через Game Bar перестанет работать.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", RegistryValueKind.DWord, 0)),

        Reg("gamebar-nexus-off", "Отключить кнопку Game Bar на геймпаде",
            "UseNexusForGameBarEnabled = 0: кнопка Xbox на контроллере не открывает Game Bar.",
            EvidenceLevel.NoData, "Убирает случайные вызовы оверлея во время игры.",
            false, "Игры", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "UseNexusForGameBarEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("hibernate-off", "Отключить гибернацию",
            "HibernateEnabled = 0: освобождает место (hiberfil.sys ≈ 40% ОЗУ на диске).",
            EvidenceLevel.Reasoned, "Освобождает несколько ГБ на системном диске.",
            true, "Система", backup, "Режим гибернации станет недоступен. Быстрый запуск тоже работает через hiberfil.sys.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("menu-delay-0", "Меню без задержки (MenuShowDelay = 0)",
            "Подменю открываются сразу, без паузы 400 мс.",
            EvidenceLevel.Reasoned, "Ощущение быстрого интерфейса, на производительность не влияет.",
            false, "Визуал", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", RegistryValueKind.String, "0", "400")),

        Reg("autoplay-off", "Отключить автозапуск носителей",
            "DisableAutoplay = 1: диски и флешки не запускаются автоматически.",
            EvidenceLevel.Reasoned, "Закрывает типичный путь заражения со сменных носителей.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay", RegistryValueKind.DWord, 1, 0)),

        Reg("track-progs-off", "Не отслеживать запуски программ для меню «Пуск»",
            "Start_TrackProgs = 0: Windows не запоминает часто запускаемые программы.",
            EvidenceLevel.Reasoned, "Приватность; на скорость почти не влияет.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", RegistryValueKind.DWord, 0, 1)),

        Reg("input-personalization-off", "Не собирать данные рукописного и текстового ввода",
            "Отключает сбор контактов и персонализацию ввода.",
            EvidenceLevel.Reasoned, "Приватность; на скорость не влияет.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", RegistryValueKind.DWord, 1, 0),
            new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", RegistryValueKind.DWord, 1, 0),
            new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", RegistryValueKind.DWord, 0, 1)),

        Reg("llmnr-off", "Отключить LLMNR (многоадресный DNS)",
            "EnableMulticast = 0: Windows не рассылает LLMNR-запросы по локальной сети.",
            EvidenceLevel.Reasoned, "Снижает риск перехвата имён в сети; в домашней сети обычно ничего не ломает.",
            false, "Сеть", backup, "Имена соседних устройств без DNS могут перестать находиться.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", RegistryValueKind.DWord, 0)),

        Reg("remote-assistance-off", "Отключить удалённого помощника",
            "fAllowToGetHelp = 0: запрещает приглашения Remote Assistance.",
            EvidenceLevel.Reasoned, "Уменьшает поверхность атаки; нужен только если вам помогают удалённо.",
            false, "Сеть", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", RegistryValueKind.DWord, 0, 1)),

        Reg("maps-autoupdate-off", "Не обновлять офлайн-карты автоматически",
            "AutoUpdateEnabled = 0 для офлайн-карт Windows.",
            EvidenceLevel.Reasoned, "Убирает фоновые загрузки; важно только если карты скачаны.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\Maps", "AutoUpdateEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("store-autodownload-off", "Не обновлять приложения Microsoft Store автоматически",
            "Политика AutoDownload = 2.",
            EvidenceLevel.Reasoned, "Меньше фоновых загрузок; приложения Store придётся обновлять вручную.",
            false, "Система", backup, "Обновления безопасности приложений Store не будут приходить сами.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", RegistryValueKind.DWord, 2)),

        Reg("feedback-prompts-off", "Не показывать запросы обратной связи",
            "NumberOfSIUFInPeriod = 0: Windows не просит оценить систему.",
            EvidenceLevel.Reasoned, "Убирает всплывающие опросы.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", RegistryValueKind.DWord, 0)),

        // ─── Из набора пользователя: службы обновления браузеров, ввод, уведомления, приватность ───
        .. BrowserUpdaterTweak(backup),

        Reg("chrome-edge-background-off", "Chrome и Edge: без ускоренного запуска и фоновой работы",
            "Политики StartupBoostEnabled = 0 и BackgroundModeEnabled = 0 для Chrome и Edge: браузер не висит в памяти после закрытия.",
            EvidenceLevel.Reasoned, "Освобождает память и убирает фоновые процессы; первый запуск браузера станет чуть медленнее.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Google\Chrome", "StartupBoostEnabled", RegistryValueKind.DWord, 0),
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Google\Chrome", "BackgroundModeEnabled", RegistryValueKind.DWord, 0),
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", RegistryValueKind.DWord, 0),
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", RegistryValueKind.DWord, 0)),

        Reg("game-mode-auto-off", "Отключить автоматический игровой режим Windows",
            "AllowAutoGameMode и AutoGameModeEnabled = 0, подсказки Game Bar не показываются.",
            EvidenceLevel.NoData, "Игровой режим Windows спорен: на части систем даёт микрофризы. Проверьте на своих играх.",
            false, "Игры", backup, "Игровой режим перестанет включаться сам.",
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "AllowAutoGameMode", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "ShowStartupPanel", RegistryValueKind.DWord, 0, 1)),

        Reg("multimedia-nolazymode", "MMCSS: NoLazyMode = 1",
            "Планировщик мультимедиа не уходит в «ленивый» режим, пока идёт игра или аудио.",
            EvidenceLevel.NoData, "Из распространённых гайдов; независимых замеров не найдено.",
            false, "Игры", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, MultimediaProfile, "NoLazyMode", RegistryValueKind.DWord, 1)),

        Reg("input-queue-size", "Очередь ввода клавиатуры и мыши: 50 событий (0x32)",
            "KeyboardDataQueueSize и MouseDataQueueSize = 50 вместо 100 по умолчанию.",
            EvidenceLevel.NoData, "Измеримого выигрыша в независимых тестах не найдено; при перегрузке системы события могут теряться раньше.",
            true, "Игры", backup, "В исходном .reg у MouseDataQueueSize стоял лишний пробел в имени — здесь он исправлен, иначе значение Windows игнорирует.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", RegistryValueKind.DWord, 0x32, 0x64),
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", RegistryValueKind.DWord, 0x32, 0x64)),

        Reg("toast-notifications-off", "Отключить всплывающие уведомления Windows",
            "ToastEnabled = 0 и запрет уведомлений на экране блокировки.",
            EvidenceLevel.Reasoned, "Меньше отвлекающих окон во время игры.",
            false, "Система", backup, "Уведомления приложений и системы показываться не будут.",
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications", "LockScreenToastEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("search-cortana-off", "Отключить Cortana и историю поиска на устройстве",
            "CortanaEnabled, CortanaConsent и IsDeviceSearchHistoryEnabled = 0.",
            EvidenceLevel.Reasoned, "Приватность; на скорость почти не влияет.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "CortanaEnabled", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "CortanaConsent", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDeviceSearchHistoryEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("privacy-language-speech-off", "Не передавать список языков сайтам и отключить онлайн-распознавание речи",
            "HttpAcceptLanguageOptOut = 1 и OnlineSpeechPrivacy HasAccepted = 0.",
            EvidenceLevel.Reasoned, "Приватность; на скорость не влияет.",
            false, "Система", backup, "Голосовой ввод через облако Windows перестанет работать.",
            new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", RegistryValueKind.DWord, 1, 0),
            new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", RegistryValueKind.DWord, 0, 1)),

        Reg("news-interests-off", "Отключить «Новости и интересы» политикой",
            "AllowNewsAndInterests = 0.",
            EvidenceLevel.Reasoned, "Убирает фоновую загрузку ленты на панели задач.",
            false, "Визуал", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", RegistryValueKind.DWord, 0)),

        Reg("device-metadata-offline", "Не загружать метаданные устройств из интернета",
            "PreventDeviceMetadataFromNetwork = 1: Windows не скачивает значки и описания устройств.",
            EvidenceLevel.Reasoned, "Убирает сетевые запросы при подключении устройств.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Metadata", "PreventDeviceMetadataFromNetwork", RegistryValueKind.DWord, 1, 0)),
    ];
}
