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
                new TweakInfo("browser-updaters-off", "Отключить фоновое обновление Edge и Chrome",
                    "Останавливает фоновые службы, которые сами обновляют браузеры. Обновления ставятся при запуске браузера или вручную.",
                    EvidenceLevel.Reasoned, "Убирает фоновые процессы обновления; браузеры обновляются при запуске или вручную.",
                    RequiresReboot: true,
                    Warning: "Браузеры перестанут обновляться в фоне, в том числе патчами безопасности. Обновляйте их вручную.",
                    Category: "Система"),
                entries, backup),
        ];
    }

    private static PageFileTweak PageFile(BackupStore backup)
    {
        var sizeMb = PageFileTweak.RecommendedSizeMb(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        var sizeGb = sizeMb / 1024.0;
        return new PageFileTweak(
            new TweakInfo(
                "pagefile-fixed",
                "Файл подкачки фиксированного размера",
                $"Размер подкачки задаётся один раз ({sizeGb:0.#} ГБ на системном диске) и больше не меняется. Windows не тратит время на её расширение и сжатие.",
                EvidenceLevel.NoData,
                "Выигрыш в играх независимыми замерами не подтверждён; фиксированный размер просто убирает динамическое изменение файла.",
                RequiresReboot: true,
                Warning: $"Под файл сразу занимается {sizeGb:0.#} ГБ на системном диске. При нехватке памяти в тяжёлых программах фиксированного размера может не хватить. Откат возвращает прежние настройки.",
                Category: "Система"),
            backup,
            sizeMb);
    }

    public static IReadOnlyList<ITweak> Create(BackupStore backup) =>
    [
        // ─── Игры ───
        new RegistryTweak(
            new TweakInfo(
                "game-dvr-off",
                "Отключить фоновую запись игр",
                "Xbox Game Bar перестаёт записывать видео и клипы в фоне, освобождая процессор и видеокарту.",
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
                "Отключить оптимизацию полноэкранного режима",
                "Игры работают в настоящем полноэкранном режиме. Помогает, если есть микрофризы или проблемы с задержкой.",
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
                "Мышь без ускорения",
                "Курсор двигается строго за рукой, без разгона от скорости движения. Точнее целиться в играх.",
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
                "Больше ресурсов для игр",
                "Windows отдаёт играм повышенный приоритет процессора и видеокарты.",
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
                "Убрать прозрачность окон",
                "Панель задач, «Пуск» и окна без эффекта стекла. Чуть меньше нагрузка на видеокарту.",
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
                "Отключить «Залипание клавиш»",
                "Окно с вопросом больше не появляется, если несколько раз нажать Shift в игре.",
                EvidenceLevel.Reasoned,
                "Устраняет навязчивое окно, а не нагрузку.",
                RequiresReboot: false,
                Category: "Интерфейс"),
            [new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", RegistryValueKind.String, "506", "510")],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "animations-off",
                "Убрать анимацию окон",
                "Окна сворачиваются и разворачиваются мгновенно.",
                EvidenceLevel.Reasoned,
                "Субъективная отзывчивость UI.",
                RequiresReboot: false,
                Category: "Интерфейс"),
            [new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\Desktop\WindowMetrics", "MinAnimate", RegistryValueKind.String, "0", "1")],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "widgets-off",
                "Убрать виджеты и новости",
                "Кнопка виджетов и новостей пропадает с панели задач.",
                EvidenceLevel.Reasoned,
                "Меньше фоновых процессов ленты новостей.",
                RequiresReboot: false,
                Category: "Интерфейс"),
            [new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", RegistryValueKind.DWord, 0, 1)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "search-highlights-off",
                "Убрать рекламу и Bing из поиска",
                "В поиске Windows нет рекламных блоков, новостей и результатов из интернета.",
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
                "Снять ограничение на сетевой трафик",
                "Windows не притормаживает сеть, пока играет музыка или видео. Меньше задержки в онлайн-играх.",
                EvidenceLevel.NoData,
                "Часто рекомендуют для онлайн-игр; замеры зависят от сети.",
                RequiresReboot: false,
                Category: "Сеть"),
            [new RegistryEntry(RegistryHive.LocalMachine, MultimediaProfile, "NetworkThrottlingIndex", RegistryValueKind.DWord, unchecked((int)0xFFFFFFFF), 10)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "system-responsiveness-10",
                "Меньше резерва процессора для фона",
                "Фоновым задачам достаётся минимум процессора, игры и программы получают больше.",
                EvidenceLevel.NoData,
                "Значения ниже 10 Windows может игнорировать.",
                RequiresReboot: false,
                Category: "Сеть"),
            [new RegistryEntry(RegistryHive.LocalMachine, MultimediaProfile, "SystemResponsiveness", RegistryValueKind.DWord, 10, 20)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "tcp-nagle-off",
                "Меньше задержки в онлайн-играх",
                "Отключает склейку мелких сетевых пакетов, из-за которой данные уходят с небольшой паузой.",
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
                "Оптимальная скорость загрузки",
                "Включает штатную автоподстройку приёма данных. Лучше работает на быстром интернете.",
                EvidenceLevel.Reasoned,
                "Соответствует netsh interface tcp set global autotuninglevel=normal.",
                RequiresReboot: false,
                Category: "Сеть"),
            [new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\Tcpip\Parameters", "TcpWindowSize", RegistryValueKind.DWord, 0)],
            backup),

        new RegistryTweak(
            new TweakInfo(
                "tcp-timestamps-off",
                "Отключить метки времени в сети",
                "Чуть меньше служебных данных в каждом пакете. Эффект небольшой.",
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
                "Больше одновременных соединений",
                "Освобождает сетевые порты быстрее и даёт их больше. Помогает торрентам и программам со множеством подключений.",
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
                "Дольше помнить адреса сайтов",
                "Windows реже запрашивает у DNS уже известные адреса, сайты открываются чуть быстрее.",
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
                "Не проверять наличие интернета",
                "Windows перестаёт в фоне обращаться к серверам Microsoft, чтобы узнать, есть ли связь.",
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
                "Не подключаться к открытым Wi‑Fi сам",
                "Windows не заходит автоматически в открытые точки доступа и не делится данными о сетях.",
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
                "Предпочитать IPv4",
                "Сначала пробуется старый протокол IPv4. Помогает, если часть сайтов или игр грузится с задержкой.",
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
                "Быстрее копировать по локальной сети",
                "Увеличивает буферы при работе с общими папками и сетевыми дисками.",
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
                "Убрать резервирование 20% интернета",
                "Windows перестаёт держать про запас часть канала. Программам доступна вся скорость.",
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
                "Не раздавать обновления другим ПК",
                "Windows не качает и не отдаёт части обновлений через интернет с чужих компьютеров. Экономит трафик.",
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
                "Не усыплять Wi‑Fi адаптер",
                "Адаптер не засыпает для экономии энергии. Меньше обрывов связи на ноутбуках.",
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
                "Отключить изоляцию Windows на виртуализации",
                "Выключает защитный слой на основе виртуализации. Игры могут давать больше FPS, но защита слабее.",
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
                "Отключить целостность памяти",
                "Выключает проверку драйверов в защищённой области. Прирост FPS на части ПК, защита слабее.",
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
                "Запретить приложениям Store работать в фоне",
                "Магазинные приложения не работают в фоне и не расходуют память и трафик.",
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
                "Отключить быстрый запуск",
                "Выключение полностью завершает работу системы. Меньше странных ошибок после включения.",
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
                "Отключить предзагрузку программ",
                "Windows не заранее подгружает данные программ. Имеет смысл на SSD.",
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
                "Ускорить планирование задач видеокарты",
                "Видеокарта сама управляет своей очередью задач. Может добавить FPS. Нужна перезагрузка.",
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
                "Режим питания «Высокая производительность»",
                "Процессор не снижает частоту и не отключает ядра. Быстрее отклик, но больше нагрев и расход батареи.",
                EvidenceLevel.NoData,
                "Зависит от платформы; на ноутбуке сильнее греет и разряжает батарею.",
                RequiresReboot: false,
                Warning: "На ноутбуках батарея разряжается быстрее.",
                Category: "Система"),
            backup),

        PageFile(backup),

        // ─── Обновления и брандмауэр ───
        new RegistryTweak(
            new TweakInfo(
                "windows-update-disable",
                "Отключить автоматические обновления Windows",
                "Центр обновления сам ничего не скачивает и не ставит.",
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
                "Обновления Windows только с разрешения",
                "Windows сообщает о новых обновлениях, а скачивает и ставит их только по вашему согласию.",
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
                "Не ставить драйверы через Центр обновления",
                "Windows не подменяет установленные вами драйверы своими версиями.",
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
                "Полностью выключает встроенный брандмауэр во всех сетях.",
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
                "Брандмауэр: сеть организации",
                "Выключает брандмауэр только в корпоративной (доменной) сети.",
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
                "Брандмауэр: домашняя сеть",
                "Выключает брандмауэр только в домашней и офисной сети.",
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
                "Брандмауэр: общественные сети",
                "Выключает брандмауэр только в кафе, отелях и на точках доступа.",
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
                "Тёмная тема для приложений",
                "Настройки и приложения Windows переключаются на тёмное оформление.",
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
                "Тёмная тема для панели задач",
                "Панель задач и меню «Пуск» становятся тёмными.",
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
                "Светлая тема для приложений",
                "Возвращает светлое оформление настроек и приложений.",
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
                "Показывать расширения файлов",
                "В именах видно .exe, .txt, .jpg и т. д. Проще заметить подозрительный файл.",
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
                "Проводник показывает файлы и папки, скрытые от глаз.",
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
                "Видны служебные файлы Windows. Осторожно: их нельзя удалять.",
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
                "Добавляет значок для быстрого доступа к дискам.",
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
                "Показывает корзину на рабочем столе.",
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
                "Добавляет значок для доступа к сетевым устройствам.",
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
                "Добавляет значок классической панели управления.",
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
                "Все папки в панели проводника",
                "Слева в проводнике показывается полное дерево папок.",
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
                "В заголовке окна видно, где именно вы находитесь, а не только имя папки.",
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
                "Зависшее приложение можно закрыть правым щелчком по значку на панели (Windows 11).",
                EvidenceLevel.Reasoned,
                "Быстрое закрытие зависших окон без диспетчера задач.",
                RequiresReboot: false,
                Category: "Визуал"),
            [
                new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarEndTask", RegistryValueKind.DWord, 1, 0),
            ],
            backup),

        // ─── Набор «Registry Tweak» (отобранное из пользовательских .reg/.bat) ───
        Reg("cpu-priority-separation", "Приоритет активного окна",
            "Игра или программа, с которой вы работаете, получает больше процессорного времени, чем фоновые.",
            EvidenceLevel.NoData, "Количественных замеров нет; на современных CPU разница обычно в пределах погрешности.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\PriorityControl", "Win32PrioritySeparation", RegistryValueKind.DWord, 0x26, 2)),

        Reg("csrss-priority", "Быстрее реакция на ввод",
            "Системный процесс, который обрабатывает мышь, клавиатуру и окна, получает повышенный приоритет.",
            EvidenceLevel.NoData, "Распространённая настройка из гайдов; независимых замеров не найдено.",
            true, "Игры", backup, "Неверные значения PerfOptions могут ухудшить отзывчивость системы. Откат возвращает исходное состояние.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "CpuPriorityClass", RegistryValueKind.DWord, 3),
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions", "IoPriority", RegistryValueKind.DWord, 3)),

        Reg("svchost-split-off", "Меньше процессов служб в диспетчере",
            "Служебные процессы Windows объединяются. Немного экономит память.",
            EvidenceLevel.Reasoned, "Меньше процессов svchost и немного меньше памяти; отдельные сбои службы могут затронуть соседние.",
            true, "Система", backup, "Одна упавшая служба может потянуть за собой остальные в том же svchost.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control", "SvcHostSplitThresholdInKB", RegistryValueKind.DWord, SvcHostThresholdKb(), 3670016)),

        Reg("spectre-meltdown-off", "Отключить защиту от уязвимостей процессора",
            "Выключает программные заплатки Spectre и Meltdown. Может дать прирост на старых процессорах.",
            EvidenceLevel.Measured, "Прирост заметен в основном на старых процессорах без аппаратных исправлений; на новых — почти нулевой.",
            true, "Система", backup, "Снижает защиту от атак на процессор (чтение чужой памяти). Не включайте на ПК, где открываете недоверенные программы и сайты.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettings", RegistryValueKind.DWord, 1),
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverride", RegistryValueKind.DWord, 3),
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management", "FeatureSettingsOverrideMask", RegistryValueKind.DWord, 3)),

        Reg("maintenance-off", "Отключить автообслуживание Windows",
            "Windows не запускает фоновые задачи обслуживания, когда вы не за компьютером.",
            EvidenceLevel.Reasoned, "Убирает неожиданную фоновую нагрузку; обслуживание придётся запускать вручную.",
            false, "Система", backup, "Дефрагментация/TRIM по расписанию и диагностика перестанут запускаться сами.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Schedule\Maintenance", "MaintenanceDisabled", RegistryValueKind.DWord, 1)),

        Reg("telemetry-off", "Минимум отправки данных в Microsoft",
            "Windows получает указание не отправлять диагностические данные.",
            EvidenceLevel.Reasoned, "Полностью работает только в редакциях Pro/Enterprise/Education; в Home действует как «обязательные данные».",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\DataCollection", "AllowTelemetry", RegistryValueKind.DWord, 0)),

        Reg("gamedvr-policy-off", "Запретить запись игр для всех пользователей",
            "Запись игр отключена на уровне системы, её нельзя включить обратно из настроек.",
            EvidenceLevel.Reasoned, "Дополняет отключение Game DVR на уровне пользователя.",
            false, "Игры", backup, "Запись через Game Bar перестанет работать.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", RegistryValueKind.DWord, 0)),

        Reg("gamebar-nexus-off", "Кнопка Xbox на геймпаде не открывает Game Bar",
            "Нажатие кнопки Xbox на контроллере больше не вызывает панель Game Bar.",
            EvidenceLevel.NoData, "Убирает случайные вызовы оверлея во время игры.",
            false, "Игры", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "UseNexusForGameBarEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("hibernate-off", "Отключить гибернацию",
            "Освобождает на диске место, равное примерно 40% оперативной памяти. Спящий режим продолжит работать.",
            EvidenceLevel.Reasoned, "Освобождает несколько ГБ на системном диске.",
            true, "Система", backup, "Режим гибернации станет недоступен. Быстрый запуск тоже работает через hiberfil.sys.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Power", "HibernateEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("menu-delay-0", "Меню открываются мгновенно",
            "Подменю появляются сразу, без паузы около 0,4 секунды.",
            EvidenceLevel.Reasoned, "Ощущение быстрого интерфейса, на производительность не влияет.",
            false, "Визуал", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\Desktop", "MenuShowDelay", RegistryValueKind.String, "0", "400")),

        Reg("autoplay-off", "Отключить автозапуск флешек и дисков",
            "Подключённые носители не запускают программы сами. Это безопаснее.",
            EvidenceLevel.Reasoned, "Закрывает типичный путь заражения со сменных носителей.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\AutoplayHandlers", "DisableAutoplay", RegistryValueKind.DWord, 1, 0)),

        Reg("track-progs-off", "Не запоминать часто запускаемые программы",
            "Меню «Пуск» перестаёт собирать список ваших самых используемых программ.",
            EvidenceLevel.Reasoned, "Приватность; на скорость почти не влияет.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackProgs", RegistryValueKind.DWord, 0, 1)),

        Reg("input-personalization-off", "Не собирать данные о вашем наборе текста",
            "Windows не запоминает почерк, контакты и привычки набора для персонализации.",
            EvidenceLevel.Reasoned, "Приватность; на скорость не влияет.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitInkCollection", RegistryValueKind.DWord, 1, 0),
            new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization", "RestrictImplicitTextCollection", RegistryValueKind.DWord, 1, 0),
            new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\InputPersonalization\TrainedDataStore", "HarvestContacts", RegistryValueKind.DWord, 0, 1)),

        Reg("llmnr-off", "Отключить лишние запросы по локальной сети",
            "Windows не рассылает по сети запросы имён компьютеров. Снижает риск атак в общей сети.",
            EvidenceLevel.Reasoned, "Снижает риск перехвата имён в сети; в домашней сети обычно ничего не ломает.",
            false, "Сеть", backup, "Имена соседних устройств без DNS могут перестать находиться.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows NT\DNSClient", "EnableMulticast", RegistryValueKind.DWord, 0)),

        Reg("remote-assistance-off", "Отключить удалённого помощника",
            "Никто не сможет отправить приглашение для удалённого управления вашим ПК.",
            EvidenceLevel.Reasoned, "Уменьшает поверхность атаки; нужен только если вам помогают удалённо.",
            false, "Сеть", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Control\Remote Assistance", "fAllowToGetHelp", RegistryValueKind.DWord, 0, 1)),

        Reg("maps-autoupdate-off", "Не обновлять офлайн-карты",
            "Скачанные карты Windows остаются как есть и не качают обновления.",
            EvidenceLevel.Reasoned, "Убирает фоновые загрузки; важно только если карты скачаны.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\Maps", "AutoUpdateEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("store-autodownload-off", "Не обновлять приложения Store сами",
            "Магазин не скачивает обновления приложений без вашей команды.",
            EvidenceLevel.Reasoned, "Меньше фоновых загрузок; приложения Store придётся обновлять вручную.",
            false, "Система", backup, "Обновления безопасности приложений Store не будут приходить сами.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\WindowsStore", "AutoDownload", RegistryValueKind.DWord, 2)),

        Reg("feedback-prompts-off", "Не просить оценить Windows",
            "Окна с просьбой оставить отзыв больше не появляются.",
            EvidenceLevel.Reasoned, "Убирает всплывающие опросы.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Siuf\Rules", "NumberOfSIUFInPeriod", RegistryValueKind.DWord, 0)),

        // ─── Из набора пользователя: службы обновления браузеров, ввод, уведомления, приватность ───
        .. BrowserUpdaterTweak(backup),

        Reg("chrome-edge-background-off", "Chrome и Edge не висят в фоне",
            "Браузеры не запускаются заранее и не работают после закрытия. Освобождает оперативную память.",
            EvidenceLevel.Reasoned, "Освобождает память и убирает фоновые процессы; первый запуск браузера станет чуть медленнее.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Google\Chrome", "StartupBoostEnabled", RegistryValueKind.DWord, 0),
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Google\Chrome", "BackgroundModeEnabled", RegistryValueKind.DWord, 0),
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "StartupBoostEnabled", RegistryValueKind.DWord, 0),
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Edge", "BackgroundModeEnabled", RegistryValueKind.DWord, 0)),

        Reg("game-mode-auto-off", "Отключить автоматический игровой режим",
            "Windows сама не включает игровой режим и не показывает подсказки Game Bar.",
            EvidenceLevel.NoData, "Игровой режим Windows спорен: на части систем даёт микрофризы. Проверьте на своих играх.",
            false, "Игры", backup, "Игровой режим перестанет включаться сам.",
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "AllowAutoGameMode", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\GameBar", "ShowStartupPanel", RegistryValueKind.DWord, 0, 1)),

        Reg("multimedia-nolazymode", "Мультимедиа без пауз в играх",
            "Система не снижает активность обработки звука и видео, пока идёт игра.",
            EvidenceLevel.NoData, "Из распространённых гайдов; независимых замеров не найдено.",
            false, "Игры", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, MultimediaProfile, "NoLazyMode", RegistryValueKind.DWord, 1)),

        Reg("input-queue-size", "Быстрее обрабатывать мышь и клавиатуру",
            "Уменьшает очередь событий ввода. Отклик может стать чуть быстрее.",
            EvidenceLevel.NoData, "Измеримого выигрыша в независимых тестах не найдено; при перегрузке системы события могут теряться раньше.",
            true, "Игры", backup, "В исходном .reg у MouseDataQueueSize стоял лишний пробел в имени — здесь он исправлен, иначе значение Windows игнорирует.",
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\kbdclass\Parameters", "KeyboardDataQueueSize", RegistryValueKind.DWord, 0x32, 0x64),
            new RegistryEntry(RegistryHive.LocalMachine, @"SYSTEM\CurrentControlSet\Services\mouclass\Parameters", "MouseDataQueueSize", RegistryValueKind.DWord, 0x32, 0x64)),

        Reg("toast-notifications-off", "Отключить всплывающие уведомления",
            "Окошки в углу экрана и на экране блокировки больше не появляются.",
            EvidenceLevel.Reasoned, "Меньше отвлекающих окон во время игры.",
            false, "Система", backup, "Уведомления приложений и системы показываться не будут.",
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications", "ToastEnabled", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\PushNotifications", "LockScreenToastEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("search-cortana-off", "Отключить Cortana и историю поиска",
            "Голосовой помощник выключен, поиск не хранит историю на устройстве.",
            EvidenceLevel.Reasoned, "Приватность; на скорость почти не влияет.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "CortanaEnabled", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Search", "CortanaConsent", RegistryValueKind.DWord, 0, 1),
            new RegistryEntry(RegistryHive.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\SearchSettings", "IsDeviceSearchHistoryEnabled", RegistryValueKind.DWord, 0, 1)),

        Reg("privacy-language-speech-off", "Меньше личных данных для сайтов и речи",
            "Сайты не узнают список ваших языков, а онлайн-распознавание речи отключено.",
            EvidenceLevel.Reasoned, "Приватность; на скорость не влияет.",
            false, "Система", backup, "Голосовой ввод через облако Windows перестанет работать.",
            new RegistryEntry(RegistryHive.CurrentUser, @"Control Panel\International\User Profile", "HttpAcceptLanguageOptOut", RegistryValueKind.DWord, 1, 0),
            new RegistryEntry(RegistryHive.CurrentUser, @"Software\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy", "HasAccepted", RegistryValueKind.DWord, 0, 1)),

        Reg("news-interests-off", "Убрать «Новости и интересы»",
            "Лента новостей и погоды на панели задач отключена на уровне системы.",
            EvidenceLevel.Reasoned, "Убирает фоновую загрузку ленты на панели задач.",
            false, "Визуал", backup, null,
            // Windows 11: ключ Dsh защищён от записи — RegistryAccess сам выдаёт «Администраторам» права на него.
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Dsh", "AllowNewsAndInterests", RegistryValueKind.DWord, 0),
            // Windows 10: та же лента управляется политикой Windows Feeds.
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\Windows Feeds", "EnableFeeds", RegistryValueKind.DWord, 0)),

        Reg("device-metadata-offline", "Не качать описания устройств из интернета",
            "Windows не загружает значки и названия подключённых устройств.",
            EvidenceLevel.Reasoned, "Убирает сетевые запросы при подключении устройств.",
            false, "Система", backup, null,
            new RegistryEntry(RegistryHive.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Device Metadata", "PreventDeviceMetadataFromNetwork", RegistryValueKind.DWord, 1, 0)),
    ];
}
