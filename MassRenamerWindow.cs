#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using Object = UnityEngine.Object;

/// <summary>
/// Batch renamer for Hierarchy objects and Project assets with a live preview,
/// type-based prefixing and team-shareable presets.
/// Scene objects: renamed with a single Undo step.
/// Project assets: renamed on disk via AssetDatabase.RenameAsset (not covered by Undo).
/// Presets are stored in ProjectSettings/MassRenamerPresets.json (version-control friendly).
/// </summary>
public class MassRenamerWindow : EditorWindow
{
    // ------------------------------------------------------------------ Types

    public enum Mode
    {
        Sequential = 0,
        SearchReplace = 1,
        TypePrefix = 2
    }

    /// <summary>Buckets used by the Type Prefix mode. Order here is the order shown in the rules UI.</summary>
    public enum AssetCategory
    {
        Texture,
        Material,
        Prefab,
        Model,
        Mesh,
        Animation,
        AnimatorController,
        Audio,
        Shader,
        ScriptableObject,
        Scene,
        SceneObject,
        Other
    }

    [Serializable]
    public class PrefixRule
    {
        public AssetCategory category;
        public string prefix;
    }

    [Serializable]
    public class RenamerPreset
    {
        public string name;
        public Mode mode;
        public string baseName;
        public int startNumber;
        public int increment;
        public int digitPadding;
        public string searchFor;
        public string replaceWith;
        public bool caseSensitive;
        public bool sortByHierarchy;
        public bool replaceKnownPrefixes;
        public List<PrefixRule> rules = new List<PrefixRule>();
    }

    [Serializable]
    public class PresetLibrary
    {
        public List<RenamerPreset> presets = new List<RenamerPreset>();
    }

    private enum EntryStatus
    {
        Unchanged,
        Rename,
        Skipped,
        Error
    }

    private sealed class PreviewEntry
    {
        public Object Target;
        public bool IsAsset;
        public string OldName;
        public string NewName;
        public EntryStatus Status;
        public string Message;
        public GUIContent OldContent;
        public GUIContent NewContent;
    }

    // -------------------------------------------------------------- Constants

    private static readonly string[] ModeLabels = { "Sequential Rename", "Search & Replace", "Type Prefix" };
    private const float RowHeight = 20f;
    private const float PresetButtonWidth = 56f;
    private const string UndoLabel = "Mass Rename Objects";
    private const string NonePresetLabel = "(None)";

    private static string PresetFilePath
    {
        get
        {
            return Path.GetFullPath(Path.Combine(
                Application.dataPath, "..", "ProjectSettings", "MassRenamerPresets.json"));
        }
    }

    // ------------------------------------------------------- Serialized state

    [SerializeField] private Mode mode = Mode.Sequential;

    // Sequential
    [SerializeField] private string baseName = "Object_";
    [SerializeField] private int startNumber = 0;
    [SerializeField] private int increment = 1;
    [SerializeField] private int digitPadding = 0;

    // Search & Replace
    [SerializeField] private string searchFor = "";
    [SerializeField] private string replaceWith = "";
    [SerializeField] private bool caseSensitive = true;

    // Type Prefix
    [SerializeField] private bool replaceKnownPrefixes = true;
    [SerializeField] private List<PrefixRule> rules = new List<PrefixRule>();
    [SerializeField] private bool rulesFoldout = false;

    // Shared
    [SerializeField] private bool sortByHierarchy = true;

    // Presets (UI state)
    [SerializeField] private string selectedPresetName = "";
    [SerializeField] private string presetNameInput = "";

    // ------------------------------------------------------ Transient state

    private readonly List<PreviewEntry> entries = new List<PreviewEntry>();
    private Vector2 scrollPos;
    private bool previewDirty = true;

    // Cached results of the last preview rebuild. All layout-affecting UI reads
    // these (never live data) so Layout and Repaint events always agree.
    private bool canApply;
    private int renameCount;
    private int selectedCount;
    private bool showAssetUndoNote;
    private string statusMessage;
    private MessageType statusType = MessageType.None;
    private string detectedSummary;

    // Presets (runtime)
    private PresetLibrary library = new PresetLibrary();
    private string[] presetPopupOptions = new[] { NonePresetLabel };
    private int selectedPresetIndex = -1;
    private bool presetModified;
    private bool hasPendingPreset;
    private string pendingPresetName;

    // Styles (created lazily, recreated if the editor skin changes)
    private GUIStyle rowStyle;
    private GUIStyle changedStyle;
    private GUIStyle mutedStyle;
    private GUIStyle errorStyle;
    private GUIStyle wrapMiniStyle;
    private bool stylesProSkin;

    // ------------------------------------------------------------ Window setup

    [MenuItem("Tools/Mass Renamer")]
    public static void ShowWindow()
    {
        var window = GetWindow<MassRenamerWindow>("Mass Renamer");
        window.minSize = new Vector2(420f, 440f);
        window.Show();
    }

    private void OnEnable()
    {
        Undo.undoRedoPerformed += OnExternalChange;
        rules = NormalizeRules(rules);
        LoadPresetLibrary();
        previewDirty = true;
    }

    private void OnDisable()
    {
        Undo.undoRedoPerformed -= OnExternalChange;
    }

    // Pick up preset edits made by teammates (version control) or another window.
    private void OnFocus()
    {
        LoadPresetLibrary();
        previewDirty = true;
    }

    private void OnSelectionChange() { OnExternalChange(); }
    private void OnHierarchyChange() { OnExternalChange(); }
    private void OnProjectChange() { OnExternalChange(); }

    private void OnExternalChange()
    {
        previewDirty = true;
        Repaint();
    }

    // -------------------------------------------------------------------- GUI

    private void OnGUI()
    {
        EnsureStyles();

        // State changes that alter which controls are drawn (loading a preset can switch
        // mode) and preview rebuilds only happen on Layout, so Layout/Repaint always agree.
        if (Event.current.type == EventType.Layout)
        {
            if (hasPendingPreset)
            {
                ApplyPendingPreset();
            }

            if (previewDirty)
            {
                RebuildPreview();
            }
        }

        GUILayout.Space(6f);
        DrawPresetBar();
        GUILayout.Space(6f);

        // Mode toolbar
        Mode newMode = (Mode)GUILayout.Toolbar((int)mode, ModeLabels, GUILayout.Height(24f));
        if (newMode != mode)
        {
            mode = newMode;
            GUI.FocusControl(null); // avoid stale text in a focused field after the layout swaps
            previewDirty = true;
        }

        GUILayout.Space(6f);

        EditorGUI.BeginChangeCheck();
        DrawSettings();
        if (EditorGUI.EndChangeCheck())
        {
            previewDirty = true;
        }

        GUILayout.Space(4f);
        EditorGUILayout.LabelField(
            $"Selected: {selectedCount}   |   Will rename: {renameCount}",
            EditorStyles.miniLabel);

        DrawPreview();
        DrawFooter();
    }

    private void DrawPresetBar()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Preset", GUILayout.Width(50f));

                int popupIndex = EditorGUILayout.Popup(selectedPresetIndex + 1, presetPopupOptions);
                if (popupIndex - 1 != selectedPresetIndex)
                {
                    QueuePresetSelection(popupIndex == 0 ? null : library.presets[popupIndex - 1].name);
                }

                using (new EditorGUI.DisabledScope(selectedPresetIndex < 0 || !presetModified))
                {
                    var updateContent = new GUIContent("Update", "Overwrite the selected preset with the current settings.");
                    if (GUILayout.Button(updateContent, EditorStyles.miniButtonLeft, GUILayout.Width(PresetButtonWidth)))
                    {
                        StorePreset(library.presets[selectedPresetIndex].name, false);
                    }
                }

                using (new EditorGUI.DisabledScope(selectedPresetIndex < 0))
                {
                    if (GUILayout.Button("Delete", EditorStyles.miniButtonRight, GUILayout.Width(PresetButtonWidth)))
                    {
                        DeleteSelectedPreset();
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Label("Save As", GUILayout.Width(50f));
                presetNameInput = EditorGUILayout.TextField(presetNameInput);

                using (new EditorGUI.DisabledScope(string.IsNullOrWhiteSpace(presetNameInput)))
                {
                    var saveContent = new GUIContent("Save", "Save the current settings as a preset (existing names are overwritten after confirmation).");
                    if (GUILayout.Button(saveContent, GUILayout.Width(PresetButtonWidth)))
                    {
                        StorePreset(presetNameInput, true);
                    }
                }
            }

            if (selectedPresetIndex >= 0 && presetModified)
            {
                EditorGUILayout.LabelField("Settings differ from the saved preset.", EditorStyles.miniLabel);
            }
        }
    }

    private void DrawSettings()
    {
        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
        {
            switch (mode)
            {
                case Mode.Sequential:
                    baseName = EditorGUILayout.TextField("Base Name", baseName);
                    startNumber = EditorGUILayout.IntField("Start Number", startNumber);
                    increment = EditorGUILayout.IntField("Increment By", increment);
                    digitPadding = Mathf.Clamp(
                        EditorGUILayout.IntField(
                            new GUIContent("Digit Padding", "Minimum digits, e.g. 3 gives 001, 002, ... (0 = none)"),
                            digitPadding),
                        0, 9);
                    break;

                case Mode.SearchReplace:
                    searchFor = EditorGUILayout.TextField("Search For", searchFor);
                    replaceWith = EditorGUILayout.TextField("Replace With", replaceWith);
                    caseSensitive = EditorGUILayout.Toggle("Case Sensitive", caseSensitive);
                    break;

                default:
                    DrawTypePrefixSettings();
                    break;
            }

            sortByHierarchy = EditorGUILayout.Toggle(
                new GUIContent("Sort By Hierarchy Order",
                    "On: preview and numbering follow Hierarchy sibling order (or asset path order).\n" +
                    "Off: they follow the order objects were selected in."),
                sortByHierarchy);
        }
    }

    private void DrawTypePrefixSettings()
    {
        EditorGUILayout.LabelField(detectedSummary ?? "Detected: -", wrapMiniStyle);

        replaceKnownPrefixes = EditorGUILayout.Toggle(
            new GUIContent("Replace Existing Prefixes",
                "On: a name that already starts with any prefix from the rules (e.g. M_Wood on a texture) " +
                "has that prefix swapped for the correct one (T_Wood).\n" +
                "Off: the new prefix is simply added in front."),
            replaceKnownPrefixes);

        rulesFoldout = EditorGUILayout.Foldout(rulesFoldout, "Prefix Rules", true);
        if (!rulesFoldout) return;

        EditorGUI.indentLevel++;
        for (int i = 0; i < rules.Count; i++)
        {
            rules[i].prefix = EditorGUILayout.TextField(
                CategoryLabel(rules[i].category), rules[i].prefix);
        }
        EditorGUI.indentLevel--;

        GUILayout.Space(2f);
        using (new EditorGUILayout.HorizontalScope())
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("Reset Rules to Defaults", EditorStyles.miniButton, GUILayout.Width(160f)))
            {
                rules = NormalizeRules(null);
                previewDirty = true;
                GUI.FocusControl(null);
            }
        }
        EditorGUILayout.LabelField("An empty prefix means \"do not rename this type\".", EditorStyles.miniLabel);
    }

    private void DrawPreview()
    {
        GUILayout.Space(4f);
        EditorGUILayout.LabelField("Preview", EditorStyles.boldLabel);

        using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true)))
        {
            // Column header
            Rect header = GUILayoutUtility.GetRect(0f, RowHeight, GUILayout.ExpandWidth(true));
            if (Event.current.type == EventType.Repaint)
            {
                EditorStyles.toolbar.Draw(header, false, false, false, false);
            }
            float headerCol = (header.width - GUI.skin.verticalScrollbar.fixedWidth) * 0.5f;
            GUI.Label(new Rect(header.x + 4f, header.y, headerCol - 8f, header.height), "Current Name", EditorStyles.boldLabel);
            GUI.Label(new Rect(header.x + headerCol + 4f, header.y, headerCol - 8f, header.height), "New Name", EditorStyles.boldLabel);

            scrollPos = EditorGUILayout.BeginScrollView(scrollPos, false, true, GUILayout.ExpandHeight(true));

            int total = entries.Count;
            if (total == 0)
            {
                GUILayout.Label("Select objects in the Hierarchy or Project window.",
                    EditorStyles.centeredGreyMiniLabel, GUILayout.ExpandHeight(true));
            }
            else
            {
                // One layout control reserves the full scroll height; rows are then
                // drawn manually and virtualized, so huge selections stay cheap.
                Rect content = GUILayoutUtility.GetRect(0f, total * RowHeight, GUILayout.ExpandWidth(true));

                if (Event.current.type == EventType.Repaint)
                {
                    int first = Mathf.Max(0, Mathf.FloorToInt(scrollPos.y / RowHeight));
                    int last = Mathf.Min(total, first + Mathf.CeilToInt(position.height / RowHeight) + 2);

                    Vector2 previousIconSize = EditorGUIUtility.GetIconSize();
                    EditorGUIUtility.SetIconSize(new Vector2(16f, 16f));

                    for (int i = first; i < last; i++)
                    {
                        var rowRect = new Rect(content.x, content.y + i * RowHeight, content.width, RowHeight);
                        DrawRow(rowRect, i, entries[i]);
                    }

                    EditorGUIUtility.SetIconSize(previousIconSize);
                }
            }

            EditorGUILayout.EndScrollView();
        }
    }

    private void DrawRow(Rect rowRect, int index, PreviewEntry entry)
    {
        if (index % 2 == 1)
        {
            EditorGUI.DrawRect(rowRect, EditorGUIUtility.isProSkin
                ? new Color(1f, 1f, 1f, 0.04f)
                : new Color(0f, 0f, 0f, 0.05f));
        }

        float half = rowRect.width * 0.5f;
        var oldRect = new Rect(rowRect.x + 4f, rowRect.y, half - 8f, rowRect.height);
        var newRect = new Rect(rowRect.x + half + 4f, rowRect.y, half - 8f, rowRect.height);

        GUI.Label(oldRect, entry.OldContent, rowStyle);
        GUI.Label(newRect, entry.NewContent, StyleFor(entry.Status));
    }

    private void DrawFooter()
    {
        GUILayout.Space(4f);

        if (!string.IsNullOrEmpty(statusMessage))
        {
            EditorGUILayout.HelpBox(statusMessage, statusType);
        }

        if (showAssetUndoNote)
        {
            EditorGUILayout.HelpBox(
                "Project assets are renamed on disk through the AssetDatabase. " +
                "That part of the operation cannot be reverted with Ctrl+Z.",
                MessageType.Info);
        }

        using (new EditorGUI.DisabledScope(!canApply))
        {
            string label = canApply
                ? $"Rename {renameCount} Object{(renameCount == 1 ? "" : "s")}"
                : "Rename Selected";

            if (GUILayout.Button(label, GUILayout.Height(30f)))
            {
                ApplyRename();
            }
        }

        GUILayout.Space(6f);
    }

    // --------------------------------------------------------------- Preview

    private void RebuildPreview()
    {
        previewDirty = false;
        entries.Clear();

        renameCount = 0;
        selectedCount = 0;
        canApply = false;
        showAssetUndoNote = false;
        statusMessage = null;
        statusType = MessageType.None;
        detectedSummary = null;

        IEnumerable<Object> selection = Selection.objects.Where(o => o != null);
        if (sortByHierarchy)
        {
            selection = selection.OrderBy(GetSortKey, StringComparer.Ordinal);
        }
        List<Object> list = selection.ToList();
        selectedCount = list.Count;

        string inputProblem = GetInputProblem();
        bool inputsValid = inputProblem == null;

        int counter = startNumber;
        string numberFormat = "D" + digitPadding;
        var claimedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var categoryCounts = new Dictionary<AssetCategory, int>();

        foreach (Object obj in list)
        {
            bool isAsset = EditorUtility.IsPersistent(obj);
            var entry = new PreviewEntry
            {
                Target = obj,
                IsAsset = isAsset,
                OldName = obj.name,
                NewName = obj.name,
                Status = EntryStatus.Unchanged
            };

            string skipReason = GetSkipReason(obj, isAsset);
            if (skipReason != null)
            {
                entry.Status = EntryStatus.Skipped;
                entry.Message = skipReason;
            }
            else if (inputsValid)
            {
                string candidate = null;

                switch (mode)
                {
                    case Mode.Sequential:
                    {
                        candidate = baseName + counter.ToString(numberFormat, CultureInfo.InvariantCulture);
                        unchecked { counter += increment; }
                        break;
                    }

                    case Mode.SearchReplace:
                    {
                        candidate = ReplaceText(obj.name, searchFor, replaceWith, caseSensitive);
                        break;
                    }

                    default:
                    {
                        AssetCategory category = GetCategory(obj);
                        int count;
                        categoryCounts.TryGetValue(category, out count);
                        categoryCounts[category] = count + 1;

                        string prefix = GetPrefix(category);
                        if (string.IsNullOrEmpty(prefix))
                        {
                            entry.Status = EntryStatus.Skipped;
                            entry.Message = "no prefix rule for " + CategoryLabel(category);
                        }
                        else
                        {
                            entry.Message = "Type: " + CategoryLabel(category);
                            candidate = ApplyTypePrefix(obj.name, prefix);
                        }
                        break;
                    }
                }

                if (candidate != null)
                {
                    entry.NewName = candidate;
                    EvaluateCandidate(entry, claimedPaths);
                }
            }

            BuildContent(entry);
            entries.Add(entry);
        }

        if (mode == Mode.TypePrefix)
        {
            var parts = new List<string>();
            foreach (AssetCategory category in Enum.GetValues(typeof(AssetCategory)))
            {
                int n;
                if (categoryCounts.TryGetValue(category, out n))
                {
                    parts.Add($"{CategoryLabel(category)} x{n}");
                }
            }
            detectedSummary = "Detected: " + (parts.Count > 0 ? string.Join(", ", parts) : "nothing selected");
        }

        int errorCount = 0;
        foreach (PreviewEntry e in entries)
        {
            if (e.Status == EntryStatus.Rename)
            {
                renameCount++;
                if (e.IsAsset) showAssetUndoNote = true;
            }
            else if (e.Status == EntryStatus.Error)
            {
                errorCount++;
            }
        }

        if (selectedCount == 0)
        {
            statusMessage = "No objects selected. Select objects in the Hierarchy or Project window.";
            statusType = MessageType.Info;
        }
        else if (!inputsValid)
        {
            statusMessage = inputProblem;
            statusType = MessageType.Warning;
        }
        else if (errorCount > 0)
        {
            statusMessage = $"{errorCount} item(s) have problems (shown in red). Fix them before renaming.";
            statusType = MessageType.Error;
        }
        else if (renameCount == 0)
        {
            statusMessage = "Nothing to rename: none of the selected objects would change.";
            statusType = MessageType.Info;
        }
        else
        {
            canApply = true;
        }

        // Does the current configuration differ from the selected preset?
        presetModified = false;
        if (selectedPresetIndex >= 0 && selectedPresetIndex < library.presets.Count)
        {
            RenamerPreset stored = library.presets[selectedPresetIndex];
            presetModified = JsonUtility.ToJson(stored) != JsonUtility.ToJson(CaptureCurrent(stored.name));
        }
    }

    private string GetInputProblem()
    {
        switch (mode)
        {
            case Mode.Sequential:
                return string.IsNullOrWhiteSpace(baseName) ? "Enter a Base Name." : null;

            case Mode.SearchReplace:
                // An empty "Replace With" is allowed on purpose: it deletes the matched text.
                return string.IsNullOrEmpty(searchFor) ? "Enter text in \"Search For\"." : null;

            default:
                return rules.Any(r => !string.IsNullOrEmpty(r.prefix))
                    ? null
                    : "Define at least one prefix rule.";
        }
    }

    private static string GetSkipReason(Object obj, bool isAsset)
    {
        if (isAsset)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path) || !path.StartsWith("Assets/", StringComparison.Ordinal))
                return "not an editable project asset";
            if (!AssetDatabase.IsMainAsset(obj))
                return "sub-asset (rename the parent file)";
            return null;
        }

        if ((obj.hideFlags & HideFlags.NotEditable) != 0)
            return "not editable";

        return null;
    }

    private static void EvaluateCandidate(PreviewEntry entry, HashSet<string> claimedPaths)
    {
        string candidate = entry.NewName;

        if (candidate == entry.OldName)
        {
            entry.Status = EntryStatus.Unchanged;
            return;
        }

        if (string.IsNullOrWhiteSpace(candidate))
        {
            entry.Status = EntryStatus.Error;
            entry.Message = "empty name";
            return;
        }

        if (!entry.IsAsset)
        {
            entry.Status = EntryStatus.Rename;
            return;
        }

        if (candidate.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            entry.Status = EntryStatus.Error;
            entry.Message = "invalid file name characters";
            return;
        }

        string oldPath = AssetDatabase.GetAssetPath(entry.Target);
        string directory = (Path.GetDirectoryName(oldPath) ?? "Assets").Replace('\\', '/');
        string extension = AssetDatabase.IsValidFolder(oldPath) ? "" : Path.GetExtension(oldPath);
        string newPath = directory + "/" + candidate + extension;

        string existingGuid = AssetDatabase.AssetPathToGUID(newPath);
        bool existsOnDisk = !string.IsNullOrEmpty(existingGuid)
                            && existingGuid != AssetDatabase.AssetPathToGUID(oldPath);

        if (existsOnDisk)
        {
            entry.Status = EntryStatus.Error;
            entry.Message = "asset with this name already exists";
        }
        else if (!claimedPaths.Add(newPath))
        {
            entry.Status = EntryStatus.Error;
            entry.Message = "duplicate name in this batch";
        }
        else
        {
            entry.Status = EntryStatus.Rename;
        }
    }

    private static void BuildContent(PreviewEntry e)
    {
        Texture icon = AssetPreview.GetMiniThumbnail(e.Target);
        e.OldContent = new GUIContent(e.OldName, icon, e.OldName);

        string text = e.NewName;
        string tooltip = e.NewName;

        switch (e.Status)
        {
            case EntryStatus.Skipped:
                text = "Skipped: " + e.Message;
                tooltip = e.Message;
                break;
            case EntryStatus.Error:
                text = e.NewName + "  (" + e.Message + ")";
                tooltip = e.Message;
                break;
            default:
                if (!string.IsNullOrEmpty(e.Message))
                {
                    tooltip = e.NewName + "\n" + e.Message;
                }
                break;
        }

        e.NewContent = new GUIContent(text, tooltip);
    }

    // ------------------------------------------------------- Type prefixing

    private static AssetCategory GetCategory(Object obj)
    {
        var go = obj as GameObject;
        if (go != null)
        {
            if (!EditorUtility.IsPersistent(go)) return AssetCategory.SceneObject;

            switch (PrefabUtility.GetPrefabAssetType(go))
            {
                case PrefabAssetType.Model:
                    return AssetCategory.Model;
                case PrefabAssetType.Regular:
                case PrefabAssetType.Variant:
                    return AssetCategory.Prefab;
                default:
                    return AssetCategory.Other;
            }
        }

        if (obj is SceneAsset) return AssetCategory.Scene;
        if (obj is Texture) return AssetCategory.Texture;
        if (obj is Material) return AssetCategory.Material;
        if (obj is Mesh) return AssetCategory.Mesh;
        if (obj is AnimationClip) return AssetCategory.Animation;
        if (obj is RuntimeAnimatorController) return AssetCategory.AnimatorController;
        if (obj is AudioClip) return AssetCategory.Audio;
        if (obj is Shader) return AssetCategory.Shader;
        if (obj is ScriptableObject) return AssetCategory.ScriptableObject;

        return AssetCategory.Other;
    }

    private static string DefaultPrefix(AssetCategory category)
    {
        switch (category)
        {
            case AssetCategory.Texture: return "T_";
            case AssetCategory.Material: return "M_";
            case AssetCategory.Prefab: return "PF_";
            case AssetCategory.Model: return "SM_";
            case AssetCategory.Mesh: return "SM_";
            case AssetCategory.Animation: return "AN_";
            case AssetCategory.AnimatorController: return "AC_";
            case AssetCategory.Audio: return "AU_";
            case AssetCategory.Shader: return "SH_";
            case AssetCategory.ScriptableObject: return "SO_";
            case AssetCategory.Scene: return "SC_";
            default: return ""; // SceneObject, Other: no rule by default
        }
    }

    private static string CategoryLabel(AssetCategory category)
    {
        return ObjectNames.NicifyVariableName(category.ToString());
    }

    private string GetPrefix(AssetCategory category)
    {
        foreach (PrefixRule rule in rules)
        {
            if (rule.category == category) return rule.prefix ?? "";
        }
        return "";
    }

    private string ApplyTypePrefix(string currentName, string prefix)
    {
        if (replaceKnownPrefixes)
        {
            // Replace mode: check if already correct
            if (currentName.StartsWith(prefix, StringComparison.Ordinal))
                return currentName;

            string core = StripKnownPrefix(currentName);
            return prefix + core;
        }
        else
        {
            // Append mode: always prepend, even if it creates double prefix
            return prefix + currentName;
        }
    }

    private string StripKnownPrefix(string currentName)
    {
        string best = null;

        foreach (PrefixRule rule in rules)
        {
            string p = rule.prefix;
            if (string.IsNullOrEmpty(p) || currentName.Length < p.Length) continue;

            if (currentName.StartsWith(p, StringComparison.Ordinal))
            {
                if (best == null || p.Length > best.Length)
                {
                    best = p;
                }
            }
        }

        // Also check default prefixes (in case the rule was changed)
        foreach (AssetCategory category in Enum.GetValues(typeof(AssetCategory)))
        {
            string defaultPrefix = DefaultPrefix(category);
            if (string.IsNullOrEmpty(defaultPrefix) || currentName.Length < defaultPrefix.Length) continue;

            if (currentName.StartsWith(defaultPrefix, StringComparison.Ordinal))
            {
                if (best == null || defaultPrefix.Length > best.Length)
                {
                    best = defaultPrefix;
                }
            }
        }

        return best == null ? currentName : currentName.Substring(best.Length);
    }

    /// <summary>Returns one rule per category, in enum order, keeping any prefixes already set.</summary>
    private static List<PrefixRule> NormalizeRules(List<PrefixRule> source)
    {
        var result = new List<PrefixRule>();

        foreach (AssetCategory category in Enum.GetValues(typeof(AssetCategory)))
        {
            AssetCategory current = category;
            PrefixRule match = source == null
                ? null
                : source.FirstOrDefault(r => r != null && r.category == current);

            result.Add(new PrefixRule
            {
                category = current,
                prefix = match != null ? (match.prefix ?? "") : DefaultPrefix(current)
            });
        }

        return result;
    }

    // ---------------------------------------------------------------- Presets

    private void QueuePresetSelection(string presetName)
    {
        hasPendingPreset = true;
        pendingPresetName = presetName;
        Repaint();
    }

    private void ApplyPendingPreset()
    {
        hasPendingPreset = false;
        selectedPresetName = pendingPresetName ?? "";
        SyncPresetState();

        if (selectedPresetIndex >= 0)
        {
            ApplyPreset(library.presets[selectedPresetIndex]);
        }

        previewDirty = true;
    }

    private void ApplyPreset(RenamerPreset p)
    {
        mode = p.mode;
        baseName = p.baseName ?? "";
        startNumber = p.startNumber;
        increment = p.increment;
        digitPadding = Mathf.Clamp(p.digitPadding, 0, 9);
        searchFor = p.searchFor ?? "";
        replaceWith = p.replaceWith ?? "";
        caseSensitive = p.caseSensitive;
        sortByHierarchy = p.sortByHierarchy;
        replaceKnownPrefixes = p.replaceKnownPrefixes;
        rules = NormalizeRules(p.rules);
        presetNameInput = p.name;

        GUI.FocusControl(null); // make sure a focused text field doesn't keep showing stale text
    }

    private RenamerPreset CaptureCurrent(string presetName)
    {
        return new RenamerPreset
        {
            name = presetName,
            mode = mode,
            baseName = baseName ?? "",
            startNumber = startNumber,
            increment = increment,
            digitPadding = digitPadding,
            searchFor = searchFor ?? "",
            replaceWith = replaceWith ?? "",
            caseSensitive = caseSensitive,
            sortByHierarchy = sortByHierarchy,
            replaceKnownPrefixes = replaceKnownPrefixes,
            rules = rules.Select(r => new PrefixRule { category = r.category, prefix = r.prefix ?? "" }).ToList()
        };
    }

    private void StorePreset(string rawName, bool confirmOverwrite)
    {
        string presetName = (rawName ?? "").Trim();
        if (presetName.Length == 0) return;

        int existing = library.presets.FindIndex(
            p => string.Equals(p.name, presetName, StringComparison.OrdinalIgnoreCase));

        bool dialogShown = false;
        if (existing >= 0 && confirmOverwrite)
        {
            dialogShown = true;
            bool overwrite = EditorUtility.DisplayDialog(
                "Overwrite Preset",
                $"A preset named \"{library.presets[existing].name}\" already exists. Overwrite it?",
                "Overwrite", "Cancel");

            if (!overwrite)
            {
                GUIUtility.ExitGUI();
                return;
            }
        }

        RenamerPreset preset = CaptureCurrent(presetName);
        if (existing >= 0) library.presets[existing] = preset;
        else library.presets.Add(preset);

        library.presets.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.OrdinalIgnoreCase));

        if (SavePresetLibrary())
        {
            selectedPresetName = presetName;
            presetNameInput = presetName;
            ShowNotification(new GUIContent($"Preset \"{presetName}\" saved"));
        }

        SyncPresetState();
        previewDirty = true;
        Repaint();

        if (dialogShown) GUIUtility.ExitGUI();
    }

    private void DeleteSelectedPreset()
    {
        if (selectedPresetIndex < 0 || selectedPresetIndex >= library.presets.Count) return;

        string presetName = library.presets[selectedPresetIndex].name;
        bool delete = EditorUtility.DisplayDialog(
            "Delete Preset", $"Delete preset \"{presetName}\"?", "Delete", "Cancel");

        if (delete)
        {
            library.presets.RemoveAt(selectedPresetIndex);
            selectedPresetName = "";
            SavePresetLibrary();
            SyncPresetState();
            previewDirty = true;
            Repaint();
        }

        GUIUtility.ExitGUI();
    }

    private void LoadPresetLibrary()
    {
        library = new PresetLibrary();

        try
        {
            string path = PresetFilePath;
            if (File.Exists(path))
            {
                PresetLibrary loaded = JsonUtility.FromJson<PresetLibrary>(File.ReadAllText(path));
                if (loaded != null && loaded.presets != null)
                {
                    library = loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.LogWarning("[Mass Renamer] Could not read presets: " + ex.Message);
        }

        library.presets.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.name));
        foreach (RenamerPreset p in library.presets)
        {
            p.rules = NormalizeRules(p.rules);
        }

        SyncPresetState();
    }

    private bool SavePresetLibrary()
    {
        try
        {
            File.WriteAllText(PresetFilePath, JsonUtility.ToJson(library, true));
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError("[Mass Renamer] Could not save presets: " + ex.Message);
            return false;
        }
    }

    private void SyncPresetState()
    {
        presetPopupOptions = new[] { NonePresetLabel }
            .Concat(library.presets.Select(p => p.name))
            .ToArray();

        selectedPresetIndex = string.IsNullOrEmpty(selectedPresetName)
            ? -1
            : library.presets.FindIndex(p => p.name == selectedPresetName);

        if (selectedPresetIndex < 0)
        {
            selectedPresetName = "";
        }
    }

    // ----------------------------------------------------------------- Apply

    private void ApplyRename()
    {
        if (!canApply) return;

        var sceneEntries = new List<PreviewEntry>();
        var assetEntries = new List<PreviewEntry>();

        foreach (PreviewEntry e in entries)
        {
            if (e.Status != EntryStatus.Rename || e.Target == null) continue;
            (e.IsAsset ? assetEntries : sceneEntries).Add(e);
        }

        // --- Scene objects: one RecordObjects call = one Ctrl+Z step for the whole batch.
        if (sceneEntries.Count > 0)
        {
            Object[] targets = sceneEntries.Select(e => e.Target).ToArray();
            Undo.RecordObjects(targets, UndoLabel);

            foreach (PreviewEntry e in sceneEntries)
            {
                e.Target.name = e.NewName;
                EditorUtility.SetDirty(e.Target);

                if (PrefabUtility.IsPartOfPrefabInstance(e.Target))
                {
                    PrefabUtility.RecordPrefabInstancePropertyModifications(e.Target);
                }

                var go = e.Target as GameObject;
                if (!Application.isPlaying && go != null && go.scene.IsValid()
                    && !EditorSceneManager.IsPreviewScene(go.scene))
                {
                    EditorSceneManager.MarkSceneDirty(go.scene);
                }
            }
        }

        // --- Project assets: obj.name alone does NOT rename the file on disk,
        // so go through AssetDatabase.RenameAsset (not undoable).
        int assetSuccess = 0;
        int assetFailures = 0;

        foreach (PreviewEntry e in assetEntries)
        {
            string path = AssetDatabase.GetAssetPath(e.Target);
            string error = AssetDatabase.RenameAsset(path, e.NewName);

            if (string.IsNullOrEmpty(error))
            {
                assetSuccess++;
            }
            else
            {
                assetFailures++;
                Debug.LogWarning($"[Mass Renamer] Could not rename '{e.OldName}' to '{e.NewName}': {error}", e.Target);
            }
        }

        if (assetSuccess > 0)
        {
            AssetDatabase.SaveAssets();
        }

        int done = sceneEntries.Count + assetSuccess;
        string summary = $"Renamed {done} object{(done == 1 ? "" : "s")}";
        if (assetFailures > 0) summary += $" ({assetFailures} failed, see Console)";
        ShowNotification(new GUIContent(summary));

        previewDirty = true;
        Repaint();
    }

    // --------------------------------------------------------------- Helpers

    private static string ReplaceText(string source, string search, string replacement, bool matchCase)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(search)) return source;

        StringComparison comparison = matchCase ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int index = source.IndexOf(search, comparison);
        
        if (index < 0) return source;  // No match found, return as-is

        // Replace only the FIRST match
        return source.Substring(0, index) + replacement + source.Substring(index + search.Length);
    }

    /// <summary>
    /// Scene objects sort by sibling-index path (Hierarchy order); assets sort by
    /// natural path order (so Item2 comes before Item10).
    /// </summary>
    private static string GetSortKey(Object obj)
    {
        if (EditorUtility.IsPersistent(obj))
        {
            string path = AssetDatabase.GetAssetPath(obj).ToLowerInvariant();
            return "1|" + Regex.Replace(path, @"\d+", m => m.Value.PadLeft(9, '0'));
        }

        var go = obj as GameObject;
        if (go != null)
        {
            var indices = new List<int>();
            for (Transform t = go.transform; t != null; t = t.parent)
            {
                indices.Add(t.GetSiblingIndex());
            }
            indices.Reverse();

            var sb = new StringBuilder("0|").Append(go.scene.name).Append('|');
            foreach (int i in indices)
            {
                sb.Append(i.ToString("D6", CultureInfo.InvariantCulture)).Append('.');
            }
            return sb.ToString();
        }

        return "2|" + obj.name;
    }

    private GUIStyle StyleFor(EntryStatus status)
    {
        switch (status)
        {
            case EntryStatus.Rename: return changedStyle;
            case EntryStatus.Error: return errorStyle;
            default: return mutedStyle;
        }
    }

    private void EnsureStyles()
    {
        if (rowStyle != null && stylesProSkin == EditorGUIUtility.isProSkin) return;

        stylesProSkin = EditorGUIUtility.isProSkin;

        rowStyle = new GUIStyle(EditorStyles.label)
        {
            alignment = TextAnchor.MiddleLeft,
            clipping = TextClipping.Clip
        };

        changedStyle = new GUIStyle(rowStyle) { fontStyle = FontStyle.Bold };
        changedStyle.normal.textColor = stylesProSkin
            ? new Color(0.45f, 0.85f, 0.45f)
            : new Color(0.05f, 0.45f, 0.10f);

        errorStyle = new GUIStyle(rowStyle);
        errorStyle.normal.textColor = stylesProSkin
            ? new Color(1f, 0.45f, 0.40f)
            : new Color(0.75f, 0.10f, 0.10f);

        mutedStyle = new GUIStyle(rowStyle);
        mutedStyle.normal.textColor = new Color(0.55f, 0.55f, 0.55f);

        wrapMiniStyle = new GUIStyle(EditorStyles.miniLabel) { wordWrap = true };
    }
}
#endif