using BepInEx.Configuration;
using UnityEngine;

namespace Hermes.Client;

internal sealed class HermesClientSettings
{
    private const string LastTabPlayerPref = "HERMES.LastTab";

    public ConfigEntry<KeyboardShortcut> ToggleWindowShortcut { get; private set; } = null!;
    public ConfigEntry<string> DefaultOpeningTab { get; private set; } = null!;
    public ConfigEntry<bool> RememberLastSelectedTab { get; private set; } = null!;
    public ConfigEntry<bool> AutomaticallyRefreshWhenOpened { get; private set; } = null!;
    public ConfigEntry<bool> EnableLiveBackgroundRefresh { get; private set; } = null!;
    public ConfigEntry<int> LiveBackgroundRefreshSeconds { get; private set; } = null!;
    public ConfigEntry<bool> SaveProfileWhileHermesOpen { get; private set; } = null!;
    public ConfigEntry<int> ProfileSaveWhileHermesOpenSeconds { get; private set; } = null!;
    public ConfigEntry<int> RequestTimeoutSeconds { get; private set; } = null!;
    public ConfigEntry<bool> DetailedLogging { get; private set; } = null!;
    public ConfigEntry<bool> ShareDuplicateRequests { get; private set; } = null!;
    public ConfigEntry<int> SlowRequestWarningSeconds { get; private set; } = null!;
    public ConfigEntry<int> CacheStatusRefreshSeconds { get; private set; } = null!;
    public ConfigEntry<bool> ShowDiagnosticsFooter { get; private set; } = null!;

    public ConfigEntry<bool> EnableConfirmedActions { get; private set; } = null!;
    public ConfigEntry<bool> AllowHarmlessTestActions { get; private set; } = null!;
    public ConfigEntry<bool> AllowTraderPurchaseActions { get; private set; } = null!;
    public ConfigEntry<bool> AllowTraderSaleActions { get; private set; } = null!;
    public ConfigEntry<bool> AllowFleaListingActions { get; private set; } = null!;
    public ConfigEntry<bool> AllowCraftActions { get; private set; } = null!;
    public ConfigEntry<bool> AllowHideoutUpgradeActions { get; private set; } = null!;

    public ConfigEntry<bool> EnableAssistantTab { get; private set; } = null!;
    public ConfigEntry<bool> ShowAssistantSuggestedPrompts { get; private set; } = null!;
    public ConfigEntry<bool> IncludeSelectedItemInAssistant { get; private set; } = null!;
    public ConfigEntry<bool> EnableAssistantFuzzyEntityMatching { get; private set; } = null!;
    public ConfigEntry<int> AssistantEntityConfidencePercent { get; private set; } = null!;
    public ConfigEntry<int> MaximumAssistantAmbiguityChoices { get; private set; } = null!;
    public ConfigEntry<bool> EnableAssistantCrossSystemReasoning { get; private set; } = null!;
    public ConfigEntry<int> MaximumAssistantRecommendations { get; private set; } = null!;
    public ConfigEntry<bool> PreferPreparedRaidRecommendations { get; private set; } = null!;
    public ConfigEntry<bool> IncludeEconomicAssistantRecommendations { get; private set; } = null!;
    public ConfigEntry<bool> EnableAssistantFollowUpContext { get; private set; } = null!;
    public ConfigEntry<bool> ShowAssistantConversationContext { get; private set; } = null!;
    public ConfigEntry<int> MaximumAssistantContextSubjects { get; private set; } = null!;
    public ConfigEntry<bool> ResetAssistantContextOnProfileChange { get; private set; } = null!;
    public ConfigEntry<int> MaximumAssistantMessages { get; private set; } = null!;

    public ConfigEntry<bool> EnableProactiveAssistantNotices { get; private set; } = null!;
    public ConfigEntry<int> AssistantNoticeCheckIntervalMinutes { get; private set; } = null!;
    public ConfigEntry<bool> ShowAssistantNoticesWhenClosed { get; private set; } = null!;
    public ConfigEntry<bool> ShowAssistantNoticesDuringRaid { get; private set; } = null!;
    public ConfigEntry<bool> NotifyLoadoutReadiness { get; private set; } = null!;
    public ConfigEntry<bool> NotifyHighValueUninsuredItems { get; private set; } = null!;
    public ConfigEntry<bool> NotifyCompletedHideoutProduction { get; private set; } = null!;
    public ConfigEntry<bool> NotifyReadyHideoutUpgrades { get; private set; } = null!;
    public ConfigEntry<bool> NotifyReadyProfitableCrafts { get; private set; } = null!;
    public ConfigEntry<int> MinimumAssistantNoticeCraftProfit { get; private set; } = null!;
    public ConfigEntry<bool> NotifyStashOpportunities { get; private set; } = null!;
    public ConfigEntry<int> MinimumAssistantNoticeStashValue { get; private set; } = null!;

    public ConfigEntry<bool> UseNativeInventoryTabs { get; private set; } = null!;

    public ConfigEntry<bool> CompactMode { get; private set; } = null!;
    public ConfigEntry<bool> ShowHelpText { get; private set; } = null!;
    public ConfigEntry<bool> ShowSectionDescriptions { get; private set; } = null!;
    public ConfigEntry<bool> CollapseSectionsByDefault { get; private set; } = null!;
    public ConfigEntry<int> MaximumRowsPerSection { get; private set; } = null!;

    public ConfigEntry<int> MaximumSearchResults { get; private set; } = null!;
    public ConfigEntry<bool> SearchWhileTyping { get; private set; } = null!;
    public ConfigEntry<int> MinimumSearchCharacters { get; private set; } = null!;
    public ConfigEntry<bool> ShowItemShortNames { get; private set; } = null!;
    public ConfigEntry<bool> ShowItemReferencePrices { get; private set; } = null!;

    public ConfigEntry<int> MinimumComparableFleaOffers { get; private set; } = null!;
    public ConfigEntry<bool> ExpandTraderComparisonByDefault { get; private set; } = null!;
    public ConfigEntry<bool> ExpandMarketByDefault { get; private set; } = null!;
    public ConfigEntry<bool> ShowConvertedBarterOffers { get; private set; } = null!;
    public ConfigEntry<bool> ShowDetailedBarterCalculations { get; private set; } = null!;
    public ConfigEntry<bool> ShowListingFeeEstimates { get; private set; } = null!;
    public ConfigEntry<bool> ShowFullAssemblyValuation { get; private set; } = null!;
    public ConfigEntry<int> MaximumFleaOffersDisplayed { get; private set; } = null!;

    public ConfigEntry<string> DefaultHideoutFilter { get; private set; } = null!;
    public ConfigEntry<bool> ShowCompletedHideoutAreas { get; private set; } = null!;
    public ConfigEntry<bool> ShowOnlyMissingHideoutRequirements { get; private set; } = null!;
    public ConfigEntry<bool> ShowHideoutAcquisitionPlans { get; private set; } = null!;
    public ConfigEntry<bool> ShowHideoutDetailedPriceSources { get; private set; } = null!;

    public ConfigEntry<string> DefaultCraftFilter { get; private set; } = null!;
    public ConfigEntry<string> DefaultCraftSorting { get; private set; } = null!;
    public ConfigEntry<int> MinimumCraftProfit { get; private set; } = null!;
    public ConfigEntry<int> MinimumCraftProfitPercent { get; private set; } = null!;
    public ConfigEntry<int> OvernightMinimumHours { get; private set; } = null!;
    public ConfigEntry<int> OvernightMaximumHours { get; private set; } = null!;
    public ConfigEntry<bool> HideCraftsWithMissingIngredients { get; private set; } = null!;
    public ConfigEntry<bool> HideUnavailableCrafts { get; private set; } = null!;
    public ConfigEntry<bool> ShowDetailedCraftAcquisitionPlan { get; private set; } = null!;

    public ConfigEntry<string> DefaultStashView { get; private set; } = null!;
    public ConfigEntry<string> DefaultStashSorting { get; private set; } = null!;
    public ConfigEntry<bool> IncludeActiveQuestReservations { get; private set; } = null!;
    public ConfigEntry<bool> IncludeFutureQuestReservations { get; private set; } = null!;
    public ConfigEntry<bool> IncludeNextHideoutReservations { get; private set; } = null!;
    public ConfigEntry<bool> IncludeFutureHideoutReservations { get; private set; } = null!;
    public ConfigEntry<bool> PreferFoundInRaidCopies { get; private set; } = null!;
    public ConfigEntry<int> DuplicateBaselineReserve { get; private set; } = null!;
    public ConfigEntry<int> StashWeaponDurabilityThreshold { get; private set; } = null!;
    public ConfigEntry<int> StashArmorDurabilityThreshold { get; private set; } = null!;
    public ConfigEntry<int> StashLowResourceThreshold { get; private set; } = null!;
    public ConfigEntry<int> StashKeyUsesWarningThreshold { get; private set; } = null!;
    public ConfigEntry<int> MinimumCleanupValue { get; private set; } = null!;
    public ConfigEntry<int> MinimumValuePerRecoveredCell { get; private set; } = null!;
    public ConfigEntry<int> MaximumStashRecommendations { get; private set; } = null!;
    public ConfigEntry<bool> ShowProtectedCurrencies { get; private set; } = null!;
    public ConfigEntry<bool> ShowUnsupportedStashItems { get; private set; } = null!;
    public ConfigEntry<bool> ShowStashReservationReasons { get; private set; } = null!;

    public ConfigEntry<int> MinimumWeaponDurabilityPercent { get; private set; } = null!;
    public ConfigEntry<int> MinimumArmorDurabilityPercent { get; private set; } = null!;
    public ConfigEntry<int> MinimumLoadedRounds { get; private set; } = null!;
    public ConfigEntry<int> MinimumSpareMagazines { get; private set; } = null!;
    public ConfigEntry<int> MinimumSpareRounds { get; private set; } = null!;
    public ConfigEntry<int> MinimumHealingResource { get; private set; } = null!;
    public ConfigEntry<bool> RequireHeavyBleedTreatment { get; private set; } = null!;
    public ConfigEntry<bool> RequireLightBleedTreatment { get; private set; } = null!;
    public ConfigEntry<bool> RequireFractureTreatment { get; private set; } = null!;
    public ConfigEntry<bool> RequirePainTreatment { get; private set; } = null!;
    public ConfigEntry<bool> RequireHydrationProvision { get; private set; } = null!;
    public ConfigEntry<bool> RequireEnergyProvision { get; private set; } = null!;
    public ConfigEntry<int> HydrationWarningPercent { get; private set; } = null!;
    public ConfigEntry<int> EnergyWarningPercent { get; private set; } = null!;
    public ConfigEntry<bool> ShowValueAndInsurance { get; private set; } = null!;
    public ConfigEntry<bool> EnableInsuranceWarnings { get; private set; } = null!;
    public ConfigEntry<int> HighValueUninsuredThreshold { get; private set; } = null!;
    public ConfigEntry<bool> ShowCompletedQuestObjectives { get; private set; } = null!;
    public ConfigEntry<bool> CollapseWarningGroupsByDefault { get; private set; } = null!;
    public ConfigEntry<string> DefaultLoadoutView { get; private set; } = null!;
    public ConfigEntry<bool> ShowReadinessScoreBar { get; private set; } = null!;
    public ConfigEntry<bool> HideEmptyLoadoutSections { get; private set; } = null!;
    public ConfigEntry<bool> ShowProtectedSlotValue { get; private set; } = null!;
    public ConfigEntry<bool> ShowInsuranceCostEstimate { get; private set; } = null!;

    public ConfigEntry<string> RaidPlannerDefaultSorting { get; private set; } = null!;
    public ConfigEntry<bool> RaidPlannerShowInferredRouteKeys { get; private set; } = null!;
    public ConfigEntry<bool> RaidPlannerShowAcquireInRaidItems { get; private set; } = null!;
    public ConfigEntry<bool> RaidPlannerShowHandoverObjectives { get; private set; } = null!;
    public ConfigEntry<bool> RaidPlannerShowFirHandoverObjectives { get; private set; } = null!;
    public ConfigEntry<bool> RaidPlannerIncludeQuestGearRestrictions { get; private set; } = null!;
    public ConfigEntry<bool> RaidPlannerIncludeMedicalReadiness { get; private set; } = null!;
    public ConfigEntry<bool> RaidPlannerIncludeAmmunitionReadiness { get; private set; } = null!;
    public ConfigEntry<bool> RaidPlannerIncludeInsuranceWarnings { get; private set; } = null!;
    public ConfigEntry<bool> RaidPlannerShowPlanNotes { get; private set; } = null!;


    public void Bind(ConfigFile config)
    {
        ToggleWindowShortcut = config.Bind(
            "General",
            "Toggle HERMES inventory tab",
            new KeyboardShortcut(KeyCode.F8),
            "Keyboard shortcut used to open HERMES inside the current Character or in-raid inventory screen, or return to the previous EFT inventory tab.");
        DefaultOpeningTab = config.Bind(
            "General",
            "Default opening workspace",
            "Assistant",
            Choice(
                "Workspace shown when HERMES opens and remembered-workspace behavior is disabled.",
                "Assistant",
                "Items & Market",
                "Actions",
                "Hideout",
                "Crafts",
                "Stash",
                "Loadout",
                "Raid Planner"));
        RememberLastSelectedTab = config.Bind(
            "General",
            "Remember last selected tab",
            true,
            "Reopens HERMES on the most recently selected main tab.");
        AutomaticallyRefreshWhenOpened = config.Bind(
            "General",
            "Read prepared workspace when opened",
            true,
            "Legacy compatibility setting. HERMES now refreshes the active prepared workspace whenever it opens or the selected HERMES workspace changes.");
        EnableLiveBackgroundRefresh = config.Bind(
            "General",
            "Live background refresh",
            true,
            "Keeps HERMES workspace data and Assistant alerts synchronized with the SPT server even when the HERMES tab or workspace is not open.");
        LiveBackgroundRefreshSeconds = config.Bind(
            "General",
            "Live background refresh seconds",
            15,
            "Seconds between lightweight server revision checks. Changed domains are refreshed and the Assistant alert feed is rebuilt automatically. Range: 10-300.");
        SaveProfileWhileHermesOpen = config.Bind(
            "General",
            "Save profile while HERMES is open",
            true,
            "Asks the SPT server to persist the active profile when HERMES opens and periodically while it remains visible, matching EFT screens that trigger profile persistence.");
        ProfileSaveWhileHermesOpenSeconds = config.Bind(
            "General",
            "Profile save while HERMES is open seconds",
            30,
            "Seconds between HERMES-triggered active profile saves while the HERMES tab is visible. Range: 15-300.");
        RequestTimeoutSeconds = config.Bind(
            "General",
            "Request timeout seconds",
            12,
            "Timeout for normal HERMES requests. Full stash and loadout analysis always receive at least 30 seconds. Range: 5-60.");
        DetailedLogging = config.Bind(
            "General",
            "Detailed logging",
            false,
            "Logs successful request timings, tab changes, and shared UI state changes to the BepInEx log.");

        ShareDuplicateRequests = config.Bind(
            "Reliability",
            "Share duplicate in-flight requests",
            true,
            "When multiple panels request the same read-only route at the same time, they share one server request instead of duplicating work.");
        SlowRequestWarningSeconds = config.Bind(
            "Reliability",
            "Slow request warning seconds",
            3,
            "Logs a warning when a HERMES request exceeds this duration. Range: 1-30 seconds.");
        CacheStatusRefreshSeconds = config.Bind(
            "Reliability",
            "Cache status refresh seconds",
            10,
            "How often the visible HERMES navigation rail refreshes server cache diagnostics. Range: 5-60 seconds.");
        ShowDiagnosticsFooter = config.Bind(
            "Reliability",
            "Show diagnostics panel",
            false,
            "Shows compact request and cache health in the HERMES navigation rail.");

        EnableConfirmedActions = config.Bind(
            "Actions",
            "Enable confirmed actions",
            true,
            "Master switch for the HERMES action-confirmation pipeline. Alpha1 still performs no real inventory actions.");
        AllowHarmlessTestActions = config.Bind(
            "Actions",
            "Allow harmless test actions",
            true,
            "Allows the alpha1 no-op test action used to verify proposal, confirmation, token, result, and history flow.");
        AllowTraderPurchaseActions = config.Bind(
            "Actions",
            "Allow trader purchase actions",
            false,
            "Reserved for a future release. Alpha1 will not buy items.");
        AllowTraderSaleActions = config.Bind(
            "Actions",
            "Allow trader sale actions",
            false,
            "Reserved for a future release. Alpha1 will not sell items.");
        AllowFleaListingActions = config.Bind(
            "Actions",
            "Allow flea listing actions",
            false,
            "Reserved for a future release. Alpha1 will not create Flea listings.");
        AllowCraftActions = config.Bind(
            "Actions",
            "Allow craft actions",
            false,
            "Reserved for a future release. Alpha1 will not start or collect crafts.");
        AllowHideoutUpgradeActions = config.Bind(
            "Actions",
            "Allow hideout upgrade actions",
            false,
            "Reserved for a future release. Alpha1 will not start hideout upgrades.");

        EnableAssistantTab = config.Bind(
            "Assistant",
            "Enable Assistant tab",
            true,
            "Shows the local conversational Assistant tab. The Assistant is deterministic, read-only, and does not use an external AI service.");
        ShowAssistantSuggestedPrompts = config.Bind(
            "Assistant",
            "Show suggested prompts",
            true,
            "Shows quick prompt buttons beneath the Assistant conversation.");
        IncludeSelectedItemInAssistant = config.Bind(
            "Assistant",
            "Include selected item context",
            true,
            "Allows Assistant questions such as 'What is this item worth?' to use the current Item Search or Ask HERMES selection.");
        EnableAssistantFuzzyEntityMatching = config.Bind(
            "Assistant",
            "Enable fuzzy entity matching",
            true,
            "Allows minor spelling differences when matching player-facing item, quest, map, craft, station, and hideout-area names.");
        AssistantEntityConfidencePercent = config.Bind(
            "Assistant",
            "Entity confidence percent",
            68,
            "Minimum local match confidence before HERMES accepts a named entity. Lower values accept broader matches; higher values require more exact names. Range: 40-100.");
        MaximumAssistantAmbiguityChoices = config.Bind(
            "Assistant",
            "Maximum ambiguity choices",
            5,
            "Maximum possible item, quest, craft, map, station, or hideout-area matches listed when HERMES needs clarification. Range: 2-10.");
        EnableAssistantCrossSystemReasoning = config.Bind(
            "Assistant",
            "Enable cross-system reasoning",
            true,
            "Ranks next steps using loadout, Raid Planner, stash, crafts, and hideout data together.");
        MaximumAssistantRecommendations = config.Bind(
            "Assistant",
            "Maximum ranked recommendations",
            5,
            "Maximum cross-system next steps shown in one Assistant answer. Range: 3-10.");
        PreferPreparedRaidRecommendations = config.Bind(
            "Assistant",
            "Prefer prepared raid plans",
            true,
            "Gives prepared maps an additional ranking bonus when HERMES recommends the best current raid.");
        IncludeEconomicAssistantRecommendations = config.Bind(
            "Assistant",
            "Include economic recommendations",
            true,
            "Allows cross-system answers to recommend profitable ready crafts, safe-to-sell surplus, and stash cleanup opportunities.");
        EnableAssistantFollowUpContext = config.Bind(
            "Assistant",
            "Enable follow-up conversation context",
            true,
            "Allows HERMES to remember resolved items, quests, maps, crafts, stations, and hideout areas for follow-up questions such as 'where do I use it?' or 'what key?'.");
        ShowAssistantConversationContext = config.Bind(
            "Assistant",
            "Show conversation context",
            true,
            "Shows the remembered conversation subject and selected-item context above the Assistant conversation.");
        MaximumAssistantContextSubjects = config.Bind(
            "Assistant",
            "Maximum remembered subjects",
            6,
            "Maximum recent item, quest, map, craft, station, and hideout-area subjects retained for the current local conversation. Range: 1-12.");
        ResetAssistantContextOnProfileChange = config.Bind(
            "Assistant",
            "Reset context when PMC profile changes",
            true,
            "Clears the local Assistant conversation and remembered subjects when the active SPT profile token changes, preventing follow-ups from using a previous PMC's context.");
        MaximumAssistantMessages = config.Bind(
            "Assistant",
            "Maximum conversation messages",
            40,
            "Maximum user and HERMES messages retained in the current local conversation. Range: 10-200.");

        EnableProactiveAssistantNotices = config.Bind(
            "Assistant Alerts",
            "Enable alerts",
            true,
            "Checks current read-only HERMES data and shows one compact alert at a time for meaningful loadout, hideout, craft, insurance, and optional stash conditions.");
        AssistantNoticeCheckIntervalMinutes = config.Bind(
            "Assistant Alerts",
            "Check interval minutes",
            5,
            "Minutes between automatic alert checks. A compact Check button remains available in the Assistant workspace. Range: 1-60.");
        ShowAssistantNoticesWhenClosed = config.Bind(
            "Assistant Alerts",
            "Show alerts while HERMES tab is inactive",
            true,
            "Shows one compact native EFT alert while the HERMES inventory tab is not selected.");
        ShowAssistantNoticesDuringRaid = config.Bind(
            "Assistant Alerts",
            "Show alerts during raid",
            false,
            "Allows automatic checks and one-line alerts while a raid is active. Disabled by default to avoid combat distractions.");
        NotifyLoadoutReadiness = config.Bind(
            "Assistant Alerts",
            "Loadout readiness alerts",
            true,
            "Notifies when the current loadout has critical readiness warnings.");
        NotifyHighValueUninsuredItems = config.Bind(
            "Assistant Alerts",
            "High-value uninsured alerts",
            true,
            "Notifies when current uninsured at-risk value meets the configured Loadout high-value threshold.");
        NotifyCompletedHideoutProduction = config.Bind(
            "Assistant Alerts",
            "Completed production alerts",
            true,
            "Notifies when hideout production is complete and ready to collect.");
        NotifyReadyHideoutUpgrades = config.Bind(
            "Assistant Alerts",
            "Ready hideout upgrade alerts",
            true,
            "Notifies when one or more hideout areas are ready to upgrade.");
        NotifyReadyProfitableCrafts = config.Bind(
            "Assistant Alerts",
            "Ready profitable craft alerts",
            true,
            "Notifies when a currently startable craft meets the minimum economic-profit threshold.");
        MinimumAssistantNoticeCraftProfit = config.Bind(
            "Assistant Alerts",
            "Minimum craft notice profit",
            25000,
            "Minimum estimated economic profit required for a ready-craft notice. Range: 0-10,000,000 roubles.");
        NotifyStashOpportunities = config.Bind(
            "Assistant Alerts",
            "Stash opportunity alerts",
            false,
            "Enables periodic Stash analysis notices for cleanup and safe-to-sell surplus. Disabled by default because Stash analysis is the heaviest notice check.");
        MinimumAssistantNoticeStashValue = config.Bind(
            "Assistant Alerts",
            "Minimum stash notice value",
            100000,
            "Minimum cleanup or safe-to-sell value required for a Stash notice. Range: 0-100,000,000 roubles.");

        UseNativeInventoryTabs = config.Bind(
            "Interface",
            "Enable HERMES inventory tab",
            true,
            "Adds the inventory-only HERMES workspace to both the main Character screen and the inventory opened during a raid. HERMES has no floating-window fallback and keeps the native EFT navigation visible.");
        CompactMode = config.Bind(
            "Interface",
            "Compact mode",
            false,
            "Reduces shared spacing, header height, and status-panel padding.");
        ShowHelpText = config.Bind(
            "Interface",
            "Show help text",
            true,
            "Shows the global Ask HERMES help line and empty-state guidance.");
        ShowSectionDescriptions = config.Bind(
            "Interface",
            "Show section descriptions",
            true,
            "Shows explanatory text beneath shared panel headers.");
        CollapseSectionsByDefault = config.Bind(
            "Interface",
            "Collapse sections by default",
            true,
            "Starts useful collapsible detail sections closed until opened. Items & Market sections with no current value, requirement, or owned copy are always collapsed initially.");
        MaximumRowsPerSection = config.Bind(
            "Interface",
            "Maximum rows per section",
            80,
            "Maximum number of rows rendered in long lists before a hidden-row notice is shown. Lower values reduce Unity layout work. Range: 25-120.");
        MaximumSearchResults = config.Bind(
            "Item Search",
            "Maximum search results",
            30,
            "Maximum player-facing item matches requested from the server. Range: 5-50.");
        SearchWhileTyping = config.Bind(
            "Item Search",
            "Search while typing",
            true,
            "Runs a debounced item search after the configured minimum character count is reached.");
        MinimumSearchCharacters = config.Bind(
            "Item Search",
            "Minimum search characters",
            2,
            "Minimum trimmed characters required before manual or automatic item search. Range: 1-10.");
        ShowItemShortNames = config.Bind(
            "Item Search",
            "Show item short names",
            true,
            "Shows short names beneath full item names in search results and selected-item details.");
        ShowItemReferencePrices = config.Bind(
            "Item Search",
            "Show handbook reference prices",
            true,
            "Shows handbook reference prices in search results and the selected-item header.");

        MinimumComparableFleaOffers = config.Bind(
            "Market Intelligence",
            "Minimum comparable flea offers",
            3,
            "Offer count required before the UI labels an active flea estimate reliable. This does not change the server's pricing order. Range: 1-20.");
        ExpandTraderComparisonByDefault = config.Bind(
            "Market Intelligence",
            "Expand trader comparison by default",
            false,
            "Starts the full Items & Market Traders section expanded, including sale comparisons and trader purchase offers.");
        ExpandMarketByDefault = config.Bind(
            "Market Intelligence",
            "Expand flea market by default",
            false,
            "Starts the full Items & Market Flea Market section expanded when selecting an item.");
        ShowConvertedBarterOffers = config.Bind(
            "Market Intelligence",
            "Show converted flea barter offers",
            true,
            "Shows converted barter offers in the lowest-offers list. They remain part of server market valuation regardless of this display option.");
        ShowDetailedBarterCalculations = config.Bind(
            "Market Intelligence",
            "Show detailed barter calculations",
            false,
            "Shows requirement-by-requirement market calculations for trader barters.");
        ShowListingFeeEstimates = config.Bind(
            "Market Intelligence",
            "Show listing fee estimates",
            true,
            "Shows estimated flea listing fees and net-sale values when available.");
        ShowFullAssemblyValuation = config.Bind(
            "Market Intelligence",
            "Show full assembly valuation",
            true,
            "Shows installed attachment and armor-insert valuation details for exact items and flea assemblies.");
        MaximumFleaOffersDisplayed = config.Bind(
            "Market Intelligence",
            "Maximum flea offers displayed",
            10,
            "Maximum comparable flea rows rendered for a selected item. Range: 1-50.");

        DefaultHideoutFilter = config.Bind(
            "Hideout",
            "Default area filter",
            "Actionable",
            Choice(
                "Initial Hideout area filter.",
                "All",
                "Actionable",
                "Ready",
                "Missing Materials",
                "Progression Blocked",
                "In Progress",
                "Completed"));
        ShowCompletedHideoutAreas = config.Bind(
            "Hideout",
            "Show completed areas",
            false,
            "Allows maximum-level hideout areas to appear outside the explicit Completed filter.");
        ShowOnlyMissingHideoutRequirements = config.Bind(
            "Hideout",
            "Show only missing requirements",
            false,
            "Hides completed requirements in the selected hideout-area detail view.");
        ShowHideoutAcquisitionPlans = config.Bind(
            "Hideout",
            "Show acquisition estimates",
            true,
            "Shows estimated missing-material costs and current acquisition sources.");
        ShowHideoutDetailedPriceSources = config.Bind(
            "Hideout",
            "Show detailed price sources",
            true,
            "Shows the unit source and unit price used for each missing item requirement.");

        DefaultCraftFilter = config.Bind(
            "Crafts",
            "Default craft filter",
            "Available Crafts",
            Choice(
                "Initial recipe filter.",
                "All",
                "Available Crafts",
                "Ready Now",
                "Profitable",
                "Overnight",
                "Active"));
        DefaultCraftSorting = config.Bind(
            "Crafts",
            "Default craft sorting",
            "Profit per hour",
            Choice(
                "Initial recipe ordering. Selecting the Profitable filter always orders highest profit first.",
                "Name",
                "Station",
                "Duration",
                "Profit",
                "Profit percentage",
                "Profit per hour",
                "Missing ingredients"));
        MinimumCraftProfit = config.Bind(
            "Crafts",
            "Minimum economic profit",
            0,
            "Minimum estimated economic profit required by the Profitable filter. Range: -10000000 to 10000000.");
        MinimumCraftProfitPercent = config.Bind(
            "Crafts",
            "Minimum economic profit percent",
            0,
            "Minimum economic return percentage required by the Profitable filter. Range: -100 to 10000.");
        OvernightMinimumHours = config.Bind(
            "Crafts",
            "Overnight minimum hours",
            4,
            "Minimum craft duration for the Overnight filter. Range: 1-48.");
        OvernightMaximumHours = config.Bind(
            "Crafts",
            "Overnight maximum hours",
            12,
            "Maximum craft duration for the Overnight filter. Range: 1-72.");
        HideCraftsWithMissingIngredients = config.Bind(
            "Crafts",
            "Hide crafts with missing ingredients",
            false,
            "Hides recipes whose required ingredients are not currently owned, even in the All filter.");
        HideUnavailableCrafts = config.Bind(
            "Crafts",
            "Hide unavailable crafts",
            false,
            "Hides recipes locked by station level, quest progression, or recipe unlock state.");
        ShowDetailedCraftAcquisitionPlan = config.Bind(
            "Crafts",
            "Show detailed acquisition plans",
            true,
            "Shows per-source acquisition lines for missing ingredients in recipe details.");

        DefaultStashView = config.Bind(
            "Stash",
            "Default stash view",
            "Overview",
            Choice(
                "View selected when the Stash workspace is reset.",
                "Overview",
                "Safe to Sell",
                "Cleanup",
                "Keep",
                "Review",
                "Duplicates",
                "Damaged"));
        DefaultStashSorting = config.Bind(
            "Stash",
            "Default stash sorting",
            "Recommendation",
            Choice(
                "Initial recommendation ordering.",
                "Recommendation",
                "Name",
                "Sell value",
                "Sellable quantity",
                "Value per cell",
                "Occupied cells",
                "Condition",
                "Destination",
                "Reserved quantity"));
        IncludeActiveQuestReservations = config.Bind(
            "Stash Reservations",
            "Include active quest reservations",
            true,
            "Protects owned quantities needed by currently active quest handover objectives.");
        IncludeFutureQuestReservations = config.Bind(
            "Stash Reservations",
            "Include future quest reservations",
            true,
            "Protects owned quantities needed by known future quest handover objectives.");
        IncludeNextHideoutReservations = config.Bind(
            "Stash Reservations",
            "Include next hideout upgrade",
            true,
            "Protects owned materials needed by the next level of each hideout area.");
        IncludeFutureHideoutReservations = config.Bind(
            "Stash Reservations",
            "Include future hideout upgrades",
            true,
            "Protects owned materials needed by later hideout levels.");
        PreferFoundInRaidCopies = config.Bind(
            "Stash Reservations",
            "Prefer found-in-raid copies",
            true,
            "When several copies can satisfy a reservation, prefers found-in-raid copies. FIR-required objectives always reserve FIR copies regardless of this setting.");
        DuplicateBaselineReserve = config.Bind(
            "Stash Recommendations",
            "Duplicate baseline reserve",
            1,
            "When no quest or hideout reserve exists, the duplicate view keeps this many units before reporting possible excess. Range: 0-1000.");
        StashWeaponDurabilityThreshold = config.Bind(
            "Stash Condition",
            "Weapon durability warning percent",
            70,
            "Stash weapons below this durability appear in Damaged. Range: 1-100.");
        StashArmorDurabilityThreshold = config.Bind(
            "Stash Condition",
            "Armor durability warning percent",
            50,
            "Stash armor and generic durable items below this durability appear in Damaged. Range: 1-100.");
        StashLowResourceThreshold = config.Bind(
            "Stash Condition",
            "Low resource warning percent",
            20,
            "Medical, repair, and consumable resources below this percentage appear in Damaged. Range: 0-100.");
        StashKeyUsesWarningThreshold = config.Bind(
            "Stash Condition",
            "Key uses warning threshold",
            1,
            "Keys with this many uses remaining or fewer appear in Damaged. Set to 0 to disable. Range: 0-100.");
        MinimumCleanupValue = config.Bind(
            "Stash Cleanup",
            "Minimum cleanup sale value",
            0,
            "Minimum best-destination value for an exact item instance to enter Cleanup. Range: 0-100000000.");
        MinimumValuePerRecoveredCell = config.Bind(
            "Stash Cleanup",
            "Minimum value per recovered cell",
            0,
            "Minimum roubles recovered per occupied stash cell for Cleanup candidates. Range: 0-10000000.");
        MaximumStashRecommendations = config.Bind(
            "Stash Display",
            "Maximum recommendations returned",
            300,
            "Maximum recommendation rows returned by the server. Totals still include all supported items. Range: 25-1000.");
        ShowProtectedCurrencies = config.Bind(
            "Stash Display",
            "Show protected currencies",
            true,
            "Shows roubles, dollars, and euros in recommendation and value lists while still excluding them from sale advice.");
        ShowUnsupportedStashItems = config.Bind(
            "Stash Display",
            "Show unsupported item count",
            true,
            "Shows the count and explanation for quest-only or handbook-less independent items excluded from analysis.");
        ShowStashReservationReasons = config.Bind(
            "Stash Display",
            "Show detailed reservation reasons",
            true,
            "Shows the exact quest and hideout reasons behind keep and surplus quantities.");

        MinimumWeaponDurabilityPercent = config.Bind(
            "Loadout Readiness",
            "Minimum weapon durability percent",
            70,
            "Weapons below this durability generate a readiness warning. Range: 1-100.");
        MinimumArmorDurabilityPercent = config.Bind(
            "Loadout Readiness",
            "Minimum armor durability percent",
            50,
            "Armor or inserts below this durability generate a readiness warning. Range: 1-100.");
        MinimumLoadedRounds = config.Bind(
            "Loadout Readiness",
            "Minimum loaded rounds per weapon",
            1,
            "Each equipped firearm should contain at least this many loaded rounds. Range: 0-200.");
        MinimumSpareMagazines = config.Bind(
            "Loadout Readiness",
            "Minimum compatible spare magazines",
            1,
            "Each equipped firearm should have at least this many compatible spare magazines. Range: 0-20.");
        MinimumSpareRounds = config.Bind(
            "Loadout Readiness",
            "Minimum compatible spare rounds",
            30,
            "Each equipped firearm should have at least this many compatible rounds outside the installed magazine. Loaded spare magazines and loose rounds both count. Range: 0-1000.");
        MinimumHealingResource = config.Bind(
            "Loadout Readiness",
            "Minimum total healing resource",
            100,
            "Combined carried health-restoration resource required before HERMES reports adequate healing. Range: 0-5000.");
        HydrationWarningPercent = config.Bind(
            "Loadout Readiness",
            "Hydration warning percent",
            50,
            "Current hydration below this percentage generates a warning. Range: 0-100.");
        EnergyWarningPercent = config.Bind(
            "Loadout Readiness",
            "Energy warning percent",
            50,
            "Current energy below this percentage generates a warning. Range: 0-100.");
        RequireHeavyBleedTreatment = config.Bind(
            "Loadout Medical Requirements",
            "Require heavy-bleed treatment",
            true,
            "Adds a critical readiness warning when no carried item can treat heavy bleeding.");
        RequireLightBleedTreatment = config.Bind(
            "Loadout Medical Requirements",
            "Require light-bleed treatment",
            true,
            "Adds a readiness warning when no carried item can treat light bleeding.");
        RequireFractureTreatment = config.Bind(
            "Loadout Medical Requirements",
            "Require fracture treatment",
            true,
            "Adds a readiness warning when no carried item can treat fractures.");
        RequirePainTreatment = config.Bind(
            "Loadout Medical Requirements",
            "Require pain treatment",
            true,
            "Adds a readiness warning when no carried item can treat pain.");
        RequireHydrationProvision = config.Bind(
            "Loadout Sustainment Requirements",
            "Require hydration provision",
            false,
            "Adds a readiness warning when no carried food or drink item provides hydration.");
        RequireEnergyProvision = config.Bind(
            "Loadout Sustainment Requirements",
            "Require energy provision",
            false,
            "Adds a readiness warning when no carried food or drink item provides energy.");

        ShowValueAndInsurance = config.Bind(
            "Loadout Display",
            "Show Value and Insurance",
            true,
            "Enables exact carried-item valuation and displays the Value & Insurance view.");
        EnableInsuranceWarnings = config.Bind(
            "Loadout Display",
            "Enable insurance warnings",
            true,
            "Adds readiness warnings for high-value uninsured equipment.");
        HighValueUninsuredThreshold = config.Bind(
            "Loadout Display",
            "High-value uninsured threshold",
            100000,
            "Replacement value in roubles at which an uninsured item generates a warning. Range: 0-10000000.");
        ShowCompletedQuestObjectives = config.Bind(
            "Loadout Display",
            "Show completed quest objectives",
            true,
            "When disabled, Raid Planner hides already-completed objective rows.");
        CollapseWarningGroupsByDefault = config.Bind(
            "Loadout Display",
            "Collapse warning groups by default",
            false,
            "Starts the Overview warning categories collapsed until opened.");
        DefaultLoadoutView = config.Bind(
            "Loadout Display",
            "Default loadout view",
            "Overview",
            Choice(
                "Initial Loadout sub-view.",
                "Overview",
                "Weapons & Ammo",
                "Armor",
                "Medical",
                "Quest Gear",
                "Raid Planner",
                "Value & Insurance"));
        ShowReadinessScoreBar = config.Bind(
            "Loadout Display",
            "Show readiness score bar",
            true,
            "Shows a visual 0-100 readiness bar beneath the Loadout summary metrics.");
        HideEmptyLoadoutSections = config.Bind(
            "Loadout Display",
            "Hide empty loadout sections",
            true,
            "Suppresses empty detail sections when the corresponding equipment or item category is not present.");
        ShowProtectedSlotValue = config.Bind(
            "Loadout Display",
            "Show protected-slot value",
            true,
            "Shows secure-container, melee, compass, armband, and special-slot value in Value & Insurance.");
        ShowInsuranceCostEstimate = config.Bind(
            "Loadout Display",
            "Show insurance-cost estimate",
            true,
            "Shows HERMES' estimated insurance checkout cost when a configured insurer coefficient can be resolved.");

        RaidPlannerDefaultSorting = config.Bind(
            "Raid Planner",
            "Default map sorting",
            "Best prepared",
            Choice(
                "Initial Raid Planner map ordering.",
                "Best prepared",
                "Most active quests",
                "Most incomplete objectives",
                "Fewest missing requirements",
                "Alphabetical"));
        RaidPlannerShowInferredRouteKeys = config.Bind(
            "Raid Planner",
            "Show inferred route keys",
            true,
            "Shows route-key requirements inferred from local quest text, locales, key data, and HERMES vanilla quest rules.");
        RaidPlannerShowAcquireInRaidItems = config.Bind(
            "Raid Planner",
            "Show items acquired during raid",
            true,
            "Shows requirements such as Saving the Mole's science-office key that are obtained after deployment.");
        RaidPlannerShowHandoverObjectives = config.Bind(
            "Raid Planner",
            "Show handover objectives",
            true,
            "Shows item handover and turn-in objective rows in map plans.");
        RaidPlannerShowFirHandoverObjectives = config.Bind(
            "Raid Planner",
            "Show FIR handover objectives",
            true,
            "Shows found-in-raid handover rows when general handover objectives are enabled.");
        RaidPlannerIncludeQuestGearRestrictions = config.Bind(
            "Raid Planner",
            "Include quest gear restrictions",
            true,
            "Shows the combined bring, equip, key, marker, and acquire-in-raid checklist.");
        RaidPlannerIncludeMedicalReadiness = config.Bind(
            "Raid Planner",
            "Include medical readiness context",
            true,
            "Shows current Medical warnings beside map plans without changing quest requirements.");
        RaidPlannerIncludeAmmunitionReadiness = config.Bind(
            "Raid Planner",
            "Include ammunition readiness context",
            true,
            "Shows current Weapons and Ammunition warnings beside map plans without changing quest requirements.");
        RaidPlannerIncludeInsuranceWarnings = config.Bind(
            "Raid Planner",
            "Include insurance warning context",
            false,
            "Shows current Insurance warnings beside map plans without changing quest requirements.");
        RaidPlannerShowPlanNotes = config.Bind(
            "Raid Planner",
            "Show plan notes",
            true,
            "Shows HERMES notes about equipment conflicts, inferred keys, and acquire-in-raid requirements.");

    }

    private static ConfigDescription Choice(string description, params string[] values)
    {
        return new ConfigDescription(
            description,
            new AcceptableValueList<string>(values));
    }

    public HermesStashRequestSettings CreateStashRequestSettings()
    {
        return new HermesStashRequestSettings(
            IncludeActiveQuestReservations.Value,
            IncludeFutureQuestReservations.Value,
            IncludeNextHideoutReservations.Value,
            IncludeFutureHideoutReservations.Value,
            PreferFoundInRaidCopies.Value,
            Math.Clamp(DuplicateBaselineReserve.Value, 0, 1000),
            Math.Clamp(StashWeaponDurabilityThreshold.Value, 1, 100),
            Math.Clamp(StashArmorDurabilityThreshold.Value, 1, 100),
            Math.Clamp(StashLowResourceThreshold.Value, 0, 100),
            Math.Clamp(StashKeyUsesWarningThreshold.Value, 0, 100),
            Math.Clamp(MinimumCleanupValue.Value, 0, 100_000_000),
            Math.Clamp(MinimumValuePerRecoveredCell.Value, 0, 10_000_000),
            Math.Clamp(MaximumStashRecommendations.Value, 25, 1000),
            ShowProtectedCurrencies.Value);
    }

    public HermesLoadoutRequestSettings CreateLoadoutRequestSettings()
    {
        return new HermesLoadoutRequestSettings(
            Math.Clamp(MinimumWeaponDurabilityPercent.Value, 1, 100),
            Math.Clamp(MinimumArmorDurabilityPercent.Value, 1, 100),
            Math.Clamp(MinimumLoadedRounds.Value, 0, 200),
            Math.Clamp(MinimumSpareMagazines.Value, 0, 20),
            Math.Clamp(MinimumSpareRounds.Value, 0, 1000),
            Math.Clamp(MinimumHealingResource.Value, 0, 5000),
            Math.Clamp(HydrationWarningPercent.Value, 0, 100),
            Math.Clamp(EnergyWarningPercent.Value, 0, 100),
            RequireHeavyBleedTreatment.Value,
            RequireLightBleedTreatment.Value,
            RequireFractureTreatment.Value,
            RequirePainTreatment.Value,
            RequireHydrationProvision.Value,
            RequireEnergyProvision.Value,
            ShowValueAndInsurance.Value,
            EnableInsuranceWarnings.Value,
            Math.Clamp(HighValueUninsuredThreshold.Value, 0, 10_000_000));
    }

    // Legacy per-panel timers remain disabled. The coordinator owns one shared live background
    // refresh loop so every workspace and the Assistant alert feed move together.
    public int GetAutomaticRefreshSeconds() => 0;
    public int GetLiveBackgroundRefreshSeconds() => Math.Clamp(LiveBackgroundRefreshSeconds.Value, 10, 300);
    public int GetProfileSaveWhileHermesOpenSeconds() => Math.Clamp(ProfileSaveWhileHermesOpenSeconds.Value, 15, 300);
    public int GetRequestTimeoutSeconds() => Math.Clamp(RequestTimeoutSeconds.Value, 5, 60);
    public int GetLongRequestTimeoutSeconds() => Math.Max(30, GetRequestTimeoutSeconds());
    public int GetSlowRequestWarningSeconds() => Math.Clamp(SlowRequestWarningSeconds.Value, 1, 30);
    public int GetCacheStatusRefreshSeconds() => Math.Clamp(CacheStatusRefreshSeconds.Value, 5, 60);
    public int GetMaximumAssistantMessages() => Math.Clamp(MaximumAssistantMessages.Value, 10, 200);
    public int GetAssistantEntityConfidencePercent() => Math.Clamp(AssistantEntityConfidencePercent.Value, 40, 100);
    public int GetMaximumAssistantAmbiguityChoices() => Math.Clamp(MaximumAssistantAmbiguityChoices.Value, 2, 10);
    public int GetMaximumAssistantRecommendations() => Math.Clamp(MaximumAssistantRecommendations.Value, 3, 10);
    public int GetMaximumAssistantContextSubjects() => Math.Clamp(MaximumAssistantContextSubjects.Value, 1, 12);
    public int GetAssistantNoticeCheckIntervalMinutes() => Math.Clamp(AssistantNoticeCheckIntervalMinutes.Value, 1, 60);
    public int GetMinimumAssistantNoticeCraftProfit() => Math.Clamp(MinimumAssistantNoticeCraftProfit.Value, 0, 10_000_000);
    public int GetMinimumAssistantNoticeStashValue() => Math.Clamp(MinimumAssistantNoticeStashValue.Value, 0, 100_000_000);
    public int GetHighValueUninsuredThreshold() => Math.Clamp(HighValueUninsuredThreshold.Value, 0, 10_000_000);
    public int GetMaximumRowsPerSection() => Math.Clamp(MaximumRowsPerSection.Value, 25, 120);
    public int GetMaximumSearchResults() => Math.Clamp(MaximumSearchResults.Value, 5, 50);
    public int GetMinimumSearchCharacters() => Math.Clamp(MinimumSearchCharacters.Value, 1, 10);
    public int GetMinimumComparableFleaOffers() => Math.Clamp(MinimumComparableFleaOffers.Value, 1, 20);
    public int GetMaximumFleaOffersDisplayed() => Math.Clamp(MaximumFleaOffersDisplayed.Value, 1, 50);
    public int GetMinimumCraftProfit() => Math.Clamp(MinimumCraftProfit.Value, -10_000_000, 10_000_000);
    public int GetMinimumCraftProfitPercent() => Math.Clamp(MinimumCraftProfitPercent.Value, -100, 10_000);
    public int GetOvernightMinimumHours() => Math.Clamp(OvernightMinimumHours.Value, 1, 48);
    public int GetOvernightMaximumHours() => Math.Max(GetOvernightMinimumHours(), Math.Clamp(OvernightMaximumHours.Value, 1, 72));
    public string GetDefaultLoadoutView() => NormalizeLoadoutView(DefaultLoadoutView.Value);
    public string GetRaidPlannerSorting() => NormalizeRaidPlannerSorting(RaidPlannerDefaultSorting.Value);

    public string GetOpeningTabName()
    {
        if (RememberLastSelectedTab.Value)
        {
            var remembered = PlayerPrefs.GetString(LastTabPlayerPref, string.Empty);
            if (!string.IsNullOrWhiteSpace(remembered))
            {
                return remembered;
            }
        }

        return NormalizeTabName(DefaultOpeningTab.Value);
    }

    public void RememberTab(string tabName)
    {
        if (!RememberLastSelectedTab.Value)
        {
            return;
        }

        PlayerPrefs.SetString(LastTabPlayerPref, NormalizeTabName(tabName));
        PlayerPrefs.Save();
    }

    private static string NormalizeLoadoutView(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "weapons" or "weapons & ammo" or "weapons and ammo" => "Weapons & Ammo",
            "armor" => "Armor",
            "medical" => "Medical",
            "quests" or "quest gear" => "Quest Gear",
            "raid" or "raid planner" => "Raid Planner",
            "value" or "insurance" or "value & insurance" => "Value & Insurance",
            _ => "Overview"
        };
    }

    private static string NormalizeRaidPlannerSorting(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "most active quests" or "active quests" => "Most active quests",
            "most incomplete objectives" or "incomplete objectives" => "Most incomplete objectives",
            "fewest missing requirements" or "fewest missing" => "Fewest missing requirements",
            "alphabetical" or "name" => "Alphabetical",
            _ => "Best prepared"
        };
    }

    private static string NormalizeTabName(string? value)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "assistant" or "chat" => "Assistant",
            "actions" or "action" or "confirmed actions" => "Actions",
            "items & market" or "items and market" or "item search" or "market" => "Item Search",
            "hideout" => "Hideout",
            "craft" or "crafts" => "Crafts",
            "stash" => "Stash",
            "loadout" => "Loadout",
            "raid" or "raid planner" => "Raid Planner",
            _ => "Item Search"
        };
    }
}

internal readonly record struct HermesStashRequestSettings(
    bool IncludeActiveQuestReservations,
    bool IncludeFutureQuestReservations,
    bool IncludeNextHideoutReservations,
    bool IncludeFutureHideoutReservations,
    bool PreferFoundInRaidCopies,
    int DuplicateBaselineReserve,
    int WeaponDurabilityThresholdPercent,
    int ArmorDurabilityThresholdPercent,
    int LowResourceThresholdPercent,
    int KeyUsesWarningThreshold,
    int MinimumCleanupValue,
    int MinimumValuePerRecoveredCell,
    int MaximumRecommendations,
    bool IncludeProtectedCurrencies)
{
    public string ToRouteSuffix()
    {
        return $"{(IncludeActiveQuestReservations ? 1 : 0)}/{(IncludeFutureQuestReservations ? 1 : 0)}/"
               + $"{(IncludeNextHideoutReservations ? 1 : 0)}/{(IncludeFutureHideoutReservations ? 1 : 0)}/"
               + $"{(PreferFoundInRaidCopies ? 1 : 0)}/{DuplicateBaselineReserve}/"
               + $"{WeaponDurabilityThresholdPercent}/{ArmorDurabilityThresholdPercent}/"
               + $"{LowResourceThresholdPercent}/{KeyUsesWarningThreshold}/{MinimumCleanupValue}/"
               + $"{MinimumValuePerRecoveredCell}/{MaximumRecommendations}/"
               + $"{(IncludeProtectedCurrencies ? 1 : 0)}";
    }
}

internal readonly record struct HermesLoadoutRequestSettings(
    int MinimumWeaponDurabilityPercent,
    int MinimumArmorDurabilityPercent,
    int MinimumLoadedRounds,
    int MinimumSpareMagazines,
    int MinimumSpareRounds,
    int MinimumHealingResource,
    int HydrationWarningPercent,
    int EnergyWarningPercent,
    bool RequireHeavyBleedTreatment,
    bool RequireLightBleedTreatment,
    bool RequireFractureTreatment,
    bool RequirePainTreatment,
    bool RequireHydrationProvision,
    bool RequireEnergyProvision,
    bool IncludeValueAnalysis,
    bool EnableInsuranceWarnings,
    int HighValueUninsuredThreshold)
{
    public string ToRouteSuffix()
    {
        return $"{MinimumWeaponDurabilityPercent}/{MinimumArmorDurabilityPercent}/{MinimumLoadedRounds}/"
               + $"{MinimumSpareMagazines}/{MinimumSpareRounds}/{MinimumHealingResource}/"
               + $"{HydrationWarningPercent}/{EnergyWarningPercent}/"
               + $"{(RequireHeavyBleedTreatment ? 1 : 0)}/{(RequireLightBleedTreatment ? 1 : 0)}/"
               + $"{(RequireFractureTreatment ? 1 : 0)}/{(RequirePainTreatment ? 1 : 0)}/"
               + $"{(RequireHydrationProvision ? 1 : 0)}/{(RequireEnergyProvision ? 1 : 0)}/"
               + $"{(IncludeValueAnalysis ? 1 : 0)}/{(EnableInsuranceWarnings ? 1 : 0)}/"
               + $"{HighValueUninsuredThreshold}";
    }
}
