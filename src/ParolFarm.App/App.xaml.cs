using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Threading;

namespace ParolFarm.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandled;
        // Zahlen in Eingabefeldern im deutschen Format (Komma)
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(CultureInfo.CurrentCulture.IetfLanguageTag)));
        base.OnStartup(e);
    }

    static void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.ToString(), "Unerwarteter Fehler", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = Brushes.Green;
    public Brush FalseBrush { get; set; } = Brushes.Gray;
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

/// <summary>Vergleicht einen int mit einem festen Wert (für RadioButtons).</summary>
public sealed class IntEqualsConverter : IValueConverter
{
    public int Target { get; init; }
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is int i && i == Target;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? Target : Binding.DoNothing;
}

public static class Converters
{
    public static readonly IntEqualsConverter EqualsOne = new() { Target = 1 };
    public static readonly IntEqualsConverter EqualsTwo = new() { Target = 2 };
}
