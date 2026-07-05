// Copyright 2025 Cyber Chaos Games. All Rights Reserved.

using UnityEngine;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEditorInternal;
using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using CCG.Utils;
using CCG.Dialogs;

#if (UNITY_EDITOR_LINUX || UNITY_EDITOR_OSX)
using System.Text;
using System.Globalization;
#endif

namespace CCG.PlayerPrefsEditor
{
    public class PreferencesEditorWindow : EditorWindow
    {
#region ErrorValues
        private readonly int ERROR_VALUE_INT = int.MinValue;
        private readonly string ERROR_VALUE_STR = "<ccgTools_error_24072017>";
        #endregion //ErrorValues

        private enum PreferenceEntryColumn
        {
            Key = 0,
            Type = 1,
            Value = 2
        }

        private enum ColOrderingStatus
        {
            None = 0,
            Ascending = 1,
            Descending = 2
        }

        private class ListOrderingState
        {
            public PreferenceEntryColumn CurrentMainOrderingCol = PreferenceEntryColumn.Key;
            public ColOrderingStatus KeyOrderingStatus = ColOrderingStatus.Ascending;
            public ColOrderingStatus TypeOrderingStatus = ColOrderingStatus.None;
            public ColOrderingStatus ValueOrderingStatus = ColOrderingStatus.None;
        }

        [Serializable]
        private class FilterEntry
        {
            public bool Active;
            public string Value;

            public FilterEntry()
            { }

            public FilterEntry(bool active, string value)
            {
                Active = active;
                Value = value;
            }
        }

        [Serializable]
        private class FilterListState
        {
            public bool Active;
            public List<FilterEntry> Entries = new List<FilterEntry>();
        }

        [Serializable]
        private class PrefListFilterState
        {
            public FilterListState IncludeFilter = new FilterListState();
            public FilterListState ExcludeFilter = new FilterListState();
        }

        [Serializable]
        private class PrefsFilterState
        {
            public PrefListFilterState PlayerPrefs = new PrefListFilterState();
            public PrefListFilterState EditorPrefs = new PrefListFilterState();
            public PrefSectionUiState PlayerPrefsUi = new PrefSectionUiState();
            public PrefSectionUiState EditorPrefsUi = new PrefSectionUiState();
        }

        [Serializable]
        private class PrefSectionUiState
        {
            public bool EditPathActive = false;
            public bool ShowAdvancedFilters = false;
            public string PathOverride = string.Empty;
        }

        [Serializable]
        private class PrefExportData
        {
            public string Kind;
            public List<PrefExportEntry> Entries = new List<PrefExportEntry>();
        }

        [Serializable]
        private class PrefExportEntry
        {
            public string Key;
            public string Type;
            public string StringValue;
            public int IntValue;
            public float FloatValue;
            public bool BoolValue;
        }

        private static string pathToPrefs = String.Empty;
        private static string pathToEditorPrefs = String.Empty;
        private static string defaultPathToPrefs = String.Empty;
        private static string defaultPathToEditorPrefs = String.Empty;
        private static string platformPathPrefix = @"~";

        private string[] userDef;
        private string[] unityDef;
        private string[] editorPrefsDef;
        private bool showSystemGroup = false;

        private ListOrderingState userDefOrdering = new ListOrderingState();
        private ListOrderingState unityDefOrdering = new ListOrderingState();
        private ListOrderingState editorPrefsOrdering = new ListOrderingState();
        private PrefsFilterState filterState;

        private SerializedObject serializedObject;
        private ReorderableList userDefList;
        private ReorderableList unityDefList;
        private ReorderableList editorPrefsList;

        private SerializedProperty[] userDefListCache = new SerializedProperty[0];
        private SerializedProperty[] editorPrefsListCache = new SerializedProperty[0];

        private const float TypeColumnWidth = 60.0f;
        private const float ValueColumnOffset = 62.0f;

        private PreferenceEntryHolder prefEntryHolder;

        private Vector2 scrollPos;
        private float relSpliterPos;
        private bool moveSplitterPos = false;

        private PreferanceStorageAccessor entryAccessor;
        private PreferanceStorageAccessor editorPrefsAccessor;

        private MySearchField searchfield;
        private string searchTxt;
        private int loadingSpinnerFrame;

        private bool updateView = false;
        private bool monitoring = false;
        private bool showLoadingIndicatorOverlay = false;
        private string playerPrefsPathEditValue = string.Empty;
        private string editorPrefsPathEditValue = string.Empty;

        private string FilterStateEditorPrefsKey => "CCG.PlayerPrefsEditor.RelativeSpliterPosition" + "_" + Application.identifier + "_filter_state";

        private readonly List<TextValidator> prefKeyValidatorList = new List<TextValidator>()
        {
            new TextValidator(TextValidator.ErrorType.Error, @"Invalid character detected. Only letters, numbers, space and ,.;:<>_|!§$%&/()=?*+~#-]+$ are allowed", @"(^$)|(^[a-zA-Z0-9 ,.;:<>_|!§$%&/()=?*+~#-]+$)"),
            new TextValidator(TextValidator.ErrorType.Warning, @"The given key already exist. The existing entry would be overwritten!", (key) => { return !PlayerPrefs.HasKey(key); })
        };

        private readonly List<TextValidator> editorPrefKeyValidatorList = new List<TextValidator>()
        {
            new TextValidator(TextValidator.ErrorType.Error, @"Invalid character detected. Only letters, numbers, space and ,.;:<>_|!§$%&/()=?*+~#-]+$ are allowed", @"(^$)|(^[a-zA-Z0-9 ,.;:<>_|!§$%&/()=?*+~#-]+$)"),
            new TextValidator(TextValidator.ErrorType.Warning, @"The given key already exist. The existing entry would be overwritten!", (key) => { return !EditorPrefs.HasKey(key); })
        };

#if UNITY_EDITOR_LINUX
        private readonly char[] invalidFilenameChars = { '"', '\\', '*', '/', ':', '<', '>', '?', '|' };
#elif UNITY_EDITOR_OSX
        private readonly char[] invalidFilenameChars = { '$', '%', '&', '\\', '/', ':', '<', '>', '|', '~' };
#endif
        [MenuItem("Tools/CCG-Tools/PlayerPrefs Editor", false, 1)]
        static void ShowWindow()
        {
            PreferencesEditorWindow window = EditorWindow.GetWindow<PreferencesEditorWindow>(false, "Prefs Editor");
            window.minSize = new Vector2(270.0f, 300.0f);
            window.name = "Prefs Editor";

            //window.titleContent = EditorGUIUtility.IconContent("SettingsIcon"); // Icon

            window.Show();
        }

        private void OnEnable()
        {
#if UNITY_EDITOR_WIN
            defaultPathToPrefs = @"SOFTWARE\Unity\UnityEditor\" + PlayerSettings.companyName + @"\" + PlayerSettings.productName;
            defaultPathToEditorPrefs = @"SOFTWARE\Unity Technologies\Unity Editor 5.x";
            platformPathPrefix = @"<CurrentUser>";
#elif UNITY_EDITOR_OSX
            defaultPathToPrefs = @"Library/Preferences/unity." + MakeValidFileName(PlayerSettings.companyName) + "." + MakeValidFileName(PlayerSettings.productName) + ".plist";
            defaultPathToEditorPrefs = @"Library/Preferences/com.unity3d.UnityEditor5.x.plist";
#elif UNITY_EDITOR_LINUX
            defaultPathToPrefs = @".config/unity3d/" + MakeValidFileName(PlayerSettings.companyName) + "/" + MakeValidFileName(PlayerSettings.productName) + "/prefs";
            defaultPathToEditorPrefs = @".local/share/unity3d/prefs";
#endif
            pathToPrefs = defaultPathToPrefs;
            pathToEditorPrefs = defaultPathToEditorPrefs;

            LoadFilterState();
            ApplyStoredPathOverrides();
            RebuildStorageAccessors();

            monitoring = EditorPrefs.GetBool("CCG.PlayerPrefsEditor.WatchingForChanges", true);
            if(monitoring)
            {
                entryAccessor.StartMonitoring();
                editorPrefsAccessor.StartMonitoring();
            }

            searchfield = new MySearchField();
            searchfield.DropdownSelectionDelegate = () => { PrepareData(); };

            // Fix for serialisation issue of static fields
            if (userDefList == null)
            {
                InitReorderedList();
                PrepareData();
            }
        }

        // Handel view updates for monitored changes
        // Necessary to avoid main thread access issue
        private void Update()
        {
            if (showLoadingIndicatorOverlay)
            {
                loadingSpinnerFrame = (int)Mathf.Repeat(Time.realtimeSinceStartup * 10, 11.99f);
                PrepareData();
                Repaint();
            }

            if (updateView)
            {
                updateView = false;
                PrepareData();
                Repaint();
            }
        }

        private void OnDisable()
        {
            entryAccessor?.StopMonitoring();
            editorPrefsAccessor?.StopMonitoring();
        }

        private void InitReorderedList()
        {
            if (prefEntryHolder == null)
            {
                var tmp = Resources.FindObjectsOfTypeAll<PreferenceEntryHolder>();
                if (tmp.Length > 0)
                {
                    prefEntryHolder = tmp[0];
                }
                else
                {
                    prefEntryHolder = ScriptableObject.CreateInstance<PreferenceEntryHolder>();
                }
            }

            if (serializedObject == null)
            {
                serializedObject = new SerializedObject(prefEntryHolder);
            }

            userDefList = new ReorderableList(serializedObject, serializedObject.FindProperty("userDefList"), false, true, true, true);
            unityDefList = new ReorderableList(serializedObject, serializedObject.FindProperty("unityDefList"), false, true, false, false);
            editorPrefsList = new ReorderableList(serializedObject, serializedObject.FindProperty("editorPrefsList"), false, true, true, true);

            relSpliterPos = EditorPrefs.GetFloat("CCG.PlayerPrefsEditor.RelativeSpliterPosition", 100 / position.width);

            userDefList.drawHeaderCallback = (Rect rect) =>
            {
                DrawColumnHeader(rect, userDefOrdering);
            };
            userDefList.drawElementBackgroundCallback = OnDrawElementBackgroundCallback;
            userDefList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
            {
                SerializedProperty element = GetUserDefListElementAtIndex(index, userDefList.serializedProperty);

                SerializedProperty key = element.FindPropertyRelative("m_key");
                SerializedProperty type = element.FindPropertyRelative("m_typeSelection");

                SerializedProperty value;

                // Load only necessary type
                switch ((PreferenceEntry.PrefTypes)type.enumValueIndex)
                {
                    case PreferenceEntry.PrefTypes.Float:
                        value = element.FindPropertyRelative("m_floatValue");
                        break;
                    case PreferenceEntry.PrefTypes.Int:
                        value = element.FindPropertyRelative("m_intValue");
                        break;
                    case PreferenceEntry.PrefTypes.String:
                        value = element.FindPropertyRelative("m_strValue");
                        break;
                    case PreferenceEntry.PrefTypes.Bool:
                        value = element.FindPropertyRelative("m_boolValue");
                        break;
                    default:
                        value = element.FindPropertyRelative("This should never happen");
                        break;
                }

                rect.y += 2;
                Rect keyRect;
                Rect typeRect;
                Rect valueRect;
                GetColumnRects(rect, out keyRect, out typeRect, out valueRect);

                EditorGUI.BeginChangeCheck();
                string prefKeyName = key.stringValue;
                EditorGUI.LabelField(keyRect, new GUIContent(prefKeyName, prefKeyName));
                GUI.enabled = false;
                EditorGUI.EnumPopup(typeRect, (PreferenceEntry.PrefTypes)type.enumValueIndex);
                GUI.enabled = !showLoadingIndicatorOverlay;
                switch ((PreferenceEntry.PrefTypes)type.enumValueIndex)
                {
                    case PreferenceEntry.PrefTypes.Float:
                        EditorGUI.DelayedFloatField(valueRect, value, GUIContent.none);
                        break;
                    case PreferenceEntry.PrefTypes.Int:
                        EditorGUI.DelayedIntField(valueRect, value, GUIContent.none);
                        break;
                    case PreferenceEntry.PrefTypes.String:
                        EditorGUI.DelayedTextField(valueRect, value, GUIContent.none);
                        break;
                    case PreferenceEntry.PrefTypes.Bool:
                        value.boolValue = EditorGUI.Toggle(valueRect, value.boolValue);
                        break;
                }
                if (EditorGUI.EndChangeCheck())
                {
                    entryAccessor.IgnoreNextChange();

                    switch ((PreferenceEntry.PrefTypes)type.enumValueIndex)
                    {
                        case PreferenceEntry.PrefTypes.Float:
                            PlayerPrefs.SetFloat(key.stringValue, value.floatValue);
                            break;
                        case PreferenceEntry.PrefTypes.Int:
                            PlayerPrefs.SetInt(key.stringValue, value.intValue);
                            break;
                        case PreferenceEntry.PrefTypes.String:
                            PlayerPrefs.SetString(key.stringValue, value.stringValue);
                            break;
                    }

                    PlayerPrefs.Save();
                }
            };
            userDefList.onRemoveCallback = (ReorderableList l) =>
            {
                userDefList.ReleaseKeyboardFocus();
                unityDefList.ReleaseKeyboardFocus();
                editorPrefsList.ReleaseKeyboardFocus();

                string prefKey = l.serializedProperty.GetArrayElementAtIndex(l.index).FindPropertyRelative("m_key").stringValue;
                if (EditorUtility.DisplayDialog("Warning!", $"Are you sure you want to delete this entry from PlayerPrefs?\n\nEntry: {prefKey}", "Yes", "No"))
                {
                    entryAccessor.IgnoreNextChange();

                    PlayerPrefs.DeleteKey(prefKey);
                    PlayerPrefs.Save();

                    ReorderableList.defaultBehaviours.DoRemoveButton(l);
                    PrepareData();
                    GUIUtility.ExitGUI();
                }
            };
            userDefList.onAddDropdownCallback = (Rect buttonRect, ReorderableList l) =>
            {
                var menu = new GenericMenu();
                foreach (PreferenceEntry.PrefTypes type in new[] { PreferenceEntry.PrefTypes.String, PreferenceEntry.PrefTypes.Int, PreferenceEntry.PrefTypes.Float })
                {
                    menu.AddItem(new GUIContent(type.ToString()), false, () =>
                    {
                        TextFieldDialog.OpenDialog("Create new property", "Key for the new property:", prefKeyValidatorList, (key) => {

                            entryAccessor.IgnoreNextChange();

                            switch (type)
                            {
                                case PreferenceEntry.PrefTypes.Float:
                                    PlayerPrefs.SetFloat(key, 0.0f);

                                    break;
                                case PreferenceEntry.PrefTypes.Int:
                                    PlayerPrefs.SetInt(key, 0);

                                    break;
                                case PreferenceEntry.PrefTypes.String:
                                    PlayerPrefs.SetString(key, string.Empty);

                                    break;
                            }
                            PlayerPrefs.Save();

                            PrepareData();

                            Focus();
                        }, this);

                    });
                }
                menu.ShowAsContext();
            };

            unityDefList.drawElementBackgroundCallback = OnDrawElementBackgroundCallback;
            unityDefList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
            {
                var element = unityDefList.serializedProperty.GetArrayElementAtIndex(index);
                SerializedProperty key = element.FindPropertyRelative("m_key");
                SerializedProperty type = element.FindPropertyRelative("m_typeSelection");

                SerializedProperty value;

                // Load only necessary type
                switch ((PreferenceEntry.PrefTypes)type.enumValueIndex)
                {
                    case PreferenceEntry.PrefTypes.Float:
                        value = element.FindPropertyRelative("m_floatValue");
                        break;
                    case PreferenceEntry.PrefTypes.Int:
                        value = element.FindPropertyRelative("m_intValue");
                        break;
                    case PreferenceEntry.PrefTypes.String:
                        value = element.FindPropertyRelative("m_strValue");
                        break;
                    case PreferenceEntry.PrefTypes.Bool:
                        value = element.FindPropertyRelative("m_boolValue");
                        break;
                    default:
                        value = element.FindPropertyRelative("This should never happen");
                        break;
                }

                rect.y += 2;
                Rect keyRect;
                Rect typeRect;
                Rect valueRect;
                GetColumnRects(rect, out keyRect, out typeRect, out valueRect);

                GUI.enabled = false;
                string prefKeyName = key.stringValue;
                EditorGUI.LabelField(keyRect, new GUIContent(prefKeyName, prefKeyName));
                EditorGUI.EnumPopup(typeRect, (PreferenceEntry.PrefTypes)type.enumValueIndex);

                switch ((PreferenceEntry.PrefTypes)type.enumValueIndex)
                {
                    case PreferenceEntry.PrefTypes.Float:
                        EditorGUI.DelayedFloatField(valueRect, value, GUIContent.none);
                        break;
                    case PreferenceEntry.PrefTypes.Int:
                        EditorGUI.DelayedIntField(valueRect, value, GUIContent.none);
                        break;
                    case PreferenceEntry.PrefTypes.String:
                        EditorGUI.DelayedTextField(valueRect, value, GUIContent.none);
                        break;
                    case PreferenceEntry.PrefTypes.Bool:
                        value.boolValue = EditorGUI.Toggle(valueRect, value.boolValue);
                        break;
                }
                GUI.enabled = !showLoadingIndicatorOverlay;
            };
            unityDefList.drawHeaderCallback = (Rect rect) =>
            {
                DrawColumnHeader(rect, unityDefOrdering);
            };

            editorPrefsList.drawHeaderCallback = (Rect rect) =>
            {
                DrawColumnHeader(rect, editorPrefsOrdering);
            };
            editorPrefsList.drawElementBackgroundCallback = OnDrawElementBackgroundCallback;
            editorPrefsList.drawElementCallback = (Rect rect, int index, bool isActive, bool isFocused) =>
            {
                SerializedProperty element = GetEditorPrefsListElementAtIndex(index, editorPrefsList.serializedProperty);

                SerializedProperty key = element.FindPropertyRelative("m_key");
                SerializedProperty type = element.FindPropertyRelative("m_typeSelection");

                SerializedProperty value;

                // Load only necessary type
                switch ((PreferenceEntry.PrefTypes)type.enumValueIndex)
                {
                    case PreferenceEntry.PrefTypes.Float:
                        value = element.FindPropertyRelative("m_floatValue");
                        break;
                    case PreferenceEntry.PrefTypes.Int:
                        value = element.FindPropertyRelative("m_intValue");
                        break;
                    case PreferenceEntry.PrefTypes.String:
                        value = element.FindPropertyRelative("m_strValue");
                        break;
                    case PreferenceEntry.PrefTypes.Bool:
                        value = element.FindPropertyRelative("m_boolValue");
                        break;
                    default:
                        value = element.FindPropertyRelative("This should never happen");
                        break;
                }

                rect.y += 2;
                Rect keyRect;
                Rect typeRect;
                Rect valueRect;
                GetColumnRects(rect, out keyRect, out typeRect, out valueRect);

                EditorGUI.BeginChangeCheck();
                string prefKeyName = key.stringValue;
                EditorGUI.LabelField(keyRect, new GUIContent(prefKeyName, prefKeyName));
                GUI.enabled = false;
                EditorGUI.EnumPopup(typeRect, (PreferenceEntry.PrefTypes)type.enumValueIndex);
                GUI.enabled = !showLoadingIndicatorOverlay;
                switch ((PreferenceEntry.PrefTypes)type.enumValueIndex)
                {
                    case PreferenceEntry.PrefTypes.Float:
                        EditorGUI.DelayedFloatField(valueRect, value, GUIContent.none);
                        break;
                    case PreferenceEntry.PrefTypes.Int:
                        EditorGUI.DelayedIntField(valueRect, value, GUIContent.none);
                        break;
                    case PreferenceEntry.PrefTypes.String:
                        EditorGUI.DelayedTextField(valueRect, value, GUIContent.none);
                        break;
                    case PreferenceEntry.PrefTypes.Bool:
                        value.boolValue = EditorGUI.Toggle(valueRect, value.boolValue);
                        break;
                }
                if (EditorGUI.EndChangeCheck())
                {
                    editorPrefsAccessor.IgnoreNextChange();

                    switch ((PreferenceEntry.PrefTypes)type.enumValueIndex)
                    {
                        case PreferenceEntry.PrefTypes.Float:
                            EditorPrefs.SetFloat(key.stringValue, value.floatValue);
                            break;
                        case PreferenceEntry.PrefTypes.Int:
                            EditorPrefs.SetInt(key.stringValue, value.intValue);
                            break;
                        case PreferenceEntry.PrefTypes.String:
                            EditorPrefs.SetString(key.stringValue, value.stringValue);
                            break;
                        case PreferenceEntry.PrefTypes.Bool:
                            EditorPrefs.SetBool(key.stringValue, value.boolValue);
                            break;
                    }
                }
            };
            editorPrefsList.onRemoveCallback = (ReorderableList l) =>
            {
                userDefList.ReleaseKeyboardFocus();
                unityDefList.ReleaseKeyboardFocus();
                editorPrefsList.ReleaseKeyboardFocus();

                string prefKey = l.serializedProperty.GetArrayElementAtIndex(l.index).FindPropertyRelative("m_key").stringValue;
                if (EditorUtility.DisplayDialog("Warning!", $"Are you sure you want to delete this entry from EditorPrefs?\n\nEntry: {prefKey}\n\nEditorPrefs are shared by the Unity Editor across projects on this computer.", "Yes", "No"))
                {
                    editorPrefsAccessor.IgnoreNextChange();

                    EditorPrefs.DeleteKey(prefKey);

                    ReorderableList.defaultBehaviours.DoRemoveButton(l);
                    PrepareData();
                    GUIUtility.ExitGUI();
                }
            };
            editorPrefsList.onAddDropdownCallback = (Rect buttonRect, ReorderableList l) =>
            {
                var menu = new GenericMenu();
                foreach (PreferenceEntry.PrefTypes type in Enum.GetValues(typeof(PreferenceEntry.PrefTypes)))
                {
                    menu.AddItem(new GUIContent(type.ToString()), false, () =>
                    {
                        TextFieldDialog.OpenDialog("Create new editor property", "Key for the new editor property:", editorPrefKeyValidatorList, (key) => {

                            editorPrefsAccessor.IgnoreNextChange();

                            switch (type)
                            {
                                case PreferenceEntry.PrefTypes.Float:
                                    EditorPrefs.SetFloat(key, 0.0f);

                                    break;
                                case PreferenceEntry.PrefTypes.Int:
                                    EditorPrefs.SetInt(key, 0);

                                    break;
                                case PreferenceEntry.PrefTypes.String:
                                    EditorPrefs.SetString(key, string.Empty);

                                    break;
                                case PreferenceEntry.PrefTypes.Bool:
                                    EditorPrefs.SetBool(key, false);

                                    break;
                            }

                            PrepareData();

                            Focus();
                        }, this);

                    });
                }
                menu.ShowAsContext();
            };
        }

        private void LoadFilterState()
        {
            filterState = CreateDefaultFilterState();

            string storedState = EditorPrefs.GetString(FilterStateEditorPrefsKey, string.Empty);
            if (!string.IsNullOrEmpty(storedState))
            {
                try
                {
                    JsonUtility.FromJsonOverwrite(storedState, filterState);
                }
                catch (Exception)
                {
                    filterState = CreateDefaultFilterState();
                }
            }

            EnsureFilterState();
        }

        private void SaveFilterState()
        {
            EnsureFilterState();
            EditorPrefs.SetString(FilterStateEditorPrefsKey, JsonUtility.ToJson(filterState, true));
        }

        private void ApplyStoredPathOverrides()
        {
            if (!string.IsNullOrEmpty(filterState.PlayerPrefsUi.PathOverride))
                pathToPrefs = filterState.PlayerPrefsUi.PathOverride;
            if (!string.IsNullOrEmpty(filterState.EditorPrefsUi.PathOverride))
                pathToEditorPrefs = filterState.EditorPrefsUi.PathOverride;

            playerPrefsPathEditValue = pathToPrefs;
            editorPrefsPathEditValue = pathToEditorPrefs;
        }

        private void RebuildStorageAccessors()
        {
            bool wasMonitoring = entryAccessor != null && entryAccessor.IsMonitoring();
            bool editorWasMonitoring = editorPrefsAccessor != null && editorPrefsAccessor.IsMonitoring();

            entryAccessor?.StopMonitoring();
            editorPrefsAccessor?.StopMonitoring();

#if UNITY_EDITOR_WIN
            entryAccessor = new WindowsPrefStorage(pathToPrefs);
            editorPrefsAccessor = new WindowsPrefStorage(pathToEditorPrefs, false);
#elif UNITY_EDITOR_OSX
            entryAccessor = new MacPrefStorage(pathToPrefs);
            entryAccessor.StartLoadingDelegate = () => { showLoadingIndicatorOverlay = true; };
            entryAccessor.StopLoadingDelegate = () => { showLoadingIndicatorOverlay = false; };
            editorPrefsAccessor = new MacPrefStorage(pathToEditorPrefs);
#elif UNITY_EDITOR_LINUX
            entryAccessor = new LinuxPrefStorage(pathToPrefs);
            editorPrefsAccessor = new LinuxPrefStorage(pathToEditorPrefs);
#endif

            entryAccessor.PrefEntryChangedDelegate = () => { updateView = true; };
            editorPrefsAccessor.PrefEntryChangedDelegate = () => { updateView = true; };

            if (wasMonitoring)
                entryAccessor.StartMonitoring();
            if (editorWasMonitoring)
                editorPrefsAccessor.StartMonitoring();
        }

        private PrefsFilterState CreateDefaultFilterState()
        {
            PrefsFilterState state = new PrefsFilterState();

            state.PlayerPrefs.IncludeFilter.Active = true;
            state.PlayerPrefs.IncludeFilter.Entries.Add(new FilterEntry(true, string.Empty));
            state.PlayerPrefs.ExcludeFilter.Active = true;
            state.PlayerPrefs.ExcludeFilter.Entries.Add(new FilterEntry(true, string.Empty));

            state.EditorPrefs.IncludeFilter.Active = true;
            state.EditorPrefs.IncludeFilter.Entries.Add(new FilterEntry(true, Application.identifier));
            state.EditorPrefs.IncludeFilter.Entries.Add(new FilterEntry(false, "CCG.PlayerPrefsEditor."));
            state.EditorPrefs.ExcludeFilter.Active = true;
            state.EditorPrefs.ExcludeFilter.Entries.Add(new FilterEntry(true, "com.Unity_Technologies."));
            state.EditorPrefs.ExcludeFilter.Entries.Add(new FilterEntry(true, "Unity."));
            state.EditorPrefs.ExcludeFilter.Entries.Add(new FilterEntry(true, "UnityEditor."));
            state.EditorPrefs.ExcludeFilter.Entries.Add(new FilterEntry(true, "CCG.PlayerPrefsEditor."));

            return state;
        }

        private void EnsureFilterState()
        {
            if (filterState == null)
                filterState = CreateDefaultFilterState();

            EnsurePrefListFilterState(ref filterState.PlayerPrefs);
            EnsurePrefListFilterState(ref filterState.EditorPrefs);
            EnsurePrefSectionUiState(ref filterState.PlayerPrefsUi);
            EnsurePrefSectionUiState(ref filterState.EditorPrefsUi);
        }

        private void EnsurePrefSectionUiState(ref PrefSectionUiState state)
        {
            if (state == null)
                state = new PrefSectionUiState();
        }

        private void EnsurePrefListFilterState(ref PrefListFilterState state)
        {
            if (state == null)
                state = new PrefListFilterState();

            EnsureFilterListState(ref state.IncludeFilter);
            EnsureFilterListState(ref state.ExcludeFilter);
        }

        private void EnsureFilterListState(ref FilterListState state)
        {
            if (state == null)
                state = new FilterListState();

            if (state.Entries == null)
                state.Entries = new List<FilterEntry>();

            for (int i = state.Entries.Count - 1; i >= 0; i--)
            {
                if (state.Entries[i] == null)
                    state.Entries.RemoveAt(i);
            }
        }

        private void DrawFilterControls(PrefListFilterState listFilterState)
        {
            EditorGUI.BeginChangeCheck();

            GUILayout.BeginHorizontal();
            DrawFilterList("include filter", listFilterState.IncludeFilter);
            DrawFilterList("exclude filter", listFilterState.ExcludeFilter);
            GUILayout.EndHorizontal();

            if (EditorGUI.EndChangeCheck())
            {
                SaveFilterState();
                PrepareData(false);
            }
        }

        private void DrawPrefsPathField(string label, bool useEditorPrefs)
        {
            PrefSectionUiState uiState = useEditorPrefs ? filterState.EditorPrefsUi : filterState.PlayerPrefsUi;
            string currentPath = useEditorPrefs ? pathToEditorPrefs : pathToPrefs;
            string editValue = useEditorPrefs ? editorPrefsPathEditValue : playerPrefsPathEditValue;

            GUILayout.BeginHorizontal();

            GUILayout.Box(ImageManager.GetOsIcon(), Styles.icon);
            GUILayout.Label(label, GUILayout.Width(70));

            EditorGUI.BeginDisabledGroup(!uiState.EditPathActive);
            string nextEditValue = GUILayout.TextField(platformPathPrefix + Path.DirectorySeparatorChar + editValue, GUILayout.MinWidth(200));
            EditorGUI.EndDisabledGroup();

            if (uiState.EditPathActive)
                SetPathEditValue(useEditorPrefs, StripPlatformPathPrefix(nextEditValue));
            else
                SetPathEditValue(useEditorPrefs, currentPath);

            DrawSectionButtons(useEditorPrefs, uiState);

            GUILayout.EndHorizontal();
        }

        private void DrawSectionButtons(bool useEditorPrefs, PrefSectionUiState uiState)
        {
            EditorGUIUtility.SetIconSize(new Vector2(14.0f, 14.0f));

            if (uiState.EditPathActive)
            {
                if (GUILayout.Button(new GUIContent(ImageManager.CancelIcon, "Revert path"), EditorStyles.toolbarButton, GUILayout.Width(22.0f)))
                {
                    uiState.EditPathActive = false;
                    SetPathEditValue(useEditorPrefs, useEditorPrefs ? pathToEditorPrefs : pathToPrefs);
                    SaveFilterState();
                    GUIUtility.ExitGUI();
                }
            }

            Texture2D editIcon = uiState.EditPathActive ? ImageManager.EditFilledIcon : ImageManager.EditIcon;
            string editTooltip = uiState.EditPathActive ? "Apply path" : "Edit path";
            if (GUILayout.Button(new GUIContent(editIcon, editTooltip), EditorStyles.toolbarButton, GUILayout.Width(22.0f)))
            {
                if (uiState.EditPathActive)
                    ApplyPathEdit(useEditorPrefs);
                else
                {
                    uiState.EditPathActive = true;
                    SetPathEditValue(useEditorPrefs, useEditorPrefs ? pathToEditorPrefs : pathToPrefs);
                    SaveFilterState();
                }
                GUIUtility.ExitGUI();
            }

            Texture2D filterIcon = uiState.ShowAdvancedFilters ? ImageManager.FilterFilledIcon : ImageManager.FilterBorderIcon;
            string filterTooltip = uiState.ShowAdvancedFilters ? "Hide advanced filters" : "Show advanced filters";
            if (GUILayout.Button(new GUIContent(filterIcon, filterTooltip), EditorStyles.toolbarButton, GUILayout.Width(22.0f)))
            {
                uiState.ShowAdvancedFilters = !uiState.ShowAdvancedFilters;
                SaveFilterState();
                GUIUtility.ExitGUI();
            }

            if (GUILayout.Button(new GUIContent(ImageManager.ImportIcon, "Import prefs from JSON template file"), EditorStyles.toolbarButton, GUILayout.Width(22.0f)))
            {
                ImportPrefsFromJsonTemplate(useEditorPrefs);
                GUIUtility.ExitGUI();
            }

            if (GUILayout.Button(new GUIContent(ImageManager.ExportIcon, "Export prefs to JSON template file"), EditorStyles.toolbarButton, GUILayout.Width(22.0f)))
            {
                ExportPrefsAsJson(useEditorPrefs, true);
                GUIUtility.ExitGUI();
            }

            EditorGUIUtility.SetIconSize(new Vector2(0.0f, 0.0f));
        }

        private void SetPathEditValue(bool useEditorPrefs, string value)
        {
            if (useEditorPrefs)
                editorPrefsPathEditValue = value;
            else
                playerPrefsPathEditValue = value;
        }

        private string StripPlatformPathPrefix(string value)
        {
            string prefix = platformPathPrefix + Path.DirectorySeparatorChar;
            if (!string.IsNullOrEmpty(value) && value.StartsWith(prefix))
                return value.Substring(prefix.Length);

            return value;
        }

        private void ApplyPathEdit(bool useEditorPrefs)
        {
            PrefSectionUiState uiState = useEditorPrefs ? filterState.EditorPrefsUi : filterState.PlayerPrefsUi;
            string nextPath = useEditorPrefs ? editorPrefsPathEditValue : playerPrefsPathEditValue;

            if (useEditorPrefs)
            {
                pathToEditorPrefs = nextPath;
                uiState.PathOverride = (nextPath == defaultPathToEditorPrefs) ? string.Empty : nextPath;
            }
            else
            {
                pathToPrefs = nextPath;
                uiState.PathOverride = (nextPath == defaultPathToPrefs) ? string.Empty : nextPath;
            }

            uiState.EditPathActive = false;
            SaveFilterState();
            RebuildStorageAccessors();
            PrepareData();
        }

        private void ExportPrefsAsJson(bool useEditorPrefs, bool asTemplate)
        {
            string defaultName = GetExportFileName(useEditorPrefs, asTemplate);
            string directory = asTemplate ? EnsureTemplateDirectory() : Application.dataPath;
            string path = EditorUtility.SaveFilePanel("Export prefs as JSON", directory, defaultName, "json");
            if (string.IsNullOrEmpty(path))
                return;

            PrefExportData exportData = BuildExportData(useEditorPrefs);
            File.WriteAllText(path, JsonUtility.ToJson(exportData, true));
            AssetDatabase.Refresh();
        }

        private void ImportPrefsFromJsonTemplate(bool useEditorPrefs)
        {
            string templateDirectory = GetTemplateDirectory();
            string directory = Directory.Exists(templateDirectory) ? templateDirectory : Application.dataPath;
            string path = EditorUtility.OpenFilePanel("Import prefs from JSON template file", directory, "json");
            if (string.IsNullOrEmpty(path))
                return;

            PrefExportData importData = JsonUtility.FromJson<PrefExportData>(File.ReadAllText(path));
            if (importData == null || importData.Entries == null)
                return;

            foreach (PrefExportEntry entry in importData.Entries)
                ApplyImportedPrefEntry(useEditorPrefs, entry);

            if (!useEditorPrefs)
                PlayerPrefs.Save();

            PrepareData();
        }

        private string GetExportFileName(bool useEditorPrefs, bool asTemplate)
        {
            string prefix = useEditorPrefs ? "editor-prefs" : "player-prefs";
            string templatePart = asTemplate ? "_template" : string.Empty;
            return prefix + templatePart + "_" + DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        }

        private string GetTemplateDirectory()
        {
            return Path.Combine(Application.dataPath, "Prefs", "Templates");
        }

        private string EnsureTemplateDirectory()
        {
            string directory = GetTemplateDirectory();
            if (!Directory.Exists(directory))
                Directory.CreateDirectory(directory);

            return directory;
        }

        private PrefExportData BuildExportData(bool useEditorPrefs)
        {
            PrefExportData data = new PrefExportData();
            data.Kind = useEditorPrefs ? "EditorPrefs" : "PlayerPrefs";

            List<PreferenceEntry> source = useEditorPrefs ? prefEntryHolder.editorPrefsList : prefEntryHolder.userDefList;
            foreach (PreferenceEntry prefEntry in source)
                data.Entries.Add(CreateExportEntry(prefEntry));

            return data;
        }

        private PrefExportEntry CreateExportEntry(PreferenceEntry prefEntry)
        {
            return new PrefExportEntry()
            {
                Key = prefEntry.m_key,
                Type = prefEntry.m_typeSelection.ToString(),
                StringValue = prefEntry.m_strValue,
                IntValue = prefEntry.m_intValue,
                FloatValue = prefEntry.m_floatValue,
                BoolValue = prefEntry.m_boolValue
            };
        }

        private void ApplyImportedPrefEntry(bool useEditorPrefs, PrefExportEntry entry)
        {
            if (entry == null || string.IsNullOrEmpty(entry.Key))
                return;

            PreferenceEntry.PrefTypes prefType = PreferenceEntry.PrefTypes.String;
            try
            {
                prefType = (PreferenceEntry.PrefTypes)Enum.Parse(typeof(PreferenceEntry.PrefTypes), entry.Type);
            }
            catch
            {
                prefType = PreferenceEntry.PrefTypes.String;
            }

            if (useEditorPrefs)
            {
                switch (prefType)
                {
                    case PreferenceEntry.PrefTypes.Float:
                        EditorPrefs.SetFloat(entry.Key, entry.FloatValue);
                        break;
                    case PreferenceEntry.PrefTypes.Int:
                        EditorPrefs.SetInt(entry.Key, entry.IntValue);
                        break;
                    case PreferenceEntry.PrefTypes.Bool:
                        EditorPrefs.SetBool(entry.Key, entry.BoolValue);
                        break;
                    case PreferenceEntry.PrefTypes.String:
                    default:
                        EditorPrefs.SetString(entry.Key, entry.StringValue ?? string.Empty);
                        break;
                }
                return;
            }

            switch (prefType)
            {
                case PreferenceEntry.PrefTypes.Float:
                    PlayerPrefs.SetFloat(entry.Key, entry.FloatValue);
                    break;
                case PreferenceEntry.PrefTypes.Int:
                    PlayerPrefs.SetInt(entry.Key, entry.IntValue);
                    break;
                case PreferenceEntry.PrefTypes.String:
                default:
                    PlayerPrefs.SetString(entry.Key, entry.StringValue ?? string.Empty);
                    break;
            }
        }

        private void DrawFilterList(string title, FilterListState filterListState)
        {
            GUILayout.BeginVertical(EditorStyles.helpBox, GUILayout.ExpandWidth(true));

            filterListState.Active = EditorGUILayout.ToggleLeft(title, filterListState.Active);

            bool previousEnabled = GUI.enabled;
            Color previousColor = GUI.color;

            if (!filterListState.Active)
            {
                GUI.enabled = false;
                GUI.color = new Color(previousColor.r, previousColor.g, previousColor.b, previousColor.a * 0.45f);
            }

            int removeIndex = -1;
            for (int i = 0; i < filterListState.Entries.Count; i++)
            {
                FilterEntry entry = filterListState.Entries[i];

                GUILayout.BeginHorizontal();
                entry.Active = EditorGUILayout.Toggle(entry.Active, GUILayout.Width(18.0f));

                Color rowColor = GUI.color;
                if (!entry.Active)
                    GUI.color = new Color(rowColor.r, rowColor.g, rowColor.b, rowColor.a * 0.45f);

                entry.Value = EditorGUILayout.TextField(entry.Value ?? string.Empty);
                GUI.color = rowColor;

                if (GUILayout.Button("-", Styles.miniButton))
                    removeIndex = i;

                GUILayout.EndHorizontal();
            }

            if (removeIndex >= 0)
                filterListState.Entries.RemoveAt(removeIndex);

            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("+", Styles.miniButton))
                filterListState.Entries.Add(new FilterEntry(true, string.Empty));
            GUILayout.EndHorizontal();

            GUI.enabled = previousEnabled;
            GUI.color = previousColor;

            GUILayout.EndVertical();
        }

        private void DrawColumnHeader(Rect rect, ListOrderingState orderingState)
        {
            rect.y += 1;
            rect.height = EditorGUIUtility.singleLineHeight;

            Rect keyRect;
            Rect typeRect;
            Rect valueRect;
            GetColumnRects(rect, out keyRect, out typeRect, out valueRect);

            DrawColumnHeaderButton(keyRect, "Key", PreferenceEntryColumn.Key, orderingState);
            DrawColumnHeaderButton(typeRect, "Type", PreferenceEntryColumn.Type, orderingState);
            DrawColumnHeaderButton(valueRect, "Value", PreferenceEntryColumn.Value, orderingState);
        }

        private void DrawColumnHeaderButton(Rect rect, string label, PreferenceEntryColumn column, ListOrderingState orderingState)
        {
            if (GUI.Button(rect, label, EditorStyles.toolbarButton))
            {
                ApplyColumnOrderingClick(orderingState, column);
                PrepareData(false);
                GUIUtility.ExitGUI();
            }

            if (column != PreferenceEntryColumn.Key && column != orderingState.CurrentMainOrderingCol)
                return;

            ColOrderingStatus status = GetColumnOrderingStatus(orderingState, column);
            Texture2D sortIcon = null;
            if (status == ColOrderingStatus.Ascending)
                sortIcon = ImageManager.SortAsscending;
            else if (status == ColOrderingStatus.Descending)
                sortIcon = ImageManager.SortDescending;

            if (sortIcon == null)
                return;

            Rect iconRect = new Rect(rect.xMax - 16.0f, rect.y + 1.0f, 14.0f, 14.0f);
            GUI.DrawTexture(iconRect, sortIcon, ScaleMode.ScaleToFit);
        }

        private void ApplyColumnOrderingClick(ListOrderingState orderingState, PreferenceEntryColumn column)
        {
            if (orderingState.CurrentMainOrderingCol == column)
            {
                SetColumnOrderingStatus(orderingState, column, ToggleColumnOrderingStatus(GetColumnOrderingStatus(orderingState, column)));
                return;
            }

            ColOrderingStatus keyStatus = orderingState.KeyOrderingStatus;
            orderingState.TypeOrderingStatus = ColOrderingStatus.None;
            orderingState.ValueOrderingStatus = ColOrderingStatus.None;
            orderingState.KeyOrderingStatus = (column == PreferenceEntryColumn.Key) ? ColOrderingStatus.Ascending : keyStatus;
            SetColumnOrderingStatus(orderingState, column, ColOrderingStatus.Ascending);
            orderingState.CurrentMainOrderingCol = column;
        }

        private ColOrderingStatus ToggleColumnOrderingStatus(ColOrderingStatus status)
        {
            return (status == ColOrderingStatus.Ascending) ? ColOrderingStatus.Descending : ColOrderingStatus.Ascending;
        }

        private ColOrderingStatus GetColumnOrderingStatus(ListOrderingState orderingState, PreferenceEntryColumn column)
        {
            switch (column)
            {
                case PreferenceEntryColumn.Key:
                    return orderingState.KeyOrderingStatus;
                case PreferenceEntryColumn.Type:
                    return orderingState.TypeOrderingStatus;
                case PreferenceEntryColumn.Value:
                    return orderingState.ValueOrderingStatus;
                default:
                    return ColOrderingStatus.None;
            }
        }

        private void SetColumnOrderingStatus(ListOrderingState orderingState, PreferenceEntryColumn column, ColOrderingStatus status)
        {
            switch (column)
            {
                case PreferenceEntryColumn.Key:
                    orderingState.KeyOrderingStatus = (status == ColOrderingStatus.None) ? ColOrderingStatus.Ascending : status;
                    break;
                case PreferenceEntryColumn.Type:
                    orderingState.TypeOrderingStatus = status;
                    break;
                case PreferenceEntryColumn.Value:
                    orderingState.ValueOrderingStatus = status;
                    break;
            }
        }

        private void GetColumnRects(Rect rect, out Rect keyRect, out Rect typeRect, out Rect valueRect)
        {
            float spliterPos = relSpliterPos * rect.width;
            keyRect = new Rect(rect.x, rect.y, Mathf.Max(0.0f, spliterPos - 1.0f), EditorGUIUtility.singleLineHeight);
            typeRect = new Rect(rect.x + spliterPos + 1.0f, rect.y, TypeColumnWidth, EditorGUIUtility.singleLineHeight);
            valueRect = new Rect(rect.x + spliterPos + ValueColumnOffset, rect.y, Mathf.Max(0.0f, rect.width - spliterPos - TypeColumnWidth), EditorGUIUtility.singleLineHeight);
        }

        private void OnDrawElementBackgroundCallback(Rect rect, int index, bool isActive, bool isFocused)
        {
            if (Event.current.type == EventType.Repaint)
            {
                ReorderableList.defaultBehaviours.elementBackground.Draw(rect, false, isActive, isActive, isFocused);
            }

            Rect spliterRect = new Rect(rect.x + relSpliterPos * rect.width, rect.y, 2, rect.height);
            EditorGUIUtility.AddCursorRect(spliterRect, MouseCursor.ResizeHorizontal);
            if (Event.current.type == EventType.MouseDown && spliterRect.Contains(Event.current.mousePosition))
            {
                moveSplitterPos = true;
            }
            if(moveSplitterPos)
            {
                if (Event.current.mousePosition.x > 100 && Event.current.mousePosition.x<rect.width - 120)
                {
                    relSpliterPos = Event.current.mousePosition.x / rect.width;
                    Repaint();
                }
            }
            if (Event.current.type == EventType.MouseUp)
            {
                moveSplitterPos = false;
                EditorPrefs.SetFloat("CCG.PlayerPrefsEditor.RelativeSpliterPosition", relSpliterPos);
            }
        }

        void OnGUI()
        {
            // Need to catch 'Stack empty' error on linux
            try
            {
                if (showLoadingIndicatorOverlay)
                {
                    GUI.enabled = false;
                }

                Color defaultColor = GUI.contentColor;
                if (!EditorGUIUtility.isProSkin)
                {
                    GUI.contentColor = Styles.Colors.DarkGray;
                }

                GUILayout.BeginVertical();

                GUILayout.BeginHorizontal(EditorStyles.toolbar);

                EditorGUI.BeginChangeCheck();
                searchTxt = searchfield.OnToolbarGUI(searchTxt);
                if (EditorGUI.EndChangeCheck())
                {
                    PrepareData(false);
                }

                GUILayout.FlexibleSpace();

                EditorGUIUtility.SetIconSize(new Vector2(14.0f, 14.0f));

                GUIContent watcherContent = (entryAccessor.IsMonitoring()) ? new GUIContent(ImageManager.Watching, "Watching changes") : new GUIContent(ImageManager.NotWatching, "Not watching changes");
                if (GUILayout.Button(watcherContent, EditorStyles.toolbarButton))
                {
                    monitoring = !monitoring;

                    EditorPrefs.SetBool("CCG.PlayerPrefsEditor.WatchingForChanges", monitoring);

                    if (monitoring)
                    {
                        entryAccessor.StartMonitoring();
                        editorPrefsAccessor.StartMonitoring();
                    }
                    else
                    {
                        entryAccessor.StopMonitoring();
                        editorPrefsAccessor.StopMonitoring();
                    }

                    Repaint();
                }
                if (GUILayout.Button(new GUIContent(ImageManager.Refresh, "Refresh"), EditorStyles.toolbarButton))
                {
                    PlayerPrefs.Save();
                    PrepareData();
                }
                if (GUILayout.Button(new GUIContent(ImageManager.Trash, "Delete all PlayerPrefs"), EditorStyles.toolbarButton))
                {
                    if (EditorUtility.DisplayDialog("Warning!", "Are you sure you want to delete ALL entries from PlayerPrefs?\n\nUse with caution! Unity defined PlayerPrefs keys are affected too.\n\nEditorPrefs are not affected.", "Yes", "No"))
                    {
                        PlayerPrefs.DeleteAll();
                        PrepareData();
                        GUIUtility.ExitGUI();
                    }
                }
                EditorGUIUtility.SetIconSize(new Vector2(0.0f, 0.0f));

                GUILayout.EndHorizontal();

                DrawPrefsPathField("PlayerPrefs", false);

                scrollPos = GUILayout.BeginScrollView(scrollPos);
                if (filterState.PlayerPrefsUi.ShowAdvancedFilters)
                    DrawFilterControls(filterState.PlayerPrefs);
                serializedObject.Update();
                userDefList.DoLayoutList();
                serializedObject.ApplyModifiedProperties();

                DrawPrefsPathField("EditorPrefs", true);
                if (filterState.EditorPrefsUi.ShowAdvancedFilters)
                    DrawFilterControls(filterState.EditorPrefs);
                serializedObject.Update();
                editorPrefsList.DoLayoutList();
                serializedObject.ApplyModifiedProperties();

                GUILayout.FlexibleSpace();

                showSystemGroup = EditorGUILayout.Foldout(showSystemGroup, new GUIContent("Show System PlayerPrefs"));
                if (showSystemGroup)
                {
                    unityDefList.DoLayoutList();
                }
                GUILayout.EndScrollView();
                GUILayout.EndVertical();

                GUI.enabled = true;

                if (showLoadingIndicatorOverlay)
                {
                    GUILayout.BeginArea(new Rect(position.size.x * 0.5f - 30, position.size.y * 0.5f - 25, 60, 50), GUI.skin.box);
                    GUILayout.FlexibleSpace();

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Box(ImageManager.SpinWheelIcons[loadingSpinnerFrame], Styles.icon);
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.BeginHorizontal();
                    GUILayout.FlexibleSpace();
                    GUILayout.Label("Loading");
                    GUILayout.FlexibleSpace();
                    GUILayout.EndHorizontal();

                    GUILayout.FlexibleSpace();
                    GUILayout.EndArea();
                }

                GUI.contentColor = defaultColor;
            }
            catch (InvalidOperationException)
            { }
        }

        private void PrepareData(bool reloadKeys = true)
        {
            prefEntryHolder.ClearLists();

            LoadKeys(out userDef, out unityDef, reloadKeys);
            LoadEditorPrefsKeys(out editorPrefsDef, reloadKeys);

            CreatePrefEntries(userDef, ref prefEntryHolder.userDefList, userDefOrdering, filterState.PlayerPrefs);
            CreatePrefEntries(unityDef, ref prefEntryHolder.unityDefList, unityDefOrdering);
            CreatePrefEntries(editorPrefsDef, ref prefEntryHolder.editorPrefsList, editorPrefsOrdering, filterState.EditorPrefs, true);

            // Clear cache
            userDefListCache = new SerializedProperty[prefEntryHolder.userDefList.Count];
            editorPrefsListCache = new SerializedProperty[prefEntryHolder.editorPrefsList.Count];
        }

        private void CreatePrefEntries(string[] keySource, ref List<PreferenceEntry> listDest, ListOrderingState orderingState, PrefListFilterState listFilterState = null, bool useEditorPrefs = false)
        {
            if (!string.IsNullOrEmpty(searchTxt) && searchfield.SearchMode == MySearchField.SearchModePreferencesEditorWindow.Key)
            {
                keySource = keySource.Where((keyEntry) => keyEntry.ToLower().Contains(searchTxt.ToLower())).ToArray();
            }

            foreach (string key in keySource)
            {
                if (!IsKeyAllowedByFilter(key, listFilterState))
                    continue;

                var entry = new PreferenceEntry();
                entry.m_key = key;

                if (useEditorPrefs)
                {
                    string editorString = EditorPrefs.GetString(key, ERROR_VALUE_STR);

                    if (editorString != ERROR_VALUE_STR)
                    {
                        entry.m_strValue = editorString;
                        entry.m_typeSelection = PreferenceEntry.PrefTypes.String;
                        listDest.Add(entry);
                        continue;
                    }

                    bool boolWhenDefaultFalse = EditorPrefs.GetBool(key, false);
                    bool boolWhenDefaultTrue = EditorPrefs.GetBool(key, true);
                    if (boolWhenDefaultFalse == boolWhenDefaultTrue)
                    {
                        entry.m_boolValue = boolWhenDefaultFalse;
                        entry.m_typeSelection = PreferenceEntry.PrefTypes.Bool;
                        listDest.Add(entry);
                        continue;
                    }

                    float editorFloat = EditorPrefs.GetFloat(key, float.NaN);
                    if (!float.IsNaN(editorFloat))
                    {
                        entry.m_floatValue = editorFloat;
                        entry.m_typeSelection = PreferenceEntry.PrefTypes.Float;
                        listDest.Add(entry);
                        continue;
                    }

                    int editorInt = EditorPrefs.GetInt(key, ERROR_VALUE_INT);
                    if (editorInt != ERROR_VALUE_INT)
                    {
                        entry.m_intValue = editorInt;
                        entry.m_typeSelection = PreferenceEntry.PrefTypes.Int;
                        listDest.Add(entry);
                        continue;
                    }

                    continue;
                }

                string s = PlayerPrefs.GetString(key, ERROR_VALUE_STR);

                if (s != ERROR_VALUE_STR)
                {
                    entry.m_strValue = s;
                    entry.m_typeSelection = PreferenceEntry.PrefTypes.String;
                    listDest.Add(entry);
                    continue;
                }

                float f = PlayerPrefs.GetFloat(key, float.NaN);
                if (!float.IsNaN(f))
                {
                    entry.m_floatValue = f;
                    entry.m_typeSelection = PreferenceEntry.PrefTypes.Float;
                    listDest.Add(entry);
                    continue;
                }

                int i = PlayerPrefs.GetInt(key, ERROR_VALUE_INT);
                if (i != ERROR_VALUE_INT)
                {
                    entry.m_intValue = i;
                    entry.m_typeSelection = PreferenceEntry.PrefTypes.Int;
                    listDest.Add(entry);
                    continue;
                }
            }

            if (!string.IsNullOrEmpty(searchTxt) && searchfield.SearchMode == MySearchField.SearchModePreferencesEditorWindow.Value)
            {
                listDest = listDest.Where((preferenceEntry) => preferenceEntry.ValueAsString().ToLower().Contains(searchTxt.ToLower())).ToList<PreferenceEntry>();
            }

            SortPrefEntries(listDest, orderingState);
        }

        private bool IsKeyAllowedByFilter(string key, PrefListFilterState listFilterState)
        {
            if (listFilterState == null)
                return true;

            List<string> includeValues = GetActiveFilterValues(listFilterState.IncludeFilter);
            List<string> excludeValues = GetActiveFilterValues(listFilterState.ExcludeFilter);

            bool includeMatch = includeValues.Count == 0 || includeValues.Any((value) => KeyMatchesFilterValue(key, value));
            bool excludeMatch = excludeValues.Any((value) => KeyMatchesFilterValue(key, value));

            return includeMatch && !excludeMatch;
        }

        private List<string> GetActiveFilterValues(FilterListState filterListState)
        {
            if (filterListState == null || !filterListState.Active || filterListState.Entries == null)
                return new List<string>();

            return filterListState.Entries
                .Where((entry) => entry != null && entry.Active && !string.IsNullOrEmpty(entry.Value))
                .Select((entry) => entry.Value)
                .ToList();
        }

        private bool KeyMatchesFilterValue(string key, string filterValue)
        {
            return key.IndexOf(filterValue, StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private void SortPrefEntries(List<PreferenceEntry> entries, ListOrderingState orderingState)
        {
            entries.Sort((PreferenceEntry x, PreferenceEntry y) =>
            {
                int result = ComparePrefEntries(x, y, orderingState.CurrentMainOrderingCol);
                result = ApplyOrderingDirection(result, GetColumnOrderingStatus(orderingState, orderingState.CurrentMainOrderingCol));

                if (result == 0 && orderingState.CurrentMainOrderingCol != PreferenceEntryColumn.Key)
                    result = ApplyOrderingDirection(ComparePrefEntries(x, y, PreferenceEntryColumn.Key), orderingState.KeyOrderingStatus);

                return result;
            });
        }

        private int ComparePrefEntries(PreferenceEntry x, PreferenceEntry y, PreferenceEntryColumn column)
        {
            switch (column)
            {
                case PreferenceEntryColumn.Key:
                    return string.Compare(x.m_key, y.m_key, StringComparison.OrdinalIgnoreCase);
                case PreferenceEntryColumn.Type:
                    return x.m_typeSelection.CompareTo(y.m_typeSelection);
                case PreferenceEntryColumn.Value:
                    return string.Compare(x.ValueAsString(), y.ValueAsString(), StringComparison.OrdinalIgnoreCase);
                default:
                    return 0;
            }
        }

        private int ApplyOrderingDirection(int result, ColOrderingStatus status)
        {
            if (status == ColOrderingStatus.Descending)
                return -result;

            return result;
        }

        private void LoadKeys(out string[] userDef, out string[] unityDef, bool reloadKeys)
        {
            string[] keys = entryAccessor.GetKeys(reloadKeys);

            //keys.ToList().ForEach( e => { Debug.Log(e); } );

            // Seperate keys int unity defined and user defined
            Dictionary<bool, List<string>> groups = keys
                .GroupBy( (key) => key.StartsWith("unity.") || key.StartsWith("UnityGraphicsQuality") )
                .ToDictionary( (g) => g.Key, (g) => g.ToList() );

            unityDef = (groups.ContainsKey(true)) ? groups[true].ToArray() : new string[0];
            userDef = (groups.ContainsKey(false)) ? groups[false].ToArray() : new string[0];
        }

        private void LoadEditorPrefsKeys(out string[] editorPrefsDef, bool reloadKeys)
        {
            editorPrefsDef = editorPrefsAccessor.GetKeys(reloadKeys);
        }

        private SerializedProperty GetUserDefListElementAtIndex(int index, SerializedProperty ListProperty)
        {
            UnityEngine.Assertions.Assert.IsTrue(ListProperty.isArray, "Given 'ListProperts' is not type of array");

            if (userDefListCache[index] == null)
            {
                userDefListCache[index] = ListProperty.GetArrayElementAtIndex(index);
            }
            return userDefListCache[index];
        }

        private SerializedProperty GetEditorPrefsListElementAtIndex(int index, SerializedProperty ListProperty)
        {
            UnityEngine.Assertions.Assert.IsTrue(ListProperty.isArray, "Given 'ListProperts' is not type of array");

            if (editorPrefsListCache[index] == null)
            {
                editorPrefsListCache[index] = ListProperty.GetArrayElementAtIndex(index);
            }
            return editorPrefsListCache[index];
        }

#if (UNITY_EDITOR_LINUX || UNITY_EDITOR_OSX)
        private string MakeValidFileName(string unsafeFileName)
        {
            string normalizedFileName = unsafeFileName.Trim().Normalize(NormalizationForm.FormD);
            StringBuilder stringBuilder = new StringBuilder();

            // We need to use a TextElementEmumerator in order to support UTF16 characters that may take up more than one char(case 1169358)
            TextElementEnumerator charEnum = StringInfo.GetTextElementEnumerator(normalizedFileName);
            while (charEnum.MoveNext())
            {
                string c = charEnum.GetTextElement();
                if (c.Length == 1 && invalidFilenameChars.Contains(c[0]))
                {
                    stringBuilder.Append('_');
                    continue;
                }
                UnicodeCategory unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c, 0);
                if (unicodeCategory != UnicodeCategory.NonSpacingMark)
                    stringBuilder.Append(c);
            }
            return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
        }
#endif
    }
}

public class MySearchField : SearchField
{
    public enum SearchModePreferencesEditorWindow { Key, Value }

    public SearchModePreferencesEditorWindow SearchMode { get; private set; }

    public Action DropdownSelectionDelegate;

    public new string OnGUI(
        Rect rect,
        string text,
        GUIStyle style,
        GUIStyle cancelButtonStyle,
        GUIStyle emptyCancelButtonStyle)
    {
        style.padding.left = 17;
        Rect ContextMenuRect = new Rect(rect.x, rect.y, 10, rect.height);

        // Add interactive area
        EditorGUIUtility.AddCursorRect(ContextMenuRect, MouseCursor.Text);
        if (Event.current.type == EventType.MouseDown && ContextMenuRect.Contains(Event.current.mousePosition))
        {
            void OnDropdownSelection(object parameter)
            {
                SearchMode = (SearchModePreferencesEditorWindow) Enum.Parse(typeof(SearchModePreferencesEditorWindow), parameter.ToString());
                DropdownSelectionDelegate();
            }

            GenericMenu menu = new GenericMenu();
            foreach(SearchModePreferencesEditorWindow EnumIt in Enum.GetValues(typeof(SearchModePreferencesEditorWindow)))
            {
                String EnumName = Enum.GetName(typeof(SearchModePreferencesEditorWindow), EnumIt);
                menu.AddItem(new GUIContent(EnumName), SearchMode == EnumIt, OnDropdownSelection, EnumName);
            }

            menu.DropDown(rect);
        }

        // Render original search field
        String result = base.OnGUI(rect, text, style, cancelButtonStyle, emptyCancelButtonStyle);

        // Render additional images
        GUIStyle ContexMenuOverlayStyle = GUIStyle.none;
        ContexMenuOverlayStyle.contentOffset = new Vector2(9, 5);
        GUI.Box(new Rect(rect.x, rect.y, 5, 5), EditorGUIUtility.IconContent("d_ProfilerTimelineDigDownArrow@2x"), ContexMenuOverlayStyle);

        if (!HasFocus() && String.IsNullOrEmpty(text))
        {
            GUI.enabled = false;
            GUI.Label(new Rect(rect.x + 14, rect.y, 40, rect.height), Enum.GetName(typeof(SearchModePreferencesEditorWindow), SearchMode));
            GUI.enabled = true;
        }
        ContexMenuOverlayStyle.contentOffset = new Vector2();
        return result;
    }

    public new string OnToolbarGUI(string text, params GUILayoutOption[] options) => this.OnToolbarGUI(GUILayoutUtility.GetRect(29f, 200f, 18f, 18f, EditorStyles.toolbarSearchField, options), text);
    public new string OnToolbarGUI(Rect rect, string text) => this.OnGUI(rect, text, EditorStyles.toolbarSearchField, EditorStyles.toolbarButton, EditorStyles.toolbarButton);
}
