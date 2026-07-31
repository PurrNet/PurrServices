using System;
using PurrNet.Editor;
using UnityEditor;
using UnityEngine;

namespace PurrNet.Services.Editor
{
    public class PurrServicesSetupWindow : EditorWindow
    {
        PurrUserProfile _profile;
        ProjectInfo[] _projects;
        string _error;
        bool _isBusy;
        Vector2 _listScrollPos;
        Vector2 _detailScrollPos;
        string _newProjectName = "";
        bool _showCreateField;
        Texture2D _logo;

        string _selectedAppId;
        bool _showGlobalSettings;

        string _buildProjectId;
        string _buildProjectName;
        string _editorProjectId;
        string _editorProjectName;
        bool _editorOverride;
        string _serverUrl;
        string _selectedBuildScope;
        string _selectedEditorScope;
        string _buildScopeError;
        string _editorScopeError;
        string _serviceUrlError;

        static readonly Color HEADER_BG = new(0.17f, 0.17f, 0.17f, 1f);
        static readonly Color SEPARATOR_COLOR = new(0.13f, 0.13f, 0.13f, 1f);
        static readonly Color LIST_BG = new(0.2f, 0.2f, 0.2f, 1f);
        static readonly Color SELECTED_BG = new(0.17f, 0.36f, 0.53f, 1f);
        static readonly Color HOVER_BG = new(0.26f, 0.26f, 0.26f, 1f);
        static readonly Color SELECTED_ACCENT = new(0.35f, 0.65f, 0.95f, 1f);
        static readonly Color LINKED_COLOR = new(0.5f, 0.95f, 0.5f, 1f);
        static readonly Color EDITOR_COLOR = new(0.45f, 0.75f, 1f, 1f);
        const string DASHBOARD_URL = "https://purrnet.dev/dashboard";

        const float HEADER_HEIGHT = 42f;
        const float PANE_HEADER_HEIGHT = 42f;
        const float ITEM_HEIGHT = 28f;
        const float SPLITTER_WIDTH = 6f;
        const float LIST_MIN_WIDTH = 170f;
        const float DETAIL_MIN_WIDTH = 440f;
        const float MIN_WINDOW_WIDTH = 700f;
        const float RUNTIME_USAGE_CHROME_WIDTH = 31f;
        const float VERTICAL_SCROLLBAR_GUTTER = 18f;
        const float DETAIL_CONTENT_MARGIN = 10f;

        float _splitWidth = 220f;
        bool _isDraggingSplitter;
        Rect _cachedSplitterRect;
        Rect _cachedWorkflowRect;

        [NonSerialized] GUIStyle _smallLabelStyle;
        [NonSerialized] GUIStyle _itemNameStyle;
        [NonSerialized] GUIStyle _externalItemStyle;
        [NonSerialized] GUIStyle _badgeStyle;
        [NonSerialized] GUIStyle _detailTitleStyle;
        [NonSerialized] GUIStyle _detailDescStyle;
        [NonSerialized] GUIStyle _paneTitleStyle;

        [MenuItem("Tools/PurrNet/PurrServices", false, -98)]
        public static void ShowWindow()
        {
            var window = GetWindow<PurrServicesSetupWindow>();
            var icon = Resources.Load<Texture2D>("purricon");
            window.titleContent = new GUIContent("PurrServices", icon);
            window.minSize = new Vector2(MIN_WINDOW_WIDTH, 520);
        }

        void InitStyles()
        {
            if (_detailTitleStyle != null) return;

            _smallLabelStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f, 1f) }
            };

            _itemNameStyle = new GUIStyle(EditorStyles.label)
            {
                fontSize = 11,
                padding = new RectOffset(12, 4, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleLeft
            };

            _externalItemStyle = new GUIStyle(EditorStyles.miniLabel)
            {
                fontSize = 9,
                alignment = TextAnchor.MiddleRight,
                normal = { textColor = new Color(0.95f, 0.7f, 0.35f, 1f) }
            };

            _badgeStyle = new GUIStyle(EditorStyles.miniBoldLabel)
            {
                fontSize = 9,
                margin = new RectOffset(0, 0, 0, 0),
                alignment = TextAnchor.MiddleCenter,
                normal = { textColor = new Color(0.08f, 0.08f, 0.08f, 1f) }
            };

            _detailTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 16,
                margin = new RectOffset(0, 0, 0, 4)
            };

            _detailDescStyle = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                fontSize = 11,
                normal = { textColor = new Color(0.78f, 0.78f, 0.78f, 1f) }
            };

            _paneTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                fontSize = 13
            };

        }

        void OnEnable()
        {
            wantsMouseMove = true;
            _logo = Resources.Load<Texture2D>("purricon");
            _profile = new PurrUserProfile(Repaint);
            _profile.Refresh();
            PurrPackageManagerAuth.onAuthChanged += OnAuthChanged;

            ReloadConfiguration();
            SelectCurrentApp();

            if (PurrPackageManagerAuth.HasApiKey())
                RefreshProjects();
        }

        void OnDisable()
        {
            PurrPackageManagerAuth.onAuthChanged -= OnAuthChanged;
        }

        void OnAuthChanged()
        {
            ReloadConfiguration();
            SelectCurrentApp();
            _profile.Refresh();
            _projects = null;
            _error = null;
            if (PurrPackageManagerAuth.HasApiKey())
                RefreshProjects();
            Repaint();
        }

        async void RefreshProjects()
        {
            if (_isBusy) return;
            _isBusy = true;
            _error = null;

            try
            {
                var result = await PurrServicesAPI.GetProjects(PurrPackageManagerAuth.GetApiKey());
                if (result.Success)
                {
                    _projects = result.Value.projects;
                    SelectAvailableProject();
                }
                else
                {
                    _error = result.Error;
                    _projects = null;
                }
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
            finally
            {
                _isBusy = false;
                Repaint();
            }
        }

        async void CreateProject(string projectName)
        {
            if (_isBusy || string.IsNullOrWhiteSpace(projectName)) return;
            _isBusy = true;
            _error = null;

            try
            {
                var result = await PurrServicesAPI.CreateProject(PurrPackageManagerAuth.GetApiKey(), projectName);
                if (result.Success)
                {
                    _newProjectName = "";
                    _showCreateField = false;
                    await RefreshProjectsAsync();
                    if (_projects != null)
                    {
                        for (int i = 0; i < _projects.Length; i++)
                        {
                            if (_projects[i].id == result.Value.project.id)
                            {
                                SelectProject(i);
                                break;
                            }
                        }
                    }
                }
                else
                    _error = result.Error;
            }
            catch (Exception e)
            {
                _error = e.Message;
            }
            finally
            {
                _isBusy = false;
                Repaint();
            }
        }

        async System.Threading.Tasks.Task RefreshProjectsAsync()
        {
            var result = await PurrServicesAPI.GetProjects(PurrPackageManagerAuth.GetApiKey());
            if (result.Success)
                _projects = result.Value.projects;
        }

        void SelectProject(int index)
        {
            if (_projects == null ||
                index < 0 ||
                index >= _projects.Length)
            {
                return;
            }

            _selectedAppId = _projects[index].id;
            LoadSelectedScopes();
            _detailScrollPos = Vector2.zero;
            Repaint();
        }

        void SelectLinkedProject(string projectId)
        {
            if (!IsLinkedProjectId(projectId))
                return;

            _selectedAppId = projectId;
            LoadSelectedScopes();
            _detailScrollPos = Vector2.zero;
            Repaint();
        }

        void SelectCurrentApp()
        {
            _selectedAppId = !string.IsNullOrEmpty(_buildProjectId)
                ? _buildProjectId
                : EffectiveEditorProjectId;
            LoadSelectedScopes();
        }

        void SelectAvailableProject()
        {
            if (_projects == null || _projects.Length == 0)
                return;

            if (FindProject(_selectedAppId) != null ||
                IsLinkedProjectId(_selectedAppId))
                return;

            var preferredId = !string.IsNullOrEmpty(_buildProjectId)
                ? _buildProjectId
                : EffectiveEditorProjectId;
            _selectedAppId = !string.IsNullOrEmpty(preferredId)
                ? preferredId
                : _projects[0].id;
            LoadSelectedScopes();
            _detailScrollPos = Vector2.zero;
        }

        void LoadSelectedScopes()
        {
            _selectedBuildScope = PurrServicesProjectLink.Scope(
                PurrServicesProfile.Build,
                _selectedAppId);
            _selectedEditorScope = PurrServicesProjectLink.Scope(
                PurrServicesProfile.Editor,
                _selectedAppId);
            _buildScopeError = null;
            _editorScopeError = null;
        }

        void SwitchToSelectedApp(PurrServicesProfile profile)
        {
            if (IsSelectedAppCurrent(profile) ||
                string.IsNullOrEmpty(_selectedAppId))
                return;

            var project = FindSelectableProject(_selectedAppId);
            if (project == null) return;
            LinkProject(project, profile);

            _detailScrollPos = Vector2.zero;
            Repaint();
        }

        string EffectiveEditorProjectId =>
            _editorOverride ? _editorProjectId : _buildProjectId;

        bool IsLinkedProjectId(string projectId) =>
            !string.IsNullOrEmpty(projectId) &&
            (string.Equals(projectId, _buildProjectId, StringComparison.Ordinal) ||
             string.Equals(projectId, EffectiveEditorProjectId, StringComparison.Ordinal));

        bool IsExternalProject(string projectId) =>
            PurrPackageManagerAuth.HasApiKey() &&
            _projects != null &&
            !string.IsNullOrEmpty(projectId) &&
            FindProject(projectId) == null;

        string LinkedProjectName(string projectId)
        {
            if (string.Equals(projectId, _buildProjectId, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(_buildProjectName))
            {
                return _buildProjectName;
            }

            if (string.Equals(projectId, _editorProjectId, StringComparison.Ordinal) &&
                !string.IsNullOrEmpty(_editorProjectName))
            {
                return _editorProjectName;
            }

            return "Linked Project";
        }

        string LinkedProjectPublicKey(string projectId)
        {
            if (string.Equals(projectId, _buildProjectId, StringComparison.Ordinal))
                return PurrServicesProjectLink.PublicKey(PurrServicesProfile.Build);

            if (string.Equals(projectId, _editorProjectId, StringComparison.Ordinal))
                return PurrServicesProjectLink.PublicKey(PurrServicesProfile.Editor);

            return null;
        }

        ProjectInfo FindSelectableProject(string projectId)
        {
            var project = FindProject(projectId);
            if (project != null)
                return string.IsNullOrWhiteSpace(project.publicKey) ? null : project;

            if (!IsLinkedProjectId(projectId))
                return null;

            var publicKey = LinkedProjectPublicKey(projectId);
            if (string.IsNullOrWhiteSpace(publicKey))
                return null;

            return new ProjectInfo
            {
                id = projectId,
                name = LinkedProjectName(projectId),
                publicKey = publicKey
            };
        }

        bool IsSelectedAppCurrent(PurrServicesProfile profile) =>
            string.Equals(
                _selectedAppId,
                profile == PurrServicesProfile.Editor
                    ? EffectiveEditorProjectId
                    : _buildProjectId,
                StringComparison.Ordinal);

        string SelectedAppDisplayName
        {
            get
            {
                if (string.IsNullOrEmpty(_selectedAppId))
                    return "No Project Selected";

                var project = FindProject(_selectedAppId);
                if (!string.IsNullOrEmpty(project?.name))
                    return project.name;

                if (_selectedAppId == _buildProjectId &&
                    !string.IsNullOrEmpty(_buildProjectName))
                {
                    return _buildProjectName;
                }

                if (_selectedAppId == _editorProjectId &&
                    !string.IsNullOrEmpty(_editorProjectName))
                {
                    return _editorProjectName;
                }

                return "Selected Project";
            }
        }

        void ReloadConfiguration()
        {
            _buildProjectId = PurrServicesProjectLink.ProjectId(PurrServicesProfile.Build);
            _buildProjectName = PurrServicesProjectLink.ProjectName(PurrServicesProfile.Build);
            _editorProjectId = PurrServicesProjectLink.ProjectId(PurrServicesProfile.Editor);
            _editorProjectName = PurrServicesProjectLink.ProjectName(PurrServicesProfile.Editor);
            _editorOverride = PurrServicesProjectLink.editorOverride;
            _serverUrl = PurrServicesProjectLink.serverUrl;
        }

        void LinkProject(ProjectInfo project, PurrServicesProfile profile)
        {
            PurrServicesProjectLink.Link(project, profile);
            ReloadConfiguration();
        }

        void OnGUI()
        {
            InitStyles();
            HandleSplitterDrag(_cachedSplitterRect);

            DrawHeader();
            DrawSeparator();

            var contentHeight = Mathf.Max(
                100f,
                position.height - HEADER_HEIGHT - 1f);
            DrawWorkflow(position.width, contentHeight);
        }

        void DrawWorkflow(float availableWidth, float availableHeight)
        {
            _splitWidth = Mathf.Clamp(
                _splitWidth,
                LIST_MIN_WIDTH,
                availableWidth - SPLITTER_WIDTH - DETAIL_MIN_WIDTH);

            EditorGUILayout.BeginHorizontal(
                GUILayout.Height(availableHeight),
                GUILayout.ExpandWidth(true));

            EditorGUILayout.BeginVertical(GUILayout.Width(_splitWidth));
            DrawPaneHeader(
                "Apps",
                "B  Builds    E  Unity Editor");
            var listRect = GUILayoutUtility.GetRect(
                0,
                10000,
                GUILayout.ExpandWidth(true),
                GUILayout.ExpandHeight(true));
            DrawAppsFooter();
            EditorGUILayout.EndVertical();

            GUILayout.Space(SPLITTER_WIDTH);

            DrawAppConfiguration(
                availableWidth - _splitWidth - SPLITTER_WIDTH,
                availableHeight);

            EditorGUILayout.EndHorizontal();

            if (Event.current.type != EventType.Layout)
            {
                var fullListRect = new Rect(
                    listRect.x,
                    listRect.y,
                    listRect.width,
                    listRect.height);
                _cachedSplitterRect = new Rect(
                    listRect.xMax,
                    listRect.y,
                    SPLITTER_WIDTH,
                    listRect.height);
                _cachedWorkflowRect = new Rect(
                    listRect.x,
                    listRect.y,
                    availableWidth,
                    listRect.height);
                DrawAppList(fullListRect);
                DrawSplitter(_cachedSplitterRect);
            }
        }

        void DrawAppsFooter()
        {
            DrawSeparator();
            GUILayout.Space(4);

            if (!PurrPackageManagerAuth.HasApiKey())
            {
                EditorGUILayout.LabelField(
                    "Sign in to use a project.",
                    _smallLabelStyle);
                if (GUILayout.Button("Login", GUILayout.Height(24)))
                    PurrPackageManagerAuth.Login();
            }
            else if (_error != null)
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
                GUI.enabled = !_isBusy;
                if (GUILayout.Button("Retry", GUILayout.Height(22)))
                    RefreshProjects();
                GUI.enabled = true;
            }
            else
            {
                if (_isBusy)
                    EditorGUILayout.LabelField("Loading projects...", _smallLabelStyle);
                else if (_projects == null || _projects.Length == 0)
                    EditorGUILayout.LabelField("No projects yet.", _smallLabelStyle);

                if (!string.IsNullOrEmpty(_selectedAppId))
                    DrawCreateProject();
            }

            GUILayout.Space(4);
        }

        void DrawAppConfiguration(float availableWidth, float availableHeight)
        {
            var contentWidth = Mathf.Max(
                1f,
                availableWidth - VERTICAL_SCROLLBAR_GUTTER - DETAIL_CONTENT_MARGIN * 2f);
            var scrollHeight = Mathf.Max(1f, availableHeight - PANE_HEADER_HEIGHT);

            EditorGUILayout.BeginVertical(
                GUILayout.Width(availableWidth),
                GUILayout.Height(availableHeight));
            DrawPaneHeader(
                "App Configuration",
                SelectedAppDisplayName);

            _detailScrollPos = EditorGUILayout.BeginScrollView(
                _detailScrollPos,
                false,
                true,
                GUIStyle.none,
                GUI.skin.verticalScrollbar,
                GUI.skin.scrollView,
                GUILayout.Width(availableWidth),
                GUILayout.Height(scrollHeight));
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(DETAIL_CONTENT_MARGIN);
            EditorGUILayout.BeginVertical(GUILayout.Width(contentWidth));
            GUILayout.Space(DETAIL_CONTENT_MARGIN);

            if (string.IsNullOrEmpty(_selectedAppId))
            {
                DrawProjectSetup();
            }
            else
            {
                EditorGUILayout.LabelField("Selected App", EditorStyles.boldLabel);
                EditorGUILayout.Space(3);
                DrawSelectedAppDetails();

                EditorGUILayout.Space(8);
                DrawSeparator();
                EditorGUILayout.Space(8);
                DrawRuntimeUsageSection(contentWidth);
            }

            EditorGUILayout.Space(8);
            DrawSeparator();
            DrawGlobalServiceSettings();
            GUILayout.Space(DETAIL_CONTENT_MARGIN);
            EditorGUILayout.EndVertical();
            GUILayout.Space(DETAIL_CONTENT_MARGIN);
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndScrollView();
            EditorGUILayout.EndVertical();
        }

        void DrawProjectSetup()
        {
            EditorGUILayout.LabelField("Project Required", _detailTitleStyle);
            EditorGUILayout.HelpBox(
                "PurrServices must be linked to a project before it can be used.",
                MessageType.Warning);
            EditorGUILayout.Space(6);

            if (!PurrPackageManagerAuth.HasApiKey())
            {
                if (GUILayout.Button(
                        "Login",
                        GUILayout.Height(28),
                        GUILayout.Width(130)))
                {
                    PurrPackageManagerAuth.Login();
                }

                return;
            }

            if (_error != null)
            {
                EditorGUILayout.HelpBox(_error, MessageType.Error);
                using (new EditorGUI.DisabledScope(_isBusy))
                {
                    if (GUILayout.Button(
                            "Retry",
                            GUILayout.Height(28),
                            GUILayout.Width(130)))
                    {
                        RefreshProjects();
                    }
                }
                return;
            }

            if (_isBusy)
            {
                EditorGUILayout.LabelField("Loading projects...", _smallLabelStyle);
                return;
            }

            EditorGUILayout.LabelField(
                "Create a project to configure Player Builds and the Unity Editor.",
                _detailDescStyle);
            EditorGUILayout.Space(4);
            DrawCreateProject("Create Project");
        }

        void DrawRuntimeUsageSection(float availableWidth)
        {
            var columnWidth = Mathf.Max(
                1f,
                (availableWidth - RUNTIME_USAGE_CHROME_WIDTH) * 0.5f);

            EditorGUILayout.LabelField("Runtime Usage", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                $"Configure how {SelectedAppDisplayName} is used.",
                _smallLabelStyle);
            EditorGUILayout.Space(5);

            EditorGUILayout.BeginHorizontal();

            EditorGUILayout.BeginVertical(GUILayout.Width(columnWidth));
            DrawProfileColumnHeader(PurrServicesProfile.Build);
            DrawProfileAssignment(PurrServicesProfile.Build);
            EditorGUILayout.EndVertical();

            GUILayout.Space(6);
            DrawVerticalSeparator();
            GUILayout.Space(6);

            EditorGUILayout.BeginVertical(GUILayout.Width(columnWidth));
            DrawEditorAssignment();
            EditorGUILayout.EndVertical();

            EditorGUILayout.EndHorizontal();
        }

        void DrawEditorAssignment()
        {
            EditorGUI.BeginChangeCheck();
            var editorOverride = DrawEditorColumnHeader(_editorOverride);
            if (EditorGUI.EndChangeCheck())
            {
                _editorOverride = editorOverride;
                PurrServicesProjectLink.SetEditorOverride(_editorOverride);
                ReloadConfiguration();
                LoadSelectedScopes();
            }

            var inheritBuild = !_editorOverride;
            EditorGUI.BeginDisabledGroup(inheritBuild);
            DrawProfileAssignment(PurrServicesProfile.Editor, inheritBuild);
            EditorGUI.EndDisabledGroup();
        }

        bool DrawEditorColumnHeader(bool editorOverride)
        {
            EditorGUILayout.BeginHorizontal();
            var badgeRect = GUILayoutUtility.GetRect(
                18,
                18,
                GUILayout.Width(18),
                GUILayout.Height(18));
            if (Event.current.type == EventType.Repaint)
                DrawProfileBadge(badgeRect, "E", EDITOR_COLOR);

            var nextEditorOverride = EditorGUILayout.ToggleLeft(
                new GUIContent(
                    "Unity Editor Override",
                    "Enable to unlock a separate project and scope for the Unity Editor. " +
                    "When disabled, the Editor uses the Player Builds configuration."),
                editorOverride,
                EditorStyles.boldLabel,
                GUILayout.Height(18));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(3);
            return nextEditorOverride;
        }

        void DrawProfileColumnHeader(PurrServicesProfile profile)
        {
            EditorGUILayout.BeginHorizontal();
            var badgeRect = GUILayoutUtility.GetRect(
                18,
                18,
                GUILayout.Width(18),
                GUILayout.Height(18));
            if (Event.current.type == EventType.Repaint)
            {
                DrawProfileBadge(
                    badgeRect,
                    profile == PurrServicesProfile.Build ? "B" : "E",
                    profile == PurrServicesProfile.Build
                        ? LINKED_COLOR
                        : EDITOR_COLOR);
            }

            EditorGUILayout.LabelField(
                ProfileDisplayName(profile),
                EditorStyles.boldLabel,
                GUILayout.Height(18));
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(3);
        }

        void DrawProfileAssignment(
            PurrServicesProfile profile,
            bool inheritBuild = false)
        {
            var profileLabel = ProfileDisplayName(profile);
            EditorGUILayout.LabelField(
                inheritBuild
                    ? "Using Player Builds project and scope"
                    : $"Scope remembered for {SelectedAppDisplayName}",
                _smallLabelStyle);

            if (profile == PurrServicesProfile.Build || inheritBuild)
            {
                DrawNamespaceConfiguration(
                    inheritBuild ? PurrServicesProfile.Build : profile,
                    profileLabel,
                    _selectedAppId,
                    ref _selectedBuildScope,
                    ref _buildScopeError);
            }
            else
            {
                DrawNamespaceConfiguration(
                    profile,
                    profileLabel,
                    _selectedAppId,
                    ref _selectedEditorScope,
                    ref _editorScopeError);
            }

            DrawSwitchAction(profile);
        }

        void DrawSelectedAppDetails()
        {
            var project = FindProject(_selectedAppId);
            var projectName = project?.name ?? SelectedAppDisplayName;

            EditorGUILayout.LabelField(
                string.IsNullOrEmpty(projectName) ? "Project" : projectName,
                _detailTitleStyle);

            if (project != null)
            {
                EditorGUILayout.LabelField(project.slug, _smallLabelStyle);
                EditorGUILayout.Space(6);
                DrawInfoRow("Created", FormatDate(project.createdAt));
                DrawInfoRow("Public Key", FormatPublicKey(project.publicKey));
            }
            else
            {
                var isExternal = IsExternalProject(_selectedAppId);
                EditorGUILayout.LabelField(
                    isExternal ? "External Project" : "Linked Project",
                    _smallLabelStyle);
                EditorGUILayout.Space(6);
                DrawInfoRow("Created", "—");
                DrawInfoRow(
                    "Public Key",
                    FormatPublicKey(LinkedProjectPublicKey(_selectedAppId)));
                EditorGUILayout.Space(6);
                EditorGUILayout.HelpBox(
                    isExternal
                        ? "This project is linked through version-controlled settings, but it " +
                          "is not available to the signed-in account. You can keep using it or " +
                          "switch to one of your projects."
                        : "This project is linked through version-controlled settings. Login " +
                          "or refresh projects to verify dashboard access.",
                    isExternal ? MessageType.Warning : MessageType.Info);
            }

            EditorGUILayout.Space(6);
            using (new EditorGUI.DisabledScope(project == null))
            {
                if (GUILayout.Button(
                        "Open Dashboard",
                        GUILayout.Height(24),
                        GUILayout.Width(130)))
                {
                    Application.OpenURL($"{DASHBOARD_URL}/{_selectedAppId}");
                }
            }
        }

        void DrawSwitchAction(PurrServicesProfile profile)
        {
            EditorGUILayout.Space(10);
            if (IsSelectedAppCurrent(profile))
            {
                var previousColor = GUI.color;
                GUI.color = profile == PurrServicesProfile.Build
                    ? LINKED_COLOR
                    : EDITOR_COLOR;
                EditorGUILayout.LabelField(
                    $"Active for {ProfileDisplayName(profile)}",
                    EditorStyles.boldLabel);
                GUI.color = previousColor;
            }
            else
            {
                var target = profile == PurrServicesProfile.Build
                    ? "Builds"
                    : "Editor";
                var canSwitch = FindSelectableProject(_selectedAppId) != null;
                using (new EditorGUI.DisabledScope(!canSwitch))
                {
                    if (GUILayout.Button(
                            $"Switch {target} to This Project",
                            GUILayout.Height(28),
                            GUILayout.ExpandWidth(true)))
                    {
                        SwitchToSelectedApp(profile);
                    }
                }

                if (!canSwitch)
                {
                    EditorGUILayout.LabelField(
                        "This project has no public key.",
                        _smallLabelStyle);
                }
            }
        }

        void DrawNamespaceConfiguration(
            PurrServicesProfile profile,
            string profileLabel,
            string appId,
            ref string scope,
            ref string error)
        {
            var previousLabelWidth = EditorGUIUtility.labelWidth;
            EditorGUIUtility.labelWidth = 42;
            EditorGUI.BeginChangeCheck();
            var nextScope = EditorGUILayout.DelayedTextField("Scope", scope);
            EditorGUIUtility.labelWidth = previousLabelWidth;
            if (EditorGUI.EndChangeCheck())
            {
                if (TryNormalizeIdentifier(nextScope, out var normalized))
                {
                    scope = normalized.ToLowerInvariant();
                    error = null;
                    PurrServicesProjectLink.SetScope(profile, appId, scope);
                }
                else
                {
                    error =
                        $"{profileLabel} scope must be 1-64 characters using letters, numbers, " +
                        "'.', '_' or '-'.";
                }
            }

            if (!string.IsNullOrEmpty(error))
                EditorGUILayout.HelpBox(error, MessageType.Error);
        }

        void DrawGlobalServiceSettings()
        {
            EditorGUILayout.BeginVertical();
            GUILayout.Space(3);
            _showGlobalSettings = EditorGUILayout.Foldout(
                _showGlobalSettings,
                "Global Service Settings",
                true);
            if (!_showGlobalSettings)
            {
                GUILayout.Space(3);
                EditorGUILayout.EndVertical();
                return;
            }

            EditorGUI.BeginChangeCheck();
            var nextServerUrl = EditorGUILayout.DelayedTextField("Service URL", _serverUrl);
            if (EditorGUI.EndChangeCheck())
            {
                if (TryNormalizeServerUrl(nextServerUrl, out var normalized))
                {
                    _serverUrl = normalized;
                    _serviceUrlError = null;
                    PurrServicesProjectLink.SetServerUrl(normalized);
                }
                else
                {
                    _serviceUrlError =
                        "Service URL must be an absolute HTTP or HTTPS URL.";
                }
            }

            if (!string.IsNullOrEmpty(_serviceUrlError))
                EditorGUILayout.HelpBox(_serviceUrlError, MessageType.Error);

            EditorGUILayout.EndVertical();
        }

        ProjectInfo FindProject(string projectId)
        {
            if (string.IsNullOrEmpty(projectId) || _projects == null)
                return null;

            for (int i = 0; i < _projects.Length; i++)
            {
                if (_projects[i].id == projectId)
                    return _projects[i];
            }

            return null;
        }

        static string ProfileDisplayName(PurrServicesProfile profile) =>
            profile == PurrServicesProfile.Build ? "Player Builds" : "Unity Editor";

        static bool TryNormalizeIdentifier(string value, out string normalized)
        {
            normalized = value?.Trim();
            if (string.IsNullOrEmpty(normalized) || normalized.Length > 64)
                return false;

            foreach (var character in normalized)
            {
                if ((character >= 'a' && character <= 'z') ||
                    (character >= 'A' && character <= 'Z') ||
                    (character >= '0' && character <= '9') ||
                    character == '.' ||
                    character == '_' ||
                    character == '-')
                {
                    continue;
                }

                return false;
            }

            return true;
        }

        static bool TryNormalizeServerUrl(string value, out string normalized)
        {
            normalized = null;
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            normalized = value.Trim().TrimEnd('/');
            return true;
        }

        void DrawPaneHeader(string title, string subtitle)
        {
            var rect = GUILayoutUtility.GetRect(
                0,
                PANE_HEADER_HEIGHT,
                GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, HEADER_BG);
            EditorGUI.DrawRect(
                new Rect(rect.x, rect.yMax - 1, rect.width, 1),
                SEPARATOR_COLOR);

            GUI.Label(
                new Rect(rect.x + 8, rect.y + 4, rect.width - 16, 18),
                title,
                _paneTitleStyle);
            GUI.Label(
                new Rect(rect.x + 8, rect.y + 22, rect.width - 16, 16),
                subtitle,
                _smallLabelStyle);
        }

        void DrawHeader()
        {
            var headerRect = GUILayoutUtility.GetRect(0, HEADER_HEIGHT, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(headerRect, HEADER_BG);

            var logoRect = new Rect(headerRect.x + 10, headerRect.y + 7, 28, 28);
            if (_logo != null)
                GUI.DrawTexture(logoRect, _logo, ScaleMode.ScaleToFit);

            var labelRect = new Rect(logoRect.xMax + 8, headerRect.y + 4, 200, 20);
            var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 14 };
            GUI.Label(labelRect, "PurrServices", headerStyle);

            var linkedRect = new Rect(labelRect.x, labelRect.yMax - 2, 320, 16);
            GUI.Label(
                linkedRect,
                "Configure services for builds and the Unity Editor",
                _smallLabelStyle);

            var refreshRect = new Rect(headerRect.xMax - 78, headerRect.y + 10, 68, 22);
            GUI.enabled = !_isBusy && PurrPackageManagerAuth.HasApiKey();
            if (GUI.Button(refreshRect, "Refresh"))
                RefreshProjects();
            GUI.enabled = true;

            if (_profile != null)
            {
                var profileAnchor = new Rect(headerRect.x, headerRect.y + 10, refreshRect.x - 4 - headerRect.x, 22);
                _profile.DrawProfileBar(profileAnchor, _smallLabelStyle);
            }
        }

        void DrawSeparator()
        {
            var rect = GUILayoutUtility.GetRect(0, 1, GUILayout.ExpandWidth(true));
            EditorGUI.DrawRect(rect, SEPARATOR_COLOR);
        }

        void DrawVerticalSeparator()
        {
            var rect = GUILayoutUtility.GetRect(
                1,
                1,
                GUILayout.Width(1),
                GUILayout.ExpandHeight(true));
            EditorGUI.DrawRect(rect, SEPARATOR_COLOR);
        }

        void DrawSplitter(Rect rect)
        {
            EditorGUI.DrawRect(rect, SEPARATOR_COLOR);

            float centerX = rect.x + rect.width / 2f;
            float centerY = rect.y + rect.height / 2f;
            var dotColor = new Color(0.35f, 0.35f, 0.35f, 1f);
            for (int i = -2; i <= 2; i++)
            {
                var dotRect = new Rect(centerX - 1, centerY + i * 5, 2, 2);
                EditorGUI.DrawRect(dotRect, dotColor);
            }

            EditorGUIUtility.AddCursorRect(rect, MouseCursor.ResizeHorizontal);
        }

        void HandleSplitterDrag(Rect splitterRect)
        {
            var e = Event.current;
            switch (e.type)
            {
                case EventType.MouseDown when splitterRect.Contains(e.mousePosition):
                    _isDraggingSplitter = true;
                    e.Use();
                    break;
                case EventType.MouseDrag when _isDraggingSplitter:
                    _splitWidth = Mathf.Clamp(
                        e.mousePosition.x - _cachedWorkflowRect.x,
                        LIST_MIN_WIDTH,
                        _cachedWorkflowRect.width - SPLITTER_WIDTH - DETAIL_MIN_WIDTH);
                    e.Use();
                    Repaint();
                    break;
                case EventType.MouseUp when _isDraggingSplitter:
                    _isDraggingSplitter = false;
                    e.Use();
                    break;
            }
        }

        void DrawAppList(Rect areaRect)
        {
            EditorGUI.DrawRect(areaRect, LIST_BG);

            var projectCount = _projects?.Length ?? 0;
            var showLinkedBuild =
                !string.IsNullOrEmpty(_buildProjectId) &&
                FindProject(_buildProjectId) == null;
            var editorProjectId = EffectiveEditorProjectId;
            var showLinkedEditor =
                !string.IsNullOrEmpty(editorProjectId) &&
                !string.Equals(editorProjectId, _buildProjectId, StringComparison.Ordinal) &&
                FindProject(editorProjectId) == null;
            var linkedProjectCount =
                (showLinkedBuild ? 1 : 0) +
                (showLinkedEditor ? 1 : 0);
            float totalHeight = (projectCount + linkedProjectCount) * ITEM_HEIGHT;
            bool needsScroll = totalHeight > areaRect.height;
            var viewRect = new Rect(
                0,
                0,
                areaRect.width - (needsScroll ? 13f : 0f),
                Mathf.Max(totalHeight, areaRect.height));

            _listScrollPos = GUI.BeginScrollView(areaRect, _listScrollPos, viewRect);

            var rowIndex = 0;
            if (showLinkedBuild)
            {
                var projectId = _buildProjectId;
                DrawAppRow(
                    new Rect(0, rowIndex++ * ITEM_HEIGHT, viewRect.width, ITEM_HEIGHT),
                    LinkedProjectName(projectId),
                    IsExternalProject(projectId) ? "External" : null,
                    projectId == _selectedAppId,
                    true,
                    projectId == editorProjectId,
                    () => SelectLinkedProject(projectId));
            }

            if (showLinkedEditor)
            {
                var projectId = editorProjectId;
                DrawAppRow(
                    new Rect(0, rowIndex++ * ITEM_HEIGHT, viewRect.width, ITEM_HEIGHT),
                    LinkedProjectName(projectId),
                    IsExternalProject(projectId) ? "External" : null,
                    projectId == _selectedAppId,
                    false,
                    true,
                    () => SelectLinkedProject(projectId));
            }

            for (int i = 0; i < projectCount; i++)
            {
                var project = _projects[i];
                var index = i;
                DrawAppRow(
                    new Rect(0, rowIndex++ * ITEM_HEIGHT, viewRect.width, ITEM_HEIGHT),
                    project.name,
                    null,
                    project.id == _selectedAppId,
                    project.id == _buildProjectId,
                    project.id == EffectiveEditorProjectId,
                    () => SelectProject(index));
            }

            GUI.EndScrollView();
        }

        void DrawAppRow(
            Rect itemRect,
            string label,
            string status,
            bool isSelected,
            bool isBuildActive,
            bool isEditorActive,
            Action select)
        {
            bool isHover =
                itemRect.Contains(Event.current.mousePosition) &&
                !isSelected;

            if (isSelected)
            {
                EditorGUI.DrawRect(itemRect, SELECTED_BG);
            }
            else if (isHover)
            {
                EditorGUI.DrawRect(itemRect, HOVER_BG);
            }

            if (isSelected)
            {
                EditorGUI.DrawRect(
                    new Rect(itemRect.x, itemRect.y, 3, itemRect.height),
                    SELECTED_ACCENT);
            }

            if (Event.current.type == EventType.Repaint)
            {
                var badgeAreaWidth = 44f;
                var statusWidth = string.IsNullOrEmpty(status)
                    ? 0f
                    : Mathf.Min(
                        58f,
                        _externalItemStyle.CalcSize(new GUIContent(status)).x + 8f);
                var nameRect = new Rect(
                    itemRect.x,
                    itemRect.y,
                    itemRect.width - badgeAreaWidth - statusWidth,
                    itemRect.height);
                _itemNameStyle.Draw(nameRect, label, false, false, false, false);

                if (!string.IsNullOrEmpty(status))
                {
                    GUI.Label(
                        new Rect(
                            itemRect.xMax - badgeAreaWidth - statusWidth,
                            itemRect.y,
                            statusWidth,
                            itemRect.height),
                        status,
                        _externalItemStyle);
                }

                if (isBuildActive)
                {
                    DrawProfileBadge(
                        new Rect(itemRect.xMax - 40, itemRect.y + 6, 16, 16),
                        "B",
                        LINKED_COLOR);
                }

                if (isEditorActive)
                {
                    DrawProfileBadge(
                        new Rect(itemRect.xMax - 20, itemRect.y + 6, 16, 16),
                        "E",
                        EDITOR_COLOR);
                }
            }

            if (Event.current.type == EventType.MouseDown &&
                itemRect.Contains(Event.current.mousePosition))
            {
                select?.Invoke();
                GUI.FocusControl(null);
                Event.current.Use();
            }

            if (isHover && Event.current.type == EventType.Repaint)
                Repaint();
        }

        void DrawProfileBadge(Rect rect, string label, Color color)
        {
            EditorGUI.DrawRect(rect, color);
            GUI.Label(rect, label, _badgeStyle);
        }

        void DrawInfoRow(string label, string value)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label(label, _smallLabelStyle, GUILayout.Width(80));
            GUILayout.Label(value, _itemNameStyle);
            GUILayout.EndHorizontal();
        }

        static string FormatDate(string isoDate)
        {
            if (DateTime.TryParse(isoDate, out var dt))
                return dt.ToString("yyyy-MM-dd");
            return isoDate ?? "—";
        }

        static string FormatPublicKey(string publicKey)
        {
            if (string.IsNullOrEmpty(publicKey))
                return "—";

            return publicKey.Length > 28
                ? publicKey[..28] + "..."
                : publicKey;
        }

        void DrawCreateProject(string buttonLabel = "+ New Project")
        {
            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(4);

            if (!_showCreateField)
            {
                if (GUILayout.Button(buttonLabel, GUILayout.Height(22)))
                    _showCreateField = true;
            }
            else
            {
                _newProjectName = EditorGUILayout.TextField(_newProjectName, GUILayout.Height(20));

                GUI.enabled = !_isBusy && !string.IsNullOrWhiteSpace(_newProjectName);
                if (GUILayout.Button("Create", GUILayout.Width(55), GUILayout.Height(20)))
                    CreateProject(_newProjectName);
                GUI.enabled = true;

                if (GUILayout.Button("Cancel", GUILayout.Width(50), GUILayout.Height(20)))
                {
                    _showCreateField = false;
                    _newProjectName = "";
                }
            }

            GUILayout.Space(4);
            EditorGUILayout.EndHorizontal();
        }

    }
}
