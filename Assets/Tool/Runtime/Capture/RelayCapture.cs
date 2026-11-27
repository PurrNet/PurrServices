using System;
using System.IO;
using PurrNet.Transports;
using UnityEngine;
using UnityEngine.Rendering;

namespace PurrNet.Services.Capture
{
    /// <summary>
    /// Keeps what this player's device did during a match (the camera they saw the game through and their input)
    /// in a small file of its own, sent to PurrServices once the match is over. Together with the traffic the
    /// PurrTransport relay already carries, that is enough to see a match again.
    /// <para>
    /// It only runs in a build of a game that is linked to a PurrNet project, while the game is connected through
    /// PurrTransport under that project, and only once the project's owner wants it
    /// (<see cref="CapturePermission"/>) and the device may be gathered from (<see cref="DataRegion"/>). Nothing
    /// is added to the game's connection: the file waits in the temporary folder, is sent after the match and
    /// deleted, and is deleted unsent when it is no longer wanted or has waited too long.
    /// </para>
    /// </summary>
    public static class RelayCapture
    {
        /// <summary>The folder files wait in, under <see cref="Application.temporaryCachePath"/>.</summary>
        public const string Folder = "PurrServices/Capture";

        /// <summary>Turn off to gather nothing on this device, whatever the project wants. Files that wait are deleted.</summary>
        public static bool enabled { get; set; } = true;

        /// <summary>Whether files may be sent over a mobile data connection. Off: they wait for another network.</summary>
        public static bool uploadOverMobileData { get; set; } = true;

        /// <summary>Whether a file is being written right now.</summary>
        public static bool isCapturing => CaptureRunner.instance && CaptureRunner.instance.isCapturing;

        /// <summary>A random name for this run of the game: it says which relay connection a file belongs to.</summary>
        public static string tag { get; private set; }

        /// <summary>Which build of the game this is: Unity's id for the build, the same one its build report carries.</summary>
        public static string build => Application.isEditor ? "editor" : Application.buildGUID;

        /// <summary>Tests turn it on to see what a build does.</summary>
        internal static bool runInEditor;

        internal static string directory => Path.Combine(Application.temporaryCachePath, "PurrServices", "Capture");

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetStatics()
        {
            tag = Guid.NewGuid().ToString("N");
            enabled = true;
            uploadOverMobileData = true;
            PurrTransport.relaySignInBuild = null;
            PurrTransport.relaySignInTag = null;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        static void Boot()
        {
            if ((Application.isEditor && !runInEditor) || CaptureEnvironment.runningOnCI)
                return;
            if (!PurrServicesProjectLink.isLinked)
                return;
            CaptureRunner.Create();
        }
    }

    [DefaultExecutionOrder(-10000)]
    [AddComponentMenu("")]
    internal sealed class CaptureRunner : MonoBehaviour
    {
        const float ChunkSeconds = 10f;
        const float ShortestSeconds = 5f;
        const long MaxFileBytes = 8L * 1024 * 1024;

        static readonly PartColumn[] TimeColumns =
        {
            new PartColumn("viewOffset", 256), new PartColumn("unixMs"), new PartColumn("frame"),
            new PartColumn("realtimeMs"), new PartColumn("screenWidth"), new PartColumn("screenHeight"),
            new PartColumn("link")
        };

        static readonly PartColumn[] CameraColumns =
        {
            new PartColumn("px", 1000), new PartColumn("py", 1000), new PartColumn("pz", 1000),
            new PartColumn("qx", 16384), new PartColumn("qy", 16384), new PartColumn("qz", 16384),
            new PartColumn("qw", 16384), new PartColumn("fov", 100), new PartColumn("aspect", 1000),
            new PartColumn("orthographic"), new PartColumn("orthographicSize", 1000)
        };

        internal static CaptureRunner instance;

        readonly CaptureInput _input = new CaptureInput();
        readonly PartUploader _uploader = new PartUploader();
        readonly long[] _timeRow = new long[7];
        readonly long[] _cameraRow = new long[11];

        PartWriter _writer;
        FileStream _stream;
        string _openPath;
        NetworkManager _manager;
        PurrTransport _transport;
        string _room;
        int _timeStream, _cameraStream, _inputStream, _actionsStream;
        long _tick, _cameraTick, _ticks;
        float _startedAt, _chunkAt, _timeAt;
        int _lastRenderFrame;
        bool _hasPlayer;
        ulong _player;
        bool _listening;
        bool _failed;
        int _serial;
        string _askedRoom;

        internal bool isCapturing => _writer != null;

        /// <summary>The file being written, which nothing else may touch.</summary>
        internal string openPath => _openPath;

        internal static void Create()
        {
            if (instance)
                return;
            var go = new GameObject("PurrServices Capture") { hideFlags = HideFlags.HideAndDontSave };
            DontDestroyOnLoad(go);
            instance = go.AddComponent<CaptureRunner>();
        }

        void Update()
        {
            if (_failed)
                return;
            try
            {
                Step();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        void Step()
        {
            string key = PurrServicesProjectLink.projectKey;
            string server = PurrServicesSettings.serverUrl;
            bool linked = RelayCapture.enabled && !string.IsNullOrEmpty(key);

            // The relay is told which build and which run this is unless the project said it wants no capture.
            // It must be in place before a connection signs in, so it does not wait for a session or an answer.
            bool signIn = linked && !CapturePermission.refused;
            PurrTransport.relaySignInBuild = signIn ? RelayCapture.build : null;
            PurrTransport.relaySignInTag = signIn ? RelayCapture.tag : null;

            NetworkManager manager = null;
            PurrTransport transport = null;
            bool playing = linked && FindSession(key, out manager, out transport);
            string room = playing ? transport.relayRoomName : null;
            if (room != _askedRoom)
            {
                _askedRoom = room;
                if (playing)
                    CapturePermission.SessionStarted();
            }
            CapturePermission.Tick(this, server, key, playing);

            bool wanted = linked && CapturePermission.wanted;
            bool record = wanted && playing && DataRegion.dataCollectionAllowed && !Application.isBatchMode;

            if (isCapturing && (!record || transport != _transport || transport.relayRoomName != _room))
                End(record ? "room" : "ended");
            if (record && !isCapturing)
                Begin(manager, transport);
            if (isCapturing)
                Sample();

            _uploader.Tick(this, wanted, isCapturing, server, key);
        }

        /// <summary>The network manager and PurrTransport this device plays through under the linked project, if any.</summary>
        static bool FindSession(string key, out NetworkManager manager, out PurrTransport transport)
        {
            transport = null;
            manager = NetworkManager.main;
            if (!manager || !manager.isClient)
                return false;

            var active = manager.transport;
            if (active is PurrTransport purr)
                transport = purr;
            else if (active is CompositeTransport composite)
            {
                if (manager.isServer)
                {
                    var all = composite.transports;
                    for (int i = 0; i < all.Count; i++)
                    {
                        if (all[i] is PurrTransport hosted && hosted.listenerState == ConnectionState.Connected)
                        {
                            transport = hosted;
                            break;
                        }
                    }
                }
                else
                    transport = composite.clientTransport as PurrTransport;
            }

            if (!transport || transport.projectKey != key)
                return false;
            return transport.clientState == ConnectionState.Connected ||
                   (manager.isServer && transport.listenerState == ConnectionState.Connected);
        }

        void Begin(NetworkManager manager, PurrTransport transport)
        {
            string folder = RelayCapture.directory;
            Directory.CreateDirectory(folder);
            _serial++;
            string name = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss") + "-" + RelayCapture.tag.Substring(0, 8) + "-" + _serial;
            _openPath = Path.Combine(folder, name + PartUploader.OpenExtension);
            _stream = new FileStream(_openPath, FileMode.Create, FileAccess.Write, FileShare.Read);

            _manager = manager;
            _transport = transport;
            _room = transport.relayRoomName;
            var header = new PartJson().BeginObject()
                .Prop("format", "purrpart").Prop("version", PartWriter.Version)
                .Prop("project", PurrServicesProjectLink.projectId)
                .Prop("build", RelayCapture.build)
                .Prop("tag", RelayCapture.tag)
                .Prop("room", _room)
                .Prop("role", manager.isServer ? "host" : "client")
                .Prop("tickRate", manager.tickRate)
                .Prop("startedUtc", DateTime.UtcNow.ToString("o"))
                .Prop("game", Application.productName)
                .Prop("gameVersion", Application.version)
                .Prop("unityVersion", Application.unityVersion)
                .Prop("platform", Application.platform.ToString())
                .Prop("netcode", PurrNet.Utils.PurrMetadata.version)
                .Prop("input", CaptureInput.Backend)
                .EndObject();

            _writer = new PartWriter(_stream, header.ToString());
            _timeStream = _writer.AddStream("time", TimeColumns);
            _cameraStream = _writer.AddStream("camera", CameraColumns);
            _inputStream = _writer.AddStream("input", CaptureInput.InputColumns);
            _actionsStream = _writer.AddStream("actions", CaptureInput.ActionColumns);

            _tick = _cameraTick = -1;
            _ticks = 0;
            _hasPlayer = false;
            _startedAt = Time.unscaledTime;
            _chunkAt = _startedAt + ChunkSeconds;
            _timeAt = 0;
            _lastRenderFrame = Time.frameCount;
            _input.Begin();
            Listen(true);
        }

        void Listen(bool on)
        {
            if (_listening == on)
                return;
            _listening = on;
            if (on)
            {
                RenderPipelineManager.beginCameraRendering += OnBeginCameraRendering;
                Camera.onPreCull += OnPreCullCamera;
            }
            else
            {
                RenderPipelineManager.beginCameraRendering -= OnBeginCameraRendering;
                Camera.onPreCull -= OnPreCullCamera;
            }
        }

        void Sample()
        {
            _input.Poll();

            var ticks = _manager.tickModule;
            if (ticks == null)
                return;
            long tick = ticks.localTick;
            if (tick == _tick)
                return;
            _tick = tick;
            _ticks++;

            _input.WriteTick(_writer, _inputStream, _actionsStream, tick);

            if (_manager.isLocalPlayerReady)
            {
                ulong player = _manager.localPlayer.id.value;
                if (!_hasPlayer || player != _player)
                {
                    _hasPlayer = true;
                    _player = player;
                    _writer.WriteNote(new PartJson().BeginObject().Prop("player", (long)player).Prop("tick", tick)
                        .EndObject().ToString());
                }
            }

            float now = Time.unscaledTime;
            if (now >= _timeAt)
            {
                _timeAt = now + 1f;
                var row = _timeRow;
                row[0] = (long)Math.Round((ticks.syncedPreciseTick - tick) * 256.0);
                row[1] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                row[2] = Time.frameCount;
                row[3] = (long)(Time.realtimeSinceStartupAsDouble * 1000.0);
                row[4] = Screen.width;
                row[5] = Screen.height;
                row[6] = (long)_transport.clientSessionLink;
                _writer.WriteRow(_timeStream, tick, row);
            }

            if (now >= _chunkAt)
            {
                _chunkAt = now + ChunkSeconds;
                _writer.EndChunk();
                if (_writer.bytesWritten >= MaxFileBytes)
                    End("size");
            }
        }

        void OnBeginCameraRendering(ScriptableRenderContext context, Camera cam) => OnRender(cam);

        void OnPreCullCamera(Camera cam) => OnRender(cam);

        /// <summary>
        /// Called while a camera is about to render, after every script and camera rig has moved it, so the pose is
        /// what the player saw. A failure here stops the capture; it never reaches the renderer.
        /// </summary>
        void OnRender(Camera cam)
        {
            if (_writer == null)
                return;
            try
            {
                if (cam != Camera.main)
                    return;
                _lastRenderFrame = Time.frameCount;
                SampleCamera(cam);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        void LateUpdate()
        {
            if (_writer == null || Time.frameCount - _lastRenderFrame <= 2)
                return;
            try
            {
                var cam = Camera.main;
                if (cam)
                    SampleCamera(cam);
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        static long Fixed(float value, double scale)
        {
            double scaled = value * scale;
            if (double.IsNaN(scaled) || scaled > 4e15 || scaled < -4e15)
                return 0;
            return (long)Math.Round(scaled);
        }

        void SampleCamera(Camera cam)
        {
            var ticks = _manager ? _manager.tickModule : null;
            if (ticks == null)
                return;
            long tick = ticks.localTick;
            if (tick == _cameraTick)
                return;
            _cameraTick = tick;

            var t = cam.transform;
            var position = t.position;
            var rotation = t.rotation;
            float sign = rotation.w < 0 ? -1f : 1f;
            var row = _cameraRow;
            row[0] = Fixed(position.x, 1000);
            row[1] = Fixed(position.y, 1000);
            row[2] = Fixed(position.z, 1000);
            row[3] = Fixed(rotation.x * sign, 16384);
            row[4] = Fixed(rotation.y * sign, 16384);
            row[5] = Fixed(rotation.z * sign, 16384);
            row[6] = Fixed(rotation.w * sign, 16384);
            row[7] = Fixed(cam.fieldOfView, 100);
            row[8] = Fixed(cam.aspect, 1000);
            row[9] = cam.orthographic ? 1 : 0;
            row[10] = Fixed(cam.orthographicSize, 1000);
            _writer.WriteChanged(_cameraStream, tick, row);
        }

        /// <summary>Closes the file. One that is too short to be of use is deleted; the rest wait to be sent.</summary>
        void End(string reason)
        {
            if (_writer == null)
                return;
            Listen(false);
            _input.End();

            bool keep = Time.unscaledTime - _startedAt >= ShortestSeconds && _ticks > 1;
            try
            {
                _writer.Close(new PartJson().BeginObject()
                    .Prop("clean", true).Prop("reason", reason)
                    .Prop("endedUtc", DateTime.UtcNow.ToString("o"))
                    .Prop("ticks", _ticks).Prop("rows", _writer.rowsWritten)
                    .EndObject().ToString());
            }
            catch (Exception)
            {
                // A file that could not be finished is still complete up to its last chunk.
            }

            _writer.Dispose();
            _writer = null;
            try
            {
                _stream.Dispose();
            }
            catch (Exception)
            {
            }
            _stream = null;
            _manager = null;
            _transport = null;
            _room = null;

            string path = _openPath;
            _openPath = null;
            try
            {
                if (keep)
                    File.Move(path, PartUploader.FinishedPath(path));
                else
                    File.Delete(path);
            }
            catch (Exception)
            {
            }
            _uploader.Rescan();
        }

        /// <summary>Something went wrong: capture stops for the rest of this run and never gets in the game's way.</summary>
        void Fail(Exception e)
        {
            _failed = true;
            try
            {
                End("error");
            }
            catch (Exception)
            {
                _writer = null;
            }
            Listen(false);
            PurrTransport.relaySignInBuild = null;
            PurrTransport.relaySignInTag = null;
            Debug.LogWarning("[PurrServices] Capture stopped: " + e.Message);
        }

        void OnApplicationPause(bool paused)
        {
            if (!paused || _writer == null)
                return;
            try
            {
                _writer.EndChunk();
            }
            catch (Exception e)
            {
                Fail(e);
            }
        }

        void OnApplicationQuit() => Shutdown();

        void OnDestroy()
        {
            Shutdown();
            if (instance == this)
                instance = null;
        }

        void Shutdown()
        {
            try
            {
                End("quit");
            }
            catch (Exception)
            {
            }
            Listen(false);
        }
    }
}
