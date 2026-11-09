namespace WinOptimizer.Tweaks;

/// <summary>Насколько твик подтверждён измерениями (а не пересказом чужих гайдов).</summary>
public enum EvidenceLevel
{
    /// <summary>Есть замеры в независимых тестах.</summary>
    Measured,

    /// <summary>Есть техническое обоснование, но не найдено бенчмарков.</summary>
    Reasoned,

    /// <summary>Количественных данных найти не удалось: эффект не гарантирован.</summary>
    NoData,
}

/// <summary>Уровень риска изменения для UI (0.6). Не заменяет EvidenceLevel.</summary>
public enum RiskLevel
{
    Low,
    Medium,
    High,
}

/// <summary>Описание твика для интерфейса.</summary>
public sealed record TweakInfo(
    string Id,
    string Title,
    string Description,
    EvidenceLevel Evidence,
    string EvidenceNote,
    bool RequiresReboot,
    string? Warning = null,
    string Category = "Система",
    /// <summary>Общий ключ группы (например firewall). Дочерние скрыты, пока группа свёрнута.</summary>
    string? GroupKey = null,
    /// <summary>True — заголовок группы со стрелкой; False при GroupKey — дочерний пункт.</summary>
    bool IsGroupHeader = false)
{
    /// <summary>Риск выводится из Warning + reboot, без правок всего каталога.</summary>
    public RiskLevel Risk =>
        !string.IsNullOrWhiteSpace(Warning) && RequiresReboot ? RiskLevel.High
        : !string.IsNullOrWhiteSpace(Warning) || RequiresReboot ? RiskLevel.Medium
        : RiskLevel.Low;
}

public interface ITweak
{
    TweakInfo Info { get; }

    /// <summary>Меняет системные параметры (HKLM, схема питания): перед применением предлагается точка восстановления.</summary>
    bool RequiresRestorePoint { get; }

    bool IsApplied();

    /// <summary>Применяет твик. При ошибке не оставляет систему в полупримененном состоянии.</summary>
    void Apply();

    /// <summary>Возвращает значения, сохранённые до применения (или значения Windows по умолчанию).</summary>
    void Revert();
}
