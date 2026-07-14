using Hermes.Client.Models;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesWindow
{
    private const int WindowId = 0x4845524D;

    private Rect _windowRect = new(70f, 70f, 1120f, 760f);
    private Vector2 _resultScroll;
    private Vector2 _detailScroll;
    private string _query = string.Empty;
    private string _status = "Search for an item or ask where it can be bought or sold.";
    private string _detailStatus = "Select an item to inspect trader and local flea information.";
    private bool _visible;
    private bool _searching;
    private bool _loadingDetails;
    private bool _saleComparisonExpanded;
    private bool _marketExpanded;
    private IReadOnlyList<HermesItemSummary> _results = [];
    private HermesItemSummary? _selectedItem;
    private HermesTraderSummaryResponse? _traderSummary;
    private HermesMarketSummaryResponse? _marketSummary;

    public void Toggle()
    {
        _visible = !_visible;
    }

    public void Draw()
    {
        if (!_visible)
        {
            return;
        }

        _windowRect = GUI.Window(
            WindowId,
            _windowRect,
            DrawWindow,
            "HERMES 0.1.0-alpha7.1 — Market Intelligence");
    }

    private void DrawWindow(int windowId)
    {
        GUILayout.BeginVertical();

        GUILayout.Label("READ-ONLY PERSONAL OPERATIONS ASSISTANT");
        GUILayout.Label("Market values come from the current local SPT flea market. HERMES never buys, sells, or lists items in Alpha7.");

        GUILayout.Space(6f);
        DrawSearchBar();

        GUILayout.Space(4f);
        GUILayout.Label(_status);
        GUILayout.Space(6f);

        GUILayout.BeginHorizontal(GUILayout.ExpandHeight(true));
        DrawResultPanel();
        GUILayout.Space(8f);
        DrawDetailPanel();
        GUILayout.EndHorizontal();

        GUILayout.Space(6f);
        DrawFooter();

        GUILayout.EndVertical();
        GUI.DragWindow(new Rect(0f, 0f, _windowRect.width, 24f));
    }

    private void DrawSearchBar()
    {
        GUILayout.BeginHorizontal();

        GUI.SetNextControlName("HermesSearchField");
        _query = GUILayout.TextField(_query, GUILayout.ExpandWidth(true), GUILayout.Height(30f));

        GUI.enabled = !_searching && !string.IsNullOrWhiteSpace(_query);
        if (GUILayout.Button(_searching ? "Searching..." : "Search", GUILayout.Width(120f), GUILayout.Height(30f)))
        {
            _ = RunSearchAsync();
        }

        GUI.enabled = true;
        GUILayout.EndHorizontal();

        if (Event.current.type == EventType.KeyDown
            && Event.current.keyCode is KeyCode.Return or KeyCode.KeypadEnter
            && !_searching
            && !string.IsNullOrWhiteSpace(_query))
        {
            Event.current.Use();
            _ = RunSearchAsync();
        }
    }

    private void DrawResultPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(330f), GUILayout.ExpandHeight(true));
        GUILayout.Label("SEARCH RESULTS");
        GUILayout.Space(4f);

        _resultScroll = GUILayout.BeginScrollView(_resultScroll, GUILayout.ExpandHeight(true));

        if (_results.Count == 0)
        {
            GUILayout.Label("No results loaded.");
        }
        else
        {
            foreach (var item in _results)
            {
                DrawResultButton(item);
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private void DrawResultButton(HermesItemSummary item)
    {
        var selected = _selectedItem?.ItemKey == item.ItemKey;
        var displayName = string.Equals(item.Name, item.ShortName, StringComparison.OrdinalIgnoreCase)
            ? item.Name
            : $"{item.Name}\n{item.ShortName}";

        var prefix = selected ? "▶ " : string.Empty;
        if (GUILayout.Button(prefix + displayName, GUILayout.MinHeight(48f), GUILayout.ExpandWidth(true)))
        {
            _ = SelectItemAsync(item);
        }

        GUILayout.Space(3f);
    }

    private void DrawDetailPanel()
    {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.ExpandWidth(true), GUILayout.ExpandHeight(true));
        GUILayout.Label("MARKET INTELLIGENCE");
        GUILayout.Space(4f);
        GUILayout.Label(_detailStatus);
        GUILayout.Space(4f);

        _detailScroll = GUILayout.BeginScrollView(_detailScroll, GUILayout.ExpandHeight(true));

        if (_selectedItem is null)
        {
            GUILayout.Label("Select a search result to compare vanilla traders and the local SPT flea market.");
        }
        else
        {
            DrawSelectedItemOverview(_selectedItem);

            if (_loadingDetails)
            {
                GUILayout.Space(10f);
                GUILayout.Label("Loading current trader assortments, player access, and local flea offers...");
            }
            else
            {
                if (_traderSummary is not null)
                {
                    DrawTraderSaleSection(_traderSummary);
                }

                if (_marketSummary is not null)
                {
                    DrawMarketSection(_marketSummary);
                }

                if (_traderSummary is not null)
                {
                    DrawTraderPurchaseSection(_traderSummary);
                }
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndVertical();
    }

    private static void DrawSelectedItemOverview(HermesItemSummary item)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label(item.Name);

        if (!string.Equals(item.Name, item.ShortName, StringComparison.OrdinalIgnoreCase))
        {
            GUILayout.Label($"Short name: {item.ShortName}");
        }

        GUILayout.Label(item.ReferencePrice.HasValue
            ? $"Handbook reference: ₽{item.ReferencePrice.Value:N0}"
            : "Handbook reference: unavailable");

        var uses = new List<string>();
        if (item.AppearsInTraderData)
        {
            uses.Add("Trader data");
        }

        if (item.AppearsInHideoutData)
        {
            uses.Add("Hideout data");
        }

        if (item.AppearsInQuestData)
        {
            uses.Add("Quest data");
        }

        GUILayout.Label(uses.Count > 0
            ? "Detected in: " + string.Join(" • ", uses)
            : "No trader, hideout, or quest references detected.");
        GUILayout.EndVertical();
    }

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
                $"{arrow}  BEST ESTIMATED TRADER SALE PRICE",
                GUILayout.Height(30f),
                GUILayout.ExpandWidth(true)))
        {
            _saleComparisonExpanded = !_saleComparisonExpanded;
        }

        DrawBestSale(summary);

        if (_saleComparisonExpanded)
        {
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
        }

        GUILayout.EndVertical();
    }

    private static void DrawBestSale(HermesTraderSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("BEST ESTIMATED SALE");

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

        GUILayout.EndVertical();
    }

    private void DrawMarketSection(HermesMarketSummaryResponse summary)
    {
        GUILayout.Space(8f);
        GUILayout.BeginVertical(GUI.skin.box);

        var arrow = _marketExpanded ? "▼" : "▶";
        var headline = summary.MedianPrice.HasValue
            ? $"Median ₽{summary.MedianPrice.Value:N0} • {summary.ValidCashOfferCount:N0} cash offer(s)"
            : "No comparable cash offers";

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

        GUILayout.Label(summary.SellRecommendation);

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
        GUILayout.Label("CURRENT LOCAL CASH OFFERS");

        if (!summary.LowestPrice.HasValue)
        {
            GUILayout.Label("No valid comparable cash offer was found.");
        }
        else
        {
            GUILayout.Label($"Lowest: ₽{summary.LowestPrice.Value:N0}");
            GUILayout.Label($"Median: ₽{summary.MedianPrice.GetValueOrDefault():N0}");
            GUILayout.Label($"Average: ₽{summary.AveragePrice.GetValueOrDefault():N0}");
            GUILayout.Label($"Highest reasonable: ₽{summary.HighestReasonablePrice.GetValueOrDefault():N0}");
        }

        GUILayout.Label($"Valid cash offers found: {summary.ValidCashOfferCount:N0}");
        GUILayout.Label($"Offers used for comparison: {summary.ComparableOfferCount:N0}");

        if (summary.UsedLowConditionFallback)
        {
            GUILayout.Label("Condition note: No 80%+ condition offers were found, so used-condition offers were analyzed.");
        }

        var ignoredParts = new List<string>();
        if (summary.IgnoredBarterOfferCount > 0)
        {
            ignoredParts.Add($"barter {summary.IgnoredBarterOfferCount}");
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
            ignoredParts.Add($"below 80% condition {summary.IgnoredLowConditionOfferCount}");
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
            GUILayout.Label("A suggested listing price is unavailable because no comparable cash offer was found.");
        }
        else
        {
            GUILayout.Label($"Suggested listing price: ₽{summary.SuggestedListPrice.Value:N0}");
            GUILayout.Label(summary.EstimatedListingFee.HasValue
                ? $"Estimated listing fee: ₽{summary.EstimatedListingFee.Value:N0}"
                : "Estimated listing fee: unavailable");
            GUILayout.Label(summary.EstimatedNetSale.HasValue
                ? $"Estimated net sale: ₽{summary.EstimatedNetSale.Value:N0}"
                : "Estimated net sale: unavailable");
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

        GUILayout.Label(summary.LowestPrice.HasValue
            ? $"Lowest local flea offer: ₽{summary.LowestPrice.Value:N0}"
            : "Lowest local flea offer: unavailable");

        GUILayout.Label(summary.CheapestAvailableTraderBuyPrice.HasValue
            ? $"Cheapest available cash trader: {summary.CheapestAvailableTraderName} — ₽{summary.CheapestAvailableTraderBuyPrice.Value:N0}"
            : "Cheapest available cash trader: none found");

        GUILayout.Label("Recommendation: " + summary.BuyRecommendation);
        GUILayout.EndVertical();
    }

    private static void DrawLowestFleaOffers(HermesMarketSummaryResponse summary)
    {
        GUILayout.BeginVertical(GUI.skin.box);
        GUILayout.Label("LOWEST COMPARABLE OFFERS");

        if (summary.LowestOffers.Count == 0)
        {
            GUILayout.Label("No comparable offers to display.");
        }
        else
        {
            foreach (var offer in summary.LowestOffers)
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"₽{offer.UnitPrice:N0} each");
                GUILayout.FlexibleSpace();
                GUILayout.Label($"Qty {offer.Quantity:N0} • {offer.ConditionLabel} {offer.ConditionPercent}% • {FormatDuration(offer.SecondsRemaining)} left");
                GUILayout.EndHorizontal();
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

                if (!payment.IsCash && payment.EstimatedRoubleValue > 0)
                {
                    GUILayout.Label($"Handbook estimate: ₽{payment.EstimatedRoubleValue:N0}");
                }

                GUILayout.EndVertical();
            }
        }

        GUILayout.EndVertical();
        GUILayout.Space(4f);
    }

    private void DrawFooter()
    {
        GUILayout.BeginHorizontal();

        if (GUILayout.Button("Clear", GUILayout.Width(90f)))
        {
            Clear();
        }

        GUILayout.FlexibleSpace();
        GUILayout.Label("F8 toggles HERMES");
        GUILayout.Space(12f);

        if (GUILayout.Button("Close", GUILayout.Width(90f)))
        {
            _visible = false;
        }

        GUILayout.EndHorizontal();
    }

    private async Task RunSearchAsync()
    {
        var query = _query.Trim();
        if (query.Length == 0 || _searching)
        {
            return;
        }

        _searching = true;
        _status = $"Searching for \"{query}\"...";
        _selectedItem = null;
        _traderSummary = null;
        _marketSummary = null;
        _saleComparisonExpanded = false;
        _marketExpanded = false;
        _detailStatus = "Select an item to inspect trader and local flea information.";

        try
        {
            var response = await HermesApiClient.SearchAsync(query);
            _results = response.Results ?? [];
            _resultScroll = Vector2.zero;
            _detailScroll = Vector2.zero;
            _status = response.TotalMatches == 0
                ? $"No items matched \"{query}\"."
                : $"Showing {_results.Count} of {response.TotalMatches} match(es).";

            if (_results.Count > 0)
            {
                await SelectItemAsync(_results[0]);
            }
        }
        catch (Exception ex)
        {
            _results = [];
            _selectedItem = null;
            _traderSummary = null;
            _marketSummary = null;
            _status = "HERMES could not contact its server route. Check the BepInEx and SPT server logs.";
            _detailStatus = "Market information unavailable.";
            Plugin.Log.LogError(ex);
        }
        finally
        {
            _searching = false;
        }
    }

    private async Task SelectItemAsync(HermesItemSummary item)
    {
        if (_loadingDetails && _selectedItem?.ItemKey == item.ItemKey)
        {
            return;
        }

        _selectedItem = item;
        _traderSummary = null;
        _marketSummary = null;
        _saleComparisonExpanded = false;
        _marketExpanded = false;
        _loadingDetails = true;
        _detailScroll = Vector2.zero;
        _detailStatus = $"Analyzing traders and the local SPT flea market for {item.Name}...";

        try
        {
            try
            {
                var traderResponse = await HermesApiClient.GetTraderSummaryAsync(item.ItemKey);
                if (_selectedItem?.ItemKey != item.ItemKey)
                {
                    return;
                }

                _traderSummary = traderResponse;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(ex);
                if (_selectedItem?.ItemKey == item.ItemKey)
                {
                    _traderSummary = new HermesTraderSummaryResponse
                    {
                        Found = false,
                        Message = "Trader request failed. Check the BepInEx and SPT server logs."
                    };
                }
            }

            try
            {
                var marketResponse = await HermesApiClient.GetMarketSummaryAsync(item.ItemKey);
                if (_selectedItem?.ItemKey != item.ItemKey)
                {
                    return;
                }

                _marketSummary = marketResponse;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError(ex);
                if (_selectedItem?.ItemKey == item.ItemKey)
                {
                    _marketSummary = new HermesMarketSummaryResponse
                    {
                        Found = false,
                        Message = "Local flea request failed. Check the BepInEx and SPT server logs."
                    };
                }
            }

            if (_selectedItem?.ItemKey == item.ItemKey)
            {
                _detailStatus = "Current profile, trader assortments, and local flea offers loaded.";
            }
        }
        finally
        {
            if (_selectedItem?.ItemKey == item.ItemKey)
            {
                _loadingDetails = false;
            }
        }
    }

    private void Clear()
    {
        _query = string.Empty;
        _results = [];
        _selectedItem = null;
        _traderSummary = null;
        _marketSummary = null;
        _saleComparisonExpanded = false;
        _marketExpanded = false;
        _resultScroll = Vector2.zero;
        _detailScroll = Vector2.zero;
        _status = "Search for an item or ask where it can be bought or sold.";
        _detailStatus = "Select an item to inspect trader and local flea information.";
    }

    private static string FormatCurrency(long amount, string currency)
    {
        return currency.ToUpperInvariant() switch
        {
            "USD" => $"${amount:N0}",
            "EUR" => $"€{amount:N0}",
            "GP" => $"{amount:N0} GP",
            _ => $"₽{amount:N0}"
        };
    }

    private static string FormatDuration(long seconds)
    {
        if (seconds <= 0)
        {
            return "due now";
        }

        var duration = TimeSpan.FromSeconds(seconds);
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration.Hours}h {duration.Minutes}m";
        }

        if (duration.TotalHours >= 1)
        {
            return $"{(int)duration.TotalHours}h {duration.Minutes}m";
        }

        return $"{Math.Max(1, duration.Minutes)}m";
    }
}
