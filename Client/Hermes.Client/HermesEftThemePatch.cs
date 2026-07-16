using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using SPT.Reflection.Patching;
using UnityEngine;

namespace Hermes.Client;

/// <summary>
/// Flat EFT-style IMGUI controls used only while the embedded HERMES inventory tab is drawn.
/// The palette and spacing are based on EFT's flea-market/category/list presentation:
/// dark uninterrupted surfaces, restrained separators, cream selected states, and strong
/// primary/secondary text hierarchy.
/// </summary>
internal static class HermesEftTheme
{
    private static GUISkin? _skin;

    private static GUIStyle? _workspaceShell;
    private static GUIStyle? _headerBand;
    private static GUIStyle? _headerTitle;
    private static GUIStyle? _headerSubtitle;
    private static GUIStyle? _headerAction;
    private static GUIStyle? _navigationRail;
    private static GUIStyle? _navigationHeading;
    private static GUIStyle? _navigationItem;
    private static GUIStyle? _navigationItemSelected;
    private static GUIStyle? _diagnosticText;
    private static GUIStyle? _panelHeader;
    private static GUIStyle? _panelTitle;
    private static GUIStyle? _subtitle;
    private static GUIStyle? _status;
    private static GUIStyle? _toolbar;
    private static GUIStyle? _searchField;
    private static GUIStyle? _filter;
    private static GUIStyle? _selectedFilter;
    private static GUIStyle? _summaryBar;
    private static GUIStyle? _summaryCell;
    private static GUIStyle? _summaryTitle;
    private static GUIStyle? _summaryValue;
    private static GUIStyle? _summaryDetail;
    private static GUIStyle? _contentPanel;
    private static GUIStyle? _sectionHeader;
    private static GUIStyle? _mapHeader;
    private static GUIStyle? _dataRow;
    private static GUIStyle? _dataRowAlternate;
    private static GUIStyle? _rowTitle;
    private static GUIStyle? _rowMeta;
    private static GUIStyle? _rowDescription;
    private static GUIStyle? _statusReady;
    private static GUIStyle? _statusWarning;
    private static GUIStyle? _statusNeutral;
    private static GUIStyle? _emptyState;
    private static GUIStyle? _smallButton;
    private static GUIStyle? _tab;
    private static GUIStyle? _selectedTab;

    private static readonly List<Texture2D> Textures = [];

    internal static readonly Color Text = Rgb(202, 207, 202);
    internal static readonly Color PrimaryText = Rgb(224, 226, 216);
    internal static readonly Color MutedText = Rgb(123, 132, 131);
    internal static readonly Color Accent = Rgb(218, 216, 188);
    internal static readonly Color AccentHover = Rgb(239, 236, 204);
    internal static readonly Color DarkText = Rgb(28, 31, 31);
    internal static readonly Color Warning = Rgb(190, 91, 45);
    internal static readonly Color WarningBright = Rgb(224, 120, 60);

    private static readonly Color Backdrop = Rgba(6, 10, 12, 248);
    private static readonly Color Header = Rgba(13, 22, 26, 250);
    private static readonly Color Panel = Rgba(13, 18, 19, 248);
    private static readonly Color PanelRaised = Rgba(20, 26, 27, 250);
    private static readonly Color Row = Rgba(13, 18, 19, 252);
    private static readonly Color RowAlternate = Rgba(18, 23, 24, 252);
    private static readonly Color Hover = Rgba(35, 42, 42, 255);
    private static readonly Color Pressed = Rgba(54, 57, 50, 255);
    private static readonly Color Field = Rgba(5, 9, 10, 255);
    private static readonly Color Line = Rgb(52, 62, 64);
    private static readonly Color LineSoft = Rgb(35, 44, 46);

    internal static GUISkin Skin
    {
        get
        {
            EnsureCreated();
            return _skin!;
        }
    }

    internal static GUIStyle WorkspaceShell => Get(ref _workspaceShell);
    internal static GUIStyle HeaderBand => Get(ref _headerBand);
    internal static GUIStyle HeaderTitle => Get(ref _headerTitle);
    internal static GUIStyle HeaderSubtitle => Get(ref _headerSubtitle);
    internal static GUIStyle HeaderAction => Get(ref _headerAction);
    internal static GUIStyle NavigationRail => Get(ref _navigationRail);
    internal static GUIStyle NavigationHeading => Get(ref _navigationHeading);
    internal static GUIStyle DiagnosticText => Get(ref _diagnosticText);
    internal static GUIStyle PanelHeader => Get(ref _panelHeader);
    internal static GUIStyle PanelTitle => Get(ref _panelTitle);
    internal static GUIStyle Subtitle => Get(ref _subtitle);
    internal static GUIStyle Status => Get(ref _status);
    internal static GUIStyle Toolbar => Get(ref _toolbar);
    internal static GUIStyle SearchField => Get(ref _searchField);
    internal static GUIStyle SummaryBar => Get(ref _summaryBar);
    internal static GUIStyle SummaryCell => Get(ref _summaryCell);
    internal static GUIStyle SummaryTitle => Get(ref _summaryTitle);
    internal static GUIStyle SummaryValue => Get(ref _summaryValue);
    internal static GUIStyle SummaryDetail => Get(ref _summaryDetail);
    internal static GUIStyle ContentPanel => Get(ref _contentPanel);
    internal static GUIStyle SectionHeader => Get(ref _sectionHeader);
    internal static GUIStyle MapHeader => Get(ref _mapHeader);
    internal static GUIStyle RowTitle => Get(ref _rowTitle);
    internal static GUIStyle RowMeta => Get(ref _rowMeta);
    internal static GUIStyle RowDescription => Get(ref _rowDescription);
    internal static GUIStyle EmptyState => Get(ref _emptyState);
    internal static GUIStyle SmallButton => Get(ref _smallButton);

    internal static GUIStyle NavigationItem(bool selected)
    {
        EnsureCreated();
        return selected ? _navigationItemSelected! : _navigationItem!;
    }

    internal static GUIStyle Filter(bool selected)
    {
        EnsureCreated();
        return selected ? _selectedFilter! : _filter!;
    }

    internal static GUIStyle Tab(bool selected)
    {
        EnsureCreated();
        return selected ? _selectedTab! : _tab!;
    }

    internal static GUIStyle DataRow(bool alternate)
    {
        EnsureCreated();
        return alternate ? _dataRowAlternate! : _dataRow!;
    }

    internal static GUIStyle StatusBadge(string status)
    {
        EnsureCreated();
        if (status.Contains("missing", StringComparison.OrdinalIgnoreCase)
            || status.Contains("critical", StringComparison.OrdinalIgnoreCase)
            || status.Contains("uninsured", StringComparison.OrdinalIgnoreCase))
        {
            return _statusWarning!;
        }

        if (status.Contains("ready", StringComparison.OrdinalIgnoreCase)
            || status.Contains("prepared", StringComparison.OrdinalIgnoreCase)
            || status.Contains("complete", StringComparison.OrdinalIgnoreCase))
        {
            return _statusReady!;
        }

        return _statusNeutral!;
    }

    private static GUIStyle Get(ref GUIStyle? style)
    {
        EnsureCreated();
        return style!;
    }

    private static void EnsureCreated()
    {
        if (_skin != null)
        {
            return;
        }

        _skin = UnityEngine.Object.Instantiate(GUI.skin);
        _skin.name = "HERMES_EFT_Flat_Workspace";
        _skin.hideFlags = HideFlags.HideAndDontSave;

        var backdropTexture = CreateFlatTexture(Backdrop);
        var headerTexture = CreateBottomLineTexture(Header, Line);
        var panelTexture = CreateFlatTexture(Panel);
        var raisedTexture = CreateBottomLineTexture(PanelRaised, Line);
        var rowTexture = CreateBottomLineTexture(Row, LineSoft);
        var alternateRowTexture = CreateBottomLineTexture(RowAlternate, LineSoft);
        var hoverTexture = CreateFlatTexture(Hover);
        var pressedTexture = CreateFlatTexture(Pressed);
        var selectedTexture = CreateFlatTexture(Accent);
        var selectedNavTexture = CreateLeftAccentTexture(PanelRaised, Accent);
        var fieldTexture = CreateBorderedTexture(Field, Line);
        var fieldFocusedTexture = CreateBorderedTexture(Field, Accent);
        var warningTexture = CreateFlatTexture(new Color(0.32f, 0.105f, 0.055f, 0.95f));
        var readyTexture = CreateFlatTexture(new Color(0.15f, 0.19f, 0.18f, 1f));
        var neutralTexture = CreateFlatTexture(new Color(0.12f, 0.16f, 0.17f, 1f));

        ConfigureSkinBox(_skin.box, panelTexture);
        ConfigureSkinButton(_skin.button, panelTexture, hoverTexture, pressedTexture, selectedTexture);
        ConfigureTextField(_skin.textField, fieldTexture, fieldFocusedTexture);
        ConfigureLabel(_skin.label);
        if (_skin.textArea != null)
        {
            ConfigureTextField(_skin.textArea, fieldTexture, fieldFocusedTexture);
        }
        if (_skin.scrollView != null)
        {
            _skin.scrollView.normal.background = backdropTexture;
            _skin.scrollView.padding = new RectOffset(0, 0, 0, 0);
            _skin.scrollView.margin = new RectOffset(0, 0, 0, 0);
        }

        _workspaceShell = FlatBox(backdropTexture, new RectOffset(4, 4, 4, 4));
        _headerBand = FlatBox(headerTexture, new RectOffset(14, 12, 8, 8));
        _headerBand.margin = new RectOffset(0, 0, 0, 0);

        _headerTitle = Label(17, PrimaryText, FontStyle.Normal, TextAnchor.MiddleLeft, false);
        _headerTitle.padding = new RectOffset(0, 0, 0, 0);
        _headerSubtitle = Label(11, MutedText, FontStyle.Normal, TextAnchor.MiddleLeft, false);
        _headerSubtitle.padding = new RectOffset(0, 0, 0, 0);

        _headerAction = Button(panelTexture, hoverTexture, pressedTexture, 11, Text, AccentHover);
        _headerAction.padding = new RectOffset(14, 14, 5, 5);
        _headerAction.margin = new RectOffset(3, 0, 2, 2);

        _navigationRail = FlatBox(Panel, new RectOffset(8, 8, 8, 8));
        _navigationRail.normal.background = panelTexture;
        _navigationHeading = Label(11, MutedText, FontStyle.Bold, TextAnchor.MiddleLeft, false);
        _navigationHeading.padding = new RectOffset(8, 4, 3, 8);

        _navigationItem = Button(panelTexture, hoverTexture, pressedTexture, 12, Text, AccentHover);
        _navigationItem.alignment = TextAnchor.MiddleLeft;
        _navigationItem.padding = new RectOffset(14, 8, 7, 7);
        _navigationItem.margin = new RectOffset(0, 0, 0, 1);
        _navigationItemSelected = new GUIStyle(_navigationItem);
        SetAllBackgrounds(_navigationItemSelected, selectedNavTexture);
        SetAllTextColors(_navigationItemSelected, AccentHover);
        _navigationItemSelected.fontStyle = FontStyle.Bold;

        _diagnosticText = Label(10, MutedText, FontStyle.Normal, TextAnchor.LowerLeft, true);
        _diagnosticText.padding = new RectOffset(8, 6, 2, 4);

        _panelHeader = FlatBox(raisedTexture, new RectOffset(12, 12, 8, 8));
        _panelHeader.margin = new RectOffset(0, 0, 0, 5);
        _panelTitle = Label(15, Accent, FontStyle.Bold, TextAnchor.MiddleLeft, false);
        _subtitle = Label(11, MutedText, FontStyle.Normal, TextAnchor.MiddleLeft, true);
        _status = Label(11, Text, FontStyle.Normal, TextAnchor.MiddleLeft, true);
        _status.normal.background = panelTexture;
        _status.padding = new RectOffset(8, 8, 4, 4);
        _status.margin = new RectOffset(0, 0, 4, 0);

        _toolbar = FlatBox(raisedTexture, new RectOffset(8, 8, 6, 6));
        _toolbar.margin = new RectOffset(0, 0, 0, 5);
        _searchField = new GUIStyle(_skin.textField)
        {
            fontSize = 12,
            padding = new RectOffset(9, 9, 5, 5),
            margin = new RectOffset(0, 4, 0, 0)
        };

        _filter = Button(panelTexture, hoverTexture, pressedTexture, 11, Text, AccentHover);
        _filter.padding = new RectOffset(10, 10, 5, 5);
        _filter.margin = new RectOffset(1, 1, 0, 0);
        _selectedFilter = new GUIStyle(_filter);
        SetAllBackgrounds(_selectedFilter, selectedTexture);
        SetAllTextColors(_selectedFilter, DarkText);
        _selectedFilter.fontStyle = FontStyle.Bold;

        _summaryBar = FlatBox(raisedTexture, new RectOffset(0, 0, 0, 0));
        _summaryBar.margin = new RectOffset(0, 0, 0, 6);
        _summaryCell = FlatBox(raisedTexture, new RectOffset(10, 10, 7, 7));
        _summaryCell.margin = new RectOffset(0, 1, 0, 0);
        _summaryTitle = Label(10, MutedText, FontStyle.Bold, TextAnchor.UpperLeft, false);
        _summaryValue = Label(18, PrimaryText, FontStyle.Normal, TextAnchor.MiddleLeft, false);
        _summaryDetail = Label(10, MutedText, FontStyle.Normal, TextAnchor.UpperLeft, true);

        _contentPanel = FlatBox(panelTexture, new RectOffset(8, 8, 5, 8));
        _contentPanel.margin = new RectOffset(0, 0, 0, 6);
        _sectionHeader = Label(11, Accent, FontStyle.Bold, TextAnchor.MiddleLeft, false);
        _sectionHeader.normal.background = raisedTexture;
        _sectionHeader.padding = new RectOffset(10, 10, 5, 5);
        _sectionHeader.margin = new RectOffset(0, 0, 5, 0);

        _mapHeader = Button(raisedTexture, hoverTexture, pressedTexture, 12, PrimaryText, AccentHover);
        _mapHeader.alignment = TextAnchor.MiddleLeft;
        _mapHeader.fontStyle = FontStyle.Bold;
        _mapHeader.padding = new RectOffset(10, 10, 7, 7);
        _mapHeader.margin = new RectOffset(0, 0, 0, 0);

        _dataRow = FlatBox(rowTexture, new RectOffset(9, 9, 6, 6));
        _dataRow.margin = new RectOffset(0, 0, 0, 0);
        _dataRowAlternate = new GUIStyle(_dataRow);
        _dataRowAlternate.normal.background = alternateRowTexture;
        _rowTitle = Label(12, PrimaryText, FontStyle.Normal, TextAnchor.UpperLeft, true);
        _rowMeta = Label(10, MutedText, FontStyle.Normal, TextAnchor.UpperLeft, true);
        _rowDescription = Label(11, Text, FontStyle.Normal, TextAnchor.UpperLeft, true);
        _rowDescription.padding = new RectOffset(0, 0, 2, 0);

        _statusReady = Badge(readyTexture, Accent);
        _statusWarning = Badge(warningTexture, WarningBright);
        _statusNeutral = Badge(neutralTexture, Text);

        _emptyState = FlatBox(rowTexture, new RectOffset(14, 14, 13, 13));
        _emptyState.margin = new RectOffset(0, 0, 4, 4);
        _emptyState.normal.textColor = MutedText;

        _smallButton = new GUIStyle(_headerAction)
        {
            fontSize = 10,
            padding = new RectOffset(10, 10, 4, 4),
            margin = new RectOffset(2, 0, 0, 0)
        };

        _tab = new GUIStyle(_filter)
        {
            fontSize = 11,
            padding = new RectOffset(11, 11, 5, 5)
        };
        _selectedTab = new GUIStyle(_selectedFilter)
        {
            fontSize = 11,
            padding = new RectOffset(11, 11, 5, 5)
        };
    }

    private static GUIStyle FlatBox(Color fill, RectOffset padding)
    {
        var style = new GUIStyle
        {
            padding = padding,
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
            richText = true,
            wordWrap = true
        };
        style.normal.background = CreateFlatTexture(fill);
        style.normal.textColor = Text;
        return style;
    }

    private static GUIStyle FlatBox(Texture2D background, RectOffset padding)
    {
        var style = new GUIStyle
        {
            padding = padding,
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
            richText = true,
            wordWrap = true
        };
        style.normal.background = background;
        style.normal.textColor = Text;
        return style;
    }

    private static GUIStyle Button(
        Texture2D normal,
        Texture2D hover,
        Texture2D pressed,
        int fontSize,
        Color normalText,
        Color hoverText)
    {
        var style = new GUIStyle
        {
            fontSize = fontSize,
            alignment = TextAnchor.MiddleCenter,
            padding = new RectOffset(8, 8, 5, 5),
            margin = new RectOffset(0, 0, 0, 0),
            border = new RectOffset(0, 0, 0, 0),
            richText = true,
            wordWrap = false,
            clipping = TextClipping.Clip
        };
        style.normal.background = normal;
        style.hover.background = hover;
        style.active.background = pressed;
        style.focused.background = hover;
        style.onNormal.background = pressed;
        style.onHover.background = pressed;
        style.onActive.background = pressed;
        style.onFocused.background = pressed;
        style.normal.textColor = normalText;
        style.hover.textColor = hoverText;
        style.active.textColor = PrimaryText;
        style.focused.textColor = hoverText;
        style.onNormal.textColor = PrimaryText;
        style.onHover.textColor = PrimaryText;
        style.onActive.textColor = PrimaryText;
        style.onFocused.textColor = PrimaryText;
        return style;
    }

    private static GUIStyle Badge(Texture2D background, Color color)
    {
        var style = Label(10, color, FontStyle.Bold, TextAnchor.MiddleCenter, false);
        style.normal.background = background;
        style.padding = new RectOffset(10, 10, 4, 4);
        style.margin = new RectOffset(6, 0, 0, 0);
        return style;
    }

    private static GUIStyle Label(
        int fontSize,
        Color color,
        FontStyle fontStyle,
        TextAnchor alignment,
        bool wordWrap)
    {
        var style = new GUIStyle(GUI.skin.label)
        {
            fontSize = fontSize,
            fontStyle = fontStyle,
            alignment = alignment,
            wordWrap = wordWrap,
            richText = true,
            padding = new RectOffset(0, 0, 0, 0),
            margin = new RectOffset(0, 0, 0, 0)
        };
        style.normal.textColor = color;
        return style;
    }

    private static void ConfigureSkinBox(GUIStyle style, Texture2D background)
    {
        style.normal.background = background;
        style.normal.textColor = Text;
        style.border = new RectOffset(0, 0, 0, 0);
        style.padding = new RectOffset(8, 8, 7, 7);
        style.margin = new RectOffset(0, 0, 0, 4);
        style.richText = true;
        style.wordWrap = true;
    }

    private static void ConfigureSkinButton(
        GUIStyle style,
        Texture2D normal,
        Texture2D hover,
        Texture2D pressed,
        Texture2D selected)
    {
        style.normal.background = normal;
        style.hover.background = hover;
        style.active.background = pressed;
        style.focused.background = hover;
        style.onNormal.background = selected;
        style.onHover.background = selected;
        style.onActive.background = pressed;
        style.onFocused.background = selected;
        style.normal.textColor = Text;
        style.hover.textColor = AccentHover;
        style.active.textColor = PrimaryText;
        style.focused.textColor = AccentHover;
        style.onNormal.textColor = DarkText;
        style.onHover.textColor = DarkText;
        style.onActive.textColor = DarkText;
        style.onFocused.textColor = DarkText;
        style.border = new RectOffset(0, 0, 0, 0);
        style.padding = new RectOffset(10, 10, 6, 6);
        style.margin = new RectOffset(0, 0, 0, 2);
        style.fontSize = 11;
        style.richText = true;
    }

    private static void ConfigureTextField(GUIStyle style, Texture2D normal, Texture2D focused)
    {
        style.normal.background = normal;
        style.hover.background = normal;
        style.active.background = focused;
        style.focused.background = focused;
        style.normal.textColor = PrimaryText;
        style.hover.textColor = PrimaryText;
        style.active.textColor = PrimaryText;
        style.focused.textColor = PrimaryText;
        style.border = new RectOffset(1, 1, 1, 1);
        style.padding = new RectOffset(9, 9, 5, 5);
        style.margin = new RectOffset(0, 0, 0, 0);
        style.fontSize = 12;
    }

    private static void ConfigureLabel(GUIStyle style)
    {
        style.normal.textColor = Text;
        style.fontSize = 11;
        style.richText = true;
        style.wordWrap = true;
        style.padding = new RectOffset(1, 1, 1, 1);
        style.margin = new RectOffset(0, 0, 0, 0);
    }

    private static void SetAllBackgrounds(GUIStyle style, Texture2D background)
    {
        style.normal.background = background;
        style.hover.background = background;
        style.active.background = background;
        style.focused.background = background;
        style.onNormal.background = background;
        style.onHover.background = background;
        style.onActive.background = background;
        style.onFocused.background = background;
    }

    private static void SetAllTextColors(GUIStyle style, Color color)
    {
        style.normal.textColor = color;
        style.hover.textColor = color;
        style.active.textColor = color;
        style.focused.textColor = color;
        style.onNormal.textColor = color;
        style.onHover.textColor = color;
        style.onActive.textColor = color;
        style.onFocused.textColor = color;
    }

    private static Texture2D CreateFlatTexture(Color fill)
    {
        var texture = new Texture2D(2, 2, TextureFormat.RGBA32, false)
        {
            name = "HERMES_EFT_Flat",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        texture.SetPixels([fill, fill, fill, fill]);
        texture.Apply(false, true);
        Textures.Add(texture);
        return texture;
    }

    private static Texture2D CreateBorderedTexture(Color fill, Color border)
    {
        const int size = 5;
        var texture = new Texture2D(size, size, TextureFormat.RGBA32, false)
        {
            name = "HERMES_EFT_Bordered",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        var pixels = new Color[size * size];
        for (var y = 0; y < size; y++)
        {
            for (var x = 0; x < size; x++)
            {
                pixels[y * size + x] = x == 0 || y == 0 || x == size - 1 || y == size - 1
                    ? border
                    : fill;
            }
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        Textures.Add(texture);
        return texture;
    }

    private static Texture2D CreateBottomLineTexture(Color fill, Color line)
    {
        const int width = 4;
        const int height = 4;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "HERMES_EFT_BottomLine",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        var pixels = Enumerable.Repeat(fill, width * height).ToArray();
        for (var x = 0; x < width; x++)
        {
            pixels[x] = line;
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        Textures.Add(texture);
        return texture;
    }

    private static Texture2D CreateLeftAccentTexture(Color fill, Color accent)
    {
        const int width = 8;
        const int height = 4;
        var texture = new Texture2D(width, height, TextureFormat.RGBA32, false)
        {
            name = "HERMES_EFT_SelectedNav",
            filterMode = FilterMode.Point,
            wrapMode = TextureWrapMode.Clamp,
            hideFlags = HideFlags.HideAndDontSave
        };
        var pixels = Enumerable.Repeat(fill, width * height).ToArray();
        for (var y = 0; y < height; y++)
        {
            pixels[y * width] = accent;
            pixels[y * width + 1] = accent;
        }
        texture.SetPixels(pixels);
        texture.Apply(false, true);
        Textures.Add(texture);
        return texture;
    }

    private static Color Rgb(byte r, byte g, byte b) => new(r / 255f, g / 255f, b / 255f, 1f);
    private static Color Rgba(byte r, byte g, byte b, byte a) => new(r / 255f, g / 255f, b / 255f, a / 255f);
}

internal static class HermesEftThemeBootstrap
{
    internal static void Enable()
    {
        TryEnable("workspace skin", () => new HermesEftWorkspaceSkinPatch().Enable());
        TryEnable("inventory header", () => new HermesEftInventoryHeaderPatch().Enable());
        TryEnable("workspace rail", () => new HermesEftNavigationRailPatch().Enable());
        TryEnable("compact workspace navigation", () => new HermesEftCompactNavigationPatch().Enable());
        TryEnable("panel header", () => new HermesEftPanelHeaderPatch().Enable());
        TryEnable("panel title", () => new HermesEftPanelTitlePatch().Enable());
        TryEnable("workspace tabs", () => new HermesEftTabButtonPatch().Enable());
        TryEnable("empty states", () => new HermesEftEmptyStatePatch().Enable());
        TryEnable("compact empty states", () => new HermesEftEmptyStateSinglePatch().Enable());
    }

    private static void TryEnable(string name, Action enable)
    {
        try
        {
            enable();
            Plugin.Log?.LogInfo($"HERMES EFT layout {name} patch enabled.");
        }
        catch (Exception ex)
        {
            Plugin.Log?.LogWarning($"HERMES EFT layout {name} patch was skipped: {ex.Message}");
        }
    }
}

internal static class HermesEftWindowReflection
{
    private const BindingFlags InstanceFlags = BindingFlags.Instance | BindingFlags.NonPublic;

    private static readonly FieldInfo ActiveTabField = typeof(HermesWindow).GetField("_activeTab", InstanceFlags)
        ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_activeTab");
    private static readonly FieldInfo RefreshingField = typeof(HermesWindow).GetField("_refreshingCurrent", InstanceFlags)
        ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_refreshingCurrent");
    private static readonly FieldInfo RefreshStatusField = typeof(HermesWindow).GetField("_refreshStatus", InstanceFlags)
        ?? throw new MissingFieldException(typeof(HermesWindow).FullName, "_refreshStatus");
    private static readonly MethodInfo SetActiveTabMethod = typeof(HermesWindow).GetMethod("SetActiveTab", InstanceFlags)
        ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "SetActiveTab");
    private static readonly MethodInfo ClearCurrentTabMethod = typeof(HermesWindow).GetMethod("ClearCurrentTab", InstanceFlags)
        ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "ClearCurrentTab");
    private static readonly MethodInfo RefreshCurrentDataMethod = typeof(HermesWindow).GetMethod("RefreshCurrentDataAsync", InstanceFlags)
        ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "RefreshCurrentDataAsync");
    private static readonly MethodInfo FormatDiagnosticsMethod = typeof(HermesWindow).GetMethod("FormatCompactDiagnosticsStatus", InstanceFlags)
        ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "FormatCompactDiagnosticsStatus");
    private static readonly MethodInfo BuildDiagnosticsMethod = typeof(HermesWindow).GetMethod("BuildDiagnosticsReport", InstanceFlags)
        ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "BuildDiagnosticsReport");

    internal static string ActiveTabName(HermesWindow window)
        => ActiveTabField.GetValue(window)?.ToString() ?? "Workspace";

    internal static string ActiveTabDisplayName(HermesWindow window)
        => ActiveTabName(window) switch
        {
            "ItemSearch" => "ITEMS & MARKET",
            "RaidPlanner" => "RAID PLANNER",
            string value => value.ToUpperInvariant()
        };

    internal static bool IsRefreshing(HermesWindow window)
        => RefreshingField.GetValue(window) is true;

    internal static string? RefreshStatus(HermesWindow window)
        => RefreshStatusField.GetValue(window) as string;

    internal static void SetRefreshStatus(HermesWindow window, string value)
        => RefreshStatusField.SetValue(window, value);

    internal static bool IsSelected(HermesWindow window, string tabName)
        => string.Equals(ActiveTabName(window), tabName, StringComparison.Ordinal);

    internal static void Select(HermesWindow window, string tabName)
    {
        var value = Enum.Parse(ActiveTabField.FieldType, tabName);
        SetActiveTabMethod.Invoke(window, [value]);
    }

    internal static void Clear(HermesWindow window)
        => ClearCurrentTabMethod.Invoke(window, null);

    internal static void Refresh(HermesWindow window)
    {
        var parameters = RefreshCurrentDataMethod.GetParameters();
        _ = parameters.Length == 0
            ? RefreshCurrentDataMethod.Invoke(window, null)
            : RefreshCurrentDataMethod.Invoke(window, [true]);
    }

    internal static string FormatDiagnostics(HermesWindow window)
        => FormatDiagnosticsMethod.Invoke(window, null) as string ?? "Diagnostics unavailable";

    internal static string BuildDiagnostics(HermesWindow window)
        => BuildDiagnosticsMethod.Invoke(window, null) as string ?? string.Empty;
}

internal sealed class HermesEftWorkspaceSkinPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesWindow).GetMethod("DrawEmbedded", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "DrawEmbedded");

    [PatchPrefix]
    private static void Prefix(out GUISkin __state)
    {
        __state = GUI.skin;
        GUI.skin = HermesEftTheme.Skin;
    }

    [PatchPostfix]
    private static void Postfix(GUISkin __state)
    {
        GUI.skin = __state;
    }
}

internal sealed class HermesEftInventoryHeaderPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesWindow).GetMethod("DrawInventoryHeader", BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "DrawInventoryHeader");

    [PatchPrefix]
    private static bool Prefix(HermesWindow __instance, Rect __0)
    {
        var rect = __0;
        GUILayout.BeginArea(rect, HermesEftTheme.HeaderBand);
        GUILayout.BeginHorizontal();

        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        GUILayout.Label($"HERMES  /  {HermesEftWindowReflection.ActiveTabDisplayName(__instance)}", HermesEftTheme.HeaderTitle);
        var status = HermesEftWindowReflection.RefreshStatus(__instance);
        GUILayout.Label(
            string.IsNullOrWhiteSpace(status)
                ? "READ-ONLY INVENTORY INTELLIGENCE"
                : status,
            HermesEftTheme.HeaderSubtitle);
        GUILayout.EndVertical();

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("RESET", HermesEftTheme.HeaderAction, GUILayout.Width(74f), GUILayout.Height(28f)))
        {
            HermesEftWindowReflection.Clear(__instance);
        }

        var previousEnabled = GUI.enabled;
        GUI.enabled = previousEnabled && !HermesEftWindowReflection.IsRefreshing(__instance);
        if (GUILayout.Button(
                HermesEftWindowReflection.IsRefreshing(__instance) ? "WORKING" : "REFRESH",
                HermesEftTheme.HeaderAction,
                GUILayout.Width(86f),
                GUILayout.Height(28f)))
        {
            HermesEftWindowReflection.Refresh(__instance);
        }
        GUI.enabled = previousEnabled;

        // EFT already supplies the native BACK control at the top-right of InventoryScreen.
        // Keeping a second Back button inside HERMES made the workspace look like a nested tool.
        GUILayout.EndHorizontal();
        GUILayout.EndArea();
        return false;
    }
}

internal sealed class HermesEftNavigationRailPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesWindow).GetMethod("DrawInventoryNavigationRail", BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "DrawInventoryNavigationRail");

    [PatchPrefix]
    private static bool Prefix(HermesWindow __instance, Rect __0)
    {
        var rect = __0;
        GUILayout.BeginArea(rect, HermesEftTheme.NavigationRail);
        GUILayout.Label("WORKSPACES", HermesEftTheme.NavigationHeading);

        if (Plugin.Settings.EnableAssistantTab.Value)
        {
            DrawItem(__instance, "Assistant", "ASSISTANT");
        }
        DrawItem(__instance, "ItemSearch", "ITEMS & MARKET");
        DrawItem(__instance, "Hideout", "HIDEOUT");
        DrawItem(__instance, "Crafts", "CRAFTS");
        DrawItem(__instance, "Stash", "STASH");
        DrawItem(__instance, "Loadout", "LOADOUT");
        DrawItem(__instance, "RaidPlanner", "RAID PLANNER");

        GUILayout.FlexibleSpace();
        if (Plugin.Settings.ShowDiagnosticsFooter.Value)
        {
            GUILayout.Label("READ ONLY", HermesEftTheme.NavigationHeading);
            GUILayout.Label(HermesEftWindowReflection.FormatDiagnostics(__instance), HermesEftTheme.DiagnosticText);
            if (GUILayout.Button("COPY DIAGNOSTICS", HermesEftTheme.SmallButton, GUILayout.Height(25f)))
            {
                GUIUtility.systemCopyBuffer = HermesEftWindowReflection.BuildDiagnostics(__instance);
                HermesEftWindowReflection.SetRefreshStatus(__instance, "Diagnostics copied.");
            }
        }
        GUILayout.EndArea();
        return false;
    }

    private static void DrawItem(HermesWindow window, string tabName, string label)
    {
        var selected = HermesEftWindowReflection.IsSelected(window, tabName);
        if (GUILayout.Button(label, HermesEftTheme.NavigationItem(selected), GUILayout.Height(31f), GUILayout.ExpandWidth(true)))
        {
            HermesEftWindowReflection.Select(window, tabName);
        }
    }
}

internal sealed class HermesEftCompactNavigationPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesWindow).GetMethod("DrawCompactInventoryNavigation", BindingFlags.Instance | BindingFlags.NonPublic)
           ?? throw new MissingMethodException(typeof(HermesWindow).FullName, "DrawCompactInventoryNavigation");

    [PatchPrefix]
    private static bool Prefix(HermesWindow __instance, Rect __0)
    {
        var rect = __0;
        GUILayout.BeginArea(rect, HermesEftTheme.Toolbar);
        DrawRow(__instance,
            Plugin.Settings.EnableAssistantTab.Value
                ? [("Assistant", "ASSISTANT"), ("ItemSearch", "ITEMS"), ("Hideout", "HIDEOUT"), ("Crafts", "CRAFTS")]
                : [("ItemSearch", "ITEMS"), ("Hideout", "HIDEOUT"), ("Crafts", "CRAFTS")]);
        GUILayout.Space(3f);
        DrawRow(__instance, [("Stash", "STASH"), ("Loadout", "LOADOUT"), ("RaidPlanner", "RAID PLANNER")]);
        GUILayout.EndArea();
        return false;
    }

    private static void DrawRow(HermesWindow window, IReadOnlyList<(string Name, string Label)> items)
    {
        GUILayout.BeginHorizontal();
        foreach (var item in items)
        {
            var selected = HermesEftWindowReflection.IsSelected(window, item.Name);
            if (GUILayout.Button(item.Label, HermesEftTheme.Filter(selected), GUILayout.Height(27f), GUILayout.ExpandWidth(true)))
            {
                HermesEftWindowReflection.Select(window, item.Name);
            }
        }
        GUILayout.EndHorizontal();
    }
}

internal sealed class HermesEftPanelHeaderPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesUi).GetMethod(
               "DrawPanelHeader",
               BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
               null,
               [typeof(string), typeof(string), typeof(string), typeof(bool), typeof(Action)],
               null)
           ?? throw new MissingMethodException(typeof(HermesUi).FullName, "DrawPanelHeader");

    [PatchPrefix]
    private static bool Prefix(string __0, string __1, string __2, bool __3, Action __4)
    {
        GUILayout.BeginVertical(HermesEftTheme.PanelHeader);
        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical(GUILayout.ExpandWidth(true));
        GUILayout.Label(__0, HermesEftTheme.PanelTitle);
        if (!string.IsNullOrWhiteSpace(__1))
        {
            GUILayout.Label(__1, HermesEftTheme.Subtitle);
        }
        GUILayout.EndVertical();
        if (__3)
        {
            GUILayout.Label("WORKING", HermesEftTheme.StatusBadge("working"), GUILayout.Width(82f), GUILayout.Height(24f));
        }
        GUILayout.EndHorizontal();

        if (!string.IsNullOrWhiteSpace(__2))
        {
            GUILayout.Label(__2, HermesEftTheme.Status);
        }
        GUILayout.EndVertical();
        return false;
    }
}

internal sealed class HermesEftPanelTitlePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesUi).GetMethod(
               "DrawPanelTitle",
               BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
               null,
               [typeof(string), typeof(string), typeof(string), typeof(bool)],
               null)
           ?? throw new MissingMethodException(typeof(HermesUi).FullName, "DrawPanelTitle");

    [PatchPrefix]
    private static bool Prefix(string __0, string __1, string __2, bool __3)
    {
        GUILayout.BeginVertical(HermesEftTheme.PanelHeader);
        GUILayout.Label(__0, HermesEftTheme.PanelTitle);
        if (!string.IsNullOrWhiteSpace(__1))
        {
            GUILayout.Label(__1, HermesEftTheme.Subtitle);
        }
        if (!string.IsNullOrWhiteSpace(__2))
        {
            GUILayout.Label(__3 ? $"WORKING  •  {__2}" : __2, HermesEftTheme.Status);
        }
        GUILayout.EndVertical();
        return false;
    }
}

internal sealed class HermesEftTabButtonPatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesUi).GetMethod(
               "DrawTabButton",
               BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
               null,
               [typeof(string), typeof(bool), typeof(float)],
               null)
           ?? throw new MissingMethodException(typeof(HermesUi).FullName, "DrawTabButton");

    [PatchPrefix]
    private static bool Prefix(string __0, bool __1, float __2, ref bool __result)
    {
        __result = GUILayout.Button(
            __0.ToUpperInvariant(),
            HermesEftTheme.Tab(__1),
            GUILayout.Width(__2),
            GUILayout.Height(28f));
        return false;
    }
}

internal sealed class HermesEftEmptyStatePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesUi).GetMethod(
               "DrawEmptyState",
               BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
               null,
               [typeof(string), typeof(string)],
               null)
           ?? throw new MissingMethodException(typeof(HermesUi).FullName, "DrawEmptyState");

    [PatchPrefix]
    private static bool Prefix(string __0, string __1)
    {
        GUILayout.BeginVertical(HermesEftTheme.EmptyState);
        GUILayout.Label(__0, HermesEftTheme.RowTitle);
        if (!string.IsNullOrWhiteSpace(__1))
        {
            GUILayout.Space(3f);
            GUILayout.Label(__1, HermesEftTheme.RowMeta);
        }
        GUILayout.EndVertical();
        return false;
    }
}

internal sealed class HermesEftEmptyStateSinglePatch : ModulePatch
{
    protected override MethodBase GetTargetMethod()
        => typeof(HermesUi).GetMethod(
               "DrawEmptyState",
               BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
               null,
               [typeof(string)],
               null)
           ?? throw new MissingMethodException(typeof(HermesUi).FullName, "DrawEmptyState(string)");

    [PatchPrefix]
    private static bool Prefix(string __0)
    {
        GUILayout.BeginVertical(HermesEftTheme.EmptyState);
        GUILayout.Label(__0, HermesEftTheme.RowMeta);
        GUILayout.EndVertical();
        return false;
    }
}
