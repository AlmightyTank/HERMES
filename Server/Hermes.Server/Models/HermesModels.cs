namespace Hermes.Server.Models;

public sealed record HermesStatusResponse(
    string Name,
    string Version,
    string SptVersion,
    bool ReadOnly,
    string[] Capabilities);

public sealed record HermesSearchResponse(
    string Query,
    int TotalMatches,
    IReadOnlyList<HermesItemSummary> Results);

public sealed record HermesItemSummary(
    string ItemKey,
    string Name,
    string ShortName,
    long? ReferencePrice,
    bool AppearsInTraderData,
    bool AppearsInHideoutData,
    bool AppearsInQuestData);

public sealed record HermesTraderSummaryResponse(
    bool Found,
    string? Message,
    string ItemKey,
    string Name,
    string ShortName,
    long? ReferencePrice,
    bool HasSupportedTraderBuyer,
    HermesSellOffer? BestSellOffer,
    IReadOnlyList<HermesSellOffer> SellOffers,
    IReadOnlyList<HermesPurchaseOffer> PurchaseOffers);

public sealed record HermesSellOffer(
    string TraderName,
    int PlayerLoyaltyLevel,
    long Amount,
    string Currency,
    long RoubleEquivalent,
    bool IsBest);

public sealed record HermesPurchaseOffer(
    string TraderName,
    int RequiredLoyaltyLevel,
    int PlayerLoyaltyLevel,
    bool IsAvailable,
    string AvailabilityReason,
    string? RequiredQuestName,
    string? RequiredQuestState,
    string? QuestRequirementText,
    bool UnlimitedStock,
    int? StockRemaining,
    int? PurchaseLimit,
    int? PurchaseLimitRemaining,
    int PackSize,
    long? SecondsUntilRestock,
    IReadOnlyList<HermesPaymentOption> PaymentOptions);

public sealed record HermesPaymentOption(
    bool IsCash,
    string DisplayPrice,
    long EstimatedRoubleValue,
    IReadOnlyList<HermesPaymentRequirement> Requirements);

public sealed record HermesPaymentRequirement(
    string Name,
    double Count,
    string? Currency);

public sealed record HermesMarketSummaryResponse(
    bool Found,
    string? Message,
    string ItemKey,
    string Name,
    bool FleaUnlocked,
    int PlayerLevel,
    int RequiredPlayerLevel,
    bool CanSellOnFlea,
    string? SellUnavailableReason,
    int ValidCashOfferCount,
    int ComparableOfferCount,
    int IgnoredBarterOfferCount,
    int IgnoredTraderOfferCount,
    int IgnoredExpiredOrInvalidOfferCount,
    int IgnoredLowConditionOfferCount,
    int IgnoredOutlierCount,
    bool UsedLowConditionFallback,
    long? LowestPrice,
    long? MedianPrice,
    long? AveragePrice,
    long? HighestReasonablePrice,
    long? SuggestedListPrice,
    long? EstimatedListingFee,
    long? EstimatedNetSale,
    long? CheapestAvailableTraderBuyPrice,
    string? CheapestAvailableTraderName,
    long? BestTraderSellPrice,
    string? BestTraderSellName,
    string BuyRecommendation,
    string SellRecommendation,
    IReadOnlyList<HermesFleaOfferSample> LowestOffers);

public sealed record HermesFleaOfferSample(
    long UnitPrice,
    int Quantity,
    int ConditionPercent,
    string ConditionLabel,
    long SecondsRemaining);
