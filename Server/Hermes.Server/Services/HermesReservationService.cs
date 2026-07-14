using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;

namespace Hermes.Server.Services;

[Injectable(InjectionType.Singleton)]
public sealed class HermesReservationService(HermesHideoutService hideoutService)
{
    internal HermesStashReservationSnapshot? Build(
        MongoId sessionId,
        IReadOnlyDictionary<string, double> stashInventory,
        IReadOnlyDictionary<string, double> stashFoundInRaidInventory)
    {
        return hideoutService.BuildStashReservations(
            sessionId,
            stashInventory,
            stashFoundInRaidInventory);
    }
}
