using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed partial class HermesWindow
{
    #region Trader And Market

    private void DrawTraderSaleSection(HermesTraderSummaryResponse summary)
    {
        GUILayout.Space(8f);

        if (!summary.Found)
        {
            GUILayout.Label(summary.Message ?? "Trader information is unavailable.");
            return;
        }

        GUILayout.BeginVertical(GUI.skin.box);

        var arrow = _saleComparisonExpanded ? "▼" : "▶";
        if (GUILayout.Button(
                $"{arrow}  TRADERS — {summary.SellOffers.Count:N0} SELL • {summary.PurchaseOffers.Count:N0} BUY",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _saleComparisonExpanded = !_saleComparisonExpanded;
        }

        DrawBestSale(summary);
        DrawBestTraderPurchase(summary);

        if (_saleComparisonExpanded)
        {
            if (!string.IsNullOrWhiteSpace(summary.Message))
            {
                GUILayout.Label(summary.Message);
            }

            if (!string.IsNullOrWhiteSpace(summary.SalePriceBasis))
            {
                GUILayout.Label(summary.SalePriceBasis);
                GUILayout.Space(4f);
            }

            GUILayout.Space(6f);
            GUILayout.Label("SALE COMPARISON ACROSS VANILLA TRADERS");

            if (summary.SellOffers.Count == 0)
            {
                if (summary.HasSupportedTraderBuyer && !summary.ReferencePrice.HasValue)
                {
                    GUILayout.Label("A supported vanilla trader accepts this item, but a sell-price estimate is unavailable because it has no handbook value.");
                }
                else
                {
                    GUILayout.Label("No supported vanilla trader buys this item.");
                }
            }
            else
            {
                foreach (var offer in summary.SellOffers)
                {
                    DrawSellOffer(offer);
                }
            }

            DrawTraderPurchaseSection(summary);
        }

        GUILayout.EndVertical();
    }

    private static void DrawBestSale(HermesTraderSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(summary.UsesSelectedStashInstance
            ? "BEST SALE — SELECTED OWNED COPY"
            : "BEST SALE — BASE ITEM");

        var best = summary.BestSellOffer;
        if (best is null)
        {
            if (summary.HasSupportedTraderBuyer && !summary.ReferencePrice.HasValue)
            {
                GUILayout.Label("Trader acceptance confirmed; price estimate unavailable because no handbook value exists.");
            }
            else
            {
                GUILayout.Label("No supported vanilla trader buys this item.");
            }
        }
        else
        {
            GUILayout.Label($"{best.TraderName} — {FormatCurrency(best.Amount, best.Currency)}");
            if (!string.Equals(best.Currency, "RUB", StringComparison.OrdinalIgnoreCase))
            {
                GUILayout.Label($"Rouble equivalent: ₽{best.RoubleEquivalent:N0}");
            }

            if (summary.UsesSelectedStashInstance && Plugin.Settings.ShowFullAssemblyValuation.Value)
            {
                GUILayout.Label($"Root item value: ₽{best.RootRoubleEquivalent:N0}");
                GUILayout.Label($"Accepted installed value: ₽{best.InstalledComponentRoubleEquivalent:N0} ({best.IncludedWeaponAttachmentCount} attachment(s), {best.IncludedArmorInsertCount} armor insert(s))");
                if (best.IgnoredInstalledItemCount > 0)
                {
                    GUILayout.Label($"Ignored by {best.TraderName}: {best.IgnoredInstalledItemCount} installed item(s), reference basis ₽{best.IgnoredInstalledReferenceValue:N0}");
                }
            }
        }

        GUILayout.EndVertical();
    }

    private static void DrawBestTraderPurchase(HermesTraderSummaryResponse summary)
    {
        var best = summary.PurchaseOffers
            .Where(offer => offer.IsAvailable)
            .SelectMany(offer => offer.PaymentOptions
                .Where(payment => payment.EstimateAvailable && payment.EstimatedRoubleValue > 0)
                .Select(payment => new
                {
                    offer.TraderName,
                    payment.DisplayPrice,
                    payment.EstimatedRoubleValue
                }))
            .OrderBy(option => option.EstimatedRoubleValue)
            .FirstOrDefault();

        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("CHEAPEST AVAILABLE TRADER PURCHASE");
        if (best is null)
        {
            GUILayout.Label("No currently available vanilla-trader offer was found.");
        }
        else
        {
            GUILayout.Label($"{best.TraderName} — {best.DisplayPrice}");
            GUILayout.Label($"Estimated value: ₽{best.EstimatedRoubleValue:N0}");
        }
        GUILayout.EndVertical();
    }

    private static void DrawSellOffer(HermesSellOffer offer)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.BeginHorizontal();
        GUILayout.Label(offer.IsBest ? $"★ {offer.TraderName}" : offer.TraderName);
        GUILayout.FlexibleSpace();
        GUILayout.Label(FormatCurrency(offer.Amount, offer.Currency));
        GUILayout.EndHorizontal();
        GUILayout.Label($"Your loyalty level: {offer.PlayerLoyaltyLevel}");

        if (!string.Equals(offer.Currency, "RUB", StringComparison.OrdinalIgnoreCase))
        {
            GUILayout.Label($"Rouble equivalent: ₽{offer.RoubleEquivalent:N0}");
        }

        if (Plugin.Settings.ShowFullAssemblyValuation.Value)
        {
            GUILayout.Label($"Root item: ₽{offer.RootRoubleEquivalent:N0}");
            if (offer.IncludedInstalledItemCount > 0)
            {
                GUILayout.Label($"Accepted installed items: ₽{offer.InstalledComponentRoubleEquivalent:N0} ({offer.IncludedWeaponAttachmentCount} attachment(s), {offer.IncludedArmorInsertCount} armor insert(s))");
            }
            else
            {
                GUILayout.Label("Accepted installed items: none");
            }

            if (offer.IgnoredInstalledItemCount > 0)
            {
                GUILayout.Label($"Ignored installed items: {offer.IgnoredInstalledItemCount} • reference basis ₽{offer.IgnoredInstalledReferenceValue:N0}");
            }
        }

        GUILayout.EndVertical();
    }

    private void DrawMarketSection(HermesMarketSummaryResponse summary)
    {
        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);

        var arrow = _marketExpanded ? "▼" : "▶";
        var configuredMinimum = Plugin.Settings.GetMinimumComparableFleaOffers();
        var reliable = summary.MarketPriceFromActiveOffers && summary.ComparableOfferCount >= configuredMinimum;
        var reliabilityBadge = reliable ? "RELIABLE" : summary.MarketPriceFromActiveOffers ? "LOW SAMPLE" : "REFERENCE";
        var headline = summary.MedianPrice.HasValue
            ? summary.MarketPriceFromActiveOffers
                ? $"[{reliabilityBadge}] adjusted median ₽{summary.MedianPrice.Value:N0} • {summary.ComparableOfferCount:N0} comparable"
                : $"[{reliabilityBadge}] market reference ₽{summary.MedianPrice.Value:N0} • {summary.MarketPriceSource}"
            : "No flea or fallback market value";

        if (GUILayout.Button(
                $"{arrow}  LOCAL FLEA MARKET — {headline}",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _marketExpanded = !_marketExpanded;
        }

        if (!summary.Found)
        {
            GUILayout.Label(summary.Message ?? "Local flea information is unavailable.");
            GUILayout.EndVertical();
            return;
        }

        DrawMarketAtAGlance(summary);
        if (summary.MarketPriceFromActiveOffers
            && summary.ComparableOfferCount < Plugin.Settings.GetMinimumComparableFleaOffers())
        {
            GUILayout.Label($"Reliability warning: {summary.ComparableOfferCount:N0} comparable offer(s); F12 minimum is {Plugin.Settings.GetMinimumComparableFleaOffers():N0}.");
        }

        if (_marketExpanded)
        {
            GUILayout.Space(6f);
            DrawFleaAccess(summary);
            GUILayout.Space(6f);
            DrawFleaStatistics(summary);
            GUILayout.Space(6f);
            DrawFleaSaleAnalysis(summary);
            GUILayout.Space(6f);
            DrawFleaBuyAnalysis(summary);
            GUILayout.Space(6f);
            DrawLowestFleaOffers(summary);
        }

        GUILayout.EndVertical();
    }

    private static void DrawMarketAtAGlance(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("FLEA AT A GLANCE");
        if (!summary.FleaUnlocked)
        {
            GUILayout.Label($"Locked until level {summary.RequiredPlayerLevel}; current level {summary.PlayerLevel}.");
        }
        else if (!summary.CanSellOnFlea)
        {
            GUILayout.Label($"Cannot list: {summary.SellUnavailableReason ?? "Unavailable"}");
        }
        else
        {
            var suggested = summary.SuggestedListPrice.HasValue
                ? $"₽{summary.SuggestedListPrice.Value:N0}"
                : "unavailable";
            var fee = summary.EstimatedListingFee.HasValue
                ? $"₽{summary.EstimatedListingFee.Value:N0}"
                : "unavailable";
            var net = summary.EstimatedNetSale.HasValue
                ? $"₽{summary.EstimatedNetSale.Value:N0}"
                : "unavailable";
            var basis = summary.UsesSelectedOwnedCopy ? "selected copy" : "base item";
            GUILayout.Label($"Suggested {basis} list: {suggested} - Fee: {fee} - Net: {net}");
        }

        GUILayout.Label(summary.LowestPrice.HasValue
            ? $"Buy reference: lowest ₽{summary.LowestPrice.Value:N0} • median ₽{summary.MedianPrice.GetValueOrDefault():N0}"
            : "Buy reference: no comparable offer available.");
        GUILayout.EndVertical();
    }

    private static void DrawFleaAccess(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("FLEA ACCESS");
        GUILayout.Label(summary.FleaUnlocked
            ? $"Unlocked — player level {summary.PlayerLevel}"
            : $"Locked — level {summary.RequiredPlayerLevel} required; current level {summary.PlayerLevel}");
        GUILayout.Label(summary.CanSellOnFlea
            ? "Listing eligibility: This item can be listed."
            : $"Listing eligibility: {summary.SellUnavailableReason ?? "Unavailable"}");
        GUILayout.EndVertical();
    }

    private static void DrawFleaStatistics(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("CURRENT MARKET VALUE");

        if (!summary.LowestPrice.HasValue)
        {
            GUILayout.Label("No active flea offer, converted barter, SPT dynamic price, or handbook fallback could be valued.");
        }
        else if (summary.MarketPriceFromActiveOffers)
        {
            if (summary.LowestListedPrice.HasValue)
            {
                GUILayout.Label($"Best comparable listing total: ₽{summary.LowestListedPrice.Value:N0}");
            }

            GUILayout.Label($"Component-adjusted lowest: ₽{summary.LowestPrice.Value:N0}");
            GUILayout.Label($"Component-adjusted median: ₽{summary.MedianPrice.GetValueOrDefault():N0}");
            GUILayout.Label($"Component-adjusted average: ₽{summary.AveragePrice.GetValueOrDefault():N0}");
            GUILayout.Label($"Component-adjusted highest reasonable: ₽{summary.HighestReasonablePrice.GetValueOrDefault():N0}");
            GUILayout.Label($"Source: {summary.MarketPriceSource}");
        }
        else
        {
            GUILayout.Label($"Market reference: ₽{summary.LowestPrice.Value:N0}");
            GUILayout.Label($"Source: {summary.MarketPriceSource}");
            if (summary.MarketPriceUsedHandbookFallback)
            {
                GUILayout.Label("Fallback note: No active cash offer, convertible barter offer, or SPT dynamic flea-market price was available.");
            }
            GUILayout.Label("No active comparable offer was available. This reference is not treated as a reliable listing recommendation.");
        }

        GUILayout.Label($"Valid cash offers found: {summary.ValidCashOfferCount:N0}");
        if (Plugin.Settings.ShowConvertedBarterOffers.Value)
        {
            GUILayout.Label($"Converted barter offers found: {summary.ConvertedBarterOfferCount:N0}");
        }
        if (Plugin.Settings.ShowConvertedBarterOffers.Value && summary.BarterOffersUsingHandbookFallback > 0)
        {
            GUILayout.Label($"Converted barters using handbook fallback: {summary.BarterOffersUsingHandbookFallback:N0}");
        }
        GUILayout.Label($"Offers used for comparison: {summary.ComparableOfferCount:N0}");
        GUILayout.Label($"Offers with installed attachments or armor inserts: {summary.OffersWithInstalledComponents:N0}");

        GUILayout.Label(
            "Valuation order: active local cash flea offer → converted flea barter offer → SPT dynamic flea-market price → handbook fallback. The same chain is used for barter requirements, installed weapon attachments, armor inserts, stash values, crafts, and hideout estimates. Stored container contents and loaded ammunition are ignored when decomposing an assembly.");

        if (summary.UsedLowConditionFallback)
        {
            GUILayout.Label("Condition note: No 80%+ root-condition offers were found, so used-condition offers were analyzed.");
        }

        var ignoredParts = new List<string>();
        if (summary.IgnoredBarterOfferCount > 0)
        {
            ignoredParts.Add($"unpriced barter {summary.IgnoredBarterOfferCount}");
        }

        if (summary.IgnoredTraderOfferCount > 0)
        {
            ignoredParts.Add($"trader duplicates {summary.IgnoredTraderOfferCount}");
        }

        if (summary.IgnoredExpiredOrInvalidOfferCount > 0)
        {
            ignoredParts.Add($"expired/invalid {summary.IgnoredExpiredOrInvalidOfferCount}");
        }

        if (summary.IgnoredLowConditionOfferCount > 0)
        {
            ignoredParts.Add($"below 80% root condition {summary.IgnoredLowConditionOfferCount}");
        }

        if (summary.IgnoredOutlierCount > 0)
        {
            ignoredParts.Add($"high outliers {summary.IgnoredOutlierCount}");
        }

        if (ignoredParts.Count > 0)
        {
            GUILayout.Label("Ignored: " + string.Join(" • ", ignoredParts));
        }

        GUILayout.EndVertical();
    }

    private static void DrawFleaSaleAnalysis(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("SELL ANALYSIS");

        if (!summary.FleaUnlocked || !summary.CanSellOnFlea)
        {
            GUILayout.Label(summary.SellUnavailableReason ?? "This item cannot currently be listed.");
        }
        else if (!summary.SuggestedListPrice.HasValue)
        {
            GUILayout.Label(summary.LowestPrice.HasValue
                ? $"No active comparable offer was found. Reference only: ₽{summary.LowestPrice.Value:N0} ({summary.MarketPriceSource})."
                : "A suggested listing price is unavailable because no market value could be resolved.");
        }
        else
        {
            var saleBasis = summary.UsesSelectedOwnedCopy ? "selected-copy" : "base-item";
            GUILayout.Label($"Suggested {saleBasis} listing price: RUB {summary.SuggestedListPrice.Value:N0}");
            if (summary.UsesSelectedOwnedCopy)
            {
                GUILayout.Label($"Selected copy: {summary.SelectedOwnedCopyLabel} - {summary.SelectedOwnedCopyLocation} - root RUB {summary.SelectedOwnedCopyRootValue.GetValueOrDefault():N0} + child items RUB {summary.SelectedOwnedCopyChildValue.GetValueOrDefault():N0}");
            }
            if (Plugin.Settings.ShowListingFeeEstimates.Value)
            {
                GUILayout.Label(summary.EstimatedListingFee.HasValue
                    ? $"Estimated listing fee: ₽{summary.EstimatedListingFee.Value:N0}"
                    : "Estimated listing fee: unavailable");
                GUILayout.Label(summary.EstimatedNetSale.HasValue
                    ? $"Estimated {saleBasis} net sale: ₽{summary.EstimatedNetSale.Value:N0}"
                    : $"Estimated {saleBasis} net sale: unavailable");
            }
        }

        if (summary.BestTraderSellPrice.HasValue)
        {
            GUILayout.Label($"Best trader estimate: {summary.BestTraderSellName} — ₽{summary.BestTraderSellPrice.Value:N0}");
        }

        GUILayout.Label("Recommendation: " + summary.SellRecommendation);
        GUILayout.EndVertical();
    }

    private static void DrawFleaBuyAnalysis(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("BUY ANALYSIS");

        GUILayout.Label(summary.LowestListedPrice.HasValue
            ? summary.LowestOfferIsBarter
                ? $"Converted requirement value for best barter assembly: ₽{summary.LowestListedPrice.Value:N0}"
                : $"Cash required for best comparable flea assembly: ₽{summary.LowestListedPrice.Value:N0}"
            : "Best comparable flea assembly value: unavailable");

        GUILayout.Label(summary.LowestPrice.HasValue
            ? summary.MarketPriceFromActiveOffers
                ? $"Component-adjusted active-market value: ₽{summary.LowestPrice.Value:N0}"
                : $"Fallback market reference: ₽{summary.LowestPrice.Value:N0} ({summary.MarketPriceSource})"
            : "Component-adjusted market value: unavailable");

        GUILayout.Label(summary.CheapestAvailableTraderBuyPrice.HasValue
            ? $"Cheapest available cash trader: {summary.CheapestAvailableTraderName} — ₽{summary.CheapestAvailableTraderBuyPrice.Value:N0}"
            : "Cheapest available cash trader: none found");

        GUILayout.Label("Recommendation: " + summary.BuyRecommendation);
        GUILayout.EndVertical();
    }

    private static void DrawLowestFleaOffers(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("LOWEST COMPONENT-ADJUSTED OFFERS");

        if (summary.LowestOffers.Count == 0)
        {
            GUILayout.Label("No comparable offers to display.");
        }
        else
        {
            var offers = summary.LowestOffers
                .Where(offer => Plugin.Settings.ShowConvertedBarterOffers.Value || !offer.IsBarter)
                .Take(Plugin.Settings.GetMaximumFleaOffersDisplayed())
                .ToList();
            if (offers.Count == 0)
            {
                GUILayout.Label("No offers match the current Market Intelligence display settings.");
            }
            foreach (var offer in offers)
            {
                GUILayout.BeginVertical(GUI.skin.box);

                GUILayout.BeginHorizontal();
                GUILayout.Label($"Base-item equivalent: ₽{offer.UnitPrice:N0}");
                GUILayout.FlexibleSpace();
                GUILayout.Label(offer.IsBarter
                    ? $"Converted requirements: ₽{offer.ListedUnitPrice:N0}"
                    : $"Listed total: ₽{offer.ListedUnitPrice:N0}");
                GUILayout.EndHorizontal();

                if (offer.IsBarter)
                {
                    GUILayout.Label($"Barter offer • {offer.BarterRequirementCount:N0} requirement type(s)");
                    GUILayout.Label("Conversion source: " + offer.PriceSource);
                    if (offer.UsedHandbookFallback)
                    {
                        GUILayout.Label("Fallback note: At least one requirement had no current cash flea offer, so its handbook value was used.");
                    }
                }

                if (Plugin.Settings.ShowFullAssemblyValuation.Value
                    && (offer.InstalledComponentValue > 0
                        || offer.WeaponAttachmentCount > 0
                        || offer.ArmorInsertCount > 0))
                {
                    GUILayout.Label(
                        $"Installed value: ₽{offer.InstalledComponentValue:N0} • "
                        + $"Weapon attachments: {offer.WeaponAttachmentCount:N0} • "
                        + $"Armor inserts: {offer.ArmorInsertCount:N0}");
                }
                else if (Plugin.Settings.ShowFullAssemblyValuation.Value)
                {
                    GUILayout.Label("Installed value: none");
                }

                GUILayout.Label(
                    $"Qty {offer.Quantity:N0} • Root condition {offer.ConditionLabel} {offer.ConditionPercent}% • "
                    + $"{FormatDuration(offer.SecondsRemaining)} left");

                GUILayout.EndVertical();
            }
        }

        GUILayout.EndVertical();
    }

    private static void DrawTraderPurchaseSection(HermesTraderSummaryResponse summary)
    {
        if (!summary.Found)
        {
            return;
        }

        GUILayout.Space(8f);
        GUILayout.Label("BUY FROM TRADERS");
        if (summary.PurchaseOffers.Count == 0)
        {
            GUILayout.Label("No current vanilla-trader offer was found for this item.");
        }
        else
        {
            foreach (var offer in summary.PurchaseOffers)
            {
                DrawPurchaseOffer(offer);
            }
        }
    }

    private static void DrawPurchaseOffer(HermesPurchaseOffer offer)
    {
        GUILayout.BeginVertical(GUI.skin.box);

        GUILayout.BeginHorizontal();
        GUILayout.Label($"{offer.TraderName} LL{offer.RequiredLoyaltyLevel}");
        GUILayout.FlexibleSpace();
        GUILayout.Label(offer.IsAvailable ? "AVAILABLE" : "LOCKED");
        GUILayout.EndHorizontal();

        GUILayout.Label($"Your loyalty level: {offer.PlayerLoyaltyLevel}");
        GUILayout.Label($"Status: {offer.AvailabilityReason}");

        if (!string.IsNullOrWhiteSpace(offer.RequiredQuestName))
        {
            if (offer.IsAvailable)
            {
                GUILayout.Label($"Quest unlock: {offer.RequiredQuestName} ({offer.RequiredQuestState ?? "requirement met"})");
            }
            else
            {
                GUILayout.Label($"Quest requirement: {offer.QuestRequirementText ?? offer.RequiredQuestName}");
            }
        }

        GUILayout.Label(offer.UnlimitedStock
            ? "Stock: Unlimited"
            : offer.StockRemaining.HasValue
                ? $"Stock remaining: {offer.StockRemaining.Value:N0}"
                : "Stock: Unknown");

        if (offer.PurchaseLimit.HasValue)
        {
            var remaining = offer.PurchaseLimitRemaining.HasValue
                ? offer.PurchaseLimitRemaining.Value.ToString("N0")
                : "unknown";
            GUILayout.Label($"Personal limit: {remaining} of {offer.PurchaseLimit.Value:N0} remaining");
        }

        if (offer.SecondsUntilRestock.HasValue)
        {
            GUILayout.Label($"Restock: {FormatDuration(offer.SecondsUntilRestock.Value)}");
        }

        if (offer.PaymentOptions.Count == 0)
        {
            GUILayout.Label("Payment information unavailable.");
        }
        else
        {
            GUILayout.Label("Payment options:");
            foreach (var payment in offer.PaymentOptions)
            {
                GUILayout.BeginVertical(GUI.skin.box);
                GUILayout.Label(payment.IsCash ? $"Cash — {payment.DisplayPrice}" : $"Barter — {payment.DisplayPrice}");

                if (!payment.IsCash)
                {
                    if (payment.EstimateAvailable && payment.EstimatedRoubleValue > 0)
                    {
                        GUILayout.Label($"Current market estimate: ₽{payment.EstimatedRoubleValue:N0}");
                        if (!string.IsNullOrWhiteSpace(payment.EstimateSource))
                        {
                            GUILayout.Label($"Source: {payment.EstimateSource}");
                        }

                        if (payment.UsedHandbookFallback)
                        {
                            GUILayout.Label("Fallback note: One or more required items had no active cash offer, convertible barter offer, or SPT dynamic flea-market price, so HERMES used handbook value.");
                        }

                        if (Plugin.Settings.ShowDetailedBarterCalculations.Value && payment.Requirements.Count > 0)
                        {
                            GUILayout.Space(3f);
                            GUILayout.Label("Market calculation:");
                            foreach (var requirement in payment.Requirements)
                            {
                                if (!requirement.EstimateAvailable
                                    || !requirement.EstimatedUnitRoubleValue.HasValue
                                    || !requirement.EstimatedSubtotalRoubleValue.HasValue)
                                {
                                    GUILayout.Label($"• {FormatCount(requirement.Count)} × {requirement.Name} — value unavailable");
                                    continue;
                                }

                                var sourceLabel = requirement.Currency is not null
                                    ? "Trader currency conversion"
                                    : requirement.EstimateSource;

                                GUILayout.Label(
                                    $"• {FormatCount(requirement.Count)} × {requirement.Name} — " +
                                    $"₽{requirement.EstimatedUnitRoubleValue.Value:N0} each • " +
                                    $"subtotal ₽{requirement.EstimatedSubtotalRoubleValue.Value:N0} ({sourceLabel})");
                            }
                        }
                    }
                    else
                    {
                        GUILayout.Label("Current market estimate unavailable.");
                        if (!string.IsNullOrWhiteSpace(payment.EstimateSource))
                        {
                            GUILayout.Label($"Reason: {payment.EstimateSource}");
                        }

                        foreach (var requirement in payment.Requirements.Where(requirement => !requirement.EstimateAvailable))
                        {
                            GUILayout.Label($"• {FormatCount(requirement.Count)} × {requirement.Name} — {requirement.EstimateSource}");
                        }
                    }
                }

                GUILayout.EndVertical();
            }
        }

        GUILayout.EndVertical();
        GUILayout.Space(4f);
    }

    #endregion
}
