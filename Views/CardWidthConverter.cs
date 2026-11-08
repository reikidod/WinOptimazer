using System.Globalization;
using System.Windows.Data;

namespace WinOptimizer.Views;

/// <summary>
/// Ширина карточки так, чтобы ряд заполнял всю доступную ширину без пустого места справа:
/// число колонок подбирается по ширине области, а карточки растягиваются поровну.
/// </summary>
public sealed class CardWidthConverter : IValueConverter
{
    private const double MinCard = 300;
    private const double Reserved = -12; // сетка шире области на 12: это правый отступ карточек, он не должен съедать место

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not double width || double.IsNaN(width) || width <= 0)
            return MinCard;

        var available = Math.Max(MinCard, width - Reserved);
        var columns = Math.Max(1, (int)Math.Floor(available / MinCard));
        return Math.Floor(available / columns);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
