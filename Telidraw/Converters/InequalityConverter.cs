// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using System.Globalization;

namespace NAPLPSApp.Converters;

public class InequalityConverter : EqualBaseConverter
{
    public override object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !(bool)ConvertInternal(value, targetType, parameter, culture);
    }
}
