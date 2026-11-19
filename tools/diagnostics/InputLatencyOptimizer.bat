@echo off
setlocal EnableExtensions EnableDelayedExpansion
chcp 65001 >nul
title WinOptimizer - Input Latency & Dongle Controller
mode con: cols=96 lines=34

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%InputLatencyOptimizer.ps1"
set "PS_EXE=%SystemRoot%\System32\WindowsPowerShell\v1.0\powershell.exe"
set "DATA_DIR=%ProgramData%\WinOptimizer\InputLatency"
set "PROFILE_FILE=%DATA_DIR%\profile.json"
set "SVC_NAME=WinOptimizerInputLatencySvc"

:: ANSI escape sequences (Windows 10/11)
for /f %%A in ('echo prompt $E^| cmd') do set "ESC=%%A"
set "C_RESET=%ESC%[0m"
set "C_BOLD=%ESC%[1m"
set "C_DIM=%ESC%[90m"
set "C_CYAN=%ESC%[96m"
set "C_BLUE=%ESC%[94m"
set "C_GREEN=%ESC%[92m"
set "C_YELLOW=%ESC%[93m"
set "C_RED=%ESC%[91m"
set "C_WHITE=%ESC%[97m"

if not exist "%PS_SCRIPT%" (
    cls
    echo.
    echo  %C_RED%[ОШИБКА]%C_RESET% Не найден основной скрипт:
    echo  %C_DIM%%PS_SCRIPT%%C_RESET%
    echo.
    pause
    exit /b 1
)

:MAIN_MENU
cls
call :REFRESH_STATUS

echo.
echo  %C_CYAN%╔════════════════════════════════════════════════════════════════════════════════════════╗%C_RESET%
echo  %C_CYAN%║%C_RESET%   %C_BOLD%%C_WHITE%WINOPTIMIZER // INPUT LATENCY ^& WIRELESS DONGLE CONTROLLER%C_RESET%                            %C_CYAN%║%C_RESET%
echo  %C_CYAN%║%C_RESET%   %C_DIM%Прямое управление HID-донглами, замер частоты опроса и фоновая служба Windows%C_RESET%         %C_CYAN%║%C_RESET%
echo  %C_CYAN%╚════════════════════════════════════════════════════════════════════════════════════════╝%C_RESET%
echo.
echo   %C_BOLD%Служба Windows:%C_RESET% !SVC_BADGE!    %C_BOLD%Профиль:%C_RESET% !PROF_BADGE!
echo  %C_DIM%──────────────────────────────────────────────────────────────────────────────────────────%C_RESET%
echo.
echo   %C_CYAN%[ БЫСТРЫЙ СТАРТ И КОНТРОЛЛЕР ДОНГЛА ]%C_RESET%
echo     %C_BOLD%%C_GREEN%1.%C_RESET% Автопоиск мыши по движению %C_DIM%(5 сек тест -^> автонастройка VID/PID и донгла)%C_RESET%
echo     %C_BOLD%%C_GREEN%2.%C_RESET% Контроллер донгла %C_DIM%(прямое переключение 125-1000 Гц и HID-инспектор без OEM-софта)%C_RESET%
echo     %C_BOLD%%C_GREEN%3.%C_RESET% Замерить реальную частоту опроса мыши %C_DIM%(10 сек тест интервалов WM_INPUT)%C_RESET%
echo.
echo   %C_CYAN%[ ПРИМЕНЕНИЕ И НАСТРОЙКА ПАРАМЕТРОВ ]%C_RESET%
echo     %C_BOLD%%C_YELLOW%4.%C_RESET% Безопасное применение твика %C_DIM%(с автооткатом при сбое за 25 сек)%C_RESET%
echo     %C_BOLD%%C_YELLOW%5.%C_RESET% Применить сохранённые настройки сейчас %C_DIM%(без ожидания перезагрузки)%C_RESET%
echo     %C_BOLD%%C_YELLOW%6.%C_RESET% Откатить последнее изменение %C_DIM%(вернуть предыдущее значение)%C_RESET%
echo.
echo   %C_CYAN%[ СИСТЕМНАЯ СЛУЖБА И ДИАГНОСТИКА ]%C_RESET%
echo     %C_BOLD%%C_BLUE%7.%C_RESET% Установить / обновить службу Windows %C_DIM%(автозапуск и контроль USB-подключений)%C_RESET%
echo     %C_BOLD%%C_BLUE%8.%C_RESET% Удалить службу Windows %C_DIM%(профиль и логи сохраняются)%C_RESET%
echo     %C_BOLD%%C_BLUE%9.%C_RESET% Сканировать подключённые HID / USB устройства %C_DIM%(список VID/PID)%C_RESET%
echo     %C_BOLD%%C_BLUE%S.%C_RESET% Подробный статус службы, схемы питания и журнал логов
echo     %C_BOLD%%C_BLUE%E.%C_RESET% Открыть profile.json в Блокноте %C_DIM%(ручное редактирование)%C_RESET%
echo.
echo  %C_DIM%──────────────────────────────────────────────────────────────────────────────────────────%C_RESET%
echo     %C_BOLD%%C_RED%0.%C_RESET% Выход
echo.

set "CHOICE="
set /p "CHOICE=  %C_BOLD%%C_WHITE%Выберите действие [0-9, S, E]: %C_RESET%"

if /i "%CHOICE%"=="1" call :RUN_PS autodetect & goto MAIN_MENU
if /i "%CHOICE%"=="2" call :RUN_PS dongle     & goto MAIN_MENU
if /i "%CHOICE%"=="3" call :RUN_PS measure    & goto MAIN_MENU
if /i "%CHOICE%"=="4" call :RUN_PS safeapply  & goto MAIN_MENU
if /i "%CHOICE%"=="5" call :RUN_PS applynow   & goto MAIN_MENU
if /i "%CHOICE%"=="6" call :RUN_PS rollback   & goto MAIN_MENU
if /i "%CHOICE%"=="7" call :RUN_PS install    & goto MAIN_MENU
if /i "%CHOICE%"=="8" call :RUN_PS uninstall  & goto MAIN_MENU
if /i "%CHOICE%"=="9" call :RUN_PS scan       & goto MAIN_MENU
if /i "%CHOICE%"=="S" call :RUN_PS status     & goto MAIN_MENU
if /i "%CHOICE%"=="E" call :RUN_PS editprofile & goto MAIN_MENU
if "%CHOICE%"=="0" exit /b 0

echo.
echo  %C_YELLOW%[!] Неизвестный пункт меню. Повторите ввод.%C_RESET%
timeout /t 1 >nul
goto MAIN_MENU

:REFRESH_STATUS
set "SVC_BADGE=%C_RED%● НЕ УСТАНОВЛЕНА%C_RESET%"
sc query "%SVC_NAME%" >nul 2>&1
if %errorlevel%==0 (
    sc query "%SVC_NAME%" | findstr /i "RUNNING" >nul 2>&1
    if !errorlevel!==0 (
        set "SVC_BADGE=%C_GREEN%● ЗАПУЩЕНА (%SVC_NAME%)%C_RESET%"
    ) else (
        set "SVC_BADGE=%C_YELLOW%● ОСТАНОВЛЕНА (%SVC_NAME%)%C_RESET%"
    )
)

if exist "%PROFILE_FILE%" (
    set "PROF_BADGE=%C_GREEN%✓ Найден%C_RESET%"
) else (
    set "PROF_BADGE=%C_DIM%○ Будет создан при запуске%C_RESET%"
)
exit /b 0

:RUN_PS
set "TARGET_MODE=%~1"
cls
echo.
echo  %C_CYAN%──────────────────────────────────────────────────────────────────────────────────────────%C_RESET%
echo   %C_BOLD%%C_WHITE%ВЫПОЛНЕНИЕ РЕЖИМА:%C_RESET% %C_CYAN%%TARGET_MODE%%C_RESET%
echo  %C_CYAN%──────────────────────────────────────────────────────────────────────────────────────────%C_RESET%
echo.

"%PS_EXE%" -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" -Mode %TARGET_MODE%
set "RC=%errorlevel%"

echo.
echo  %C_DIM%──────────────────────────────────────────────────────────────────────────────────────────%C_RESET%
if "%RC%"=="0" (
    echo  %C_GREEN%[OK] Операция завершена успешно.%C_RESET%
) else (
    echo  %C_RED%[!] Операция завершилась с кодом: %RC%%C_RESET%
)
echo.
pause
exit /b 0
