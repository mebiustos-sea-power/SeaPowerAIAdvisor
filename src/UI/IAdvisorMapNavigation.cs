namespace SeaPowerAIAdvisor;

internal interface IAdvisorMapNavigation : IAdvisorMap
{
    void Focus(double latitude, double longitude);
}
