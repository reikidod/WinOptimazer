[CmdletBinding()]
param(
    [ValidateSet('scan','measure','autodetect','dongle','safeapply','rollback','install','uninstall','editprofile','status','boot','service','applynow')]
    [string]$Mode = 'scan',
    [int]$Hz = 0,
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------- paths & service config
$Root               = Join-Path $env:ProgramData 'WinOptimizer\InputLatency'
$Profile_           = Join-Path $Root 'profile.json'
$StateF             = Join-Path $Root 'state.json'
$LogF               = Join-Path $Root 'log.txt'
$Installed          = Join-Path $Root 'InputLatencyOptimizer.ps1'
$ServiceName        = 'WinOptimizerInputLatencySvc'
$ServiceDisplayName = 'WinOptimizer Input Latency Service'
$ServiceExe         = Join-Path $Root 'WinOptimizerInputLatencySvc.exe'
$LegacyTaskName     = 'WinOptimizer-InputLatency-Service'
$OldWatchdogTask    = 'WinOptimizer-InputLatency-Watchdog'
$UsbGroup           = '2a737441-1930-4402-8d77-b2bebba308a3'
$UsbSuspend         = '48e6b7a6-50f5-4782-a5d4-53bb8f07e226'

function Test-IsAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if ($Mode -in @('autodetect','dongle','safeapply','rollback','install','uninstall','editprofile','applynow') -and -not (Test-IsAdministrator)) {
    try {
        $scriptPath = $PSCommandPath
        if (-not $scriptPath) { $scriptPath = $MyInvocation.MyCommand.Path }
        $hzArg = if ($Hz -gt 0) { " -Hz $Hz" } else { "" }
        $argList = "-NoLogo -NoProfile -ExecutionPolicy Bypass -File `"$scriptPath`" -Mode $Mode$hzArg -Elevated"
        $psExe = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
        $process = Start-Process -FilePath $psExe -ArgumentList $argList -Verb RunAs -Wait -PassThru
        exit $process.ExitCode
    }
    catch {
        Write-Host "Не удалось получить права администратора (UAC отменён?): $($_.Exception.Message)" -ForegroundColor Red
        exit 5
    }
}

# ---------------------------------------------------------------- logging / files
function Write-Log([string]$Text) {
    try {
        if (Test-Path -LiteralPath $Root) {
            Add-Content -LiteralPath $LogF -Value ((Get-Date).ToString('yyyy-MM-dd HH:mm:ss') + ' [' + $Mode + '] ' + $Text) -Encoding UTF8
        }
    } catch { }
}

function Initialize-Root {
    New-Item -ItemType Directory -Force -Path $Root | Out-Null
    & icacls.exe $Root /inheritance:r /grant:r '*S-1-5-18:(OI)(CI)F' '*S-1-5-32-544:(OI)(CI)F' '*S-1-5-32-545:(OI)(CI)RX' 2>&1 | Out-Null
    if (-not (Test-Path -LiteralPath $Profile_)) { Write-DefaultProfile }
}

function Write-DefaultProfile {
    $json = @'
{
  "ConfirmTimeoutSec": 25,
  "SettleSec": 2,
  "AutoApplyOnServiceStart": true,
  "WatchUsbReconnect": true,
  "Tweaks": [
    {
      "Name": "USB selective suspend off",
      "Type": "usb-suspend",
      "Enabled": true,
      "Target": "both"
    },
    {
      "Name": "Mouse polling rate",
      "Type": "polling",
      "Method": "hid-report",
      "Enabled": false,
      "Target": "mouse",
      "ControllerFamily": "auto",
      "Vid": "",
      "Pid": "",
      "ReportType": "auto",
      "VendorPageOnly": true,
      "ReportsByHz": {
        "125": "",
        "250": "",
        "500": "",
        "1000": ""
      },
      "DefaultHz": 1000,
      "Levels": [125, 250, 500, 1000],
      "ApplyCommand": "",
      "Comment": "Заполняется автоматически через режим -Mode autodetect или инспектор донгла -Mode dongle."
    },
    {
      "Name": "Keyboard polling rate",
      "Type": "polling",
      "Method": "command",
      "Enabled": false,
      "Target": "keyboard",
      "DefaultHz": 1000,
      "Levels": [125, 250, 500, 1000],
      "ApplyCommand": ""
    }
  ]
}
'@
    [IO.File]::WriteAllText($Profile_, $json.Replace("`r`n","`n").Replace("`n","`r`n"), (New-Object System.Text.UTF8Encoding($true)))
}

function Get-Prof {
    if (-not (Test-Path -LiteralPath $Profile_)) { throw "Профиль не найден: $Profile_" }
    try { return (Get-Content -Raw -LiteralPath $Profile_ | ConvertFrom-Json) }
    catch { throw "Профиль повреждён (ошибка JSON): $($_.Exception.Message)" }
}

function Save-Prof($Prof) {
    $json = $Prof | ConvertTo-Json -Depth 8
    $tmp = $Profile_ + '.tmp'
    [IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($true)))
    Move-Item -LiteralPath $tmp -Destination $Profile_ -Force
}

function Get-State {
    $s = @{ Pending = $null; Good = @{}; Prev = @{} }
    if (Test-Path -LiteralPath $StateF) {
        try {
            $j = Get-Content -Raw -LiteralPath $StateF | ConvertFrom-Json
            if ($j.Pending) { $s.Pending = $j.Pending }
            if ($j.Good) { foreach ($p in $j.Good.PSObject.Properties) { $s.Good[$p.Name] = [int]$p.Value } }
            if ($j.Prev) { foreach ($p in $j.Prev.PSObject.Properties) { $s.Prev[$p.Name] = $p.Value } }
        } catch { Write-Log "state.json повреждён, создаётся заново: $($_.Exception.Message)" }
    }
    return $s
}

function Save-State($State) {
    $json = $State | ConvertTo-Json -Depth 6
    $tmp = $StateF + '.tmp'
    [IO.File]::WriteAllText($tmp, $json, (New-Object System.Text.UTF8Encoding($true)))
    Move-Item -LiteralPath $tmp -Destination $StateF -Force
}

# ---------------------------------------------------------------- built-in dongle controller database
function Get-KnownDonglePreset([string]$VidHex, [string]$PidHex) {
    $v = $VidHex.ToUpperInvariant()
    $p = $PidHex.ToUpperInvariant()

    if ($v -eq '25A7') {
        return @{
            Family         = 'Compx CX52850 Wireless Dongle'
            Supported      = $false
            ReportType     = 'auto'
            VendorPageOnly = $true
            Levels         = @(125, 250, 500, 1000)
            ReportsByHz    = @{
                '125'  = ''
                '250'  = ''
                '500'  = ''
                '1000' = ''
            }
            Notes          = 'Обнаружен контроллер Compx (Lamzu / OEM 2.4 ГГц). У разных ревизий прошивок отличается Report ID и длина пакета — используйте пункт 2 (Контроллер донгла -> Считать Feature-репорт), чтобы увидеть поддерживаемый канал.'
        }
    }

    if ($v -in @('1915', '3554', '373B')) {
        return @{
            Family         = 'Nordic nRF52840 / High-Speed Dongle'
            Supported      = $false
            ReportType     = 'auto'
            VendorPageOnly = $true
            Levels         = @(125, 250, 500, 1000, 2000, 4000)
            ReportsByHz    = @{
                '125'  = ''
                '250'  = ''
                '500'  = ''
                '1000' = ''
                '2000' = ''
                '4000' = ''
            }
            Notes          = "Обнаружен Nordic-совместимый донгл (VID_$v PID_$p). Проинспектируйте каналы в пункте 2."
        }
    }

    return @{
        Family         = "Generic HID Mouse/Dongle (VID_$v PID_$p)"
        Supported      = $false
        ReportType     = 'auto'
        VendorPageOnly = $true
        Levels         = @(125, 250, 500, 1000)
        ReportsByHz    = @{
            '125'  = ''
            '250'  = ''
            '500'  = ''
            '1000' = ''
        }
        Notes          = 'Неизвестный OEM-контроллер. Проверьте каналы через пункт 2 (Контроллер донгла).'
    }
}

# ---------------------------------------------------------------- power scheme helpers
function Get-Scheme {
    $output = (& powercfg.exe /getactivescheme 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "powercfg /getactivescheme завершился с ошибкой: $output" }
    $match = [regex]::Match($output, '[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}')
    if (-not $match.Success) { throw "Не удалось определить активную схему питания: $output" }
    return $match.Value
}

function Get-UsbValues([string]$Scheme) {
    $output = (& powercfg.exe /query $Scheme $UsbGroup $UsbSuspend 2>&1 | Out-String)
    if ($LASTEXITCODE -ne 0) { throw "powercfg /query завершился с ошибкой: $output" }
    $hex = [regex]::Matches($output, '0x[0-9a-fA-F]{8}')
    if ($hex.Count -lt 2) { throw "Не удалось прочитать значения USB selective suspend.`n$output" }
    return @{
        AC = [Convert]::ToInt32($hex[$hex.Count - 2].Value.Substring(2), 16)
        DC = [Convert]::ToInt32($hex[$hex.Count - 1].Value.Substring(2), 16)
    }
}

function Set-UsbValues([string]$Scheme, [int]$AC, [int]$DC) {
    foreach ($pair in @(@('ac', $AC), @('dc', $DC))) {
        $out = & powercfg.exe @("/set$($pair[0])valueindex", $Scheme, $UsbGroup, $UsbSuspend, [string]$pair[1]) 2>&1
        if ($LASTEXITCODE -ne 0) { throw "Ошибка powercfg: $($out -join ' ')" }
    }
    $out = & powercfg.exe /setactive $Scheme 2>&1
    if ($LASTEXITCODE -ne 0) { throw "Не удалось активировать схему: $($out -join ' ')" }
}

# ---------------------------------------------------------------- HID & dongle controller (C#)
$HidSenderSource = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

public class HidEndpointInfo
{
    public string DevicePath;
    public ushort UsagePage;
    public ushort Usage;
    public short InputReportByteLength;
    public short OutputReportByteLength;
    public short FeatureReportByteLength;
    public bool IsVendorPage { get { return UsagePage >= 0xFF00; } }
}

public static class HidRawSender
{
    const uint GENERIC_READ = 0x80000000;
    const uint GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 0x00000001;
    const uint FILE_SHARE_WRITE = 0x00000002;
    const uint OPEN_EXISTING = 3;
    const int DIGCF_PRESENT = 0x00000002;
    const int DIGCF_DEVICEINTERFACE = 0x00000010;
    static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

    static readonly Guid GUID_DEVINTERFACE_HID = new Guid("4D1E55B2-F16F-11CF-88CB-001111000030");

    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVICE_INTERFACE_DATA
    {
        public int cbSize;
        public Guid InterfaceClassGuid;
        public int Flags;
        public IntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct SP_DEVICE_INTERFACE_DETAIL_DATA
    {
        public int cbSize;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 512)]
        public string DevicePath;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public short InputReportByteLength;
        public short OutputReportByteLength;
        public short FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public short[] Reserved;
        public short NumberLinkCollectionNodes;
        public short NumberInputButtonCaps;
        public short NumberInputValueCaps;
        public short NumberInputDataIndices;
        public short NumberOutputButtonCaps;
        public short NumberOutputValueCaps;
        public short NumberOutputDataIndices;
        public short NumberFeatureButtonCaps;
        public short NumberFeatureValueCaps;
        public short NumberFeatureDataIndices;
    }

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_SetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetFeature(IntPtr hidDeviceObject, byte[] reportBuffer, int reportBufferLength);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_GetPreparsedData(IntPtr hidDeviceObject, out IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

    [DllImport("hid.dll", SetLastError = true)]
    static extern int HidP_GetCaps(IntPtr preparsedData, out HIDP_CAPS capabilities);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, int memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, ref SP_DEVICE_INTERFACE_DETAIL_DATA deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool WriteFile(IntPtr hFile, byte[] lpBuffer, uint nNumberOfBytesToWrite, out uint lpNumberOfBytesWritten, IntPtr lpOverlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool CloseHandle(IntPtr hObject);

    static IntPtr OpenHidHandle(string path, bool readWrite)
    {
        uint access = readWrite ? (GENERIC_READ | GENERIC_WRITE) : 0;
        IntPtr h = CreateFile(path, access, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h == INVALID_HANDLE_VALUE && readWrite)
        {
            h = CreateFile(path, GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        }
        return h;
    }

    public static List<HidEndpointInfo> DescribeEndpoints(string vidHex, string pidHex)
    {
        List<HidEndpointInfo> result = new List<HidEndpointInfo>();
        Guid hidGuid = GUID_DEVINTERFACE_HID;
        IntPtr info = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
        if (info == INVALID_HANDLE_VALUE) return result;

        try
        {
            string pattern = "vid_" + Regex.Escape(vidHex.ToLowerInvariant()) + "&pid_" + Regex.Escape(pidHex.ToLowerInvariant());
            int index = 0;
            while (true)
            {
                SP_DEVICE_INTERFACE_DATA iface = new SP_DEVICE_INTERFACE_DATA();
                iface.cbSize = Marshal.SizeOf(typeof(SP_DEVICE_INTERFACE_DATA));
                if (!SetupDiEnumDeviceInterfaces(info, IntPtr.Zero, ref hidGuid, index, ref iface)) break;
                index++;

                SP_DEVICE_INTERFACE_DETAIL_DATA detail = new SP_DEVICE_INTERFACE_DETAIL_DATA();
                detail.cbSize = (IntPtr.Size == 8) ? 8 : (4 + Marshal.SystemDefaultCharSize);
                int req = 0;
                if (SetupDiGetDeviceInterfaceDetail(info, ref iface, ref detail, Marshal.SizeOf(detail), out req, IntPtr.Zero) || !string.IsNullOrEmpty(detail.DevicePath))
                {
                    if (!string.IsNullOrEmpty(detail.DevicePath) && Regex.IsMatch(detail.DevicePath.ToLowerInvariant(), pattern))
                    {
                        HidEndpointInfo ep = new HidEndpointInfo();
                        ep.DevicePath = detail.DevicePath;
                        IntPtr h = OpenHidHandle(detail.DevicePath, false);
                        if (h != INVALID_HANDLE_VALUE)
                        {
                            try
                            {
                                IntPtr ppd;
                                if (HidD_GetPreparsedData(h, out ppd))
                                {
                                    try
                                    {
                                        HIDP_CAPS caps;
                                        if (HidP_GetCaps(ppd, out caps) == 0x00110000)
                                        {
                                            ep.UsagePage = caps.UsagePage;
                                            ep.Usage = caps.Usage;
                                            ep.InputReportByteLength = caps.InputReportByteLength;
                                            ep.OutputReportByteLength = caps.OutputReportByteLength;
                                            ep.FeatureReportByteLength = caps.FeatureReportByteLength;
                                        }
                                    }
                                    finally { HidD_FreePreparsedData(ppd); }
                                }
                            }
                            finally { CloseHandle(h); }
                        }
                        result.Add(ep);
                    }
                }
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(info);
        }
        return result;
    }

    public static byte[] TryReadFeature(string devicePath, byte reportId, int length)
    {
        if (length <= 0) return null;
        IntPtr h = OpenHidHandle(devicePath, true);
        if (h == INVALID_HANDLE_VALUE) return null;
        try
        {
            byte[] buf = new byte[length];
            buf[0] = reportId;
            if (HidD_GetFeature(h, buf, buf.Length)) return buf;
            return null;
        }
        finally { CloseHandle(h); }
    }

    public static int SendReport(string vidHex, string pidHex, byte[] payload, string reportMode, bool vendorPageOnly)
    {
        if (payload == null || payload.Length == 0) throw new ArgumentException("Пустой буфер репорта.");
        List<HidEndpointInfo> endpoints = DescribeEndpoints(vidHex, pidHex);
        if (endpoints.Count == 0) throw new InvalidOperationException("Не найдены HID-интерфейсы для VID_" + vidHex + "&PID_" + pidHex);

        List<HidEndpointInfo> targets = new List<HidEndpointInfo>();
        if (vendorPageOnly)
        {
            foreach (HidEndpointInfo ep in endpoints)
            {
                if (ep.IsVendorPage) targets.Add(ep);
            }
        }
        if (targets.Count == 0) targets = endpoints;

        string modeNorm = (reportMode ?? "auto").ToLowerInvariant();
        int sentCount = 0;
        List<string> errors = new List<string>();

        foreach (HidEndpointInfo ep in targets)
        {
            bool canFeature = ep.FeatureReportByteLength > 0 && (modeNorm == "feature" || modeNorm == "auto");
            bool canOutput  = ep.OutputReportByteLength > 0  && (modeNorm == "output"  || modeNorm == "auto");

            if (!canFeature && !canOutput)
            {
                errors.Add("UsagePage=0x" + ep.UsagePage.ToString("X4") + " не поддерживает ни Feature, ни Output репорты (FeatureLen=0, OutLen=0)");
                continue;
            }

            IntPtr h = OpenHidHandle(ep.DevicePath, true);
            if (h == INVALID_HANDLE_VALUE)
            {
                errors.Add("CreateFile failed on UsagePage=0x" + ep.UsagePage.ToString("X4") + " (err=" + Marshal.GetLastWin32Error() + ")");
                continue;
            }

            try
            {
                bool sentOnThis = false;

                if (canFeature)
                {
                    int targetLen = Math.Max(payload.Length, (int)ep.FeatureReportByteLength);
                    byte[] copy = new byte[targetLen];
                    Array.Copy(payload, copy, payload.Length);
                    if (HidD_SetFeature(h, copy, copy.Length))
                    {
                        sentCount++;
                        sentOnThis = true;
                    }
                    else
                    {
                        errors.Add("SetFeature failed on UsagePage=0x" + ep.UsagePage.ToString("X4") + " len=" + targetLen + " (err=" + Marshal.GetLastWin32Error() + ")");
                    }
                }

                if (!sentOnThis && canOutput)
                {
                    int targetLen = Math.Max(payload.Length, (int)ep.OutputReportByteLength);
                    byte[] copy = new byte[targetLen];
                    Array.Copy(payload, copy, payload.Length);
                    uint written = 0;
                    if (WriteFile(h, copy, (uint)copy.Length, out written, IntPtr.Zero) && written > 0)
                    {
                        sentCount++;
                        sentOnThis = true;
                    }
                    else
                    {
                        errors.Add("WriteFile(Output) failed on UsagePage=0x" + ep.UsagePage.ToString("X4") + " len=" + targetLen + " (err=" + Marshal.GetLastWin32Error() + ")");
                    }
                }
            }
            finally
            {
                CloseHandle(h);
            }
        }

        if (sentCount == 0)
        {
            throw new InvalidOperationException(errors.Count > 0 ? string.Join("; ", errors.ToArray()) : "Не удалось отправить HID-репорт ни в один интерфейс.");
        }
        return sentCount;
    }
}
'@

function Initialize-HidSender {
    if (-not ('HidRawSender' -as [type])) {
        Add-Type -TypeDefinition $HidSenderSource
    }
}

function Convert-HexToBytes([string]$HexStr) {
    $clean = ($HexStr -replace '[^0-9A-Fa-f]', '')
    if ([string]::IsNullOrWhiteSpace($clean) -or ($clean.Length % 2 -ne 0)) {
        throw "Некорректная hex-строка репорта: '$HexStr'"
    }
    $bytes = New-Object byte[] ($clean.Length / 2)
    for ($i = 0; $i -lt $bytes.Length; $i++) {
        $bytes[$i] = [Convert]::ToByte($clean.Substring($i * 2, 2), 16)
    }
    return ,$bytes
}

function Invoke-HidReportTweak($Tw, [int]$HzVal) {
    $devVid = ([string]$Tw.Vid).Trim() -replace '^0x', ''
    $devPid = ([string]$Tw.Pid).Trim() -replace '^0x', ''
    if ([string]::IsNullOrWhiteSpace($devVid) -or [string]::IsNullOrWhiteSpace($devPid)) {
        throw "Для '$($Tw.Name)' не указаны Vid и Pid (запустите пункт 1 — Автопоиск мыши)."
    }
    if ($null -eq $Tw.ReportsByHz) {
        throw "Для '$($Tw.Name)' не задан блок ReportsByHz."
    }
    $hzKey = [string]$HzVal
    $hexVal = $null
    if ($Tw.ReportsByHz.PSObject.Properties.Match($hzKey).Count -gt 0) {
        $hexVal = [string]$Tw.ReportsByHz.$hzKey
    }
    if ([string]::IsNullOrWhiteSpace($hexVal)) {
        throw "Для частоты ${HzVal} Гц не задан hex-репорт в ReportsByHz (настройте через пункт 2 -> Контроллер донгла)."
    }
    $repMode = if ([string]::IsNullOrWhiteSpace([string]$Tw.ReportType)) { 'auto' } else { [string]$Tw.ReportType }
    $vendorOnly = if ($null -ne $Tw.VendorPageOnly) { [bool]$Tw.VendorPageOnly } else { $true }
    $bytes = Convert-HexToBytes $hexVal
    Initialize-HidSender
    $count = [HidRawSender]::SendReport($devVid, $devPid, $bytes, $repMode, $vendorOnly)
    Write-Log ("hid-report sent: {0} -> {1}Hz via VID_{2}&PID_{3} (mode={4}, vendorOnly={5}, interfaces={6})" -f $Tw.Name, $HzVal, $devVid.ToUpper(), $devPid.ToUpper(), $repMode, $vendorOnly, $count)
}

function Invoke-VendorCommand([string]$Cmd) {
    if ([string]::IsNullOrWhiteSpace($Cmd)) { throw 'ApplyCommand пуст: впишите команду или выберите Method: "hid-report".' }
    Write-Log "run: $Cmd"
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = Join-Path $env:SystemRoot 'System32\cmd.exe'
    $psi.Arguments = '/d /s /c "' + $Cmd + '"'
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $so = $p.StandardOutput.ReadToEndAsync()
    $se = $p.StandardError.ReadToEndAsync()
    if (-not $p.WaitForExit(60000)) {
        try { $p.Kill() } catch { }
        throw 'Команда не завершилась за 60 секунд и была остановлена.'
    }
    $p.WaitForExit()
    Write-Log ("exit=$($p.ExitCode) out=" + $so.Result.Trim() + ' err=' + $se.Result.Trim())
    if ($p.ExitCode -ne 0) { throw "Команда вернула код $($p.ExitCode): $($se.Result.Trim()) $($so.Result.Trim())" }
}

# ---------------------------------------------------------------- tweaks
function Get-Baseline($Tw, $State) {
    if ($State.Good.ContainsKey($Tw.Name)) { return [int]$State.Good[$Tw.Name] }
    if ($Tw.DefaultHz) { return [int]$Tw.DefaultHz }
    return 1000
}

function New-Snapshot($Tw, $State) {
    if ($Tw.Type -eq 'usb-suspend') {
        $scheme = Get-Scheme
        $v = Get-UsbValues $scheme
        return @{ Tweak = $Tw.Name; Type = 'usb-suspend'; Scheme = $scheme; AC = $v.AC; DC = $v.DC; Started = (Get-Date).ToString('o') }
    }
    return @{
        Tweak        = $Tw.Name
        Type         = 'polling'
        Method       = [string]$Tw.Method
        Hz           = (Get-Baseline $Tw $State)
        ApplyCommand = [string]$Tw.ApplyCommand
        Started      = (Get-Date).ToString('o')
    }
}

function Restore-Snapshot($Snap) {
    if ($Snap.Type -eq 'usb-suspend') {
        Set-UsbValues ([string]$Snap.Scheme) ([int]$Snap.AC) ([int]$Snap.DC)
    }
    elseif ($Snap.Method -eq 'hid-report') {
        $prof = Get-Prof
        $tw = @($prof.Tweaks | Where-Object { $_.Name -eq $Snap.Tweak } | Select-Object -First 1)
        if ($tw.Count -eq 0) { throw "Твик '$($Snap.Tweak)' не найден в профиле для отката." }
        Invoke-HidReportTweak $tw[0] ([int]$Snap.Hz)
    }
    else {
        Invoke-VendorCommand (([string]$Snap.ApplyCommand).Replace('{HZ}', [string][int]$Snap.Hz))
    }
}

function Apply-Tweak($Tw, [int]$HzVal) {
    if ($Tw.Type -eq 'usb-suspend') {
        $scheme = Get-Scheme
        Set-UsbValues $scheme 0 0
    }
    elseif ($Tw.Method -eq 'hid-report') {
        Invoke-HidReportTweak $Tw $HzVal
    }
    else {
        Invoke-VendorCommand (([string]$Tw.ApplyCommand).Replace('{HZ}', [string]$HzVal))
    }
}

function Apply-StoredGoodTweaks([string]$Reason) {
    $prof = Get-Prof
    $state = Get-State
    foreach ($tw in @($prof.Tweaks | Where-Object { $_.Enabled })) {
        if ($tw.Type -eq 'polling' -and $state.Good.ContainsKey($tw.Name)) {
            $hzVal = [int]$state.Good[$tw.Name]
            try {
                Apply-Tweak $tw $hzVal
                Write-Log "$Reason : применено '$($tw.Name)' = $hzVal Гц"
            }
            catch {
                Write-Log "$Reason : не удалось применить '$($tw.Name)' = $hzVal Гц : $($_.Exception.Message)"
            }
        }
    }
}

# ---------------------------------------------------------------- input probe (C#)
$ProbeSource = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows.Forms;

public class DetectedDeviceInfo
{
    public string Vid;
    public string Pid;
    public string DisplayName;
    public int Events;
    public double EffectiveHz;
    public double WindowPeakHz;
}

public class ProbeResult
{
    public int MouseEvents;
    public int KeyEvents;
    public double PeakHz;
    public double WindowPeakHz;
    public string Decision;
    public List<string> Devices = new List<string>();
    public List<DetectedDeviceInfo> DetectedList = new List<DetectedDeviceInfo>();
}

public static class InputProbe
{
    [StructLayout(LayoutKind.Sequential)]
    struct RAWINPUTDEVICE { public ushort UsagePage; public ushort Usage; public uint Flags; public IntPtr Target; }

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
    [DllImport("user32.dll")]
    static extern uint GetRawInputData(IntPtr hRawInput, uint command, byte[] data, ref uint size, uint headerSize);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetRawInputDeviceInfo(IntPtr device, uint command, StringBuilder data, ref uint size);
    [DllImport("user32.dll")]
    static extern uint MsgWaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint ms, uint mask);
    [DllImport("user32.dll")]
    static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")]
    static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")]
    static extern IntPtr DispatchMessage(ref MSG msg);

    class DevStat
    {
        public Queue<long> Window = new Queue<long>();
        public List<double> InstantSamples = new List<double>();
        public long LastTick = -1;
        public int Events;
        public double WindowPeak;
        public double InstantPeak;
        public string Name;
        public string Vid = "";
        public string Pid = "";
    }

    sealed class ProbeWindow : NativeWindow
    {
        public Dictionary<long, DevStat> Devs = new Dictionary<long, DevStat>();
        public int KeyEvents;
        public int MouseEvents;
        public string Decision;
        public bool Decide;
        public bool RequireMouse;
        public int MinMouse;
        public Stopwatch Clock = Stopwatch.StartNew();

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x00FF) { HandleInput(m.LParam); }
            base.WndProc(ref m);
        }

        void FillDeviceInfo(IntPtr dev, DevStat st)
        {
            try
            {
                StringBuilder sb = new StringBuilder(512);
                uint size = 512;
                uint r = GetRawInputDeviceInfo(dev, 0x20000007, sb, ref size);
                if (r == 0 || r == uint.MaxValue) { st.Name = "?"; return; }
                Match mt = Regex.Match(sb.ToString(), "VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})");
                if (mt.Success)
                {
                    st.Vid = mt.Groups[1].Value.ToUpper();
                    st.Pid = mt.Groups[2].Value.ToUpper();
                    st.Name = "VID_" + st.Vid + " PID_" + st.Pid;
                }
                else
                {
                    st.Name = "HID";
                }
            }
            catch { st.Name = "?"; }
        }

        void HandleInput(IntPtr lp)
        {
            byte[] buf = new byte[256];
            uint size = (uint)buf.Length;
            uint hdr = (uint)(8 + 2 * IntPtr.Size);
            uint r = GetRawInputData(lp, 0x10000003, buf, ref size, hdr);
            if (r == 0 || r == uint.MaxValue) return;
            uint type = BitConverter.ToUInt32(buf, 0);
            long dev = (IntPtr.Size == 8) ? BitConverter.ToInt64(buf, 8) : (long)BitConverter.ToInt32(buf, 8);
            int h = (int)hdr;

            if (type == 0)
            {
                ushort flags = BitConverter.ToUInt16(buf, h);
                int x = BitConverter.ToInt32(buf, h + 12);
                int y = BitConverter.ToInt32(buf, h + 16);
                if (x == 0 && y == 0 && (flags & 1) == 0) return;
                MouseEvents++;
                DevStat st;
                if (!Devs.TryGetValue(dev, out st))
                {
                    st = new DevStat();
                    FillDeviceInfo(new IntPtr(dev), st);
                    Devs[dev] = st;
                }
                st.Events++;
                long now = Clock.ElapsedTicks;

                if (st.LastTick > 0)
                {
                    double dtSec = (double)(now - st.LastTick) / Stopwatch.Frequency;
                    if (dtSec >= 0.00010 && dtSec <= 0.05)
                    {
                        double instHz = 1.0 / dtSec;
                        st.InstantSamples.Add(instHz);
                        if (instHz > st.InstantPeak) st.InstantPeak = instHz;
                    }
                }
                st.LastTick = now;

                st.Window.Enqueue(now);
                while (st.Window.Count > 0 && st.Window.Peek() < now - Stopwatch.Frequency) st.Window.Dequeue();
                if (st.Window.Count > st.WindowPeak) st.WindowPeak = st.Window.Count;
            }
            else if (type == 1)
            {
                ushort kflags = BitConverter.ToUInt16(buf, h + 2);
                ushort vkey = BitConverter.ToUInt16(buf, h + 6);
                if ((kflags & 1) != 0) return;
                KeyEvents++;
                if (Decide && Decision == null)
                {
                    bool allowed = !RequireMouse || MouseEvents >= MinMouse;
                    if (vkey == 0x59 && allowed) Decision = "keep";
                    else if (vkey == 0x4E) Decision = "revert";
                }
            }
        }
    }

    static double Percentile95(List<double> samples)
    {
        if (samples == null || samples.Count == 0) return 0;
        List<double> copy = new List<double>(samples);
        copy.Sort();
        int idx = (int)Math.Floor((copy.Count - 1) * 0.95);
        return copy[idx];
    }

    static double EffectivePeak(DevStat st)
    {
        double p95 = Percentile95(st.InstantSamples);
        return Math.Max(p95, st.WindowPeak);
    }

    static double CurrentInstantPeak(ProbeWindow w)
    {
        double p = 0;
        foreach (KeyValuePair<long, DevStat> kv in w.Devs)
        {
            double val = EffectivePeak(kv.Value);
            if (val > p) p = val;
        }
        return p;
    }

    static double CurrentWindowPeak(ProbeWindow w)
    {
        double p = 0;
        foreach (KeyValuePair<long, DevStat> kv in w.Devs)
        {
            if (kv.Value.WindowPeak > p) p = kv.Value.WindowPeak;
        }
        return p;
    }

    public static ProbeResult Run(int timeoutSec, bool decide, bool requireMouse, int minMouse)
    {
        ProbeWindow w = new ProbeWindow();
        w.Decide = decide;
        w.RequireMouse = requireMouse;
        w.MinMouse = minMouse;
        CreateParams cp = new CreateParams();
        cp.Parent = new IntPtr(-3);
        w.CreateHandle(cp);

        try
        {
            RAWINPUTDEVICE[] rid = new RAWINPUTDEVICE[2];
            rid[0].UsagePage = 1; rid[0].Usage = 2; rid[0].Flags = 0x100; rid[0].Target = w.Handle;
            rid[1].UsagePage = 1; rid[1].Usage = 6; rid[1].Flags = 0x100; rid[1].Target = w.Handle;
            if (!RegisterRawInputDevices(rid, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE))))
                throw new InvalidOperationException("RegisterRawInputDevices failed: " + Marshal.GetLastWin32Error());

            Stopwatch total = Stopwatch.StartNew();
            long lastPrint = -1000;
            while (total.ElapsedMilliseconds < timeoutSec * 1000L && w.Decision == null)
            {
                MsgWaitForMultipleObjects(0, null, false, 100, 0x04FF);
                MSG msg;
                while (PeekMessage(out msg, IntPtr.Zero, 0, 0, 1))
                {
                    TranslateMessage(ref msg);
                    DispatchMessage(ref msg);
                }
                if (total.ElapsedMilliseconds - lastPrint >= 400)
                {
                    lastPrint = total.ElapsedMilliseconds;
                    long left = timeoutSec - total.ElapsedMilliseconds / 1000;
                    string line = "Осталось " + left + "с | мышь: " + w.MouseEvents + " соб., частота ~" + ((int)Math.Round(CurrentInstantPeak(w))) + " Гц (за 1с: " + ((int)CurrentWindowPeak(w)) + " Гц) | клавиш: " + w.KeyEvents;
                    if (decide) line += " | Y=оставить N=откат";
                    Console.Write("\r" + line.PadRight(Math.Max(line.Length, 112)));
                }
            }
            Console.WriteLine();
        }
        finally
        {
            RAWINPUTDEVICE[] off = new RAWINPUTDEVICE[2];
            off[0].UsagePage = 1; off[0].Usage = 2; off[0].Flags = 0x1; off[0].Target = IntPtr.Zero;
            off[1].UsagePage = 1; off[1].Usage = 6; off[1].Flags = 0x1; off[1].Target = IntPtr.Zero;
            RegisterRawInputDevices(off, 2, (uint)Marshal.SizeOf(typeof(RAWINPUTDEVICE)));
            w.DestroyHandle();
        }

        ProbeResult res = new ProbeResult();
        res.MouseEvents = w.MouseEvents;
        res.KeyEvents = w.KeyEvents;
        res.PeakHz = CurrentInstantPeak(w);
        res.WindowPeakHz = CurrentWindowPeak(w);
        res.Decision = w.Decision != null ? w.Decision : (decide ? "timeout" : "done");
        foreach (KeyValuePair<long, DevStat> kv in w.Devs)
        {
            double eff = EffectivePeak(kv.Value);
            int instHz = (int)Math.Round(eff);
            int rawMax = (int)Math.Round(kv.Value.InstantPeak);
            int winMax = (int)kv.Value.WindowPeak;
            res.Devices.Add(kv.Value.Name + ": ~" + instHz + " Гц (пик дельты " + rawMax + " Гц, за 1с: " + winMax + " Гц, " + kv.Value.Events + " событий)");

            DetectedDeviceInfo dInfo = new DetectedDeviceInfo();
            dInfo.Vid = kv.Value.Vid;
            dInfo.Pid = kv.Value.Pid;
            dInfo.DisplayName = kv.Value.Name;
            dInfo.Events = kv.Value.Events;
            dInfo.EffectiveHz = eff;
            dInfo.WindowPeakHz = kv.Value.WindowPeak;
            res.DetectedList.Add(dInfo);
        }
        return res;
    }
}
'@

function Initialize-Probe {
    if (-not ('InputProbe' -as [type])) {
        Add-Type -TypeDefinition $ProbeSource -ReferencedAssemblies System.Windows.Forms, System.Drawing
    }
}

function Clear-KeyBuffer {
    try { while ([Console]::KeyAvailable) { $null = [Console]::ReadKey($true) } } catch { }
}

# ---------------------------------------------------------------- auto-detect & built-in dongle tool
function Invoke-AutoDetectMouse {
    Initialize-Root
    $st = Get-State
    if ($st.Pending) {
        $st.Pending = $null
        Save-State $st
        Write-Log 'autodetect: cleared stale Pending state'
    }

    Initialize-Probe
    Write-Host "`n=== Автопоиск мыши и настройка контроллера донгла ===" -ForegroundColor Cyan
    Write-Host 'Двигайте ТОЛЬКО ту мышь, которую хотите настроить, в течение 5 секунд...' -ForegroundColor Yellow
    $res = [InputProbe]::Run(5, $false, $false, 0)
    Clear-KeyBuffer

    $candidates = @($res.DetectedList | Where-Object { -not [string]::IsNullOrWhiteSpace($_.Vid) -and $_.Events -gt 0 } | Sort-Object -Property Events -Descending)
    if ($candidates.Count -eq 0) {
        Write-Host 'Не удалось поймать ни одной активной мыши с VID/PID. Попробуйте подвигать мышью активнее.' -ForegroundColor Red
        return
    }

    $best = $candidates[0]
    $friendly = ''
    if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
        $pnp = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
            $_.InstanceId -match ("VID_{0}&PID_{1}" -f $best.Vid, $best.Pid) -and $_.FriendlyName
        } | Select-Object -First 1
        if ($pnp) { $friendly = $pnp.FriendlyName }
    }

    Write-Host ''
    Write-Host ("Обнаружена активная мышь/донгл: VID_{0} PID_{1} {2}" -f $best.Vid, $best.Pid, $(if ($friendly) {"($friendly)"} else {''})) -ForegroundColor Green
    Write-Host ("Текущая замеренная частота: ~{0} Гц ({1} событий за 5с)" -f [int][Math]::Round($best.EffectiveHz), $best.Events)

    $preset = Get-KnownDonglePreset $best.Vid $best.Pid
    Write-Host ("Контроллер: {0}" -f $preset.Family) -ForegroundColor Cyan
    Write-Host ("Инфо: {0}" -f $preset.Notes)

    Initialize-HidSender
    $eps = [HidRawSender]::DescribeEndpoints($best.Vid, $best.Pid)
    Write-Host "`nHID-интерфейсы устройства:" -ForegroundColor Cyan
    foreach ($ep in $eps) {
        $tag = if ($ep.IsVendorPage) { '[VENDOR]' } else { '[STD]' }
        Write-Host ("  UsagePage=0x{0:X4} Usage=0x{1:X4} | FeatureLen={2} OutLen={3} {4}" -f $ep.UsagePage, $ep.Usage, $ep.FeatureReportByteLength, $ep.OutputReportByteLength, $tag)
    }

    $prof = Get-Prof
    $mouseTw = @($prof.Tweaks | Where-Object { $_.Target -eq 'mouse' } | Select-Object -First 1)
    if ($mouseTw.Count -eq 0) {
        throw 'В profile.json не найден блок твика с Target = "mouse".'
    }
    $tw = $mouseTw[0]
    $tw.Method = 'hid-report'
    $tw.Vid = $best.Vid
    $tw.Pid = $best.Pid
    $tw.ReportType = $preset.ReportType
    $tw.Levels = $preset.Levels
    $tw.Enabled = [bool]$preset.Supported

    if ($tw.PSObject.Properties.Match('ControllerFamily').Count -gt 0) { $tw.ControllerFamily = $preset.Family }
    else { $tw | Add-Member -NotePropertyName 'ControllerFamily' -NotePropertyValue $preset.Family }

    if ($tw.PSObject.Properties.Match('VendorPageOnly').Count -gt 0) { $tw.VendorPageOnly = [bool]$preset.VendorPageOnly }
    else { $tw | Add-Member -NotePropertyName 'VendorPageOnly' -NotePropertyValue ([bool]$preset.VendorPageOnly) }

    $newReports = New-Object PSObject
    foreach ($k in $preset.ReportsByHz.Keys) {
        $newReports | Add-Member -NotePropertyName $k -NotePropertyValue $preset.ReportsByHz[$k]
    }
    $tw.ReportsByHz = $newReports
    Save-Prof $prof

    Write-Host ''
    Write-Host ("VID_{0} и PID_{1} сохранены в profile.json." -f $best.Vid, $best.Pid) -ForegroundColor Green
    if (-not $preset.Supported) {
        Write-Host 'Для записи команды переключения частоты откройте пункт 2 (Контроллер донгла) -> Действие 2 (Считать Feature-репорты).' -ForegroundColor Yellow
    }
}

function Invoke-DongleTool {
    Initialize-Root
    Initialize-HidSender
    $prof = Get-Prof
    $mouseTw = @($prof.Tweaks | Where-Object { $_.Target -eq 'mouse' } | Select-Object -First 1)[0]

    $devVid = [string]$mouseTw.Vid
    $devPid = [string]$mouseTw.Pid
    if ([string]::IsNullOrWhiteSpace($devVid) -or [string]::IsNullOrWhiteSpace($devPid)) {
        Write-Host 'VID/PID мыши ещё не заданы. Запускаю автопоиск...' -ForegroundColor Yellow
        Invoke-AutoDetectMouse
        $prof = Get-Prof
        $mouseTw = @($prof.Tweaks | Where-Object { $_.Target -eq 'mouse' } | Select-Object -First 1)[0]
        $devVid = [string]$mouseTw.Vid
        $devPid = [string]$mouseTw.Pid
        if ([string]::IsNullOrWhiteSpace($devVid)) { return }
    }

    Write-Host "`n=== Встроенный контроллер донгла (VID_$devVid PID_$devPid) ===" -ForegroundColor Cyan
    $eps = [HidRawSender]::DescribeEndpoints($devVid, $devPid)
    foreach ($ep in $eps) {
        $tag = if ($ep.IsVendorPage) { '[VENDOR - управляющий]' } else { '[STANDARD]' }
        Write-Host ("  UsagePage=0x{0:X4} Usage=0x{1:X4} | FeatureLen={2} OutLen={3} {4}" -f $ep.UsagePage, $ep.Usage, $ep.FeatureReportByteLength, $ep.OutputReportByteLength, $tag)
    }

    Write-Host "`nДействия:"
    Write-Host "  1. Установить частоту опроса донгла (125 / 250 / 500 / 1000 Гц)"
    Write-Host "  2. Считать активные Feature-репорты с управляющих интерфейсов (узнать Report ID и формат)"
    Write-Host "  3. Записать свой hex-репорт для частоты (например, 1000 Гц) в profile.json"
    $choice = Read-Host 'Выбор (пусто = выход)'
    if ([string]::IsNullOrWhiteSpace($choice)) { return }

    if ($choice -eq '1') {
        $targetHz = if ($Hz -gt 0) { $Hz } else { [int](Read-Host 'Введите частоту (125, 250, 500, 1000)') }
        Apply-Tweak $mouseTw $targetHz
        $state = Get-State
        $state.Good[$mouseTw.Name] = $targetHz
        Save-State $state
        Write-Host "Команда ${targetHz} Гц отправлена напрямую в донгл и сохранена для автоприменения службой." -ForegroundColor Green
    }
    elseif ($choice -eq '2') {
        $vendorEps = @($eps | Where-Object { $_.IsVendorPage -and $_.FeatureReportByteLength -gt 0 })
        if ($vendorEps.Count -eq 0) {
            Write-Host 'Интерфейсов с FeatureReportByteLength > 0 на UsagePage >= 0xFF00 не найдено. Проверяю все интерфейсы с FeatureLen > 0...' -ForegroundColor Yellow
            $vendorEps = @($eps | Where-Object { $_.FeatureReportByteLength -gt 0 })
        }
        if ($vendorEps.Count -eq 0) {
            Write-Host 'У этого устройства нет ни одного интерфейса с поддержкой Feature-репортов (FeatureLen=0). Управление идёт через Output-репорт (WriteFile).' -ForegroundColor Yellow
            return
        }
        $foundAny = $false
        foreach ($ep in $vendorEps) {
            for ($rid = 0; $rid -le 32; $rid++) {
                $data = [HidRawSender]::TryReadFeature($ep.DevicePath, [byte]$rid, [int]$ep.FeatureReportByteLength)
                if ($null -ne $data) {
                    $foundAny = $true
                    $hex = ($data | ForEach-Object { $_.ToString('X2') }) -join ' '
                    Write-Host ("  UsagePage=0x{0:X4} ReportID=0x{1:X2} ({2}B): {3}" -f $ep.UsagePage, $rid, $data.Length, $hex) -ForegroundColor Green
                }
            }
        }
        if (-not $foundAny) {
            Write-Host 'Устройство не отдало ни одного Feature-репорта на ReportID 0x00..0x20.' -ForegroundColor Yellow
        }
    }
    elseif ($choice -eq '3') {
        $hzKey = Read-Host 'Для какой частоты задать репорт? (например, 1000)'
        $hexIn = Read-Host 'Введите hex-байты репорта (первый байт = Report ID)'
        $null = Convert-HexToBytes $hexIn
        if ($mouseTw.ReportsByHz.PSObject.Properties.Match($hzKey).Count -gt 0) {
            $mouseTw.ReportsByHz.$hzKey = $hexIn.Trim()
        }
        else {
            $mouseTw.ReportsByHz | Add-Member -NotePropertyName $hzKey -NotePropertyValue $hexIn.Trim()
        }
        $mouseTw.Enabled = $true
        Save-Prof $prof
        Write-Host "Репорт для ${hzKey} Гц сохранён в profile.json (Enabled: true)." -ForegroundColor Green
    }
}

# ---------------------------------------------------------------- safe trial
function Invoke-Trial($Tw, [int]$HzVal, $State, $prof) {
    $timeout = 25; if ($prof.ConfirmTimeoutSec) { $timeout = [int]$prof.ConfirmTimeoutSec }
    $settle  = 2;  if ($null -ne $prof.SettleSec) { $settle = [int]$prof.SettleSec }

    $snap = New-Snapshot $Tw $State
    $State.Pending = $snap
    Save-State $State
    Write-Log "trial start: $($Tw.Name) -> $HzVal (snapshot saved)"

    $keep = $false
    $applyFailed = $false
    try {
        Apply-Tweak $Tw $HzVal
        if ($settle -gt 0) { Start-Sleep -Seconds $settle }

        Initialize-Probe
        Write-Host ''
        Write-Host "Проверка: $($Tw.Name) = $HzVal" -ForegroundColor Cyan
        Write-Host 'Двигайте мышь НЕПРЕРЫВНО кругами. Если всё работает — нажмите Y, иначе N.' -ForegroundColor Cyan
        Write-Host "Нет нажатия за $timeout с (или устройство не отвечает) = автоматический откат." -ForegroundColor Yellow
        $needMouse = ($Tw.Target -eq 'mouse' -or $Tw.Target -eq 'both')
        $res = [InputProbe]::Run($timeout, $true, $needMouse, 30)
        Clear-KeyBuffer
        foreach ($d in $res.Devices) { Write-Host "  $d" }
        if ($res.Decision -eq 'keep') {
            $keep = $true
        }
        elseif ($res.Decision -eq 'revert') { Write-Host 'Выбран откат.' -ForegroundColor Yellow }
        else { Write-Host 'Подтверждения нет - откат.' -ForegroundColor Yellow }
    }
    catch {
        Write-Host "Ошибка во время применения: $($_.Exception.Message)" -ForegroundColor Red
        Write-Log "trial error: $($_.Exception.Message)"
        $applyFailed = $true
        $keep = $false
    }

    if ($keep) {
        $State.Prev[$Tw.Name] = $snap
        $State.Good[$Tw.Name] = $HzVal
        $State.Pending = $null
        Save-State $State
        Write-Log "trial kept: $($Tw.Name) = $HzVal"
        Write-Host "Принято: $($Tw.Name) = $HzVal" -ForegroundColor Green
        return $true
    }

    if ($applyFailed) {
        # Если само применение не прошло (ошибка HID-записи), устройство не менялось — чистим Pending без повторного вызова сбойной команды
        $State.Pending = $null
        Save-State $State
        Write-Log "trial aborted (apply failed, pending cleared): $($Tw.Name)"
        return $false
    }

    try {
        Restore-Snapshot $snap
        $State.Pending = $null
        Save-State $State
        Write-Log "trial reverted: $($Tw.Name)"
        Write-Host 'Предыдущее рабочее значение восстановлено.' -ForegroundColor Green
    }
    catch {
        Write-Host "НЕ УДАЛОСЬ откатить: $($_.Exception.Message)" -ForegroundColor Red
        $State.Pending = $null
        Save-State $State
        Write-Log "revert FAILED (pending cleared): $($_.Exception.Message)"
        throw
    }
    return $false
}

# ---------------------------------------------------------------- native Windows Service builder & manager
$WindowsServiceSource = @'
using System;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace WinOptimizerInputLatency
{
    public class LatencyWorkerService : ServiceBase
    {
        private Process _worker;
        private readonly string _root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"WinOptimizer\InputLatency");
        private readonly string _scriptPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"WinOptimizer\InputLatency\InputLatencyOptimizer.ps1");
        private readonly string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"WinOptimizer\InputLatency\log.txt");

        public LatencyWorkerService()
        {
            ServiceName = "WinOptimizerInputLatencySvc";
            CanStop = true;
            CanPauseAndContinue = false;
            AutoLog = true;
        }

        private void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(_root);
                File.AppendAllText(_logPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [WinSvc] " + msg + Environment.NewLine);
            }
            catch { }
        }

        protected override void OnStart(string[] args)
        {
            Log("Service starting...");
            StartWorker();
        }

        private void StartWorker()
        {
            try
            {
                if (!File.Exists(_scriptPath))
                {
                    Log("Script not found: " + _scriptPath);
                    return;
                }

                string psExe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\WindowsPowerShell\v1.0\powershell.exe");
                var psi = new ProcessStartInfo
                {
                    FileName = psExe,
                    Arguments = "-NoLogo -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"" + _scriptPath + "\" -Mode service",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = _root
                };

                _worker = Process.Start(psi);
                Log("Worker started, PID=" + (_worker != null ? _worker.Id.ToString() : "none"));
            }
            catch (Exception ex)
            {
                Log("StartWorker error: " + ex.Message);
            }
        }

        protected override void OnStop()
        {
            Log("Service stopping...");
            try
            {
                if (_worker != null && !_worker.HasExited)
                {
                    _worker.Kill();
                    _worker.WaitForExit(5000);
                }
            }
            catch (Exception ex)
            {
                Log("OnStop error: " + ex.Message);
            }
            Log("Service stopped.");
        }
    }

    static class Program
    {
        static void Main()
        {
            ServiceBase.Run(new ServiceBase[] { new LatencyWorkerService() });
        }
    }
}
'@

function Remove-LegacyScheduledTasks {
    foreach ($t in @($LegacyTaskName, $OldWatchdogTask)) {
        try {
            if (Get-ScheduledTask -TaskName $t -ErrorAction SilentlyContinue) {
                Unregister-ScheduledTask -TaskName $t -Confirm:$false
                Write-Log "Removed legacy scheduled task '$t'"
            }
        } catch { }
    }
}

function Build-WindowsServiceExe {
    Initialize-Root
    if ($PSCommandPath -and ($PSCommandPath -ne $Installed)) {
        Copy-Item -LiteralPath $PSCommandPath -Destination $Installed -Force
    }

    Add-Type -AssemblyName System.ServiceProcess

    $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    if (-not (Test-Path -LiteralPath $csc)) {
        $csc = Join-Path $env:SystemRoot 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
    }
    if (-not (Test-Path -LiteralPath $csc)) {
        throw "Не найден компилятор csc.exe для сборки службы Windows."
    }

    $csFile = Join-Path $Root 'WinOptimizerInputLatencySvc.cs'
    [IO.File]::WriteAllText($csFile, $WindowsServiceSource, [System.Text.Encoding]::UTF8)

    $cscArgs = @('/nologo', '/target:exe', "/out:`"$ServiceExe`"", '/r:System.ServiceProcess.dll', "`"$csFile`"")
    $out = & $csc @cscArgs 2>&1
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $ServiceExe)) {
        throw "Ошибка компиляции службы: $($out -join ' ')"
    }
    Write-Log "Compiled Windows service binary: $ServiceExe"
}

function Get-ServiceInstalled {
    return [bool](Get-Service -Name $ServiceName -ErrorAction SilentlyContinue)
}

function Install-ServiceMode {
    Remove-LegacyScheduledTasks
    Build-WindowsServiceExe

    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
        }
        & sc.exe delete $ServiceName | Out-Null
        Start-Sleep -Seconds 1
    }

    $binPath = "`"$ServiceExe`""
    $out = & sc.exe create $ServiceName binPath= $binPath start= auto DisplayName= "`"$ServiceDisplayName`"" 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "sc create завершился с ошибкой: $($out -join ' ')"
    }

    & sc.exe description $ServiceName "WinOptimizer input latency Windows service (USB watch, rollback and polling apply)." | Out-Null
    Start-Service -Name $ServiceName
    Write-Log "Windows service '$ServiceName' installed and started."
    Write-Host "Служба Windows '$ServiceName' собрана, установлена и запущена." -ForegroundColor Green
    Write-Host "Профиль: $Profile_"
}

function Uninstall-ServiceMode {
    Remove-LegacyScheduledTasks
    $existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($existing) {
        if ($existing.Status -ne 'Stopped') {
            Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
        }
        & sc.exe delete $ServiceName | Out-Null
        Write-Log "Windows service '$ServiceName' removed."
        Write-Host "Служба Windows '$ServiceName' остановлена и удалена. Данные и профиль сохранены." -ForegroundColor Green
    }
    else {
        Write-Host "Служба Windows '$ServiceName' не была установлена."
    }
}

# ---------------------------------------------------------------- modes
function Show-Scan {
    Write-Host "`nПодключённые устройства ввода (с VID/PID):" -ForegroundColor Cyan
    if (Get-Command Get-PnpDevice -ErrorAction SilentlyContinue) {
        $rows = Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object {
            $_.Class -in @('Mouse','Keyboard','HIDClass') -or
            $_.FriendlyName -match 'LAMZU|ARDOR|Mouse|Keyboard|Мыш|Клавиат'
        } | ForEach-Object {
            $devVid = ''; $devPid = ''
            if ($_.InstanceId -match 'VID_([0-9A-Fa-f]{4})&PID_([0-9A-Fa-f]{4})') {
                $devVid = $matches[1].ToUpper(); $devPid = $matches[2].ToUpper()
            }
            [PSCustomObject]@{
                Class        = $_.Class
                Status       = $_.Status
                VID          = $devVid
                PID          = $devPid
                FriendlyName = $_.FriendlyName
            }
        }
        if ($rows) { $rows | Format-Table -AutoSize | Out-String -Width 220 | Write-Host }
        else { Write-Host '(устройства не найдены)' }
    }
    else {
        Get-CimInstance Win32_PnPEntity | Where-Object {
            $_.PNPClass -in @('Mouse','Keyboard','HIDClass') -or
            $_.Name -match 'LAMZU|ARDOR|Mouse|Keyboard|Мыш|Клавиат'
        } | Select-Object PNPClass, Status, Name | Format-Table -AutoSize | Out-String -Width 200 | Write-Host
    }
}

function Show-Measure {
    Initialize-Probe
    Write-Host 'Двигайте мышь быстро и НЕПРЕРЫВНО кругами 10 секунд...' -ForegroundColor Cyan
    $res = [InputProbe]::Run(10, $false, $false, 0)
    Clear-KeyBuffer
    Write-Host ''
    if ($res.Devices.Count -eq 0) { Write-Host 'События мыши не получены.' -ForegroundColor Yellow }
    foreach ($d in $res.Devices) { Write-Host "  $d" }
}

function Show-Status {
    Write-Host "Данные: $Root"
    $svc = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($svc) {
        Write-Host ("Служба Windows '{0}': установлена, статус = {1}, тип запуска = {2}" -f $ServiceName, $svc.Status, $svc.StartType)
    }
    else {
        Write-Host ("Служба Windows '{0}': НЕ установлена" -f $ServiceName)
    }
    try {
        $scheme = Get-Scheme; $v = Get-UsbValues $scheme
        Write-Host "USB selective suspend: AC=$($v.AC), DC=$($v.DC) (0 = выкл., 1 = вкл.), схема $scheme"
    } catch { Write-Host "USB selective suspend: не удалось прочитать ($($_.Exception.Message))" }
    $st = Get-State
    if ($st.Pending) { Write-Host "ОЖИДАЕТ ОТКАТА: незавершённая проверка '$($st.Pending.Tweak)'" -ForegroundColor Yellow }
    else { Write-Host 'Незавершённых проверок нет.' }
    if ($st.Good.Count -gt 0) {
        Write-Host 'Принятые значения:'
        foreach ($k in $st.Good.Keys) { Write-Host "  $k = $($st.Good[$k])" }
    }
    if (Test-Path -LiteralPath $LogF) {
        Write-Host "`nПоследние записи журнала:"
        Get-Content -LiteralPath $LogF -Tail 10 | ForEach-Object { Write-Host "  $_" }
    }
}

function Select-Tweak($prof) {
    $list = @($prof.Tweaks | Where-Object { $_.Enabled })
    if ($list.Count -eq 0) { throw 'В профиле нет включённых твиков (Enabled: true). Отредактируйте профиль.' }
    Write-Host 'Доступные твики:'
    for ($i = 0; $i -lt $list.Count; $i++) { Write-Host ("  {0}. {1}" -f ($i + 1), $list[$i].Name) }
    $n = Read-Host 'Номер (пусто = отмена)'
    if ([string]::IsNullOrWhiteSpace($n)) { return $null }
    $idx = 0
    if (-not [int]::TryParse($n, [ref]$idx) -or $idx -lt 1 -or $idx -gt $list.Count) { throw 'Неверный номер.' }
    return $list[$idx - 1]
}

function Invoke-SafeApply {
    $prof = Get-Prof
    $state = Get-State
    if ($state.Pending) {
        Write-Host "Сбрасываю зависшую отметку Pending для '$($state.Pending.Tweak)'..." -ForegroundColor Yellow
        $state.Pending = $null
        Save-State $state
    }
    if (-not (Get-ServiceInstalled)) {
        Write-Host "ВНИМАНИЕ: служба '$ServiceName' не установлена." -ForegroundColor Yellow
    }
    $tw = Select-Tweak $prof
    if ($null -eq $tw) { Write-Host 'Отменено.'; return }

    if ($tw.Type -eq 'usb-suspend') {
        if ((Read-Host 'Отключить USB selective suspend с проверкой? Введите YES') -cne 'YES') { Write-Host 'Отменено.'; return }
        $null = Invoke-Trial $tw 0 $state $prof
        return
    }

    if (([string]$tw.Method) -ne 'hid-report' -and [string]::IsNullOrWhiteSpace([string]$tw.ApplyCommand)) {
        throw "Для '$($tw.Name)' не задан способ применения: запустите пункт 1 или заполните ApplyCommand."
    }

    $base = Get-Baseline $tw $state
    $levels = @($tw.Levels | ForEach-Object { [int]$_ } | Sort-Object)
    Write-Host ("Текущее проверенное значение: {0} Гц. Уровни: {1}" -f $base, ($levels -join ', '))
    $ans = Read-Host 'Введите частоту в Гц или AUTO (подъём по уровням до первого сбоя)'
    if ([string]::IsNullOrWhiteSpace($ans)) { Write-Host 'Отменено.'; return }

    if ($ans -ieq 'auto') {
        foreach ($lvl in ($levels | Where-Object { $_ -gt $base })) {
            Write-Host "`n=== Пробую $lvl Гц ===" -ForegroundColor Cyan
            $ok = Invoke-Trial $tw $lvl $state $prof
            if (-not $ok) { Write-Host "Остановка на $lvl Гц. Рабочее значение: $(Get-Baseline $tw $state) Гц." -ForegroundColor Yellow; return }
            $state = Get-State
        }
        Write-Host "Достигнут максимум из списка. Рабочее значение: $(Get-Baseline $tw $state) Гц." -ForegroundColor Green
        return
    }
    $hzVal = 0
    if (-not [int]::TryParse($ans, [ref]$hzVal) -or $hzVal -lt 125 -or $hzVal -gt 32000) { throw 'Неверное значение частоты.' }
    $null = Invoke-Trial $tw $hzVal $state $prof
}

function Invoke-Rollback {
    $prof = Get-Prof
    $state = Get-State
    if ($state.Pending) {
        $state.Pending = $null
        Save-State $state
        Write-Host 'Зависшая запись Pending очищена.' -ForegroundColor Green
        return
    }
    if ($state.Prev.Count -eq 0) { Write-Host 'Нет сохранённых предыдущих значений.'; return }
    $names = @($state.Prev.Keys)
    for ($i = 0; $i -lt $names.Count; $i++) { Write-Host ("  {0}. {1}" -f ($i + 1), $names[$i]) }
    $n = Read-Host 'Какой твик вернуть к предыдущему значению? (номер, пусто = отмена)'
    if ([string]::IsNullOrWhiteSpace($n)) { return }
    $idx = 0
    if (-not [int]::TryParse($n, [ref]$idx) -or $idx -lt 1 -or $idx -gt $names.Count) { throw 'Неверный номер.' }
    $snap = $state.Prev[$names[$idx - 1]]
    Restore-Snapshot $snap
    if ($snap.Type -eq 'polling') { $state.Good[$snap.Tweak] = [int]$snap.Hz } else { $state.Good.Remove($snap.Tweak) }
    $state.Prev.Remove($snap.Tweak)
    Save-State $state
    Write-Log "rollback: $($snap.Tweak)"
    Write-Host 'Предыдущее значение восстановлено.' -ForegroundColor Green
}

function Invoke-ServiceMode {
    Initialize-Root
    $state = Get-State
    if ($state.Pending) {
        Write-Log "service: найдена незавершённая проверка '$($state.Pending.Tweak)', откат"
        try {
            Restore-Snapshot $state.Pending
            Write-Log "service: откат выполнен"
        }
        catch {
            Write-Log "service: ошибка отката (запись очищена): $($_.Exception.Message)"
        }
        $state.Pending = $null
        Save-State $state
    }

    $prof = Get-Prof
    if ($null -eq $prof.AutoApplyOnServiceStart -or [bool]$prof.AutoApplyOnServiceStart) {
        Apply-StoredGoodTweaks 'service-start'
    }

    if ($null -eq $prof.WatchUsbReconnect -or [bool]$prof.WatchUsbReconnect) {
        Write-Log "service: запущен мониторинг переподключения USB/HID"
        $watcher = New-Object System.Management.ManagementEventWatcher("SELECT * FROM __InstanceCreationEvent WITHIN 2 WHERE TargetInstance ISA 'Win32_PnPEntity'")
        try {
            while ($true) {
                $ev = $watcher.WaitForNextEvent()
                $dev = $ev.TargetInstance
                $id = [string]$dev.PNPDeviceID
                $cls = [string]$dev.PNPClass
                if ($cls -in @('Mouse','Keyboard','HIDClass') -or $id -match 'VID_[0-9A-Fa-f]{4}&PID_[0-9A-Fa-f]{4}') {
                    Start-Sleep -Milliseconds 1200
                    Apply-StoredGoodTweaks "usb-plug ($id)"
                }
            }
        }
        finally {
            $watcher.Stop()
            $watcher.Dispose()
        }
    }
}

function Invoke-Boot {
    $state = Get-State
    if ($state.Pending) {
        try { Restore-Snapshot $state.Pending } catch { }
        $state.Pending = $null
        Save-State $state
    }
    Apply-StoredGoodTweaks 'boot'
}

# ---------------------------------------------------------------- main
$exitCode = 0
try {
    if ($Mode -in @('autodetect','dongle','safeapply','rollback','install','uninstall','editprofile','service','applynow')) { Initialize-Root }
    switch ($Mode) {
        'scan'        { Show-Scan }
        'measure'     { Show-Measure }
        'autodetect'  { Invoke-AutoDetectMouse }
        'dongle'      { Invoke-DongleTool }
        'status'      { Show-Status }
        'safeapply'   { Invoke-SafeApply }
        'rollback'    { Invoke-Rollback }
        'install'     { Install-ServiceMode }
        'uninstall'   { Uninstall-ServiceMode }
        'editprofile' { Start-Process -FilePath 'notepad.exe' -ArgumentList "`"$Profile_`"" -Wait; Write-Host 'Профиль сохранён (если вы нажали Сохранить).' }
        'service'     { Invoke-ServiceMode }
        'boot'        { Invoke-Boot }
        'applynow'    { Apply-StoredGoodTweaks 'manual-applynow'; Write-Host 'Сохранённые значения применены.' -ForegroundColor Green }
    }
}
catch {
    Write-Host "ОШИБКА: $($_.Exception.Message)" -ForegroundColor Red
    Write-Log "ERROR: $($_.Exception.Message)"
    $exitCode = 1
}

if ($Elevated) {
    Write-Host ''
    Read-Host 'Нажмите Enter, чтобы закрыть это окно' | Out-Null
}
exit $exitCode
