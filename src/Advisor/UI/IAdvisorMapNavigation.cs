namespace SeaPowerAIAdvisor;
using SeaPowerAIAdvisor.Api;

internal interface IAdvisorMapNavigation : IAdvisorMap
{
    void Focus(double latitude, double longitude);
}
