// Copyright (c) 2026 FoxCouncil & Contributors - https://github.com/FoxCouncil/NAPLPS

using Avalonia.Data;
using Avalonia.Data.Converters;
using System.Globalization;

namespace NAPLPSApp.Converters;

public abstract class EqualBaseConverter : IValueConverter
{
    public virtual object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // One-way binding, so no need to implement
        return BindingOperations.DoNothing;
    }

    internal object ConvertInternal(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (parameter == null)
        {
            // If the parameter is null, handle accordingly
            return value == null;
        }

        if (value is string stringValue)
        {
            return stringValue.Equals(parameter.ToString(), StringComparison.Ordinal);
        }

        if (value is int intValue && int.TryParse(parameter.ToString(), out int intParameter))
        {
            return intValue == intParameter;
        }

        if (value is uint uintValue && uint.TryParse(parameter.ToString(), out uint uintParameter))
        {
            return uintValue == uintParameter;
        }

        if (value is Enum enumValue && Enum.TryParse(value.GetType(), parameter.ToString(), out var enumParameter))
        {
            return enumValue.Equals(enumParameter);
        }

        return Equals(value, parameter);
    }
}
