using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace HidusbfModernGui
{
    // Maps a tested StatusLevel to a brush. The XAML never picks a colour itself;
    // it asks this, which asks PollingCore.
    //
    // BrushFor is the single place this mapping exists. Code-behind calls it
    // directly; XAML goes through Convert. Do not re-implement the switch anywhere
    // else — two copies of a colour rule is how a colour rule starts lying.
    public class StatusLevelToBrushConverter : IValueConverter
    {
        public static Brush BrushFor(StatusLevel level)
        {
            string key = level switch
            {
                StatusLevel.Ok => "StatusOkBrush",
                StatusLevel.Warn => "StatusWarnBrush",
                StatusLevel.Error => "StatusErrorBrush",
                _ => "TextMutedBrush"
            };
            return Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;
        }

        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
            => BrushFor(value is StatusLevel level ? level : StatusLevel.Idle);

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}
