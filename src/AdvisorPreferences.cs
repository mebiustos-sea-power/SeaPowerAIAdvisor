namespace SeaPowerAIAdvisor;

// Publish runtime options only after all saved settings succeed; undo earlier writes on failure.
internal static class AdvisorPreferences
{
    internal static AdvisorProfile Save(AdvisorProfileDraft profile, AdvisorSettingsDraft connection,
        AdvisorOptionsStore options, AdvisorOptions nextOptions, string profileDirectory, string connectionPath)
    {
        var data = connection.Data();
        _ = profile.Build();
        var beforeConnection = AdvisorSettings.Backup(connectionPath);
        var beforeOptions = options.Backup();
        var connectionWritten = false; var optionsWritten = false;
        try
        {
            AdvisorSettings.WriteBytes(connectionPath, data); connectionWritten = true;
            options.Write(nextOptions); optionsWritten = true;
            var saved = profile.Save(profileDirectory);
            options.Apply(nextOptions);
            return saved;
        }
        catch
        {
            var rollbackFailed = false;
            if (optionsWritten)
                try { options.Restore(beforeOptions); } catch { rollbackFailed = true; }
            if (connectionWritten)
                try { AdvisorSettings.Restore(connectionPath, beforeConnection); } catch { rollbackFailed = true; }
            if (rollbackFailed) throw new AdvisorConnectionException("settings_rollback_failed");
            throw;
        }
    }
}
