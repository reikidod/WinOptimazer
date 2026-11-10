using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace WinOptimizer.Views;

/// <summary>Показывает элемент (например, подсказку «список пуст»), только когда в списке нет элементов.</summary>
public sealed class CountToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is int count && count > 0 ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
