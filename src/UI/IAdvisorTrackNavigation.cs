namespace SeaPowerAIAdvisor;

internal interface IAdvisorTrackNavigation
{
    bool Contains(int trackNumber);
    void Focus(int trackNumber);
    void OpenMenu(int trackNumber);
}
