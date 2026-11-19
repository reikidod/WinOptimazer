using System.Text.Json;

namespace WinOptimizer.Services;

/// <summary>Validates the supported InputLatency profile contract before it can be saved.</summary>
internal static class DiagnosticProfileValidator
{
    private static readonly HashSet<int> SupportedRates = [125, 250, 500, 1000, 2000, 4000, 8000];

    internal static void Validate(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Корнем профиля должен быть JSON-объект.");

        ValidateOptionalInteger(root, "ConfirmTimeoutSec", 1, 120);
        ValidateOptionalInteger(root, "SettleSec", 0, 30);
        ValidateOptionalBoolean(root, "AutoApplyOnServiceStart");
        ValidateOptionalBoolean(root, "WatchUsbReconnect");

        if (!root.TryGetProperty("Tweaks", out var tweaks) || tweaks.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Поле Tweaks обязательно и должно быть массивом.");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tweak in tweaks.EnumerateArray())
        {
            if (tweak.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("Каждый элемент Tweaks должен быть JSON-объектом.");

            var name = RequiredString(tweak, "Name");
            if (!names.Add(name))
                throw new InvalidDataException($"Название твика «{name}» повторяется.");

            var type = RequiredString(tweak, "Type");
            if (type is not ("usb-suspend" or "polling"))
                throw new InvalidDataException($"Тип твика «{type}» не поддерживается.");

            if (!tweak.TryGetProperty("Enabled", out var enabled) ||
                enabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException($"Для твика «{name}» поле Enabled должно быть логическим.");

            if (tweak.TryGetProperty("Target", out var target))
            {
                if (target.ValueKind != JsonValueKind.String ||
                    target.GetString() is not ("both" or "mouse" or "keyboard"))
                    throw new InvalidDataException($"Для твика «{name}» Target должен быть both, mouse или keyboard.");
            }

            if (type == "polling")
                ValidatePollingTweak(tweak, name);
        }
    }

    private static void ValidatePollingTweak(JsonElement tweak, string name)
    {
        var method = RequiredString(tweak, "Method");
        if (method is not ("hid-report" or "command"))
            throw new InvalidDataException($"Для твика «{name}» Method должен быть hid-report или command.");

        if (tweak.TryGetProperty("DefaultHz", out var defaultHz) &&
            (!defaultHz.TryGetInt32(out var rate) || rate is < 125 or > 32000))
            throw new InvalidDataException($"Для твика «{name}» DefaultHz должен быть в диапазоне 125–32000.");

        if (tweak.TryGetProperty("Levels", out var levels))
        {
            if (levels.ValueKind != JsonValueKind.Array || levels.GetArrayLength() == 0)
                throw new InvalidDataException($"Для твика «{name}» Levels должен быть непустым массивом.");
            foreach (var level in levels.EnumerateArray())
            {
                if (!level.TryGetInt32(out var rate) || rate is < 125 or > 32000)
                    throw new InvalidDataException($"Для твика «{name}» Levels содержит недопустимую частоту.");
            }
        }

        foreach (var id in new[] { "Vid", "Pid" })
        {
            if (tweak.TryGetProperty(id, out var value) &&
                (value.ValueKind != JsonValueKind.String ||
                 (value.GetString() is { Length: > 0 } text && !System.Text.RegularExpressions.Regex.IsMatch(text, "^[0-9A-Fa-f]{4}$"))))
                throw new InvalidDataException($"Для твика «{name}» {id} должен содержать четыре шестнадцатеричных символа.");
        }

        if (tweak.TryGetProperty("ReportsByHz", out var reports))
        {
            if (reports.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException($"Для твика «{name}» ReportsByHz должен быть объектом.");
            foreach (var report in reports.EnumerateObject())
            {
                if (!int.TryParse(report.Name, out var rate) || rate is < 125 or > 32000 ||
                    report.Value.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException($"Для твика «{name}» обнаружен некорректный HID-репорт.");
                var hex = report.Value.GetString();
                if (!string.IsNullOrEmpty(hex) &&
                    (hex.Length % 2 != 0 || !System.Text.RegularExpressions.Regex.IsMatch(hex, "^[0-9A-Fa-f]+$")))
                    throw new InvalidDataException($"Для твика «{name}» HID-репорт должен быть HEX-строкой чётной длины.");
            }
        }
    }

    private static string RequiredString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value) ||
            value.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Поле {property} обязательно и должно быть непустой строкой.");
        return value.GetString()!;
    }

    private static void ValidateOptionalInteger(JsonElement root, string property, int min, int max)
    {
        if (root.TryGetProperty(property, out var value) &&
            (!value.TryGetInt32(out var number) || number < min || number > max))
            throw new InvalidDataException($"Поле {property} должно быть в диапазоне {min}–{max}.");
    }

    private static void ValidateOptionalBoolean(JsonElement root, string property)
    {
        if (root.TryGetProperty(property, out var value) &&
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new InvalidDataException($"Поле {property} должно быть логическим.");
    }
}
