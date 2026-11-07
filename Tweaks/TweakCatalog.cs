using Microsoft.Win32;

namespace WinOptimizer.Tweaks;

/// <summary>
/// Список твиков по категориям: Игры, Интерфейс, Сеть, Система, Службы.
/// </summary>
public static class TweakCatalog
{
    private const string DeviceGuardKey = @"SYSTEM\CurrentControlSet\Control\DeviceGuard";
    private const string MultimediaProfile = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

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
    ];
}
