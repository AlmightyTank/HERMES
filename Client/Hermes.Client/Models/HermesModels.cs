namespace Hermes.Client.Models;

public sealed class HermesSearchResponse
{
    public string Query { get; set; } = string.Empty;
    public int TotalMatches { get; set; }
    public List<HermesItemSummary> Results { get; set; } = [];
}

public sealed class HermesItemSummary
{
    public string ItemKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public long? ReferencePrice { get; set; }
    public bool AppearsInTraderData { get; set; }
    public bool AppearsInHideoutData { get; set; }
    public bool AppearsInQuestData { get; set; }
}

public sealed class HermesTraderSummaryResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public string ItemKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public long? ReferencePrice { get; set; }
    public bool HasSupportedTraderBuyer { get; set; }
    public HermesSellOffer? BestSellOffer { get; set; }
    public List<HermesSellOffer> SellOffers { get; set; } = [];
    public List<HermesPurchaseOffer> PurchaseOffers { get; set; } = [];
}

public sealed class HermesSellOffer
{
    public string TraderName { get; set; } = string.Empty;
    public int PlayerLoyaltyLevel { get; set; }
    public long Amount { get; set; }
    public string Currency { get; set; } = "RUB";
    public long RoubleEquivalent { get; set; }
    public bool IsBest { get; set; }
}

public sealed class HermesPurchaseOffer
{
    public string TraderName { get; set; } = string.Empty;
    public int RequiredLoyaltyLevel { get; set; }
    public int PlayerLoyaltyLevel { get; set; }
    public bool IsAvailable { get; set; }
    public string AvailabilityReason { get; set; } = string.Empty;
    public string? RequiredQuestName { get; set; }
    public string? RequiredQuestState { get; set; }
    public string? QuestRequirementText { get; set; }
    public bool UnlimitedStock { get; set; }
    public int? StockRemaining { get; set; }
    public int? PurchaseLimit { get; set; }
    public int? PurchaseLimitRemaining { get; set; }
    public int PackSize { get; set; } = 1;
    public long? SecondsUntilRestock { get; set; }
    public List<HermesPaymentOption> PaymentOptions { get; set; } = [];
}

public sealed class HermesPaymentOption
{
    public bool IsCash { get; set; }
    public string DisplayPrice { get; set; } = string.Empty;
    public long EstimatedRoubleValue { get; set; }
    public List<HermesPaymentRequirement> Requirements { get; set; } = [];
}

public sealed class HermesPaymentRequirement
{
    public string Name { get; set; } = string.Empty;
    public double Count { get; set; }
    public string? Currency { get; set; }
}

public sealed class HermesMarketSummaryResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public string ItemKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool FleaUnlocked { get; set; }
    public int PlayerLevel { get; set; }
    public int RequiredPlayerLevel { get; set; }
    public bool CanSellOnFlea { get; set; }
    public string? SellUnavailableReason { get; set; }
    public int ValidCashOfferCount { get; set; }
    public int ComparableOfferCount { get; set; }
    public int IgnoredBarterOfferCount { get; set; }
    public int IgnoredTraderOfferCount { get; set; }
    public int IgnoredExpiredOrInvalidOfferCount { get; set; }
    public int IgnoredLowConditionOfferCount { get; set; }
    public int IgnoredOutlierCount { get; set; }
    public bool UsedLowConditionFallback { get; set; }
    public long? LowestPrice { get; set; }
    public long? MedianPrice { get; set; }
    public long? AveragePrice { get; set; }
    public long? HighestReasonablePrice { get; set; }
    public long? SuggestedListPrice { get; set; }
    public long? EstimatedListingFee { get; set; }
    public long? EstimatedNetSale { get; set; }
    public long? CheapestAvailableTraderBuyPrice { get; set; }
    public string? CheapestAvailableTraderName { get; set; }
    public long? BestTraderSellPrice { get; set; }
    public string? BestTraderSellName { get; set; }
    public string BuyRecommendation { get; set; } = string.Empty;
    public string SellRecommendation { get; set; } = string.Empty;
    public List<HermesFleaOfferSample> LowestOffers { get; set; } = [];
}

public sealed class HermesFleaOfferSample
{
    public long UnitPrice { get; set; }
    public int Quantity { get; set; }
    public int ConditionPercent { get; set; }
    public string ConditionLabel { get; set; } = string.Empty;
    public long SecondsRemaining { get; set; }
}
