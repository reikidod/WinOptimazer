# Рекомендуемые файлы

Файлы, которые приложение скачивает по кнопке «Установить» в разделе «Установить программы → Рекомендуемые файлы».
Перед запуском проверяется SHA-256 (см. `SHA256SUMS.txt` и `Services/RecommendedFilesService.cs`).

| Файл | Что это |
|------|---------|
| `vc-runtimes-aio-v56.exe` | Visual C++ Runtimes v56 (Burf): 2005–2017, x86 и x64. Неофициальная сборка, не подписана Microsoft. |
| `dxwebsetup.exe` | DirectX 9.0 Web Setup (9.29.1974.0) — официальный установщик Microsoft, подписан. |

Чтобы заменить файл: положите новый сюда, обновите SHA-256 в `Services/RecommendedFilesService.cs` и `SHA256SUMS.txt`.
