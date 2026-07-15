namespace Hermes.Server.Models;

public sealed record HermesStatusResponse(
    string Name,
    string Version,
    string SptVersion,
    bool ReadOnly,
    string[] Capabilities);

public sealed record HermesProfileContextResponse(
    bool Found,
    string? Message,
    string ContextToken);


public sealed record HermesCacheStatusResponse(
    bool Found,
    string? Message,
    int MarketUnitValueEntryCount,
    int MarketSummaryEntryCount,
    int StashAnalysisEntryCount,
    int LoadoutAnalysisEntryCount,
    long CacheHits,
    long CacheMisses,
    long CacheWrites,
    long StashCacheHits,
    long StashCacheMisses,
    long StashCacheWrites,
    long LoadoutCacheHits,
    long LoadoutCacheMisses,
    long LoadoutCacheWrites,
    long Generation,
    int TtlSeconds,
    int StashTtlSeconds,
    int LoadoutTtlSeconds,
    long? OldestEntryAgeSeconds,
    long? NewestEntryAgeSeconds,
    string LastInvalidationReason,
    long? LastInvalidatedUnixTime);

public sealed record HermesCacheClearResponse(
    bool Cleared,
    string Message,
    HermesCacheStatusResponse Status);

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


public sealed record HermesItemSelectionResponse(
    bool Found,
    string? Message,
    HermesItemSummary? Item);

public sealed record HermesStashInstancesResponse(
    bool Found,
    string? Message,
    string ItemKey,
    string Name,
    IReadOnlyList<HermesStashInstanceSummary> Instances);

public sealed record HermesStashInstanceSelectionResponse(
    bool Found,
    string? Message,
    HermesItemSummary? Item,
    HermesStashInstanceSummary? Instance,
    string? InventoryLocation);

public sealed record HermesStashInstanceSummary(
    string InstanceKey,
    string Label,
    double Quantity,
    int ConditionPercent,
    string ConditionDescription,
    string ConditionKind,
    double ConditionCurrent,
    double ConditionMaximum,
    bool FoundInRaid,
    int ChildItemCount,
    int WeaponAttachmentCount,
    int ArmorInsertCount,
    long RootConditionAdjustedReferenceValue,
    long InstalledComponentReferenceValue,
    long ConditionAdjustedReferenceValue);

public sealed record HermesTraderSummaryResponse(
    bool Found,
    string? Message,
    string ItemKey,
    string Name,
    string ShortName,
    long? ReferencePrice,
    bool HasSupportedTraderBuyer,
    bool UsesSelectedStashInstance,
    string? SelectedInstanceKey,
    string? SelectedInstanceLabel,
    int? SelectedInstanceConditionPercent,
    double SelectedInstanceQuantity,
    int SelectedInstanceChildItemCount,
    int SelectedInstanceWeaponAttachmentCount,
    int SelectedInstanceArmorInsertCount,
    long? SelectedInstanceRootReferenceValue,
    long? SelectedInstanceInstalledReferenceValue,
    long? SelectedInstanceReferenceValue,
    string SalePriceBasis,
    HermesSellOffer? BestSellOffer,
    IReadOnlyList<HermesSellOffer> SellOffers,
    IReadOnlyList<HermesPurchaseOffer> PurchaseOffers);

public sealed record HermesSellOffer(
    string TraderName,
    int PlayerLoyaltyLevel,
    long Amount,
    string Currency,
    long RoubleEquivalent,
    long RootRoubleEquivalent,
    long InstalledComponentRoubleEquivalent,
    int IncludedInstalledItemCount,
    int IncludedWeaponAttachmentCount,
    int IncludedArmorInsertCount,
    int IgnoredInstalledItemCount,
    long IgnoredInstalledReferenceValue,
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
    string EstimateSource,
    bool UsedHandbookFallback,
    bool EstimateAvailable,
    IReadOnlyList<HermesPaymentRequirement> Requirements);

public sealed record HermesPaymentRequirement(
    string Name,
    double Count,
    string? Currency,
    long? EstimatedUnitRoubleValue,
    long? EstimatedSubtotalRoubleValue,
    string EstimateSource,
    bool UsedHandbookFallback,
    bool EstimateAvailable);

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
    int ConvertedBarterOfferCount,
    int BarterOffersUsingHandbookFallback,
    int ComparableOfferCount,
    int IgnoredBarterOfferCount,
    int IgnoredTraderOfferCount,
    int IgnoredExpiredOrInvalidOfferCount,
    int IgnoredLowConditionOfferCount,
    int IgnoredOutlierCount,
    bool UsedLowConditionFallback,
    int OffersWithInstalledComponents,
    string MarketPriceSource,
    bool MarketPriceFromActiveOffers,
    bool MarketPriceUsedHandbookFallback,
    long? LowestListedPrice,
    bool LowestOfferIsBarter,
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
    bool IsBarter,
    string PriceSource,
    int BarterRequirementCount,
    bool UsedHandbookFallback,
    long UnitPrice,
    long ListedUnitPrice,
    long InstalledComponentValue,
    int WeaponAttachmentCount,
    int ArmorInsertCount,
    int Quantity,
    int ConditionPercent,
    string ConditionLabel,
    long SecondsRemaining);

public sealed record HermesHideoutSummaryResponse(
    bool Found,
    string? Message,
    int ReadyAreaCount,
    int MaterialBlockedAreaCount,
    int ProgressionBlockedAreaCount,
    IReadOnlyList<HermesHideoutAreaSummary> Areas,
    IReadOnlyList<HermesActiveProductionSummary> ActiveProductions,
    HermesHideoutResourceSummary Resources);

public sealed record HermesHideoutAreaSummary(
    string AreaKey,
    string Name,
    int CurrentLevel,
    int MaximumLevel,
    int? TargetLevel,
    string Status,
    bool IsActive,
    bool IsConstructing,
    long? SecondsUntilComplete,
    int MissingItemTypes,
    long EstimatedMissingHandbookCost);

public sealed record HermesHideoutAreaDetailResponse(
    bool Found,
    string? Message,
    HermesHideoutAreaSummary? Area,
    int ConstructionSeconds,
    IReadOnlyList<HermesHideoutRequirement> Requirements,
    long EstimatedMissingAcquisitionCost);

public sealed record HermesHideoutRequirement(
    string Type,
    string Name,
    string? ItemTemplateId,
    double Required,
    double Owned,
    double Missing,
    bool IsMet,
    bool FoundInRaidRequired,
    string? Details,
    string? AcquisitionSource,
    long? UnitPrice,
    long? EstimatedMissingCost);

public sealed record HermesActiveProductionSummary(
    string StationName,
    string OutputName,
    string? OutputTemplateId,
    int OutputQuantity,
    bool IsComplete,
    bool IsContinuous,
    long SecondsRemaining,
    string Status);

public sealed record HermesHideoutResourceSummary(
    bool GeneratorActive,
    int FuelContainerCount,
    double FuelResourceRemaining,
    long? EstimatedGeneratorRuntimeSeconds,
    double? FuelCounter,
    double? AirFilterCounter,
    double? WaterFilterCounter,
    int ActiveProductionCount,
    int CompletedProductionCount);

public sealed record HermesCraftsResponse(
    bool Found,
    string? Message,
    int TotalCrafts,
    IReadOnlyList<HermesCraftSummary> Crafts);

public sealed record HermesCraftSummary(
    string CraftKey,
    string StationName,
    int RequiredStationLevel,
    string OutputName,
    string? OutputTemplateId,
    int OutputQuantity,
    int DurationSeconds,
    bool IsAvailable,
    bool CanStartNow,
    string Status,
    bool AcquisitionPlanComplete,
    long EstimatedAdditionalCashCost,
    long EstimatedOwnedIngredientValue,
    long EstimatedEconomicInputValue,
    long EstimatedOutputValue,
    long EstimatedCashProfit,
    long EstimatedEconomicProfit,
    long EstimatedEconomicProfitPerHour,
    bool IsActive,
    bool IsComplete);

public sealed record HermesCraftDetailResponse(
    bool Found,
    string? Message,
    HermesCraftSummary? Craft,
    IReadOnlyList<HermesCraftIngredient> Ingredients,
    string? RequiredQuestName,
    bool RequiredQuestComplete,
    string ValuationBasis);

public sealed record HermesCraftIngredient(
    string Name,
    string TemplateId,
    string RequirementType,
    double Required,
    double Owned,
    double OwnedUsed,
    double Missing,
    bool IsMet,
    bool FoundInRaidRequired,
    bool IsReusableTool,
    long? UnitHandbookValue,
    long? OwnedEconomicUnitValue,
    long EstimatedOwnedEconomicValue,
    IReadOnlyList<HermesCraftAcquisitionLine> AcquisitionPlan,
    double UnavailableQuantity,
    long EstimatedPurchaseCost,
    bool AcquisitionAvailable,
    string? CostNote);

public sealed record HermesCraftAcquisitionLine(
    string Source,
    double Quantity,
    long UnitPrice,
    long TotalCost,
    bool IsFallback);

public sealed record HermesItemHideoutUsageResponse(
    bool Found,
    string? Message,
    string ItemKey,
    string Name,
    double OwnedQuantity,
    double OwnedFoundInRaidQuantity,
    IReadOnlyList<HermesQuestItemUse> QuestUses,
    IReadOnlyList<HermesUpgradeUse> UpgradeUses,
    IReadOnlyList<HermesCraftUse> ProducedBy,
    IReadOnlyList<HermesCraftUse> UsedBy);

public sealed record HermesQuestItemUse(
    string QuestName,
    string TraderName,
    string QuestStatus,
    string ConditionType,
    double Required,
    double OwnedMatchingTargets,
    double OwnedSelectedItem,
    double Missing,
    bool FoundInRaidRequired,
    bool ConditionCompleted,
    bool QuestCompleted,
    bool IsActive,
    string ProgressText);

public sealed record HermesUpgradeUse(
    string AreaName,
    int CurrentLevel,
    int TargetLevel,
    string Status,
    double Required,
    double Owned,
    double Missing,
    bool IsMet,
    bool IsNextUpgrade,
    bool FoundInRaidRequired,
    string? AcquisitionSource,
    long? EstimatedMissingCost);

public sealed record HermesCraftUse(
    string CraftKey,
    string StationName,
    int CurrentStationLevel,
    int RequiredStationLevel,
    string OutputName,
    int OutputQuantity,
    int DurationSeconds,
    double ItemCount,
    double Owned,
    double Missing,
    bool IsUnlocked,
    bool CanStartNow,
    bool IsActive,
    bool IsComplete,
    string Status);


public sealed record HermesStashSummaryResponse(
    bool Found,
    string? Message,
    int TotalItemInstances,
    int IndependentItemCount,
    int ValuedIndependentItemCount,
    int UnsupportedIndependentItemCount,
    int OccupiedCells,
    long FullHandbookReferenceValue,
    long ConditionAdjustedHandbookValue,
    long BestTraderLiquidationValue,
    long EstimatedFleaNetValue,
    long BestDestinationLiquidationValue,
    int TraderValuedItemCount,
    int FleaValuedItemCount,
    int NoTraderBuyerItemCount,
    int NoFleaEstimateItemCount,
    int SafeToSellInstanceCount,
    int SellSurplusInstanceCount,
    int KeepInstanceCount,
    int ReviewInstanceCount,
    int DuplicateGroupCount,
    int DamagedOrDepletedItemCount,
    double RecommendedKeepQuantity,
    double PotentiallySellQuantity,
    long PotentialTraderSaleValue,
    long PotentialFleaNetValue,
    long PotentialBestSaleValue,
    long GeneratedUnixTime,
    int CacheTtlSeconds,
    IReadOnlyList<HermesStashTraderBreakdown> TraderBreakdown,
    IReadOnlyList<HermesStashSaleDestinationBreakdown> SaleDestinationBreakdown,
    IReadOnlyList<HermesStashValuationItem> MostValuableItems,
    IReadOnlyList<HermesStashValuationItem> Recommendations,
    IReadOnlyList<HermesStashDuplicateGroup> DuplicateGroups,
    IReadOnlyList<HermesStashConditionItem> DamagedOrDepletedItems,
    int CleanupCandidateInstanceCount,
    int RecoverableCells,
    long CleanupTraderSaleValue,
    long CleanupFleaNetValue,
    long CleanupBestSaleValue,
    IReadOnlyList<HermesStashValuationItem> CleanupCandidates);

public sealed record HermesStashTraderBreakdown(
    string TraderName,
    int ItemCount,
    long RoubleEquivalent);

public sealed record HermesStashSaleDestinationBreakdown(
    string Destination,
    int ItemCount,
    long RoubleEquivalent);

public sealed record HermesStashValuationItem(
    string ItemKey,
    string Name,
    string ShortName,
    string Category,
    bool FoundInRaid,
    bool IsProtectedCurrency,
    string InstanceKey,
    string InstanceLabel,
    double Quantity,
    int ConditionPercent,
    string ConditionDescription,
    string ConditionKind,
    double ConditionCurrent,
    double ConditionMaximum,
    int OccupiedCells,
    int InstalledItemCount,
    int ContainedItemCount,
    long FullHandbookReferenceValue,
    long ConditionAdjustedHandbookValue,
    string? BestTraderName,
    long? BestTraderValue,
    bool FleaEstimateAvailable,
    bool FleaEstimateReliable,
    int FleaComparableOfferCount,
    long? EstimatedFleaListPrice,
    long? EstimatedFleaFee,
    long? EstimatedFleaNetValue,
    string FleaEstimateSource,
    string? BestSaleDestination,
    long? BestSaleValue,
    string Recommendation,
    double RecommendedKeepQuantity,
    double PotentiallySellQuantity,
    double ActiveQuestReserve,
    double FutureQuestReserve,
    double NextHideoutReserve,
    double FutureHideoutReserve,
    long PotentialTraderSaleValue,
    long PotentialFleaNetValue,
    long PotentialBestSaleValue,
    IReadOnlyList<string> Reasons);

public sealed record HermesStashDuplicateGroup(
    string ItemKey,
    string Name,
    string ShortName,
    int InstanceCount,
    double OwnedQuantity,
    double ExplicitlyReservedQuantity,
    double SuggestedReserveQuantity,
    double PotentialExcessQuantity,
    int OccupiedCells,
    string? BestSaleDestination,
    long PotentialExcessSaleValue,
    string Note);

public sealed record HermesStashConditionItem(
    string ItemKey,
    string Name,
    string ShortName,
    string InstanceKey,
    string InstanceLabel,
    string ConditionKind,
    int ConditionPercent,
    double ConditionCurrent,
    double ConditionMaximum,
    int ThresholdPercent,
    string Status,
    string? BestSaleDestination,
    long? BestSaleValue,
    string Recommendation);

public sealed record HermesLoadoutSummaryResponse(
    bool Found,
    string? Message,
    string Readiness,
    int ReadinessScore,
    int WarningCount,
    int CriticalCount,
    HermesVitalsSummary Vitals,
    IReadOnlyList<HermesLoadoutSlotSummary> EquippedSlots,
    IReadOnlyList<HermesWeaponReadiness> Weapons,
    IReadOnlyList<HermesArmorReadiness> Armor,
    HermesMedicalReadiness Medical,
    IReadOnlyList<HermesQuestLoadoutRequirement> QuestRequirements,
    IReadOnlyList<HermesRaidPlanSummary> RaidPlans,
    HermesLoadoutValueSummary ValueSummary,
    IReadOnlyList<HermesLoadoutWarning> Warnings,
    long GeneratedUnixTime);

public sealed record HermesLoadoutValueSummary(
    bool Found,
    string? Message,
    long TraderLiquidationValue,
    long MarketReplacementValue,
    long BestReplacementValue,
    long AtRiskReplacementValue,
    long ProtectedReplacementValue,
    long InsuredReplacementValue,
    long UninsuredReplacementValue,
    long? EstimatedInsuranceCost,
    string InsuranceEstimateSource,
    int ValuedItemCount,
    int UnsupportedItemCount,
    int AtRiskItemCount,
    int ProtectedItemCount,
    int InsuredItemCount,
    int UninsuredItemCount,
    string InsuranceStatus,
    IReadOnlyList<HermesLoadoutValueCategory> Categories,
    IReadOnlyList<HermesLoadoutValueItem> Items,
    IReadOnlyList<string> Notes);

public sealed record HermesLoadoutValueCategory(
    string Category,
    int ItemCount,
    long TraderLiquidationValue,
    long MarketReplacementValue,
    long BestReplacementValue,
    long AtRiskReplacementValue,
    long UninsuredReplacementValue);

public sealed record HermesLoadoutValueItem(
    string ProfileItemId,
    string Name,
    string Category,
    string SlotName,
    double Quantity,
    int ConditionPercent,
    string ConditionDescription,
    long? TraderLiquidationValue,
    string? BestTraderName,
    long? MarketReplacementValue,
    string MarketSource,
    bool UsedHandbookFallback,
    long? TraderReplacementValue,
    string TraderReplacementSource,
    long? BestReplacementValue,
    string BestReplacementSource,
    bool IsAtRisk,
    bool IsProtected,
    bool IsInsurable,
    string InsuranceStatus,
    bool IsHighValueUninsured);

public sealed record HermesVitalsSummary(
    double CurrentHealth,
    double MaximumHealth,
    int HealthPercent,
    double CurrentHydration,
    double MaximumHydration,
    int HydrationPercent,
    double CurrentEnergy,
    double MaximumEnergy,
    int EnergyPercent);

public sealed record HermesLoadoutSlotSummary(
    string SlotName,
    string ItemName,
    int ConditionPercent,
    string ConditionDescription,
    int ChildItemCount,
    string Status);

public sealed record HermesWeaponReadiness(
    string SlotName,
    string Name,
    int DurabilityPercent,
    string DurabilityDescription,
    string Caliber,
    string? MagazineName,
    int MagazineCapacity,
    double LoadedRounds,
    int CompatibleSpareMagazineCount,
    double SpareMagazineRounds,
    double LooseCompatibleRounds,
    string? LoadedAmmoName,
    bool HasMixedAmmo,
    string Status,
    IReadOnlyList<string> Warnings);

public sealed record HermesArmorReadiness(
    string SlotName,
    string Name,
    int ConditionPercent,
    string ConditionDescription,
    int MaximumArmorClass,
    int ArmorInsertSlotCount,
    int InstalledArmorInsertCount,
    int MissingRequiredArmorInsertCount,
    int EmptyOptionalArmorInsertCount,
    string Status,
    IReadOnlyList<string> Warnings);

public sealed record HermesMedicalReadiness(
    int MedicalItemCount,
    double TotalHealingResource,
    bool HasLightBleedTreatment,
    bool HasHeavyBleedTreatment,
    bool HasFractureTreatment,
    bool HasPainTreatment,
    bool HasSurgeryKit,
    int HydrationProvisionCount,
    int EnergyProvisionCount,
    IReadOnlyList<HermesCarriedMedicalItem> Items);

public sealed record HermesCarriedMedicalItem(
    string Name,
    double CurrentResource,
    double MaximumResource,
    string Coverage);

public sealed record HermesQuestLoadoutRequirement(
    string QuestName,
    string TraderName,
    string MapName,
    string RequirementKind,
    string ConditionType,
    string RequiredEquipment,
    double RequiredQuantity,
    double CarriedQuantity,
    double FoundInRaidCarriedQuantity,
    bool FoundInRaidRequired,
    bool IsCompleted,
    bool IsRaidCritical,
    bool AcquireInRaid,
    bool IsSatisfied,
    string Note);

public sealed record HermesRaidPlanSummary(
    string MapName,
    string Status,
    int ActiveQuestCount,
    int ObjectiveCount,
    int CompletedObjectiveCount,
    int RaidRequirementCount,
    int MissingRequirementCount,
    IReadOnlyList<HermesRaidPlanQuest> Quests,
    IReadOnlyList<HermesRaidPlanRequirement> CombinedRequirements,
    IReadOnlyList<string> Notes);

public sealed record HermesRaidPlanQuest(
    string QuestName,
    string TraderName,
    string Status,
    int ObjectiveCount,
    int CompletedObjectiveCount,
    int MissingRequirementCount,
    IReadOnlyList<HermesRaidPlanObjective> Objectives);

public sealed record HermesRaidPlanObjective(
    string ConditionType,
    string Description,
    bool IsCompleted,
    bool IsRaidObjective,
    string Status);

public sealed record HermesRaidPlanRequirement(
    string RequirementKind,
    string RequiredEquipment,
    double RequiredQuantity,
    double CarriedQuantity,
    double FoundInRaidCarriedQuantity,
    double MissingQuantity,
    bool FoundInRaidRequired,
    bool AcquireInRaid,
    bool IsSatisfied,
    IReadOnlyList<string> QuestNames,
    string Note);

public sealed record HermesLoadoutWarning(
    string Severity,
    string Category,
    string Message);
