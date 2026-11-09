using WinOptimizer.Infrastructure;
using WinOptimizer.Tweaks;

namespace WinOptimizer.ViewModels;

/// <summary>Карточка одного твика: текущее состояние в системе и желаемое состояние (переключатель).</summary>
public sealed class TweakViewModel : ObservableObject
{
    private bool _isApplied;
    private bool _isSelected;
    private bool _isAvailable = true;
    private string? _errorText;

    public TweakViewModel(ITweak tweak)
    {
        Tweak = tweak ?? throw new ArgumentNullException(nameof(tweak));
    }

    public ITweak Tweak { get; }

    public string Title => Tweak.Info.Title;

    public string Category => Tweak.Info.Category;

    public string? GroupKey => Tweak.Info.GroupKey;

    public bool IsGroupHeader => Tweak.Info.IsGroupHeader;

    public bool IsGroupChild => !string.IsNullOrEmpty(GroupKey) && !IsGroupHeader;

    private bool _isGroupExpanded;

    public bool IsGroupExpanded
    {
        get => _isGroupExpanded;
        set
        {
            if (SetField(ref _isGroupExpanded, value))
                OnPropertyChanged(nameof(GroupArrow));
        }
    }

    public string GroupArrow => IsGroupExpanded ? "▼" : "▶";

    public string Description => Tweak.Info.Description;

    public string? Warning => Tweak.Info.Warning;

    public bool HasWarning => !string.IsNullOrWhiteSpace(Tweak.Info.Warning);

    public string EvidenceNote => Tweak.Info.EvidenceNote;

    public EvidenceLevel Evidence => Tweak.Info.Evidence;

    public string EvidenceLabel => Tweak.Info.Evidence switch
    {
        EvidenceLevel.Measured => "Есть замеры",
        EvidenceLevel.Reasoned => "Только обоснование",
        _ => "Нет данных",
    };

    public RiskLevel Risk => Tweak.Info.Risk;

    public string RiskLabel => Risk switch
    {
        RiskLevel.High => "Высокий риск",
        RiskLevel.Medium => "Средний риск",
        _ => "Низкий риск",
    };

    /// <summary>Текст подсказки «?» при наведении.</summary>
    public string HelpTip
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.AppendLine(Description);
            sb.AppendLine();
            sb.Append("Риск: ").Append(RiskLabel).AppendLine(".");
            sb.Append("Насколько помогает: ").Append(EvidenceLabel).AppendLine(".");
            if (!string.IsNullOrWhiteSpace(EvidenceNote))
                sb.AppendLine(EvidenceNote);
            if (HasWarning)
            {
                sb.AppendLine();
                sb.Append("⚠ ").Append(Warning);
            }
            if (RequiresReboot)
            {
                sb.AppendLine();
                sb.Append("После применения нужна перезагрузка.");
            }
            return sb.ToString().Trim();
        }
    }

    public bool RequiresReboot => Tweak.Info.RequiresReboot;

    public bool RequiresRestorePoint => Tweak.RequiresRestorePoint;

    public bool IsApplied
    {
        get => _isApplied;
        private set
        {
            if (SetField(ref _isApplied, value))
                OnPropertyChanged(nameof(IsChanged));
        }
    }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (SetField(ref _isSelected, value))
                OnPropertyChanged(nameof(IsChanged));
        }
    }

    public bool IsChanged => IsSelected != IsApplied;

    public bool IsAvailable
    {
        get => _isAvailable;
        private set => SetField(ref _isAvailable, value);
    }

    public string? ErrorText
    {
        get => _errorText;
        set
        {
            if (SetField(ref _errorText, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorText);

    public void SetState(bool? applied, string? error)
    {
        IsAvailable = applied.HasValue;
        IsApplied = applied ?? false;
        IsSelected = IsApplied;
        ErrorText = error;
    }
}
