using System;
using System.Collections;
using System.Globalization;
using System.Linq;
using System.Reflection;

namespace SeaPowerAIAdvisor;

// Primitive reads shared by the player projection and public-reference adapters.
internal static class BattleValue
{
    internal static object? Member(object value, string name)
    {
        const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
        var type = value.GetType();
        // NodeCanvas hides BBParameter.value with a typed property in BBParameter<T>.
        // Resolve the most-derived declaration rather than asking reflection to choose between both.
        for (var declaration = type; declaration != null; declaration = declaration.BaseType)
        {
            var property = declaration.GetProperty(name, flags | BindingFlags.DeclaredOnly);
            if (property != null) return property.GetValue(value);
        }
        var field = type.GetField(name, flags) ?? throw new MissingMemberException(type.FullName, name);
        return field.GetValue(value);
    }
    internal static object Required(object value, string name) => Member(value, name)
        ?? throw new InvalidOperationException(value.GetType().FullName + "." + name + " is unavailable.");
    internal static object[] Rows(object? value) => (value as IEnumerable
        ?? throw new InvalidOperationException("A battle knowledge collection is unavailable.")).Cast<object>().ToArray();
    internal static double Number(object value)
    {
        var number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
        if (double.IsNaN(number) || double.IsInfinity(number)) throw new InvalidOperationException("Invalid battle knowledge value.");
        return number;
    }
    internal static double Nonnegative(object value)
    {
        var number = Number(value);
        if (number < 0) throw new InvalidOperationException("Negative battle knowledge value.");
        return number;
    }
    internal static string Code(object? value) => value?.ToString()!.ToLowerInvariant()
        ?? throw new InvalidOperationException("A displayed battle classification is missing.");
}
