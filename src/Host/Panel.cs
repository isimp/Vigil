using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using HarmonyLib;
using UnityEngine;
using Vigil.Core;

namespace Vigil;

/// <summary>
/// The panel: one window, opened and closed with its key (Shift+F9 unless changed), also closed with
/// Escape or its close button, with a tab per kind of task. Each area adds its sections to the tabs;
/// buttons run the same commands as the console, and the last answer shows at the bottom. While it is
/// open the game takes no keys or mouse (it treats the panel like one of its own text boxes) and the
/// cursor is free; the window is drawn only while open, so a closed panel costs nothing. Its header
/// moves it and the grip in its corner resizes it; where it was left, its size, the tab last used and
/// the cards and results folded are kept in Vigil-state/panel.txt on this machine. A card's title
/// line folds it; its edge and a tag in its zone's colour say what its tools do to the game.
/// </summary>
internal sealed class Panel
{
    public static readonly string[] Tabs = { "Overview", "Network", "World", "Tools", "Health" };

    private readonly VigilPlugin _host;
    private readonly ConfigEntry<KeyboardShortcut> _key;
    private readonly ConfigEntry<float> _scale;
    private readonly string _placeFile;

    /// <summary>Where the panel is, in units of the unscaled panel.</summary>
    private Rect _rect;

    private int _tab;

    /// <summary>A tab clicked, opened on the next layout pass (see <see cref="Ui"/> on why clicks wait for it); -1 for none.</summary>
    private int _pendingTab = -1;

    /// <summary>The shown tab's sections and their badges as the last layout pass read them (see <see cref="LaidOut{T}"/>).</summary>
    private readonly LaidOut<List<(PanelSection Section, PanelBadge Badge)>> _cards = new();

    private Vector2 _scroll, _answerScroll;
    private string _answer;
    private bool _closeAtEndOfFrame;
    private Drag _drag;
    private Vector2 _dragFrom;
    private Rect _dragStart;

    private enum Drag
    {
        None,
        Move,
        Resize,
    }

    public Panel(VigilPlugin host, ConfigFile config, string stateDir)
    {
        _host = host;
        _key = config.Bind("Panel", "Key", new KeyboardShortcut(KeyCode.F9, KeyCode.LeftShift),
            "Opens and closes the panel. Escape closes it too. While it is open the game takes no keys or mouse.");
        _scale = config.Bind("Panel", "Scale", 1f, new ConfigDescription("Size of the panel and its text.", new AcceptableValueRange<float>(0.75f, 2f)));
        _placeFile = Path.Combine(stateDir, "panel.txt");
    }

    /// <summary>Whether the panel is open; the input patches read it.</summary>
    public static bool IsOpen { get; private set; }

    /// <summary>
    /// True while the right mouse button, pressed with the cursor off the panel, is held: the camera
    /// then follows the mouse (<see cref="PanelInput"/>); only the look, the rest stays held back.
    /// </summary>
    public static bool Looking { get; private set; }

    private static bool _lookEnded;

    /// <summary>Where the panel was last drawn, in screen pixels from the top left, for telling clicks off it.</summary>
    private Rect _drawn;

    /// <summary>True once after a look ends, so what reads the crosshair reads it again.</summary>
    public static bool TakeLookEnded()
    {
        bool ended = _lookEnded;
        _lookEnded = false;
        return ended;
    }

    /// <summary>The tab shown, or null while closed, for areas that only fetch while their tab is shown.</summary>
    public string OpenTab => IsOpen ? Tabs[_tab] : null;

    public string KeyName => _key.Value.ToString();

    /// <summary>While a self-test watches the panel: sections that fail to draw are listed in <see cref="DrawErrors"/>.</summary>
    public bool Collecting { get; set; }

    public List<string> DrawErrors { get; } = new();

    public int TabIndex => _tab;

    /// <summary>Opens a tab on the next layout pass, as a click on it would.</summary>
    public void ShowTab(int tab)
    {
        if (tab >= 0 && tab < Tabs.Length && tab != _tab)
        {
            _pendingTab = tab;
        }
    }

    /// <summary>Opens a card: its tab on the next layout pass, the card unfolded.</summary>
    public void Reveal(string tab, string card)
    {
        Ui.SetFolded("card:" + tab + "/" + card, false);
        ShowTab(Array.IndexOf(Tabs, tab));
    }

    // Right mouse pressed off the panel starts a look, letting go ends it; closing the panel ends it too.
    private void UpdateLook()
    {
        if (!IsOpen)
        {
            if (Looking)
            {
                Looking = false;
                _lookEnded = false;
            }

            return;
        }

        if (!Looking && Input.GetMouseButtonDown(1))
        {
            var m = Input.mousePosition;
            Looking = !_drawn.Contains(new Vector2(m.x, Screen.height - m.y));
        }
        else if (Looking && !Input.GetMouseButton(1))
        {
            Looking = false;
            _lookEnded = true;
        }
    }

    public void Update()
    {
        UpdateLook();
        if (!IsOpen)
        {
            if (_key.Value.IsDown() && MayOpen())
            {
                Open();
            }

            return;
        }

        if (_key.Value.IsDown() || Input.GetKeyDown(KeyCode.Escape))
        {
            _closeAtEndOfFrame = true;
        }
    }

    /// <summary>
    /// Closing waits for the end of the frame: the game still sees the panel as a text box for the
    /// rest of this one, so it does not take the same Escape for its own menu.
    /// </summary>
    public void LateUpdate()
    {
        if (_closeAtEndOfFrame)
        {
            _closeAtEndOfFrame = false;
            Close();
        }
    }

    public void Toggle()
    {
        if (IsOpen)
        {
            Close();
        }
        else
        {
            Open();
        }
    }

    /// <summary>Shows an answer at the bottom of the panel.</summary>
    public void Show(string answer)
    {
        _answer = answer;
        _answerScroll = Vector2.zero;
    }

    public void OnGUI()
    {
        if (!IsOpen)
        {
            return;
        }

        float s = Scale;
        Skin.Ensure(s);
        var skin = GUI.skin;
        GUI.skin = Skin.Gui;
        try
        {
            Draw(s);
        }
        finally
        {
            GUI.skin = skin;
        }
    }

    private float Scale => Mathf.Clamp(_scale.Value, 0.75f, 2f);

    /// <summary>
    /// The panel, laid out at its scale: the header with the name and a close button, the tabs,
    /// the open tab's sections each on a card, the last answer in a well at the bottom, and a grip
    /// in the corner. The header moves the panel and the grip resizes it. Its place is kept in
    /// units of the unscaled panel, so a change of scale keeps it where it was.
    /// </summary>
    private void Draw(float s)
    {
        var e = Event.current;
        int dragId = GUIUtility.GetControlID(FocusType.Passive);
        Drags(e, dragId, s);
        Ui.BeginPass();
        if (_pendingTab >= 0 && e.type == EventType.Layout)
        {
            _tab = _pendingTab;
            _pendingTab = -1;
            _scroll = Vector2.zero;
        }

        if (_drag != Drag.Resize)
        {
            _rect = OnScreen(_rect);
        }

        var win = new Rect(_rect.x * s, _rect.y * s, _rect.width * s, _rect.height * s);
        _drawn = win;
        float w = win.width, h = win.height, pad = Skin.U(16f);
        Skin.Box(win, Skin.Backdrop, Skin.Outline);
        GUI.BeginGroup(win);

        float titleWidth = Skin.Title.CalcSize(new GUIContent("Vigil")).x;
        GUI.Label(new Rect(pad, Skin.U(8f), titleWidth, Skin.U(36f)), "Vigil", Skin.Title);
        GUI.Label(new Rect(pad + titleWidth + Skin.U(10f), Skin.U(14f), Skin.U(120f), Skin.U(28f)), VigilVersion.Value, Skin.Version);
        var close = new Rect(w - pad - Skin.U(30f), Skin.U(10f), Skin.U(30f), Skin.U(30f));
        if (GUI.Button(close, "×", Skin.Close))
        {
            _closeAtEndOfFrame = true;
        }

        float tabsTop = Skin.U(48f), tabsHeight = Skin.U(30f);
        DrawTabs(tabsTop, tabsHeight, pad);
        Skin.Fill(new Rect(pad, tabsTop + tabsHeight, w - pad * 2f, Mathf.Max(1f, Skin.U(1f))), Skin.Outline);

        float answerHeight = _answer != null ? Skin.U(130f) : 0f;
        float top = tabsTop + tabsHeight + Skin.U(12f);
        float bottom = h - pad - (_answer != null ? answerHeight + Skin.U(10f) : 0f);
        DrawSections(new Rect(pad, top, w - pad * 2f, Mathf.Max(0f, bottom - top)));
        if (_answer != null)
        {
            DrawAnswer(new Rect(pad, h - pad - answerHeight, w - pad * 2f, answerHeight));
        }

        var grip = new Rect(w - Skin.U(20f), h - Skin.U(20f), Skin.U(18f), Skin.U(18f));
        for (int i = 0; i < 3; i++)
        {
            float d = Skin.U(4f) * i;
            Skin.Fill(new Rect(grip.xMax - Skin.U(6f) - d, grip.yMax - Skin.U(6f), Skin.U(2f), Skin.U(2f)), Skin.Faint);
            Skin.Fill(new Rect(grip.xMax - Skin.U(6f), grip.yMax - Skin.U(6f) - d, Skin.U(2f), Skin.U(2f)), Skin.Faint);
        }

        GUI.EndGroup();

        // A press no button took: on the grip it resizes, on the header it moves the panel, and
        // anywhere else on the panel it takes the keyboard from a text box.
        if (e.type == EventType.MouseDown && e.button == 0 && win.Contains(e.mousePosition))
        {
            var at = e.mousePosition - win.position;
            if (grip.Contains(at))
            {
                StartDrag(Drag.Resize, e, dragId);
            }
            else if (at.y < tabsTop)
            {
                StartDrag(Drag.Move, e, dragId);
            }
            else
            {
                GUIUtility.keyboardControl = 0;
                e.Use();
            }
        }

        Ui.EndPass();
    }

    /// <summary>The tabs as words in a row, the open one in the accent and underlined.</summary>
    private void DrawTabs(float top, float height, float left)
    {
        float x = left;
        for (int i = 0; i < Tabs.Length; i++)
        {
            var style = i == _tab ? Skin.TabTextOn : Skin.TabText;
            float width = style.CalcSize(new GUIContent(Tabs[i])).x + Skin.U(4f);
            var rect = new Rect(x, top, width, height);
            if (GUI.Button(rect, Tabs[i], style) && i != _tab)
            {
                _pendingTab = i;
            }

            if (i == _tab)
            {
                Skin.Fill(new Rect(rect.x, rect.yMax - Skin.U(2f), rect.width, Skin.U(2f)), Skin.Accent);
            }

            x += width + Skin.U(20f);
        }
    }

    private void DrawSections(Rect rect)
    {
        GUILayout.BeginArea(rect);
        _scroll = GUILayout.BeginScrollView(_scroll, false, false, GUIStyle.none, Skin.Gui.verticalScrollbar, GUIStyle.none);
        foreach (var (section, badge) in _cards.Read(Event.current.type == EventType.Layout, ReadCards))
        {
            string fold = "card:" + section.Tab + "/" + section.Title;
            bool open = !Ui.Folds.Contains(fold);
            Ui.StartSection(section.Title);
            GUILayout.BeginVertical(Skin.CardBox);
            GUILayout.BeginHorizontal();
            var chevron = GUILayoutUtility.GetRect(Skin.U(14f), Skin.U(18f), GUILayout.ExpandWidth(false));
            GUILayout.Label(section.Title, Skin.CardTitle, GUILayout.ExpandWidth(false));
            if (badge != null)
            {
                GUILayout.Label(badge.Text, Skin.Badge(badge.Tone), GUILayout.ExpandWidth(false));
            }

            GUILayout.FlexibleSpace();
            if (section.ZoneTag != null)
            {
                GUILayout.Label("<color=#" + ColorUtility.ToHtmlStringRGB(Skin.Of(section.Zone)) + ">" + section.ZoneTag + "</color>", Skin.ZoneTag, GUILayout.ExpandWidth(false));
            }

            GUILayout.EndHorizontal();

            // The whole title line folds the card, not only its triangle.
            var head = GUILayoutUtility.GetLastRect();
            if (Ui.Fold(chevron, new Rect(chevron.x, head.y, head.xMax - chevron.x, head.height), open))
            {
                Ui.Later(() => Ui.SetFolded(fold, open));
            }

            if (open)
            {
                try
                {
                    section.Draw();
                }
                catch (Exception e)
                {
                    Ui.ForgetLine();
                    Ui.Note("This section could not be drawn: " + e.GetType().Name + " " + e.Message);
                    if (Collecting)
                    {
                        DrawErrors.Add(section.Tab + " / " + section.Title + ": " + e.GetType().Name + " " + e.Message);
                    }
                }
            }

            GUILayout.EndVertical();
            if (section.Zone != PanelZone.Watch)
            {
                var card = GUILayoutUtility.GetLastRect();
                Skin.Fill(new Rect(card.x, card.y + Skin.U(10f), Skin.U(3f), Mathf.Max(0f, card.height - Skin.U(20f))), Skin.Of(section.Zone));
            }
        }

        GUILayout.EndScrollView();
        GUILayout.EndArea();
    }

    /// <summary>
    /// The shown tab's sections, each with its badge, read on a layout pass. A click handled during
    /// that pass can make a badge appear or go (Compare, Show, Find); the pass that draws the cards
    /// keeps the line it laid out, and the next layout pass shows the change.
    /// </summary>
    private List<(PanelSection Section, PanelBadge Badge)> ReadCards()
    {
        var cards = new List<(PanelSection Section, PanelBadge Badge)>();
        foreach (var section in _host.SectionsOn(Tabs[_tab]))
        {
            cards.Add((section, BadgeOf(section)));
        }

        return cards;
    }

    /// <summary>The section's badge; none when it has none or cannot say.</summary>
    private static PanelBadge BadgeOf(PanelSection section)
    {
        try
        {
            return section.Badge?.Invoke();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private void DrawAnswer(Rect rect)
    {
        GUILayout.BeginArea(rect, Skin.Well);
        GUILayout.BeginHorizontal();
        GUILayout.Label("Answer", Skin.WellTitle);
        GUILayout.FlexibleSpace();
        bool clear = GUILayout.Button("Clear", Skin.Quiet, GUILayout.ExpandWidth(false));
        GUILayout.EndHorizontal();
        _answerScroll = GUILayout.BeginScrollView(_answerScroll, false, false, GUIStyle.none, Skin.Gui.verticalScrollbar, GUIStyle.none);
        GUILayout.Label(_answer, Skin.Answer);
        GUILayout.EndScrollView();
        GUILayout.EndArea();
        if (clear)
        {
            _answer = null;
        }
    }

    private void StartDrag(Drag drag, Event e, int dragId)
    {
        _drag = drag;
        _dragFrom = e.mousePosition;
        _dragStart = _rect;
        GUIUtility.hotControl = dragId;
        e.Use();
    }

    /// <summary>Moves or resizes the panel by how far the mouse went since it was pressed, until it is let go.</summary>
    private void Drags(Event e, int dragId, float s)
    {
        if (_drag == Drag.None)
        {
            return;
        }

        if (e.rawType == EventType.MouseUp || !Input.GetMouseButton(0))
        {
            _drag = Drag.None;
            if (GUIUtility.hotControl == dragId)
            {
                GUIUtility.hotControl = 0;
            }

            return;
        }

        if (e.type != EventType.MouseDrag)
        {
            return;
        }

        var moved = (e.mousePosition - _dragFrom) / s;
        int screenWidth = (int)(Screen.width / s), screenHeight = (int)(Screen.height / s);
        if (_drag == Drag.Move)
        {
            _rect = OnScreen(new Rect(_dragStart.x + moved.x, _dragStart.y + moved.y, _rect.width, _rect.height));
        }
        else
        {
            var p = new PanelPlacement { X = (int)_dragStart.x, Y = (int)_dragStart.y, Width = (int)_dragStart.width, Height = (int)_dragStart.height }
                .Resized((int)(_dragStart.width + moved.x), (int)(_dragStart.height + moved.y), screenWidth, screenHeight);
            _rect = new Rect(p.X, p.Y, p.Width, p.Height);
        }

        e.Use();
    }

    private void Open()
    {
        string text = null;
        try
        {
            if (File.Exists(_placeFile))
            {
                text = File.ReadAllText(_placeFile);
            }
        }
        catch (Exception)
        {
            // an unreadable file opens the panel centred
        }

        var place = PanelPlacement.Parse(text);
        _tab = Math.Max(0, Array.IndexOf(Tabs, place.Tab));
        Ui.Folds = place.Folds;
        var p = place.Clamped((int)(Screen.width / Scale), (int)(Screen.height / Scale));
        _rect = new Rect(p.X, p.Y, p.Width, p.Height);
        IsOpen = true;
    }

    private void Close()
    {
        IsOpen = false;
        _drag = Drag.None;
        try
        {
            Ui.MakeWaitingChanges();
        }
        finally
        {
            SavePlace();
        }
    }

    private void SavePlace()
    {
        try
        {
            var place = new PanelPlacement { X = (int)_rect.x, Y = (int)_rect.y, Width = (int)_rect.width, Height = (int)_rect.height, Tab = Tabs[_tab], Folds = Ui.Folds };
            Directory.CreateDirectory(Path.GetDirectoryName(_placeFile));
            File.WriteAllText(_placeFile, place.Format());
        }
        catch (Exception)
        {
            // where the panel was is only a convenience
        }
    }

    private Rect OnScreen(Rect r)
    {
        var p = new PanelPlacement { X = (int)r.x, Y = (int)r.y, Width = (int)r.width, Height = (int)r.height }
            .Clamped((int)(Screen.width / Scale), (int)(Screen.height / Scale));
        return new Rect(p.X, p.Y, r.width, r.height);
    }

    /// <summary>Not while the console, the chat, a text box or the game's menu has the keyboard.</summary>
    private static bool MayOpen()
    {
        try
        {
            return !global::Console.IsVisible() && !(Chat.instance && Chat.instance.HasFocus()) && !TextInput.IsVisible() && !Menu.IsVisible();
        }
        catch (Exception)
        {
            return true;
        }
    }
}

/// <summary>
/// Keeps the game's hands off the keyboard and mouse while the panel is open. The game already
/// stops moving, looking, attacking and opening its menu while one of its text boxes is up, and
/// frees the cursor: <c>TextInput.IsVisible</c> answering yes gets all of that at once. The mouse
/// wheel is read apart from that, so it is held back as well, and only scrolls the panel.
/// </summary>
internal static class PanelInput
{
    /// <summary>What could not be put in, for the self-test.</summary>
    public static List<string> Problems { get; } = new();

    public static int Installed { get; private set; }

    /// <summary>Lives as long as the game: the patches stay in.</summary>
    private static Harmony _harmony;

    public static void Patch(string id)
    {
        _harmony ??= new Harmony(id);
        var harmony = _harmony;
        Put(harmony, AccessTools.Method(typeof(TextInput), nameof(TextInput.IsVisible)), nameof(TextBox), "keys held back from the game");
        Put(harmony, AccessTools.Method(typeof(ZInput), "Internal_GetMouseScrollWheel"), nameof(Wheel), "mouse wheel held back from the camera");
        Put(harmony, AccessTools.Method(typeof(PlayerController), "LateUpdate"), nameof(Look), "camera look with right mouse off the panel");
        Put(harmony, AccessTools.Method(typeof(GameCamera), nameof(GameCamera.UpdateMouseCapture)), nameof(Capture), "cursor held while looking");
    }

    private static void Put(Harmony harmony, System.Reflection.MethodInfo original, string postfix, string what)
    {
        try
        {
            if (original == null)
            {
                Problems.Add(what + ": not found in this game version");
                return;
            }

            harmony.Patch(original, postfix: new HarmonyMethod(typeof(PanelInput), postfix));
            Installed++;
        }
        catch (Exception e)
        {
            Problems.Add(what + ": " + e.GetType().Name + " " + e.Message);
        }
    }

    private static void TextBox(ref bool __result)
    {
        if (Panel.IsOpen)
        {
            __result = true;
        }
    }

    // The game set no look (it sees a text box up); while looking, the mouse moves the camera as the game's own look does.
    private static void Look()
    {
        var player = Player.m_localPlayer;
        if (!Panel.IsOpen || !Panel.Looking || player == null)
        {
            return;
        }

        var delta = ZInput.GetMouseDelta() * PlayerController.m_mouseSens;
        if (PlayerController.m_invertMouse)
        {
            delta.y *= -1f;
        }

        player.SetMouseLook(delta);
    }

    private static void Capture()
    {
        if (Panel.IsOpen && Panel.Looking)
        {
            ZCursor.LockState = CursorLockMode.Locked;
        }
    }

    private static void Wheel(ref float __result)
    {
        if (Panel.IsOpen)
        {
            __result = 0f;
        }
    }
}
