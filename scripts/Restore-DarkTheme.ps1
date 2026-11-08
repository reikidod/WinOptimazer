$ErrorActionPreference = "Stop"
$url = "https://raw.githubusercontent.com/reikidod/WinOptimazer/14a4fdbfb7592e4f515a021fbbdf1004937669d4/Themes/DarkTheme.xaml"
$out = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot "..\Themes\DarkTheme.xaml"))
Write-Host "Downloading DarkTheme from good commit..."
$t = (Invoke-WebRequest -Uri $url -UseBasicParsing).Content
$t = $t.Replace('Padding="14,11"', 'Padding="12,10"')
$t = $t.Replace('Margin="-14,-11"', 'Margin="-12,-10"')
[IO.File]::WriteAllText($out, $t)
Write-Host "Written $out ($((Get-Item $out).Length) bytes)"
