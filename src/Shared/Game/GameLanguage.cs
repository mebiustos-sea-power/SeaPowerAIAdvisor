using System;
using SeaPower;

namespace SeaPowerAIAdvisor.Shared;

internal static class GameLanguage
{
    internal static string Read()
    {
        // The singleton getter creates an instance when missing. Never create settings during capture.
        if (!LanguageResourceHandler.InstanceExists(false))
            throw new InvalidOperationException("The game's language handler is not initialized.");
        return LanguageCode.Validate(LanguageResourceHandler.Instance.Language);
    }

}
