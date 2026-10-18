using System;
using System.Text.RegularExpressions;

namespace SeaPowerAIAdvisor;

internal static class LanguageCode
{
    internal static string Validate(string? code)
    {
        if (code == null || !Regex.IsMatch(code, @"\A[A-Za-z]{2,3}(?:[-_][A-Za-z0-9]{2,8})*\z"))
            throw new InvalidOperationException("The game returned an invalid language code.");
        return code.Replace('_', '-');
    }
}
