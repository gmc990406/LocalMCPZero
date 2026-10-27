#nullable enable
namespace ZERO;

interface ISizeTierAware
{
    void ApplySizeTier(string tier, double listCol, double visionList, double shotMin, double gap);
}
