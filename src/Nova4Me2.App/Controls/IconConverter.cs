using System.Globalization;
using System.Windows.Data;
using Nova4Me2.App.Services;

namespace Nova4Me2.App.Controls;

/// <summary>Lets XAML write Data="{Binding Source=help, Converter={x:Static c:IconConverter.Instance}}".</summary>
public sealed class IconConverter : IValueConverter
{
    public static readonly IconConverter Instance = new();
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => Icons.Get(value?.ToString() ?? "info");
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
