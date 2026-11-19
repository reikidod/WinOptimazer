$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8

$Root = "$env:ProgramData\CoreAffinity"
$null = New-Item -ItemType Directory -Path $Root -Force
$Log  = Join-Path $Root 'log.txt'
$EnumRoot = 'Registry::HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Enum'

function Write-Log([string]$m) {
    Add-Content -LiteralPath $Log -Value ("{0} {1}" -f (Get-Date -Format 's'), $m) -Encoding UTF8
}

# ---------- CPU topology ----------
$cpu     = @(Get-CimInstance Win32_Processor)
$phys    = [int]($cpu | Measure-Object NumberOfCores -Sum).Sum
$logical = [int]($cpu | Measure-Object NumberOfLogicalProcessors -Sum).Sum
$ht      = $logical -gt $phys
$step    = if ($ht) { 2 } else { 1 }
$maxCore = [Math]::Min($logical, 64) - 1

# Exact topology via GetSystemCpuSetInformation (siblings + P/E efficiency class); fallback = old heuristic
$TopoSrc = @'
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

public static class CpuTopo
{
    [DllImport("kernel32.dll", SetLastError = true)]
    static extern bool GetSystemCpuSetInformation(IntPtr info, uint len, out uint ret, IntPtr proc, uint flags);

    public static int[][] Get()
    {
        uint len;
        GetSystemCpuSetInformation(IntPtr.Zero, 0, out len, IntPtr.Zero, 0);
        if (len == 0) { return new int[0][]; }
        IntPtr buf = Marshal.AllocHGlobal((int)len);
        try
        {
            if (!GetSystemCpuSetInformation(buf, len, out len, IntPtr.Zero, 0)) { return new int[0][]; }
            List<int[]> r = new List<int[]>();
            int off = 0;
            while (off < (int)len)
            {
                int size = Marshal.ReadInt32(buf, off);
                int type = Marshal.ReadInt32(buf, off + 4);
                if (size <= 0) { break; }
                if (type == 0)
                {
                    int grp = Marshal.ReadInt16(buf, off + 12);
                    int lp = Marshal.ReadByte(buf, off + 14);
                    int core = Marshal.ReadByte(buf, off + 15);
                    int eff = Marshal.ReadByte(buf, off + 18);
                    if (grp == 0) { r.Add(new int[] { lp, core, eff }); }
                }
                off += size;
            }
            return r.ToArray();
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
'@
$topo = @()
try {
    Add-Type -TypeDefinition $TopoSrc -Language CSharp
    $topo = @([CpuTopo]::Get() | ForEach-Object { [pscustomobject]@{ Lp = $_[0]; Core = $_[1]; Eff = $_[2] } })
} catch { $topo = @() }

$hybrid = $false
if ($topo.Count -gt 0) {
    $maxEff     = ($topo | Measure-Object Eff -Maximum).Maximum
    $hybrid     = (@($topo | ForEach-Object { $_.Eff } | Sort-Object -Unique).Count -gt 1)
    $perfTopo   = @($topo | Where-Object { $_.Eff -eq $maxEff })
    $perfLp     = @($perfTopo | ForEach-Object { $_.Lp } | Sort-Object)
    $coreGroups = @($perfTopo | Group-Object Core | Sort-Object { [int]$_.Name })
    $physList   = @($coreGroups | ForEach-Object { @($_.Group | ForEach-Object { $_.Lp } | Sort-Object)[0] })
    $pairs      = @($coreGroups | Where-Object { $_.Count -gt 1 } | ForEach-Object { ,@($_.Group | ForEach-Object { $_.Lp } | Sort-Object) })
} else {
    $perfLp   = @(0..$maxCore)
    $physList = @(); for ($i = 0; $i -lt $phys; $i++) { $physList += ($i * $step) }
    $pairs    = @(); if ($ht) { for ($i = 0; $i -lt $phys; $i++) { if (($i * 2 + 1) -le $maxCore) { $pairs += ,@(($i * 2), ($i * 2 + 1)) } } }
}

# ---------- helpers ----------
function Get-Paths([string]$id) {
    $base = "$EnumRoot\$id\Device Parameters\Interrupt Management"
    [pscustomobject]@{ Base = $base; Aff = "$base\Affinity Policy"; Msi = "$base\MessageSignaledInterruptProperties" }
}

function ConvertTo-MaskBytes([int[]]$cores) {
    $m = [uint64]0
    foreach ($c in $cores) { $m = $m -bor ([uint64]1 -shl $c) }
    ,([BitConverter]::GetBytes($m))
}

function ConvertFrom-MaskBytes([byte[]]$b) {
    $r = @()
    for ($i = 0; $i -lt ($b.Length * 8); $i++) {
        if (($b[[Math]::Floor($i / 8)] -band (1 -shl ($i % 8))) -ne 0) { $r += $i }
    }
    $r
}

function Read-Prop([string]$path, [string]$name) {
    if (-not (Test-Path -LiteralPath $path)) { return $null }
    $k = Get-ItemProperty -LiteralPath $path
    $p = $k.PSObject.Properties[$name]
    if ($p) { $p.Value } else { $null }
}

function Get-AffInfo([string]$id) {
    $p   = Get-Paths $id
    $pol = Read-Prop $p.Aff 'DevicePolicy'
    $ov  = Read-Prop $p.Aff 'AssignmentSetOverride'
    $msi = Read-Prop $p.Msi 'MSISupported'
    $cores = @(); if ($null -ne $ov) { $cores = @(ConvertFrom-MaskBytes ([byte[]]$ov)) }
    [pscustomobject]@{ Policy = $pol; Cores = $cores; Msi = $msi }
}

function Get-AffText([string]$id) {
    $a = Get-AffInfo $id
    $t = if ($a.Policy -eq $null) { 'по умолчанию' }
         elseif ($a.Policy -eq 4 -and $a.Cores.Count -gt 0) { 'ядра: ' + ($a.Cores -join ',') }
         else { 'политика ' + $a.Policy }
    if ($a.Msi -eq 1) { $t += ' | MSI' }
    $t
}

function Get-Devs {
    $map = @{ Display = 'GPU'; Net = 'NIC'; USB = 'USB'; SCSIAdapter = 'Storage'; HDC = 'Storage'; MEDIA = 'Audio' }
    $list = @(Get-PnpDevice -PresentOnly -Class ([string[]]$map.Keys) -ErrorAction SilentlyContinue |
              Where-Object { $_.InstanceId -like 'PCI\*' } |
              Sort-Object Class, FriendlyName)
    $i = 0
    foreach ($d in $list) {
        $i++
        [pscustomobject]@{ Idx = $i; Kind = $map[$d.Class]; Name = $d.FriendlyName; Id = $d.InstanceId; Aff = (Get-AffText $d.InstanceId) }
    }
}

function Show-Devs($devs) {
    Write-Host ''
    foreach ($d in $devs) {
        Write-Host ('{0,3}) [{1,-7}] {2}' -f $d.Idx, $d.Kind, $d.Name)
        Write-Host ('       привязка: {0}' -f $d.Aff) -ForegroundColor DarkGray
    }
    Write-Host ''
}

function Show-Topology {
    Write-Host ''
    Write-Host ("CPU: {0}" -f $cpu[0].Name.Trim())
    Write-Host ("Физических ядер: {0}, логических: {1}, Hyper-Threading/SMT: {2}" -f $phys, $logical, $(if ($ht) { 'да' } else { 'нет' }))
    if ($ht) { Write-Host ("Физические ядра = логические: {0}" -f ($physList -join ',')) -ForegroundColor Cyan }
    if ($logical -gt 64) { Write-Host 'Внимание: больше 64 логических ядер - поддерживается только группа 0 (ядра 0-63).' -ForegroundColor Yellow }
    if ($hybrid) { Write-Host ('Гибридный CPU. P-ядра (логические процессоры): {0}; остальные - E-ядра, в тестах не участвуют.' -f ($perfLp -join ',')) -ForegroundColor Cyan }
}

function Read-Cores([string]$text) {
    $r = @()
    foreach ($part in ($text -split '[,; ]+' | Where-Object { $_ })) {
        if ($part -match '^(\d+)-(\d+)$') {
            if ($Matches[1].Length -gt 3 -or $Matches[2].Length -gt 3) { return $null }
            $a = [int]$Matches[1]; $b = [int]$Matches[2]
            if ($a -gt $b -or $b -gt $maxCore) { return $null }
            $r += ($a..$b)
        }
        elseif ($part -match '^\d+$') { $r += [int]$part }
        else { return $null }
    }
    $r = @($r | Sort-Object -Unique)
    if ($r.Count -eq 0) { return $null }
    foreach ($c in $r) { if ($c -lt 0 -or $c -gt $maxCore) { return $null } }
    $r
}

function Select-Devs($devs) {
    $t = Read-Host 'Номера устройств через запятую (или all, пусто = отмена)'
    if (-not $t) { return @() }
    if ($t.Trim() -eq 'all') { return @($devs) }
    $sel = @()
    foreach ($n in ($t -split '[,; ]+' | Where-Object { $_ })) {
        if ($n -notmatch '^\d+$') { Write-Host "Неверный номер: $n" -ForegroundColor Red; return @() }
        $d = $devs | Where-Object { $_.Idx -eq [int]$n }
        if (-not $d) { Write-Host "Нет устройства с номером $n" -ForegroundColor Red; return @() }
        $sel += $d
    }
    $sel
}

# ---------- backup / restore ----------
$script:RpDone = $false
function New-RestorePointOnce {
    if ($script:RpDone) { return }
    $script:RpDone = $true
    try {
        Write-Host 'Создаю точку восстановления Windows...' -ForegroundColor DarkGray
        Checkpoint-Computer -Description 'CoreAffinity' -RestorePointType MODIFY_SETTINGS -ErrorAction Stop
        Write-Log 'restore point created'
    } catch {
        Write-Host 'Точка восстановления не создана (защита системы выключена или лимит 1 раз в 24 ч). Бэкап JSON всё равно сохраняется.' -ForegroundColor Yellow
        Write-Log ("restore point failed: {0}" -f $_.Exception.Message)
    }
}

function Save-State($devs) {
    New-RestorePointOnce
    $entries = @()
    foreach ($d in $devs) {
        $p   = Get-Paths $d.Id
        $pol = Read-Prop $p.Aff 'DevicePolicy'
        $ov  = Read-Prop $p.Aff 'AssignmentSetOverride'
        $msi = Read-Prop $p.Msi 'MSISupported'
        $entries += [pscustomobject]@{
            Id = $d.Id; Name = $d.Name
            HadPolicy = ($null -ne $pol); Policy = $pol
            HadOverride = ($null -ne $ov); Override = $(if ($null -ne $ov) { [Convert]::ToBase64String([byte[]]$ov) } else { $null })
            HadMsi = ($null -ne $msi); Msi = $msi
        }
    }
    $file = Join-Path $Root ('state_{0}.json' -f (Get-Date -Format 'yyyyMMdd_HHmmss'))
    ConvertTo-Json -InputObject @($entries) -Depth 4 | Set-Content -LiteralPath $file -Encoding UTF8
    Write-Log "backup $file"
    $file
}

function Remove-Aff([string]$id) {
    $p = Get-Paths $id
    foreach ($n in 'DevicePolicy', 'AssignmentSetOverride') {
        Remove-ItemProperty -LiteralPath $p.Aff -Name $n -ErrorAction SilentlyContinue
    }
    if (Test-Path -LiteralPath $p.Aff) {
        $k = Get-Item -LiteralPath $p.Aff
        if ($k.ValueCount -eq 0 -and $k.SubKeyCount -eq 0) { Remove-Item -LiteralPath $p.Aff -ErrorAction SilentlyContinue }
    }
}

function Restore-Entry($e) {
    if ($e.Id -notmatch '^PCI\\[^\\]+\\[^\\]+$' -or $e.Id -match '\.\.') { throw "Недопустимый Id в бэкапе: $($e.Id)" }
    $p = Get-Paths $e.Id
    Remove-Aff $e.Id
    if ($e.HadPolicy) {
        $null = New-Item -Path $p.Aff -Force
        New-ItemProperty -LiteralPath $p.Aff -Name DevicePolicy -Value ([int]$e.Policy) -PropertyType DWord -Force | Out-Null
        if ($e.HadOverride) {
            New-ItemProperty -LiteralPath $p.Aff -Name AssignmentSetOverride -Value ([byte[]][Convert]::FromBase64String($e.Override)) -PropertyType Binary -Force | Out-Null
        }
    }
    if ($e.HadMsi) {
        $null = New-Item -Path $p.Msi -Force
        New-ItemProperty -LiteralPath $p.Msi -Name MSISupported -Value ([int]$e.Msi) -PropertyType DWord -Force | Out-Null
    } elseif (Test-Path -LiteralPath $p.Msi) {
        Remove-ItemProperty -LiteralPath $p.Msi -Name MSISupported -ErrorAction SilentlyContinue
    }
}

# ---------- apply + verify ----------
function Set-Aff($d, [int[]]$cores, [bool]$msi) {
    $p = Get-Paths $d.Id
    $null = New-Item -Path $p.Aff -Force
    New-ItemProperty -LiteralPath $p.Aff -Name DevicePolicy -Value 4 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -LiteralPath $p.Aff -Name AssignmentSetOverride -Value (ConvertTo-MaskBytes $cores) -PropertyType Binary -Force | Out-Null
    if ($msi) {
        $null = New-Item -Path $p.Msi -Force
        New-ItemProperty -LiteralPath $p.Msi -Name MSISupported -Value 1 -PropertyType DWord -Force | Out-Null
    }
    # verify
    $a  = Get-AffInfo $d.Id
    $ok = ($a.Policy -eq 4) -and ((($a.Cores -join ',') -eq (($cores | Sort-Object) -join ','))) -and ((-not $msi) -or $a.Msi -eq 1)
    if ($ok) { Write-Host ("[OK]     {0}: ядра {1}" -f $d.Name, ($cores -join ',')) -ForegroundColor Green; Write-Log "set $($d.Id) cores=$($cores -join ',') msi=$msi" }
    else     { Write-Host ("[ОШИБКА] {0}: проверка после записи не совпала" -f $d.Name) -ForegroundColor Red; Write-Log "verify FAIL $($d.Id)" }
    $ok
}

function Ask-Yes([string]$q) { (Read-Host "$q (y/n)") -match '^[yYдД]' }

# ---------- actions ----------
function Do-Manual($devs) {
    Show-Devs $devs
    $sel = @(Select-Devs $devs); if ($sel.Count -eq 0) { return }
    $c = Read-Cores (Read-Host ("Ядра (напр. 2,4 или 4-7), доступно 0-{0}" -f $maxCore))
    if (-not $c) { Write-Host 'Неверный список ядер.' -ForegroundColor Red; return }
    Write-Host 'MSI: включайте только если драйвер устройства его поддерживает (иначе возможны сбои/чёрный экран).' -ForegroundColor Yellow
    $msi = Ask-Yes 'Включить MSI-режим для этих устройств (MSISupported=1)?'
    $f = Save-State $sel; Write-Host "Бэкап: $f" -ForegroundColor DarkGray
    $bad = 0; foreach ($d in $sel) { if (-not (Set-Aff $d $c $msi)) { $bad++ } }
    Write-Host 'Нужна перезагрузка, чтобы изменения вступили в силу.' -ForegroundColor Yellow
}

function Do-Preset($devs) {
    if ($phys -lt 4) { Write-Host 'Пресет требует минимум 4 физических ядра.' -ForegroundColor Red; return }
    $plan = @(
        @{ Kind = 'GPU'; Core = $physList[1] },
        @{ Kind = 'NIC'; Core = $physList[2] },
        @{ Kind = 'USB'; Core = $physList[3] }
    )
    Write-Host ''
    Write-Host ("Ядро {0} остаётся ОС/DPC. План:" -f $physList[0]) -ForegroundColor Cyan
    $targets = @()
    foreach ($pl in $plan) {
        foreach ($d in ($devs | Where-Object { $_.Kind -eq $pl.Kind })) {
            Write-Host ("  [{0}] {1} -> ядро {2}" -f $pl.Kind, $d.Name, $pl.Core)
            $targets += [pscustomobject]@{ Dev = $d; Core = [int]$pl.Core }
        }
    }
    if ($targets.Count -eq 0) { Write-Host 'Подходящих устройств нет.'; return }
    if (-not (Ask-Yes 'Применить?')) { return }
    $f = Save-State ($targets | ForEach-Object { $_.Dev }); Write-Host "Бэкап: $f" -ForegroundColor DarkGray
    foreach ($t in $targets) { $null = Set-Aff $t.Dev @($t.Core) $false }
    Write-Host 'Нужна перезагрузка, чтобы изменения вступили в силу.' -ForegroundColor Yellow
}

function Do-Reset($devs) {
    Show-Devs $devs
    $sel = @(Select-Devs $devs); if ($sel.Count -eq 0) { return }
    $f = Save-State $sel; Write-Host "Бэкап: $f" -ForegroundColor DarkGray
    foreach ($d in $sel) {
        Remove-Aff $d.Id
        $a = Get-AffInfo $d.Id
        if ($a.Policy -eq $null) { Write-Host ("[OK]     {0}: сброшено" -f $d.Name) -ForegroundColor Green; Write-Log "reset $($d.Id)" }
        else { Write-Host ("[ОШИБКА] {0}: не сброшено" -f $d.Name) -ForegroundColor Red }
    }
    Write-Host 'Нужна перезагрузка.' -ForegroundColor Yellow
}

function Do-Restore {
    $files = @(Get-ChildItem -LiteralPath $Root -Filter 'state_*.json' -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
    if ($files.Count -eq 0) { Write-Host 'Бэкапов нет.'; return }
    for ($i = 0; $i -lt [Math]::Min(10, $files.Count); $i++) { Write-Host ("{0,2}) {1}" -f ($i + 1), $files[$i].Name) }
    $n = Read-Host 'Номер бэкапа (пусто = отмена)'
    if ($n -notmatch '^\d+$' -or [int]$n -lt 1 -or [int]$n -gt [Math]::Min(10, $files.Count)) { return }
    $entries = @(Get-Content -LiteralPath $files[[int]$n - 1].FullName -Raw -Encoding UTF8 | ConvertFrom-Json)
    foreach ($e in $entries) {
        try { Restore-Entry $e; Write-Host ("[OK]     {0}: восстановлено" -f $e.Name) -ForegroundColor Green; Write-Log "restore $($e.Id)" }
        catch { Write-Host ("[ОШИБКА] {0}: {1}" -f $e.Name, $_.Exception.Message) -ForegroundColor Red }
    }
    Write-Host 'Нужна перезагрузка.' -ForegroundColor Yellow
}

# ---------- FPS triangle benchmark ----------
$FpsSrc = @'
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

public class FpsResult
{
    public string Label { get; set; }
    public int Round { get; set; }
    public double Avg { get; set; }
    public double Low5 { get; set; }
    public double Max { get; set; }
    public double Low1 { get; set; }
    public double MaxMs { get; set; }
    public int Stutter { get; set; }
    public string Renderer { get; set; }
}

public static class FpsTriangle
{
    public static bool Aborted;
    public static string Renderer = "";

    [StructLayout(LayoutKind.Sequential)]
    struct PFD
    {
        public ushort nSize; public ushort nVersion; public uint dwFlags;
        public byte iPixelType; public byte cColorBits;
        public byte cRedBits; public byte cRedShift; public byte cGreenBits; public byte cGreenShift;
        public byte cBlueBits; public byte cBlueShift; public byte cAlphaBits; public byte cAlphaShift;
        public byte cAccumBits; public byte cAccumRedBits; public byte cAccumGreenBits; public byte cAccumBlueBits; public byte cAccumAlphaBits;
        public byte cDepthBits; public byte cStencilBits; public byte cAuxBuffers; public byte iLayerType; public byte bReserved;
        public uint dwLayerMask; public uint dwVisibleMask; public uint dwDamageMask;
    }

    delegate bool SwapIntervalFn(int interval);

    [DllImport("user32.dll")] static extern IntPtr GetDC(IntPtr h);
    [DllImport("user32.dll")] static extern int ReleaseDC(IntPtr h, IntPtr dc);
    [DllImport("gdi32.dll")] static extern int ChoosePixelFormat(IntPtr dc, ref PFD pfd);
    [DllImport("gdi32.dll")] static extern bool SetPixelFormat(IntPtr dc, int fmt, ref PFD pfd);
    [DllImport("gdi32.dll")] static extern bool SwapBuffers(IntPtr dc);
    [DllImport("opengl32.dll")] static extern IntPtr wglCreateContext(IntPtr dc);
    [DllImport("opengl32.dll")] static extern bool wglMakeCurrent(IntPtr dc, IntPtr rc);
    [DllImport("opengl32.dll")] static extern bool wglDeleteContext(IntPtr rc);
    [DllImport("opengl32.dll")] static extern IntPtr wglGetProcAddress(string name);
    [DllImport("opengl32.dll")] static extern IntPtr glGetString(uint name);
    [DllImport("opengl32.dll")] static extern void glClearColor(float r, float g, float b, float a);
    [DllImport("opengl32.dll")] static extern void glClear(uint mask);
    [DllImport("opengl32.dll")] static extern void glViewport(int x, int y, int w, int h);
    [DllImport("opengl32.dll")] static extern void glBegin(uint mode);
    [DllImport("opengl32.dll")] static extern void glEnd();
    [DllImport("opengl32.dll")] static extern void glColor3f(float r, float g, float b);
    [DllImport("opengl32.dll")] static extern void glVertex2f(float x, float y);
    [DllImport("opengl32.dll")] static extern void glLoadIdentity();
    [DllImport("opengl32.dll")] static extern void glRotatef(float a, float x, float y, float z);

    public static List<FpsResult> Run(string[] labels, long[] masks, int warmMs, int measMs, int rounds, int tris)
    {
        Aborted = false;
        List<FpsResult> results = new List<FpsResult>();
        Process proc = Process.GetCurrentProcess();
        IntPtr origAff = proc.ProcessorAffinity;
        ProcessPriorityClass origPrio = proc.PriorityClass;
        try { proc.PriorityClass = ProcessPriorityClass.High; } catch (Exception) { }
        bool done = false;

        Form form = new Form();
        form.Text = "FPS Triangle";
        form.ClientSize = new Size(800, 600);
        form.StartPosition = FormStartPosition.CenterScreen;
        form.KeyPreview = true;
        Label info = new Label();
        info.Dock = DockStyle.Top;
        info.Height = 70;
        info.ForeColor = Color.Lime;
        info.BackColor = Color.FromArgb(20, 20, 20);
        info.Font = new Font("Consolas", 14f, FontStyle.Bold);
        Panel surface = new Panel();
        surface.Dock = DockStyle.Fill;
        form.Controls.Add(surface);
        form.Controls.Add(info);
        form.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Escape) { form.Close(); } };
        form.FormClosing += delegate(object s, FormClosingEventArgs e) { if (!done) { Aborted = true; } };

        IntPtr hwnd = IntPtr.Zero;
        IntPtr dc = IntPtr.Zero;
        IntPtr rc = IntPtr.Zero;
        try
        {
            form.Show();
            Application.DoEvents();
            hwnd = surface.Handle;
            dc = GetDC(hwnd);
            PFD pfd = new PFD();
            pfd.nSize = (ushort)Marshal.SizeOf(typeof(PFD));
            pfd.nVersion = 1;
            pfd.dwFlags = 0x25; // DRAW_TO_WINDOW | SUPPORT_OPENGL | DOUBLEBUFFER
            pfd.iPixelType = 0;
            pfd.cColorBits = 32;
            pfd.cDepthBits = 16;
            int fmt = ChoosePixelFormat(dc, ref pfd);
            if (fmt == 0 || !SetPixelFormat(dc, fmt, ref pfd)) { throw new InvalidOperationException("OpenGL pixel format failed"); }
            rc = wglCreateContext(dc);
            if (rc == IntPtr.Zero) { throw new InvalidOperationException("wglCreateContext failed"); }
            if (!wglMakeCurrent(dc, rc)) { throw new InvalidOperationException("wglMakeCurrent failed"); }
            IntPtr sp = wglGetProcAddress("wglSwapIntervalEXT");
            if (sp != IntPtr.Zero)
            {
                SwapIntervalFn swap = (SwapIntervalFn)Marshal.GetDelegateForFunctionPointer(sp, typeof(SwapIntervalFn));
                swap(0); // vsync off
            }
            glClearColor(0.05f, 0.05f, 0.08f, 1f);
            IntPtr rp = glGetString(0x1F01); // GL_RENDERER
            Renderer = (rp == IntPtr.Zero) ? "unknown" : Marshal.PtrToStringAnsi(rp);
            form.Text = "FPS Triangle - " + Renderer;

            float[] tx = new float[tris];
            float[] ty = new float[tris];
            float[] tc = new float[tris];
            Random rnd = new Random(1);
            for (int i = 0; i < tris; i++)
            {
                tx[i] = (float)(rnd.NextDouble() * 1.8 - 0.9);
                ty[i] = (float)(rnd.NextDouble() * 1.8 - 0.9);
                tc[i] = (float)rnd.NextDouble();
            }

            int n = labels.Length;
            float angle = 0f;
            const int RING = 240;
            float[] ring = new float[RING];
            int ringPos = 0;
            double scaleMs = 2.0;
            for (int r = 0; r < rounds && !Aborted; r++)
            {
                for (int k = 0; k < n && !Aborted; k++)
                {
                    int ci = (r % 2 == 0) ? k : (n - 1 - k); // reverse order on odd rounds to cancel thermal drift
                    try { proc.ProcessorAffinity = new IntPtr(masks[ci]); }
                    catch (Exception) { continue; }

                    Stopwatch sw = Stopwatch.StartNew();
                    double mStart = -1, tEnd = 0, lastUi = 0, segStart = 0, shownFps = 0;
                    long mFrames = 0, segFrames = 0, uiFrames = 0;
                    List<double> samples = new List<double>();
                    List<double> ft = new List<double>();
                    double prevT = 0;

                    while (!Aborted)
                    {
                        double t = sw.Elapsed.TotalMilliseconds;
                        if (t >= warmMs + measMs) { tEnd = t; break; }

                        glViewport(0, 0, Math.Max(1, surface.ClientSize.Width), Math.Max(1, surface.ClientSize.Height));
                        double dt = t - prevT;
                        prevT = t;
                        ring[ringPos] = (float)dt;
                        ringPos = (ringPos + 1) % RING;
                        glClear(0x4000);
                        glLoadIdentity();
                        // frametime graph (bottom strip, not rotated)
                        glBegin(3);
                        glColor3f(0.2f, 1f, 0.2f);
                        for (int gi = 0; gi < RING; gi++)
                        {
                            float v = ring[(ringPos + gi) % RING];
                            float h = (float)Math.Min(1.0, v / (scaleMs * 3.0));
                            glVertex2f(-0.97f + gi * (1.94f / (RING - 1)), -0.97f + h * 0.28f);
                        }
                        glEnd();
                        glRotatef(angle, 0f, 0f, 1f);
                        angle += 0.5f;
                        glBegin(4);
                        glColor3f(1f, 0f, 0f); glVertex2f(-0.6f, -0.5f);
                        glColor3f(0f, 1f, 0f); glVertex2f(0.6f, -0.5f);
                        glColor3f(0f, 0f, 1f); glVertex2f(0f, 0.7f);
                        for (int i = 0; i < tris; i++)
                        {
                            glColor3f(tc[i], 1f - tc[i], 0.5f);
                            glVertex2f(tx[i], ty[i]);
                            glVertex2f(tx[i] + 0.02f, ty[i]);
                            glVertex2f(tx[i], ty[i] + 0.02f);
                        }
                        glEnd();
                        SwapBuffers(dc);
                        uiFrames++;

                        if (t >= warmMs)
                        {
                            if (mStart < 0) { mStart = t; segStart = t; mFrames = 0; segFrames = 0; }
                            mFrames++; segFrames++;
                            ft.Add(dt);
                            if (t - segStart >= 100)
                            {
                                samples.Add(segFrames * 1000.0 / (t - segStart));
                                segStart = t; segFrames = 0;
                            }
                        }

                        if (t - lastUi >= 200)
                        {
                            shownFps = uiFrames * 1000.0 / (t - lastUi);
                            scaleMs = Math.Max(0.05, 1000.0 / Math.Max(1.0, shownFps));
                            double avgNow = (mStart >= 0 && t > mStart) ? mFrames * 1000.0 / (t - mStart) : 0;
                            info.Text = string.Format("FPS {0:F0}   frame {1:F2} ms   avg {2:F0}\r\nSet {3}/{4}: {5}   round {6}/{7}   {8}",
                                shownFps, scaleMs, avgNow, k + 1, n, labels[ci], r + 1, rounds, (t < warmMs ? "warmup" : "measuring"));
                            lastUi = t; uiFrames = 0;
                            Application.DoEvents();
                        }
                    }

                    if (!Aborted && mStart >= 0 && tEnd > mStart && samples.Count > 0)
                    {
                        samples.Sort();
                        FpsResult res = new FpsResult();
                        res.Label = labels[ci];
                        res.Round = r + 1;
                        res.Avg = Math.Round(mFrames * 1000.0 / (tEnd - mStart), 1);
                        res.Low5 = Math.Round(samples[(int)(samples.Count * 0.05)], 1);
                        res.Max = Math.Round(samples[samples.Count - 1], 1);
                        ft.Sort();
                        int cnt = ft.Count;
                        if (cnt > 0)
                        {
                            int worst = Math.Max(1, cnt / 100);
                            double sumW = 0;
                            for (int q = cnt - worst; q < cnt; q++) { sumW += ft[q]; }
                            if (sumW > 0) { res.Low1 = Math.Round(1000.0 / (sumW / worst), 1); }
                            res.MaxMs = Math.Round(ft[cnt - 1], 2);
                            double med = ft[cnt / 2];
                            int st = 0;
                            for (int q = 0; q < cnt; q++) { if (ft[q] > med * 2.5) { st++; } }
                            res.Stutter = st;
                        }
                        res.Renderer = Renderer;
                        results.Add(res);
                    }
                }
            }
        }
        finally
        {
            done = true;
            try { proc.ProcessorAffinity = origAff; } catch (Exception) { }
            try { proc.PriorityClass = origPrio; } catch (Exception) { }
            if (rc != IntPtr.Zero) { wglMakeCurrent(IntPtr.Zero, IntPtr.Zero); wglDeleteContext(rc); }
            if (dc != IntPtr.Zero) { ReleaseDC(hwnd, dc); }
            if (!form.IsDisposed) { form.Close(); }
            if (!form.IsDisposed) { form.Dispose(); }
        }
        return results;
    }
}
'@

function Initialize-Fps {
    if (-not ('FpsTriangle' -as [type])) {
        Add-Type -TypeDefinition $FpsSrc -ReferencedAssemblies 'System.Windows.Forms', 'System.Drawing' -Language CSharp
    }
}

function ConvertTo-Mask64([int[]]$cores) {
    $m = [int64]0
    foreach ($c in $cores) { $m = $m -bor ([int64]1 -shl $c) }
    $m
}

function Do-FpsTest {
    $gpus = @(Get-Devs | Where-Object { $_.Kind -eq 'GPU' })
    if ($gpus.Count -eq 0) { Write-Host 'GPU на шине PCI не найдена.' -ForegroundColor Red; return }

    $secs = 3;   $t = Read-Host 'Секунд замера на вариант (1-30, по умолчанию 3)'
    if ($t -match '^\d{1,2}$' -and [int]$t -ge 1 -and [int]$t -le 30) { $secs = [int]$t }
    $rounds = 2; $t = Read-Host 'Кругов (1-5, по умолчанию 2)'
    if ($t -match '^\d$' -and [int]$t -ge 1 -and [int]$t -le 5) { $rounds = [int]$t }
    $tris = 3000; $t = Read-Host 'Нагрузка: 1 = лёгкая (1000 треугольников), 2 = средняя (3000), 3 = тяжёлая (20000); по умолчанию 2'
    if ($t.Trim() -eq '1') { $tris = 1000 } elseif ($t.Trim() -eq '3') { $tris = 20000 }

    $mode = Read-Host 'Режим: 1 = полный (каждый поток + пары HT), 2 = быстрый (по одному потоку на физ. ядро); по умолчанию 1'
    $quick = ($mode.Trim() -eq '2')
    $cands = @(Build-Cands $quick)
    $cands += [pscustomobject]@{ Label = 'all cores'; Cores = @(0..$maxCore) }

    $total = $cands.Count * $rounds * (1 + $secs)
    Write-Host ''
    Write-Host ("Вариантов: {0} (потоки/пары HT, все ядра), кругов: {1}, ~{2} сек." -f $cands.Count, $rounds, $total) -ForegroundColor Cyan
    Write-Host 'Закройте игры и фоновые программы. Откроется окно с треугольником и FPS. Esc - прервать.' -ForegroundColor Yellow
    Write-Host 'Тест двигает ядра для потока отрисовки (сразу, без перезагрузки). Лучший вариант затем можно применить к прерыванию GPU.' -ForegroundColor DarkGray
    if (-not (Ask-Yes 'Начать?')) { return }

    Initialize-Fps
    [string[]]$labels = @($cands | ForEach-Object { $_.Label })
    [long[]]$masks    = @($cands | ForEach-Object { ConvertTo-Mask64 $_.Cores })
    $res = @([FpsTriangle]::Run($labels, $masks, 1000, ($secs * 1000), $rounds, $tris))
    $aborted = [FpsTriangle]::Aborted
    $rend = [FpsTriangle]::Renderer
    Write-Host ('Рендерер OpenGL: {0}' -f $rend) -ForegroundColor Cyan
    if ($rend -match 'GDI Generic|Microsoft|llvmpipe|Software') { Write-Host 'ВНИМАНИЕ: OpenGL идёт через программный рендер, а не через GPU - результаты бессмысленны (удалённый рабочий стол/виртуалка/нет драйвера).' -ForegroundColor Red }
    if ($res.Count -eq 0) { Write-Host 'Результатов нет.' -ForegroundColor Yellow; return }

    $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
    $raw = Join-Path $Root "fps_$stamp.csv"
    $res | Select-Object Label, Round, Avg, Low1, Low5, Max, MaxMs, Stutter, Renderer | Export-Csv -LiteralPath $raw -NoTypeInformation -Encoding UTF8
    Write-Log "fps raw $raw"

    $sum = @($res | Group-Object Label | ForEach-Object {
        $avg = [Math]::Round(($_.Group | Measure-Object Avg  -Average).Average, 1)
        $l1  = [Math]::Round(($_.Group | Measure-Object Low1 -Average).Average, 1)
        [pscustomobject]@{
            Label   = $_.Name
            Avg     = $avg
            Low1    = $l1
            Stutter = [Math]::Round(($_.Group | Measure-Object Stutter -Average).Average, 1)
            Score   = [Math]::Round(0.6 * $avg + 0.4 * $l1, 1)
            Runs    = $_.Count
        }
    } | Sort-Object Score -Descending)
    $sumFile = Join-Path $Root "fps_summary_$stamp.csv"
    $sum | Export-Csv -LiteralPath $sumFile -NoTypeInformation -Encoding UTF8

    Write-Host ''
    Write-Host 'Топ-10 по оценке Score = 60% средний FPS + 40% 1% low (Stutter = рывки: кадры дольше 2.5x медианы):' -ForegroundColor Cyan
    Write-Host (($sum | Select-Object -First 10 | Format-Table Label, Avg, Low1, Stutter, Score, Runs -AutoSize | Out-String -Width 200).TrimEnd())
    Write-Host "Результаты: $sumFile" -ForegroundColor DarkGray

    if ($aborted) { Write-Host 'Тест прерван - результаты неполные, автоприменение отключено.' -ForegroundColor Yellow; return }

    $best = $sum[0]
    $base = $sum | Where-Object { $_.Label -eq 'all cores' } | Select-Object -First 1
    if ($base -and $base.Score -gt 0) {
        Write-Host ("Лучший: {0} - {1} FPS, 1% low {2} (оценка {3:+0.0;-0.0}% к 'all cores')" -f $best.Label, $best.Avg, $best.Low1, (($best.Score / $base.Score - 1) * 100)) -ForegroundColor Green
    }
    if ($best.Label -eq 'all cores') { Write-Host "Лучший результат без привязки - прерывание GPU не меняем." ; return }

    $bestC = $cands | Where-Object { $_.Label -eq $best.Label } | Select-Object -First 1
    $a = Read-Host ("Привязать прерывание GPU к '{0}' (ядра {1})? Enter = да, n = нет" -f $best.Label, ($bestC.Cores -join ','))
    if ($a -match '^[nNнН]') { return }
    $f = Save-State $gpus; Write-Host "Бэкап: $f" -ForegroundColor DarkGray
    foreach ($g in $gpus) { $null = Set-Aff $g @($bestC.Cores) $false }
    Write-Host 'Нужна перезагрузка, чтобы изменения вступили в силу.' -ForegroundColor Yellow
}

function Build-Cands([bool]$quick) {
    $list = @()
    $singles = if ($quick) { $physList } else { $perfLp }
    foreach ($c in $singles) { $list += [pscustomobject]@{ Label = "core $c"; Cores = @([int]$c) } }
    if (-not $quick) {
        foreach ($pr in $pairs) { $list += [pscustomobject]@{ Label = ('pair ' + ($pr -join '+')); Cores = @($pr | ForEach-Object { [int]$_ }) } }
    }
    $list
}

# ---------- FPS test with REAL GPU interrupt re-pinning (GPU driver restart per variant) ----------
# Worker = separate process per variant (the benchmark window dies when the GPU driver restarts).
$WorkerSrc = @'
param([string]$Cs, [string]$Out, [long]$Mask, [int]$Secs, [int]$Tris = 3000)
$ErrorActionPreference = 'Stop'
try {
    Add-Type -TypeDefinition ([IO.File]::ReadAllText($Cs)) -ReferencedAssemblies 'System.Windows.Forms', 'System.Drawing' -Language CSharp
    $r = @([FpsTriangle]::Run([string[]]@('run'), [long[]]@($Mask), 1000, ($Secs * 1000), 1, $Tris))
    if ($r.Count -gt 0 -and -not [FpsTriangle]::Aborted) {
        @{ ok = $true; Avg = $r[0].Avg; Low5 = $r[0].Low5; Max = $r[0].Max; Low1 = $r[0].Low1; MaxMs = $r[0].MaxMs; Stutter = $r[0].Stutter; Renderer = $r[0].Renderer } | ConvertTo-Json | Set-Content -LiteralPath $Out -Encoding UTF8
    } else {
        @{ ok = $false; err = 'aborted' } | ConvertTo-Json | Set-Content -LiteralPath $Out -Encoding UTF8
    }
} catch {
    @{ ok = $false; err = $_.Exception.Message } | ConvertTo-Json | Set-Content -LiteralPath $Out -Encoding UTF8
}
'@

function Restart-Gpu([string]$id) {
    & pnputil.exe /restart-device "$id" 2>&1 | Out-Null
    if ($LASTEXITCODE -ne 0) {
        try {
            Disable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop
            Start-Sleep -Seconds 2
            Enable-PnpDevice -InstanceId $id -Confirm:$false -ErrorAction Stop
        } catch { return $false }
    }
    for ($i = 0; $i -lt 40; $i++) {
        Start-Sleep -Seconds 1
        $d = Get-PnpDevice -InstanceId $id -ErrorAction SilentlyContinue
        if ($d -and $d.Status -eq 'OK') { Start-Sleep -Seconds 3; return $true }
    }
    $false
}

function Get-IrqSample {
    try {
        Get-CimInstance Win32_PerfFormattedData_Counters_ProcessorInformation -ErrorAction Stop |
            Where-Object { $_.Name -match '^0,\d+$' } |
            ForEach-Object { [pscustomobject]@{ Lp = ([int](($_.Name -split ',')[1])); Irq = [double]$_.InterruptsPersec } }
    } catch { @() }
}

function Invoke-FpsWorker([int]$secs, [int[]]$target, [int]$tris) {
    $out = Join-Path $Root 'fps_worker_out.json'
    Remove-Item -LiteralPath $out -ErrorAction SilentlyContinue
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f (Join-Path $Root 'fps_worker.ps1')),
        '-Cs',   ('"{0}"' -f (Join-Path $Root 'FpsTriangle.cs')),
        '-Out',  ('"{0}"' -f $out),
        '-Mask', [string](ConvertTo-Mask64 @(0..$maxCore)),
        '-Secs', [string]$secs,
        '-Tris', [string]$tris)
    $p = Start-Process -FilePath 'powershell.exe' -ArgumentList $argList -PassThru -WindowStyle Hidden
    $sum = @{}; $cnt = @{}
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $limit = 60 + $secs
    while (-not $p.HasExited) {
        if ($sw.Elapsed.TotalSeconds -gt $limit) { try { $p.Kill() } catch { }; break }
        if ($sw.Elapsed.TotalSeconds -ge 5) {
            foreach ($s in @(Get-IrqSample)) {
                $sum[$s.Lp] = [double]$sum[$s.Lp] + $s.Irq
                $cnt[$s.Lp] = [int]$cnt[$s.Lp] + 1
            }
        }
        Start-Sleep -Milliseconds 800
    }
    $top = $null; $ver = $null
    if ($sum.Count -gt 0) {
        $topLp = $sum.Keys | Sort-Object { $sum[$_] / [Math]::Max(1, $cnt[$_]) } -Descending | Select-Object -First 1
        if (($sum[$topLp] / [Math]::Max(1, $cnt[$topLp])) -gt 0) {
            $top = [int]$topLp
            if ($target.Count -gt 0) { $ver = ($target -contains $top) }
        }
    }
    $res = $null
    if (Test-Path -LiteralPath $out) { $res = Get-Content -LiteralPath $out -Raw -Encoding UTF8 | ConvertFrom-Json }
    [pscustomobject]@{ Res = $res; IrqTop = $top; Verified = $ver }
}

function Restore-Orig([string]$file, $gpu) {
    try {
        foreach ($e in @(Get-Content -LiteralPath $file -Raw -Encoding UTF8 | ConvertFrom-Json)) { Restore-Entry $e }
        $null = Restart-Gpu $gpu.Id
        Write-Host 'Исходная привязка GPU восстановлена.' -ForegroundColor Green
        Write-Log "restored original $file"
    } catch {
        Write-Host ("Не удалось восстановить автоматически: {0}. Пункт 5 меню, бэкап: {1}" -f $_.Exception.Message, $file) -ForegroundColor Red
    }
}

function Do-FpsIrqTest {
    $gpus = @(Get-Devs | Where-Object { $_.Kind -eq 'GPU' })
    if ($gpus.Count -eq 0) { Write-Host 'GPU на шине PCI не найдена.' -ForegroundColor Red; return }
    $gpu = $gpus[0]
    if ($gpus.Count -gt 1) {
        Show-Devs $gpus
        $n = Read-Host 'Номер GPU, на которой рисует окно теста (Enter = первая)'
        if ($n -match '^\d+$') { $pick = $gpus | Where-Object { $_.Idx -eq [int]$n } | Select-Object -First 1; if ($pick) { $gpu = $pick } }
    }
    $mode = Read-Host 'Режим: 1 = полный (потоки + пары HT), 2 = быстрый (по одному потоку на физ. ядро); по умолчанию 2'
    $quick = ($mode.Trim() -ne '1')
    $secs = 4;   $t = Read-Host 'Секунд замера на вариант (1-30, по умолчанию 4)'
    if ($t -match '^\d{1,2}$' -and [int]$t -ge 1 -and [int]$t -le 30) { $secs = [int]$t }
    $rounds = 2; $t = Read-Host 'Кругов (1-5, по умолчанию 2)'
    if ($t -match '^\d$' -and [int]$t -ge 1 -and [int]$t -le 5) { $rounds = [int]$t }
    $tris = 3000; $t = Read-Host 'Нагрузка: 1 = лёгкая (1000 треугольников), 2 = средняя (3000), 3 = тяжёлая (20000); по умолчанию 2'
    if ($t.Trim() -eq '1') { $tris = 1000 } elseif ($t.Trim() -eq '3') { $tris = 20000 }

    $cands = @(Build-Cands $quick)
    $cands += [pscustomobject]@{ Label = 'default'; Cores = @() }
    $total = [int]($cands.Count * $rounds * (14 + $secs) / 60)

    Write-Host ''
    Write-Host ("GPU: {0}" -f $gpu.Name) -ForegroundColor Cyan
    Write-Host ("Вариантов: {0}, кругов: {1}, примерно {2} мин." -f $cands.Count, $rounds, $total) -ForegroundColor Cyan
    Write-Host 'ВНИМАНИЕ: перед КАЖДЫМ замером драйвер видеокарты перезапускается - экран мигает/гаснет на несколько секунд.' -ForegroundColor Yellow
    Write-Host 'Закройте игры, браузеры и всё, что использует GPU. Не трогайте мышь и окно теста. Esc в окне теста - прервать.' -ForegroundColor Yellow
    Write-Host 'При сбое или прерывании исходная привязка вернётся автоматически. Окно теста рисует на GPU по умолчанию (на ноутбуках с двумя GPU результат может относиться не к ней).' -ForegroundColor DarkGray
    if (-not (Ask-Yes 'Начать?')) { return }

    $backup = Save-State @($gpu)
    Write-Host "Бэкап: $backup" -ForegroundColor DarkGray
    Set-Content -LiteralPath (Join-Path $Root 'FpsTriangle.cs') -Value $FpsSrc -Encoding ASCII
    Set-Content -LiteralPath (Join-Path $Root 'fps_worker.ps1') -Value $WorkerSrc -Encoding ASCII

    $raw = @(); $keep = $false
    try {
        for ($r = 1; $r -le $rounds; $r++) {
            $order = @(0..($cands.Count - 1)); if ($r % 2 -eq 0) { [Array]::Reverse($order) }
            $k = 0
            foreach ($ci in $order) {
                $k++
                $cand = $cands[$ci]
                if ($cand.Cores.Count -eq 0) { Remove-Aff $gpu.Id }
                elseif (-not (Set-Aff $gpu @($cand.Cores) $false)) { throw 'Не удалось записать привязку в реестр.' }
                Write-Host ("[{0}/{1}] круг {2}: {3} - перезапуск GPU..." -f $k, $cands.Count, $r, $cand.Label) -ForegroundColor DarkGray
                if (-not (Restart-Gpu $gpu.Id)) { throw 'GPU не перезапустилась (проверьте драйвер).' }
                $w = Invoke-FpsWorker $secs @($cand.Cores) $tris
                if (-not $w.Res -or -not $w.Res.ok) { throw ('Замер прерван или не удался: ' + $(if ($w.Res) { $w.Res.err } else { 'нет результата' })) }
                $raw += [pscustomobject]@{ Label = $cand.Label; Round = $r; Avg = [double]$w.Res.Avg; Low1 = [double]$w.Res.Low1; Low5 = [double]$w.Res.Low5; Max = [double]$w.Res.Max; MaxMs = [double]$w.Res.MaxMs; Stutter = [int]$w.Res.Stutter; IrqCore = $w.IrqTop; Verified = $w.Verified; Renderer = $w.Res.Renderer }
                if ($raw.Count -eq 1) {
                    Write-Host ('        Рендерер OpenGL: {0}' -f $w.Res.Renderer) -ForegroundColor Cyan
                    if ([string]$w.Res.Renderer -match 'GDI Generic|Microsoft|llvmpipe|Software') { throw 'OpenGL идёт через программный рендер, а не через GPU - тест бессмысленен.' }
                }
                $vt = if ($w.Verified -eq $true) { 'прерывания на целевом ядре' } elseif ($w.Verified -eq $false) { 'ВНИМАНИЕ: прерывания не на целевом ядре' } else { 'ядро прерываний не определено' }
                Write-Host ("        {0:F0} FPS (1% low {1:F0}, рывков {4}); топ по прерываниям: ядро {2}; {3}" -f $w.Res.Avg, $w.Res.Low1, $w.IrqTop, $vt, $w.Res.Stutter)
            }
        }

        $stamp = Get-Date -Format 'yyyyMMdd_HHmmss'
        $rawFile = Join-Path $Root "fps_irq_$stamp.csv"
        $raw | Export-Csv -LiteralPath $rawFile -NoTypeInformation -Encoding UTF8
        $sum = @($raw | Group-Object Label | ForEach-Object {
            $avg = [Math]::Round(($_.Group | Measure-Object Avg  -Average).Average, 1)
            $l1  = [Math]::Round(($_.Group | Measure-Object Low1 -Average).Average, 1)
            [pscustomobject]@{
                Label    = $_.Name
                Avg      = $avg
                Low1     = $l1
                Stutter  = [Math]::Round(($_.Group | Measure-Object Stutter -Average).Average, 1)
                Score    = [Math]::Round(0.6 * $avg + 0.4 * $l1, 1)
                Runs     = $_.Count
                Mismatch = @($_.Group | Where-Object { $_.Verified -eq $false }).Count
            }
        } | Sort-Object Score -Descending)
        $sumFile = Join-Path $Root "fps_irq_summary_$stamp.csv"
        $sum | Export-Csv -LiteralPath $sumFile -NoTypeInformation -Encoding UTF8

        Write-Host ''
        Write-Host 'Топ-10 по оценке Score = 60% средний FPS + 40% 1% low (Mismatch = замеры, где прерывания были не на целевом ядре):' -ForegroundColor Cyan
        Write-Host (($sum | Select-Object -First 10 | Format-Table Label, Avg, Low1, Stutter, Score, Runs, Mismatch -AutoSize | Out-String -Width 200).TrimEnd())
        Write-Host "Результаты: $sumFile" -ForegroundColor DarkGray

        $eligible = @($sum | Where-Object { $_.Mismatch -eq 0 })
        if ($eligible.Count -eq 0) { Write-Host 'Нигде не удалось подтвердить перенос прерываний - драйвер, возможно, игнорирует политику. Итог ненадёжен.' -ForegroundColor Yellow; $eligible = $sum }
        $best = $eligible[0]
        $base = $sum | Where-Object { $_.Label -eq 'default' } | Select-Object -First 1
        if ($base -and $base.Score -gt 0) {
            Write-Host ("Лучший: {0} - {1} FPS, 1% low {2} (оценка {3:+0.0;-0.0}% к 'default')" -f $best.Label, $best.Avg, $best.Low1, (($best.Score / $base.Score - 1) * 100)) -ForegroundColor Green
        }
        $ans = Read-Host ("Оставить '{0}'? Enter = да (GPU перезапустится), n = вернуть исходное" -f $best.Label)
        if ($ans -notmatch '^[nNнН]') {
            if ($best.Label -eq 'default') { Remove-Aff $gpu.Id }
            else {
                $bc = $cands | Where-Object { $_.Label -eq $best.Label } | Select-Object -First 1
                if (-not (Set-Aff $gpu @($bc.Cores) $false)) { throw 'Не удалось записать лучшую привязку.' }
            }
            if (-not (Restart-Gpu $gpu.Id)) { throw 'GPU не перезапустилась после применения.' }
            $keep = $true
            Write-Host 'Применено. Перезагрузка не требуется (драйвер уже перезапущен); для надёжности можно перезагрузиться.' -ForegroundColor Green
        }
    } catch {
        Write-Host ("ОШИБКА: {0}" -f $_.Exception.Message) -ForegroundColor Red
        Write-Log ("irq test exception: {0}" -f $_.Exception.Message)
    } finally {
        if (-not $keep) { Write-Host 'Возвращаю исходную привязку GPU...' -ForegroundColor Yellow; Restore-Orig $backup $gpu }
    }
}

# ---------- main ----------
Show-Topology
$run = $true
while ($run) {
    Write-Host ''
    Write-Host '=== CoreAffinity: привязка прерываний устройств к ядрам ===' -ForegroundColor Cyan
    Write-Host ' 1) Топология CPU и список устройств'
    Write-Host ' 2) Привязать устройства к ядрам (вручную)'
    Write-Host ' 3) Пресет для игр (GPU/NIC/USB на отдельные физические ядра)'
    Write-Host ' 4) Сбросить устройства к умолчанию'
    Write-Host ' 5) Восстановить из бэкапа'
    Write-Host ' 6) FPS-тест по ядрам (треугольник) + применить лучший вариант'
    Write-Host ' 7) FPS-тест с РЕАЛЬНОЙ сменой прерывания GPU (перезапуск драйвера на каждый вариант)'
    Write-Host ' 0) Выход'
    $c = Read-Host 'Выбор'
    try {
        switch ($c) {
            '1' { Show-Topology; Show-Devs @(Get-Devs) }
            '2' { Do-Manual @(Get-Devs) }
            '3' { Do-Preset @(Get-Devs) }
            '4' { Do-Reset  @(Get-Devs) }
            '5' { Do-Restore }
            '6' { Do-FpsTest }
            '7' { Do-FpsIrqTest }
            '0' { $run = $false }
            default { Write-Host 'Неверный выбор.' -ForegroundColor Yellow }
        }
    } catch {
        Write-Host ("ОШИБКА: {0}" -f $_.Exception.Message) -ForegroundColor Red
        Write-Log ("exception: {0}" -f $_.Exception.Message)
    }
}
