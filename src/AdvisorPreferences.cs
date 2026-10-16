namespace SeaPowerAIAdvisor;

// Commit both tabs together. A profile write failure must not leave new connection settings active.
internal static class AdvisorPreferences
{
    internal static AdvisorProfile Save(AdvisorProfileDraft profile, AdvisorSettingsDraft connection,
        string profileDirectory, string connectionPath)
    {
        var data = connection.Data();
        _ = profile.Build();
        var before = AdvisorSettings.Backup(connectionPath);
        AdvisorSettings.WriteBytes(connectionPath, data);
        try { return profile.Save(profileDirectory); }
        catch
        {
            try { AdvisorSettings.Restore(connectionPath, before); }
            catch { throw new AdvisorConnectionException("settings_rollback_failed"); }
            throw;
        }
    }
}
