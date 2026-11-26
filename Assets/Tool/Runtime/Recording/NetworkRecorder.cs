using System;
using System.Collections.Generic;
using PurrNet.Services;
using PurrNet.Transports;
using UnityEngine;

namespace PurrNet.Recording
{
    /// <summary>
    /// Records a match to a .purrrec file on the server or host (<see cref="NetworkRecording"/>: every spawned network
    /// identity's transform, colliders and recorded sync types per tick, the RPCs and broadcasts, the players) and sends
    /// finished recordings to the PurrNet recordings service. Add one next to the <see cref="NetworkManager"/>. On a
    /// client it writes what only that client has (its camera, raw input, UI and anything written through
    /// <see cref="WriteSample{T}"/>) to a part of the recording: a file of the client's own that it uploads itself.
    /// Nothing but the server's start and stop announcement is sent over the connection.
    /// </summary>
    [DefaultExecutionOrder(1000)]
    [AddComponentMenu("PurrNet/Recording/Network Recorder")]
    public sealed class NetworkRecorder : MonoBehaviour
    {
        [SerializeField] private NetworkManager _networkManager;
        [Tooltip("Start recording as soon as this machine is the server, in release builds, once purrnet.dev says the " +
                 "project wants recordings (it shares anonymous data and its recordings service is on). Never on CI " +
                 "machines (the CI environment variable and the like). StartRecording() records anywhere.")]
        [SerializeField] private bool _recordOnServer = true;
        [Tooltip("Also start on their own in the editor (kept on disk, never uploaded) and in development builds. " +
                 "Off: those sessions are development, not gameplay.")]
        [SerializeField] private bool _recordInEditor;
        [Tooltip("Record the camera each player renders with. Clients write it to a file of their own, uploaded by them.")]
        [SerializeField] private bool _recordCamera = true;
        [Tooltip("Record each player's raw device input (mouse, keys, gamepad). Clients write it to a file of their own, " +
                 "uploaded by them; it is not sent over the game's connection. Keys are left out while a text field has focus.")]
        [SerializeField] private bool _recordInput = true;
        [Tooltip("Record what each player's UI shows (uGUI, TextMeshPro, UI Toolkit) as data: rectangles, sprite " +
                 "names, text and control values, written when they change.")]
        [SerializeField] private bool _recordUI = true;
        [Tooltip("Leave what players type into text fields (chat, names) out of the recorded UI. Password and PIN " +
                 "fields are left out either way.")]
        [SerializeField] private bool _redactTextInput = true;
        [Tooltip("Most time per tick, in milliseconds, spent looking for UI changes that are not announced (elements " +
                 "starting to move, fade or change style). With a very large UI such a change is only noticed a " +
                 "little later.")]
        [SerializeField] private float _uiCheckBudgetMs = 0.1f;
        [Tooltip("Folder for recordings. Relative paths are under Application.temporaryCachePath: recordings only " +
                 "wait there until they are sent.")]
        [SerializeField] private string _directory = DefaultDirectory;
        [SerializeField] private float _chunkSeconds = 10f;
        [Tooltip("A recording that has run for this many minutes is ended and the next one starts on the following " +
                 "tick, so a server that runs for days is sent piece by piece instead of as one file nobody sees " +
                 "until it stops. Every piece is a recording of its own that says which session it is a piece of. " +
                 "0 never splits.")]
        [SerializeField] private float _splitMinutes = 30f;
        [Tooltip("Send finished recordings to the PurrNet recordings service, then delete them. Needs a project key: " +
                 "the one below, or the project linked through PurrServices.")]
        [SerializeField] private bool _upload = true;
        [SerializeField] private string _uploadEndpoint = "https://purrnet.dev/api/services/recordings";
        [Tooltip("A dedicated server sends each recording it finishes while it keeps running, so a server that is " +
                 "up for days shows up piece by piece. It sends slowly: a recording is spread over a quarter of the " +
                 "time it took to record, so it never takes the players' bandwidth. Off: it sends them once it is " +
                 "offline, like every other machine.")]
        [SerializeField] private bool _uploadWhileServing = true;
        [Tooltip("Project public key. Empty uses the project linked through PurrServices.")]
        [SerializeField] private string _projectKey = "";
        [Tooltip("Recordings that could not be sent yet are kept up to this size, and never more than " +
                 "512 MB or two files (one being sent, one waiting); the oldest are deleted first.")]
        [SerializeField] private int _maxLocalMegabytes = 256;
        [Tooltip("Poses (transforms and moving colliders) are rounded to about this many units before they are " +
                 "written, so objects that only jitter write nothing and the file is much smaller. 0.001 is about a " +
                 "millimetre; 0 writes them exactly.")]
        [SerializeField] private float _positionPrecision = 0.001f;
        [Tooltip("How far, in units, a terrain height may be from the real one in the recording. A few centimetres " +
                 "cannot be seen and makes terrain much smaller; 0 writes terrain exactly.")]
        [SerializeField] private float _terrainTolerance = 0.05f;
        [Tooltip("Most time per tick, in milliseconds, spent checking objects for changes nothing announced: colliders " +
                 "added, removed, resized or moved, renderers, and whether an object still shows. What the game " +
                 "does through its own code (activating an object, enabling a renderer or a collider, instantiating " +
                 "or destroying something) is heard and recorded on that tick; everything else is found by this " +
                 "check, which comes round to an object every half second or so, and less often to one nothing " +
                 "has changed on.")]
        [SerializeField] private float _colliderCheckBudgetMs = 0.1f;
        [Tooltip("Memory reserved when recording starts for one chunk's rows, in KiB (two such buffers, plus the " +
                 "writer's working memory of about the same size). A chunk ends early when its buffer is full, so " +
                 "these buffers never grow during play; bigger buffers make slightly smaller files for big games. " +
                 "A client's part starts with small buffers that grow up to this size. An object allocates a " +
                 "little memory the first time it enters a recording.")]
        [SerializeField] private int _chunkBufferKilobytes = 2048;
        [Tooltip("Records what recorded objects' animators, particle systems and audio sources do: a row when an " +
                 "animation state, a particle system or a sound starts or stops.")]
        [SerializeField] private bool _recordEffects = true;
        [Tooltip("Most time per tick, in milliseconds, spent asking animators, particle systems and audio sources " +
                 "what they are doing. What the game starts through its own code is recorded when it does, and " +
                 "followed every tick until it ends; this is for the rest, which is asked in turn, so such a " +
                 "change is noticed a little later.")]
        [SerializeField] private float _effectsBudgetMs = 0.02f;
        [Tooltip("Most time per tick, in milliseconds, spent on objects the recording has not seen before (declaring " +
                 "their streams, colliders and renderers). When more objects appear in one tick than fit, the rest " +
                 "enter the recording on the following ticks. 0 records every object on the tick it appears, " +
                 "whatever that costs.")]
        [SerializeField] private float _spawnBudgetMs = 0.25f;

        NetworkRecording _recording;
        readonly NameScrubber _names = new NameScrubber();

        /// <summary>The folder recordings go to unless another is set: Recordings, under the temporary folder.</summary>
        public const string DefaultDirectory = "Recordings";

        /// <summary>
        /// Whether this machine may record: always when recordings are not sent anywhere, otherwise only once
        /// purrnet.dev said the project wants them (see <see cref="RecordingPermission"/>).
        /// </summary>
        bool permitted => !_upload || RecordingPermission.permits;

        /// <summary>Makes every recorder created from now on carry a <typeparamref name="T"/>. Call it once, at load.</summary>
        public static void RegisterExtension<T>() where T : RecorderExtension, new() => NetworkRecording.RegisterExtension<T>();

        public static NetworkRecorder instance { get; private set; }

        /// <summary>The recording this component runs.</summary>
        public NetworkRecording recording
        {
            get
            {
                if (_recording == null)
                {
                    _recording = new NetworkRecording(gameObject, _networkManager);
                    Push();
                }
                return _recording;
            }
        }

        /// <summary>Free-form labels written into the header (map, mode, build hash...). Set before recording starts.</summary>
        public Dictionary<string, string> labels => recording.labels;

        public RecordingSession session => recording.session;
        public NetworkManager networkManager => recording.networkManager;
        public bool isRecording => _recording != null && _recording.isRecording;
        public string currentPath => _recording?.currentPath;
        public long bytesWritten => _recording?.bytesWritten ?? 0;

        public event Action<string> onRecordingFinished
        {
            add => recording.onRecordingFinished += value;
            remove => recording.onRecordingFinished -= value;
        }

        /// <summary>How many times the recording started last was split into a next piece.</summary>
        public int splits => _recording?.splits ?? 0;

        /// <summary>What the last split cost the main thread, in milliseconds.</summary>
        public double lastSplitMs => _recording?.lastSplitMs ?? 0;

        /// <summary>Objects that entered the last recording later than they appeared, to keep within the spawn budget.</summary>
        public int spawnsDeferred => _recording?.spawnsDeferred ?? 0;

        void Awake()
        {
            instance = this;
            _ = recording;
            if (_recordCamera && !GetComponent<RecordLocalCamera>())
                gameObject.AddComponent<RecordLocalCamera>();
            if (_recordInput && !GetComponent<RecordLocalInput>())
                gameObject.AddComponent<RecordLocalInput>();
            if (_recordUI && !GetComponent<RecordLocalUI>())
            {
                var ui = gameObject.AddComponent<RecordLocalUI>();
                ui.redactTextInput = _redactTextInput;
                ui.checkBudgetMs = _uiCheckBudgetMs;
            }
            ConfigureUploads();
        }

        void Update()
        {
            Push();
            bool permitted = this.permitted;
            if (!permitted && _recording.isRecording)
                _recording.StopRecording();
            _recording.Update(permitted && _recordOnServer && RecordingSession.RecordsAutomatically(_recordInEditor));
        }

        void OnDestroy()
        {
            _recording?.Dispose();
            if (instance == this)
                instance = null;
        }

        void OnApplicationQuit() => _recording?.Close();

        /// <summary>Hands the inspector's settings to the recording.</summary>
        void Push()
        {
            var r = _recording;
            if (_networkManager)
                r.networkManager = _networkManager;
            if (_names.Refresh(((IRecordingContext)r).localPlayer))
                r.session.textFilter = _names.Scrub;
            r.session.recordLocalPlayer = DataRegion.dataCollectionAllowed && permitted;
            r.session.positionGrid = RecordingSession.GridFor(_positionPrecision);
            r.session.terrainTolerance = _terrainTolerance;
            r.session.recordPlayer ??= player => DataRegion.IsAllowedFor(r.networkManager, player);
            r.directory = _directory;
            r.chunkSeconds = _chunkSeconds;
            r.splitMinutes = _splitMinutes;
            r.maxLocalMegabytes = _maxLocalMegabytes;
            r.colliderCheckBudgetMs = _colliderCheckBudgetMs;
            r.chunkBufferKilobytes = _chunkBufferKilobytes;
            r.recordEffects = _recordEffects;
            r.effectsBudgetMs = _effectsBudgetMs;
            r.spawnBudgetMs = _spawnBudgetMs;
        }

        public T GetExtension<T>() where T : RecorderExtension => recording.GetExtension<T>();

        /// <summary>Folder for the next recording. Relative paths are under Application.temporaryCachePath.</summary>
        public void SetDirectory(string directory)
        {
            _directory = directory;
            Push();
            recording.SetDirectory(directory);
            ConfigureUploads();
        }

        /// <summary>
        /// Where finished recordings are sent and with which project key (empty uses the project linked through
        /// PurrServices). An empty endpoint turns uploads off.
        /// </summary>
        public void SetUpload(string endpoint, string projectKey)
        {
            _upload = !string.IsNullOrEmpty(endpoint);
            _uploadEndpoint = endpoint;
            _projectKey = projectKey;
            ConfigureUploads();
        }

        void ConfigureUploads()
        {
            RecordingUploader.sendWhileServing = _uploadWhileServing;
            RecordingUploader.RemoveLegacyRecordings(_directory);
            RecordingUploader.ConfigureUploads(_upload, _uploadEndpoint, _projectKey, _directory, _maxLocalMegabytes);
            if (_upload)
                RecordingPermission.Ask(_uploadEndpoint,
                    string.IsNullOrWhiteSpace(_projectKey) ? PurrServicesProjectLink.projectKey : _projectKey);
        }

        /// <summary>Starts a new recording file. Called automatically on the server when <c>recordOnServer</c> is set.</summary>
        public void StartRecording(string fileName = null)
        {
            Push();
            recording.StartRecording(fileName);
        }

        /// <summary>Flushes and closes the current recording; the next one starts when this machine is the server again.</summary>
        public void StopRecording() => _recording?.StopRecording();

        /// <summary>
        /// Adds a sample to a custom stream. On the server (and host) it goes into the recording; on a client into
        /// that client's part of it, a file of its own that the client uploads. Nothing is sent to the server.
        /// </summary>
        public void WriteSample<T>(string stream, in T sample) where T : struct => recording.WriteSample(stream, sample);

        /// <summary>
        /// Server only. Adds a sample to a custom stream on behalf of <paramref name="player"/>, for players that have
        /// no client of their own (bots) or data the server observes about a player.
        /// </summary>
        public void WriteSampleFor<T>(string stream, PlayerID player, in T sample) where T : struct =>
            recording.WriteSampleFor(stream, player, sample);

        /// <summary>The last finished recording's size and the recorder's cost per section, as text.</summary>
        public string DescribeLastRecording() => recording.DescribeLastRecording();

        /// <summary>Records one tick (see <see cref="NetworkRecording.RecordTick"/>).</summary>
        public void RecordTick(ulong tick) => recording.RecordTick(tick);
    }
}
