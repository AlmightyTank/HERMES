namespace Hermes.Client.Models;


public sealed class HermesProfileContextResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public string ContextToken { get; set; } = string.Empty;
}

public sealed class HermesProfileSaveResponse
{
    public bool Saved { get; set; }
    public string? Message { get; set; }
    public double DurationSeconds { get; set; }
}

public sealed class HermesActionPreview
{
    public string ActionName { get; set; } = string.Empty;
    public List<string> AffectedItems { get; set; } = [];
    public string Quantity { get; set; } = string.Empty;
    public string PriceOrCost { get; set; } = string.Empty;
    public string TraderStationOrDestination { get; set; } = string.Empty;
    public string ExpectedResult { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = [];
    public string? CannotExecuteReason { get; set; }
}

public sealed class HermesActionProposal
{
    public string ProposalId { get; set; } = string.Empty;
    public string ConfirmationToken { get; set; } = string.Empty;
    public string ActionKind { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public bool CanExecute { get; set; }
    public bool IsHarmlessTestAction { get; set; }
    public bool RequiresConfirmation { get; set; }
    public long CreatedUnixTime { get; set; }
    public long ExpiresUnixTime { get; set; }
    public int ExpiresInSeconds { get; set; }
    public HermesActionPreview Preview { get; set; } = new();
}

public sealed class HermesActionProposalResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public HermesActionProposal? Proposal { get; set; }
}

public sealed class HermesActionResultResponse
{
    public bool Found { get; set; }
    public bool Executed { get; set; }
    public bool Cancelled { get; set; }
    public bool Expired { get; set; }
    public bool Duplicate { get; set; }
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public HermesActionProposal? Proposal { get; set; }
    public HermesActionHistoryEntry? HistoryEntry { get; set; }
}

public sealed class HermesActionHistoryResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public int TotalActions { get; set; }
    public List<HermesActionHistoryEntry> Entries { get; set; } = [];
}

public sealed class HermesActionHistoryEntry
{
    public string HistoryId { get; set; } = string.Empty;
    public string ProposalId { get; set; } = string.Empty;
    public string ActionKind { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public long RequestedUnixTime { get; set; }
    public long ResolvedUnixTime { get; set; }
    public bool Executed { get; set; }
    public bool HarmlessTestAction { get; set; }
    public HermesActionPreview Preview { get; set; } = new();
}

public sealed class HermesCacheStatusResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public int MarketUnitValueEntryCount { get; set; }
    public int MarketSummaryEntryCount { get; set; }
    public int StashAnalysisEntryCount { get; set; }
    public int LoadoutAnalysisEntryCount { get; set; }
    public long CacheHits { get; set; }
    public long CacheMisses { get; set; }
    public long CacheWrites { get; set; }
    public long StashCacheHits { get; set; }
    public long StashCacheMisses { get; set; }
    public long StashCacheWrites { get; set; }
    public long LoadoutCacheHits { get; set; }
    public long LoadoutCacheMisses { get; set; }
    public long LoadoutCacheWrites { get; set; }
    public long Generation { get; set; }
    public int TtlSeconds { get; set; }
    public int StashTtlSeconds { get; set; }
    public int LoadoutTtlSeconds { get; set; }
    public long? OldestEntryAgeSeconds { get; set; }
    public long? NewestEntryAgeSeconds { get; set; }
    public string LastInvalidationReason { get; set; } = string.Empty;
    public long? LastInvalidatedUnixTime { get; set; }
}

public sealed class HermesCacheClearResponse
{
    public bool Cleared { get; set; }
    public string Message { get; set; } = string.Empty;
    public HermesCacheStatusResponse Status { get; set; } = new();
}

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


public sealed class HermesItemSelectionResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public HermesItemSummary? Item { get; set; }
}

public sealed class HermesStashInstancesResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public string ItemKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public List<HermesStashInstanceSummary> Instances { get; set; } = [];
}

public sealed class HermesStashInstanceSelectionResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public HermesItemSummary? Item { get; set; }
    public HermesStashInstanceSummary? Instance { get; set; }
    public string? InventoryLocation { get; set; }
}

public sealed class HermesStashInstanceSummary
{
    public string InstanceKey { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string Location { get; set; } = string.Empty;
    public double Quantity { get; set; }
    public int ConditionPercent { get; set; }
    public string ConditionDescription { get; set; } = string.Empty;
    public string ConditionKind { get; set; } = string.Empty;
    public double ConditionCurrent { get; set; }
    public double ConditionMaximum { get; set; }
    public bool FoundInRaid { get; set; }
    public int ChildItemCount { get; set; }
    public int WeaponAttachmentCount { get; set; }
    public int ArmorInsertCount { get; set; }
    public long RootConditionAdjustedReferenceValue { get; set; }
    public long InstalledComponentReferenceValue { get; set; }
    public long ConditionAdjustedReferenceValue { get; set; }
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
    public bool UsesSelectedStashInstance { get; set; }
    public string? SelectedInstanceKey { get; set; }
    public string? SelectedInstanceLabel { get; set; }
    public int? SelectedInstanceConditionPercent { get; set; }
    public double SelectedInstanceQuantity { get; set; } = 1d;
    public int SelectedInstanceChildItemCount { get; set; }
    public int SelectedInstanceWeaponAttachmentCount { get; set; }
    public int SelectedInstanceArmorInsertCount { get; set; }
    public long? SelectedInstanceRootReferenceValue { get; set; }
    public long? SelectedInstanceInstalledReferenceValue { get; set; }
    public long? SelectedInstanceReferenceValue { get; set; }
    public string SalePriceBasis { get; set; } = string.Empty;
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
    public long RootRoubleEquivalent { get; set; }
    public long InstalledComponentRoubleEquivalent { get; set; }
    public int IncludedInstalledItemCount { get; set; }
    public int IncludedWeaponAttachmentCount { get; set; }
    public int IncludedArmorInsertCount { get; set; }
    public int IgnoredInstalledItemCount { get; set; }
    public long IgnoredInstalledReferenceValue { get; set; }
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
    public string EstimateSource { get; set; } = string.Empty;
    public bool UsedHandbookFallback { get; set; }
    public bool EstimateAvailable { get; set; }
    public List<HermesPaymentRequirement> Requirements { get; set; } = [];
}

public sealed class HermesPaymentRequirement
{
    public string Name { get; set; } = string.Empty;
    public double Count { get; set; }
    public string? Currency { get; set; }
    public long? EstimatedUnitRoubleValue { get; set; }
    public long? EstimatedSubtotalRoubleValue { get; set; }
    public string EstimateSource { get; set; } = string.Empty;
    public bool UsedHandbookFallback { get; set; }
    public bool EstimateAvailable { get; set; }
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
    public int ConvertedBarterOfferCount { get; set; }
    public int BarterOffersUsingHandbookFallback { get; set; }
    public int ComparableOfferCount { get; set; }
    public int IgnoredBarterOfferCount { get; set; }
    public int IgnoredTraderOfferCount { get; set; }
    public int IgnoredExpiredOrInvalidOfferCount { get; set; }
    public int IgnoredLowConditionOfferCount { get; set; }
    public int IgnoredOutlierCount { get; set; }
    public bool UsedLowConditionFallback { get; set; }
    public int OffersWithInstalledComponents { get; set; }
    public string MarketPriceSource { get; set; } = string.Empty;
    public bool MarketPriceFromActiveOffers { get; set; }
    public bool MarketPriceUsedHandbookFallback { get; set; }
    public long? LowestListedPrice { get; set; }
    public bool LowestOfferIsBarter { get; set; }
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
    public bool UsesSelectedOwnedCopy { get; set; }
    public string? SelectedOwnedCopyKey { get; set; }
    public string? SelectedOwnedCopyLabel { get; set; }
    public string? SelectedOwnedCopyLocation { get; set; }
    public long? SelectedOwnedCopyRootValue { get; set; }
    public long? SelectedOwnedCopyChildValue { get; set; }
    public long? SelectedOwnedCopyReferenceValue { get; set; }
    public string BuyRecommendation { get; set; } = string.Empty;
    public string SellRecommendation { get; set; } = string.Empty;
    public List<HermesFleaOfferSample> LowestOffers { get; set; } = [];
}

public sealed class HermesFleaOfferSample
{
    public bool IsBarter { get; set; }
    public string PriceSource { get; set; } = string.Empty;
    public int BarterRequirementCount { get; set; }
    public bool UsedHandbookFallback { get; set; }
    public long UnitPrice { get; set; }
    public long ListedUnitPrice { get; set; }
    public long InstalledComponentValue { get; set; }
    public int WeaponAttachmentCount { get; set; }
    public int ArmorInsertCount { get; set; }
    public int Quantity { get; set; }
    public int ConditionPercent { get; set; }
    public string ConditionLabel { get; set; } = string.Empty;
    public long SecondsRemaining { get; set; }
}

public sealed class HermesHideoutSummaryResponse
{
    public bool Found { get; set; }
    public long ContentRevision { get; set; }
    public string? Message { get; set; }
    public int ReadyAreaCount { get; set; }
    public int MaterialBlockedAreaCount { get; set; }
    public int ProgressionBlockedAreaCount { get; set; }
    public List<HermesHideoutAreaSummary> Areas { get; set; } = [];
    public List<HermesActiveProductionSummary> ActiveProductions { get; set; } = [];
    public HermesHideoutResourceSummary Resources { get; set; } = new();
}

public sealed class HermesHideoutAreaSummary
{
    public string AreaKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int CurrentLevel { get; set; }
    public int MaximumLevel { get; set; }
    public int? TargetLevel { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool IsConstructing { get; set; }
    public long? SecondsUntilComplete { get; set; }
    public int MissingItemTypes { get; set; }
    public long EstimatedMissingHandbookCost { get; set; }
    public List<HermesHideoutItemRequirementSummary> RequiredItems { get; set; } = [];
}

public sealed class HermesHideoutItemRequirementSummary
{
    public string ItemTemplateId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double Required { get; set; }
    public double Owned { get; set; }
    public double Missing { get; set; }
    public bool IsMet { get; set; }
    public bool FoundInRaidRequired { get; set; }
}

public sealed class HermesHideoutAreaDetailResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public HermesHideoutAreaSummary? Area { get; set; }
    public int ConstructionSeconds { get; set; }
    public List<HermesHideoutRequirement> Requirements { get; set; } = [];
    public long EstimatedMissingAcquisitionCost { get; set; }
}

public sealed class HermesHideoutRequirement
{
    public string Type { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string? ItemTemplateId { get; set; }
    public string? AreaKey { get; set; }
    public double Required { get; set; }
    public double Owned { get; set; }
    public double Missing { get; set; }
    public bool IsMet { get; set; }
    public bool FoundInRaidRequired { get; set; }
    public string? Details { get; set; }
    public string? AcquisitionSource { get; set; }
    public long? UnitPrice { get; set; }
    public long? EstimatedMissingCost { get; set; }
}

public sealed class HermesActiveProductionSummary
{
    public string StationName { get; set; } = string.Empty;
    public string OutputName { get; set; } = string.Empty;
    public string? OutputTemplateId { get; set; }
    public int OutputQuantity { get; set; }
    public bool IsComplete { get; set; }
    public bool IsContinuous { get; set; }
    public long SecondsRemaining { get; set; }
    public string Status { get; set; } = string.Empty;
}

public sealed class HermesHideoutResourceSummary
{
    public bool GeneratorActive { get; set; }
    public int FuelContainerCount { get; set; }
    public double FuelResourceRemaining { get; set; }
    public long? EstimatedGeneratorRuntimeSeconds { get; set; }
    public double? FuelCounter { get; set; }
    public double? AirFilterCounter { get; set; }
    public double? WaterFilterCounter { get; set; }
    public int ActiveProductionCount { get; set; }
    public int CompletedProductionCount { get; set; }
}

public sealed class HermesCraftsResponse
{
    public bool Found { get; set; }
    public long ContentRevision { get; set; }
    public string? Message { get; set; }
    public int TotalCrafts { get; set; }
    public List<HermesCraftSummary> Crafts { get; set; } = [];
}

public sealed class HermesCraftSummary
{
    public string CraftKey { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public int CurrentStationLevel { get; set; }
    public int RequiredStationLevel { get; set; }
    public bool StationLevelMet { get; set; }
    public string OutputName { get; set; } = string.Empty;
    public string? OutputTemplateId { get; set; }
    public int OutputQuantity { get; set; }
    public int DurationSeconds { get; set; }
    public bool IsAvailable { get; set; }
    public bool CanStartNow { get; set; }
    public string Status { get; set; } = string.Empty;
    public bool AcquisitionPlanComplete { get; set; }
    public long EstimatedAdditionalCashCost { get; set; }
    public long EstimatedOwnedIngredientValue { get; set; }
    public long EstimatedEconomicInputValue { get; set; }
    public long EstimatedOutputValue { get; set; }
    public long EstimatedCashProfit { get; set; }
    public long EstimatedEconomicProfit { get; set; }
    public long EstimatedEconomicProfitPerHour { get; set; }
    public string? BestTraderName { get; set; }
    public long EstimatedTraderSaleValue { get; set; }
    public long EstimatedTraderProfit { get; set; }
    public long EstimatedTraderProfitPerHour { get; set; }
    public bool FleaUnlocked { get; set; }
    public bool CanSellOnFlea { get; set; }
    public long EstimatedFleaNetSaleValue { get; set; }
    public long EstimatedFleaProfit { get; set; }
    public long EstimatedFleaProfitPerHour { get; set; }
    public string BestSaleSource { get; set; } = string.Empty;
    public long EstimatedBestSaleValue { get; set; }
    public long EstimatedBestSaleProfit { get; set; }
    public long EstimatedBestSaleProfitPerHour { get; set; }
    public bool IsActive { get; set; }
    public bool IsComplete { get; set; }
}

public sealed class HermesCraftDetailResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public HermesCraftSummary? Craft { get; set; }
    public List<HermesCraftIngredient> Ingredients { get; set; } = [];
    public string? RequiredQuestName { get; set; }
    public bool RequiredQuestComplete { get; set; }
    public string ValuationBasis { get; set; } = string.Empty;
}

public sealed class HermesCraftIngredient
{
    public string Name { get; set; } = string.Empty;
    public string TemplateId { get; set; } = string.Empty;
    public string RequirementType { get; set; } = string.Empty;
    public double Required { get; set; }
    public double Owned { get; set; }
    public double OwnedUsed { get; set; }
    public double Missing { get; set; }
    public bool IsMet { get; set; }
    public bool FoundInRaidRequired { get; set; }
    public bool IsReusableTool { get; set; }
    public long? UnitHandbookValue { get; set; }
    public long? OwnedEconomicUnitValue { get; set; }
    public long EstimatedOwnedEconomicValue { get; set; }
    public List<HermesCraftAcquisitionLine> AcquisitionPlan { get; set; } = [];
    public double UnavailableQuantity { get; set; }
    public long EstimatedPurchaseCost { get; set; }
    public bool AcquisitionAvailable { get; set; }
    public string? CostNote { get; set; }
}

public sealed class HermesCraftAcquisitionLine
{
    public string Source { get; set; } = string.Empty;
    public double Quantity { get; set; }
    public long UnitPrice { get; set; }
    public long TotalCost { get; set; }
    public bool IsFallback { get; set; }
}

public sealed class HermesItemHideoutUsageResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public string ItemKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public double OwnedQuantity { get; set; }
    public double OwnedFoundInRaidQuantity { get; set; }
    public List<HermesQuestItemUse> QuestUses { get; set; } = [];
    public List<HermesQuestKeyUse> QuestKeyUses { get; set; } = [];
    public List<HermesUpgradeUse> UpgradeUses { get; set; } = [];
    public List<HermesCraftUse> ProducedBy { get; set; } = [];
    public List<HermesCraftUse> UsedBy { get; set; } = [];
}

public sealed class HermesQuestKeyUse
{
    public string QuestName { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
    public string Opens { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string Acquisition { get; set; } = string.Empty;
    public bool AcquireInRaid { get; set; }
    public string QuestStatus { get; set; } = string.Empty;
    public bool IsActive { get; set; }
    public bool QuestCompleted { get; set; }
}

public sealed class HermesQuestItemUse
{
    public string QuestName { get; set; } = string.Empty;
    public string TraderName { get; set; } = string.Empty;
    public string QuestStatus { get; set; } = string.Empty;
    public string ConditionType { get; set; } = string.Empty;
    public double Required { get; set; }
    public double OwnedMatchingTargets { get; set; }
    public double OwnedSelectedItem { get; set; }
    public double Missing { get; set; }
    public bool FoundInRaidRequired { get; set; }
    public bool ConditionCompleted { get; set; }
    public bool QuestCompleted { get; set; }
    public bool IsActive { get; set; }
    public string ProgressText { get; set; } = string.Empty;
}

public sealed class HermesUpgradeUse
{
    public string AreaName { get; set; } = string.Empty;
    public int CurrentLevel { get; set; }
    public int TargetLevel { get; set; }
    public string Status { get; set; } = string.Empty;
    public double Required { get; set; }
    public double Owned { get; set; }
    public double Missing { get; set; }
    public bool IsMet { get; set; }
    public bool IsNextUpgrade { get; set; }
    public bool FoundInRaidRequired { get; set; }
    public string? AcquisitionSource { get; set; }
    public long? EstimatedMissingCost { get; set; }
}

public sealed class HermesCraftUse
{
    public string CraftKey { get; set; } = string.Empty;
    public string StationName { get; set; } = string.Empty;
    public int CurrentStationLevel { get; set; }
    public int RequiredStationLevel { get; set; }
    public string OutputName { get; set; } = string.Empty;
    public int OutputQuantity { get; set; }
    public int DurationSeconds { get; set; }
    public double ItemCount { get; set; }
    public double Owned { get; set; }
    public double Missing { get; set; }
    public bool IsUnlocked { get; set; }
    public bool CanStartNow { get; set; }
    public bool IsActive { get; set; }
    public bool IsComplete { get; set; }
    public string Status { get; set; } = string.Empty;
}


internal sealed class HermesStashSummaryResponse
{
    public bool Found { get; set; }
    public long ContentRevision { get; set; }
    public string? Message { get; set; }
    public int TotalItemInstances { get; set; }
    public int IndependentItemCount { get; set; }
    public int ValuedIndependentItemCount { get; set; }
    public int UnsupportedIndependentItemCount { get; set; }
    public int OccupiedCells { get; set; }
    public long FullHandbookReferenceValue { get; set; }
    public long ConditionAdjustedHandbookValue { get; set; }
    public long BestTraderLiquidationValue { get; set; }
    public long EstimatedFleaNetValue { get; set; }
    public long BestDestinationLiquidationValue { get; set; }
    public int TraderValuedItemCount { get; set; }
    public int FleaValuedItemCount { get; set; }
    public int NoTraderBuyerItemCount { get; set; }
    public int NoFleaEstimateItemCount { get; set; }
    public int SafeToSellInstanceCount { get; set; }
    public int SellSurplusInstanceCount { get; set; }
    public int KeepInstanceCount { get; set; }
    public int ReviewInstanceCount { get; set; }
    public int DuplicateGroupCount { get; set; }
    public int DamagedOrDepletedItemCount { get; set; }
    public double RecommendedKeepQuantity { get; set; }
    public double PotentiallySellQuantity { get; set; }
    public long PotentialTraderSaleValue { get; set; }
    public long PotentialFleaNetValue { get; set; }
    public long PotentialBestSaleValue { get; set; }
    public long GeneratedUnixTime { get; set; }
    public int CacheTtlSeconds { get; set; }
    public List<HermesStashTraderBreakdown> TraderBreakdown { get; set; } = [];
    public List<HermesStashSaleDestinationBreakdown> SaleDestinationBreakdown { get; set; } = [];
    public List<HermesStashValuationItem> MostValuableItems { get; set; } = [];
    public List<HermesStashValuationItem> Recommendations { get; set; } = [];
    public List<HermesStashDuplicateGroup> DuplicateGroups { get; set; } = [];
    public List<HermesStashConditionItem> DamagedOrDepletedItems { get; set; } = [];
    public int CleanupCandidateInstanceCount { get; set; }
    public int RecoverableCells { get; set; }
    public long CleanupTraderSaleValue { get; set; }
    public long CleanupFleaNetValue { get; set; }
    public long CleanupBestSaleValue { get; set; }
    public List<HermesStashValuationItem> CleanupCandidates { get; set; } = [];
}

internal sealed class HermesStashTraderBreakdown
{
    public string TraderName { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public long RoubleEquivalent { get; set; }
}

internal sealed class HermesStashSaleDestinationBreakdown
{
    public string Destination { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public long RoubleEquivalent { get; set; }
}

internal sealed class HermesStashValuationItem
{
    public string ItemKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public bool FoundInRaid { get; set; }
    public bool IsProtectedCurrency { get; set; }
    public string InstanceKey { get; set; } = string.Empty;
    public string InstanceLabel { get; set; } = string.Empty;
    public double Quantity { get; set; }
    public int ConditionPercent { get; set; }
    public string ConditionDescription { get; set; } = string.Empty;
    public string ConditionKind { get; set; } = string.Empty;
    public double ConditionCurrent { get; set; }
    public double ConditionMaximum { get; set; }
    public int OccupiedCells { get; set; }
    public int InstalledItemCount { get; set; }
    public int ContainedItemCount { get; set; }
    public long FullHandbookReferenceValue { get; set; }
    public long ConditionAdjustedHandbookValue { get; set; }
    public string? BestTraderName { get; set; }
    public long? BestTraderValue { get; set; }
    public bool FleaEstimateAvailable { get; set; }
    public bool FleaEstimateReliable { get; set; }
    public int FleaComparableOfferCount { get; set; }
    public long? EstimatedFleaListPrice { get; set; }
    public long? EstimatedFleaFee { get; set; }
    public long? EstimatedFleaNetValue { get; set; }
    public string FleaEstimateSource { get; set; } = string.Empty;
    public string? BestSaleDestination { get; set; }
    public long? BestSaleValue { get; set; }
    public string Recommendation { get; set; } = string.Empty;
    public double RecommendedKeepQuantity { get; set; }
    public double PotentiallySellQuantity { get; set; }
    public double ActiveQuestReserve { get; set; }
    public double FutureQuestReserve { get; set; }
    public double NextHideoutReserve { get; set; }
    public double FutureHideoutReserve { get; set; }
    public long PotentialTraderSaleValue { get; set; }
    public long PotentialFleaNetValue { get; set; }
    public long PotentialBestSaleValue { get; set; }
    public List<string> Reasons { get; set; } = [];
}

internal sealed class HermesStashDuplicateGroup
{
    public string ItemKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public int InstanceCount { get; set; }
    public double OwnedQuantity { get; set; }
    public double ExplicitlyReservedQuantity { get; set; }
    public double SuggestedReserveQuantity { get; set; }
    public double PotentialExcessQuantity { get; set; }
    public int OccupiedCells { get; set; }
    public string? BestSaleDestination { get; set; }
    public long PotentialExcessSaleValue { get; set; }
    public string Note { get; set; } = string.Empty;
}

internal sealed class HermesStashConditionItem
{
    public string ItemKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string ShortName { get; set; } = string.Empty;
    public string InstanceKey { get; set; } = string.Empty;
    public string InstanceLabel { get; set; } = string.Empty;
    public string ConditionKind { get; set; } = string.Empty;
    public int ConditionPercent { get; set; }
    public double ConditionCurrent { get; set; }
    public double ConditionMaximum { get; set; }
    public int ThresholdPercent { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? BestSaleDestination { get; set; }
    public long? BestSaleValue { get; set; }
    public string Recommendation { get; set; } = string.Empty;
}

internal sealed class HermesLoadoutSummaryResponse
{
    public bool Found { get; set; }
    public long ContentRevision { get; set; }
    public string? Message { get; set; }
    public string Readiness { get; set; } = string.Empty;
    public int ReadinessScore { get; set; }
    public int WarningCount { get; set; }
    public int CriticalCount { get; set; }
    public HermesVitalsSummary Vitals { get; set; } = new();
    public List<HermesLoadoutSlotSummary> EquippedSlots { get; set; } = [];
    public List<HermesWeaponReadiness> Weapons { get; set; } = [];
    public List<HermesArmorReadiness> Armor { get; set; } = [];
    public HermesMedicalReadiness Medical { get; set; } = new();
    public List<HermesQuestLoadoutRequirement> QuestRequirements { get; set; } = [];
    public List<HermesRaidPlanSummary> RaidPlans { get; set; } = [];
    public HermesLoadoutValueSummary ValueSummary { get; set; } = new();
    public List<HermesLoadoutWarning> Warnings { get; set; } = [];
    public long GeneratedUnixTime { get; set; }
}

internal sealed class HermesLoadoutValueSummary
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public long TraderLiquidationValue { get; set; }
    public long MarketReplacementValue { get; set; }
    public long BestReplacementValue { get; set; }
    public long AtRiskReplacementValue { get; set; }
    public long ProtectedReplacementValue { get; set; }
    public long InsuredReplacementValue { get; set; }
    public long UninsuredReplacementValue { get; set; }
    public long? EstimatedInsuranceCost { get; set; }
    public string InsuranceEstimateSource { get; set; } = string.Empty;
    public int ValuedItemCount { get; set; }
    public int UnsupportedItemCount { get; set; }
    public int AtRiskItemCount { get; set; }
    public int ProtectedItemCount { get; set; }
    public int InsuredItemCount { get; set; }
    public int UninsuredItemCount { get; set; }
    public string InsuranceStatus { get; set; } = string.Empty;
    public List<HermesLoadoutValueCategory> Categories { get; set; } = [];
    public List<HermesLoadoutValueItem> Items { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

internal sealed class HermesLoadoutValueCategory
{
    public string Category { get; set; } = string.Empty;
    public int ItemCount { get; set; }
    public long TraderLiquidationValue { get; set; }
    public long MarketReplacementValue { get; set; }
    public long BestReplacementValue { get; set; }
    public long AtRiskReplacementValue { get; set; }
    public long UninsuredReplacementValue { get; set; }
}

internal sealed class HermesLoadoutValueItem
{
    public string ProfileItemId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string SlotName { get; set; } = string.Empty;
    public double Quantity { get; set; }
    public int ConditionPercent { get; set; }
    public string ConditionDescription { get; set; } = string.Empty;
    public long? TraderLiquidationValue { get; set; }
    public string? BestTraderName { get; set; }
    public long? MarketReplacementValue { get; set; }
    public string MarketSource { get; set; } = string.Empty;
    public bool UsedHandbookFallback { get; set; }
    public long? TraderReplacementValue { get; set; }
    public string TraderReplacementSource { get; set; } = string.Empty;
    public long? BestReplacementValue { get; set; }
    public string BestReplacementSource { get; set; } = string.Empty;
    public bool IsAtRisk { get; set; }
    public bool IsProtected { get; set; }
    public bool IsInsurable { get; set; }
    public string InsuranceStatus { get; set; } = string.Empty;
    public bool IsHighValueUninsured { get; set; }
}

internal sealed class HermesVitalsSummary
{
    public double CurrentHealth { get; set; }
    public double MaximumHealth { get; set; }
    public int HealthPercent { get; set; }
    public double CurrentHydration { get; set; }
    public double MaximumHydration { get; set; }
    public int HydrationPercent { get; set; }
    public double CurrentEnergy { get; set; }
    public double MaximumEnergy { get; set; }
    public int EnergyPercent { get; set; }
}

internal sealed class HermesLoadoutSlotSummary
{
    public string SlotName { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public int ConditionPercent { get; set; }
    public string ConditionDescription { get; set; } = string.Empty;
    public int ChildItemCount { get; set; }
    public string Status { get; set; } = string.Empty;
}

internal sealed class HermesWeaponReadiness
{
    public string SlotName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int DurabilityPercent { get; set; }
    public string DurabilityDescription { get; set; } = string.Empty;
    public string Caliber { get; set; } = string.Empty;
    public string? MagazineName { get; set; }
    public int MagazineCapacity { get; set; }
    public double LoadedRounds { get; set; }
    public int CompatibleSpareMagazineCount { get; set; }
    public double SpareMagazineRounds { get; set; }
    public double LooseCompatibleRounds { get; set; }
    public string? LoadedAmmoName { get; set; }
    public bool HasMixedAmmo { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = [];
}

internal sealed class HermesArmorReadiness
{
    public string SlotName { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public int ConditionPercent { get; set; }
    public string ConditionDescription { get; set; } = string.Empty;
    public int MaximumArmorClass { get; set; }
    public int ArmorInsertSlotCount { get; set; }
    public int InstalledArmorInsertCount { get; set; }
    public int MissingRequiredArmorInsertCount { get; set; }
    public int EmptyOptionalArmorInsertCount { get; set; }
    public string Status { get; set; } = string.Empty;
    public List<string> Warnings { get; set; } = [];
}

internal sealed class HermesMedicalReadiness
{
    public int MedicalItemCount { get; set; }
    public double TotalHealingResource { get; set; }
    public bool HasLightBleedTreatment { get; set; }
    public bool HasHeavyBleedTreatment { get; set; }
    public bool HasFractureTreatment { get; set; }
    public bool HasPainTreatment { get; set; }
    public bool HasSurgeryKit { get; set; }
    public int HydrationProvisionCount { get; set; }
    public int EnergyProvisionCount { get; set; }
    public List<HermesCarriedMedicalItem> Items { get; set; } = [];
}

internal sealed class HermesCarriedMedicalItem
{
    public string Name { get; set; } = string.Empty;
    public double CurrentResource { get; set; }
    public double MaximumResource { get; set; }
    public string Coverage { get; set; } = string.Empty;
}

internal sealed class HermesQuestLoadoutRequirement
{
    public string QuestName { get; set; } = string.Empty;
    public string TraderName { get; set; } = string.Empty;
    public string MapName { get; set; } = string.Empty;
    public string RequirementKind { get; set; } = string.Empty;
    public string ConditionType { get; set; } = string.Empty;
    public string RequiredEquipment { get; set; } = string.Empty;
    public double RequiredQuantity { get; set; }
    public double CarriedQuantity { get; set; }
    public double FoundInRaidCarriedQuantity { get; set; }
    public bool FoundInRaidRequired { get; set; }
    public bool IsCompleted { get; set; }
    public bool IsRaidCritical { get; set; }
    public bool AcquireInRaid { get; set; }
    public bool IsSatisfied { get; set; }
    public string Note { get; set; } = string.Empty;
}

internal sealed class HermesRaidPlanSummary
{
    public string MapName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ActiveQuestCount { get; set; }
    public int ObjectiveCount { get; set; }
    public int CompletedObjectiveCount { get; set; }
    public int RaidRequirementCount { get; set; }
    public int MissingRequirementCount { get; set; }
    public List<HermesRaidPlanQuest> Quests { get; set; } = [];
    public List<HermesRaidPlanRequirement> CombinedRequirements { get; set; } = [];
    public List<string> Notes { get; set; } = [];
}

internal sealed class HermesRaidPlanQuest
{
    public string QuestName { get; set; } = string.Empty;
    public string TraderName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int ObjectiveCount { get; set; }
    public int CompletedObjectiveCount { get; set; }
    public int MissingRequirementCount { get; set; }
    public List<HermesRaidPlanObjective> Objectives { get; set; } = [];
}

internal sealed class HermesRaidPlanObjective
{
    public string ConditionType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public bool IsCompleted { get; set; }
    public bool IsRaidObjective { get; set; }
    public string Status { get; set; } = string.Empty;
}

internal sealed class HermesRaidPlanRequirement
{
    public string RequirementKind { get; set; } = string.Empty;
    public string RequiredEquipment { get; set; } = string.Empty;
    public double RequiredQuantity { get; set; }
    public double CarriedQuantity { get; set; }
    public double FoundInRaidCarriedQuantity { get; set; }
    public double MissingQuantity { get; set; }
    public bool FoundInRaidRequired { get; set; }
    public bool AcquireInRaid { get; set; }
    public bool IsSatisfied { get; set; }
    public List<string> QuestNames { get; set; } = [];
    public string Note { get; set; } = string.Empty;
}

internal sealed class HermesLoadoutWarning
{
    public string Severity { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
}

public sealed class HermesAssistantAlertsResponse
{
    public bool Found { get; set; }
    public string? Message { get; set; }
    public string ContextToken { get; set; } = string.Empty;
    public long Revision { get; set; }
    public bool IsStale { get; set; }
    public int TotalAlerts { get; set; }
    public IReadOnlyList<HermesAssistantAlertSummary> Alerts { get; set; } = [];
}

public sealed class HermesAssistantAlertSummary
{
    public string Fingerprint { get; set; } = string.Empty;
    public string Kind { get; set; } = string.Empty;
    public string Severity { get; set; } = "Information";
    public string Category { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string TargetTab { get; set; } = "Assistant";
    public long NumericValue { get; set; }
    public int SeverityRank { get; set; }
}
