using System.Globalization;
using System.Windows.Data;

namespace PicHarbor.Gui.ViewModels;

public class EnumToBoolConverter : IValueConverter
{
    public static readonly EnumToBoolConverter Instance = new();

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value == null || parameter == null) return false;
        return value.ToString() == parameter.ToString();
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is bool boolVal && boolVal && parameter != null)
        {
            if (int.TryParse(parameter.ToString(), out int intVal))
            {
                return intVal;
            }
        }
        return Binding.DoNothing;
    }
}
