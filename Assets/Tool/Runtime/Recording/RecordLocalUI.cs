using System.Collections.Generic;
using System.Diagnostics;
using Unity.Profiling;
using UnityEngine;

namespace PurrNet.Recording
{
    /// <summary>
    /// Writes what this machine's UI shows to the "ui" stream, as data: every uGUI graphic (Image, RawImage, Text,
    /// TextMeshPro) and UI Toolkit element with its rectangle, colours, sprite name, text and control value. A row
    /// is written when an element changes, and the whole tree again at the start of every chunk. Recorders add one
    /// next to themselves unless their UI recording is turned off.
    ///
    /// Changes the UI systems announce (text, colour, sprite, size) are recorded on the tick they happen. Changes
    /// they do not announce (an element moved by its transform, faded by a CanvasGroup, restyled in UI Toolkit) are
    /// found by going over the elements, as many per tick as <see cref="checkBudgetMs"/> allows; an element found
    /// changing is then looked at every tick until it stops.
    ///
    /// Password and PIN fields are never recorded. <see cref="DontRecordUI"/> keeps a part of the UI out.
    /// </summary>
    [DefaultExecutionOrder(10001)]
    [AddComponentMenu("PurrNet/Recording/Record Local UI")]
    public sealed partial class RecordLocalUI : MonoBehaviour
    {
        public const string Stream = "ui";

        [Tooltip("Most time per tick, in milliseconds, spent looking for UI changes that are not announced (elements " +
                 "starting to move, fade or change style). Announced changes (text, colour, sprite) are always " +
                 "recorded on their tick, and so is an element that keeps changing; with a very large UI the start " +
                 "of such a change is noticed a little later.")]
        [SerializeField] private float _checkBudgetMs = 0.1f;
        [Tooltip("Leave out what players type into text fields (chat, names, search). Password and PIN fields are " +
                 "left out either way.")]
        [SerializeField] private bool _redactTextInput = true;

        public float checkBudgetMs
        {
            get => _checkBudgetMs;
            set => _checkBudgetMs = value;
        }

        public bool redactTextInput
        {
            get => _redactTextInput;
            set
            {
                if (_redactTextInput == value)
                    return;
                _redactTextInput = value;
                _recheckAll = true;
            }
        }

        sealed partial class Element
        {
            public int id;
            public bool toolkit;
            public bool live;
            public bool queued;
            public bool hot;
            public bool content = true;
            public bool dropped;
            public int pass;
            public UISample last;
            public Element parent;
            public Element root;

            public Rect rootRect;
            public float screenX, screenY, screenWidth = 1, screenHeight = 1;

            /// <summary>Where the element was when it was last captured, from its root: what a capture starts from.</summary>
            public float absX, absY;

            /// <summary>The parent the last written row is relative to, when it is.</summary>
            public Element shownParent;

            /// <summary>
            /// What a graphic that draws a whole UI by itself showed (see <see cref="UIContentSources"/>): an element
            /// for each item, by the item's id. Those elements are not in the list the passes walk; the graphic's
            /// capture keeps them.
            /// </summary>
            public Dictionary<ulong, Element> contents;
            public int contentsVersion = -1;
            public int contentsSeen;
        }

        static readonly ProfilerMarker Marker = new ProfilerMarker("RecordLocalUI.Pass");

        readonly List<Element> _elements = new List<Element>(256);
        readonly List<Element> _dirty = new List<Element>(64);
        readonly List<Element> _hot = new List<Element>(64);
        RecordingSession _session;
        int _nextId = 1;
        int _cursor;
        int _epoch;
        int _pass;
        int _screenWidth, _screenHeight;
        int _exclusions = -1;
        ulong _sampledTick = ulong.MaxValue;
        int _lastRenderFrame = -1000;
        bool _recheckAll;
        bool _anyDropped;

        public int passes { get; private set; }
        public long rows { get; private set; }
        public long checks { get; private set; }
        public double totalMs { get; private set; }
        public double maxMs { get; private set; }
        public int elementCount => _elements.Count;
        public long allocatedBytes { get; private set; }
        public long maxAllocatedBytes { get; private set; }
        public int passesAllocating { get; private set; }
        public static bool measureAllocations;
        ProfilerRecorder _allocations;

        public void ResetStats()
        {
            passes = 0;
            rows = 0;
            checks = 0;
            totalMs = 0;
            maxMs = 0;
            allocatedBytes = 0;
            maxAllocatedBytes = 0;
            passesAllocating = 0;
        }

        public string Describe() =>
            $"[RecordLocalUI] {_elements.Count} elements, {passes} passes, {rows} rows, {checks} checks, " +
            $"{(passes > 0 ? totalMs / passes * 1000 : 0):0.0} us per pass (longest {maxMs * 1000:0} us)" +
            (measureAllocations
                ? $", {allocatedBytes} bytes allocated in {passesAllocating} passes (most in one: {maxAllocatedBytes})"
                : "");

        void OnEnable()
        {
#if PURRNET_UGUI
            EnableCanvases();
#endif
        }

        void OnDisable()
        {
#if PURRNET_UGUI
            DisableCanvases();
#endif
            Clear();
            if (_allocations.Valid)
                _allocations.Dispose();
        }

        void Clear()
        {
#if PURRNET_UGUI
            ClearCanvases();
#endif
#if PURRNET_UITK
            ClearPanels();
#endif
            _elements.Clear();
            _dirty.Clear();
            _hot.Clear();
            _cursor = 0;
            Forget();
        }

        void LateUpdate()
        {
            if (Time.frameCount - _lastRenderFrame > 2)
                Pass();
        }

        void Pass()
        {
            var session = RecordingSession.active;
            int epoch = session != null ? session.sampleEpoch : 0;
            if (epoch == 0)
            {
                if (_epoch != 0)
                    Clear();
                return;
            }

            ulong tick = session.localTick;
            if (tick == _sampledTick && epoch == _epoch)
                return;
            _sampledTick = tick;
            _session = session;

            Marker.Begin();
            if (measureAllocations && !_allocations.Valid)
                _allocations = ProfilerRecorder.StartNew(ProfilerCategory.Memory, "GC Allocated In Frame");
            bool counting = measureAllocations && _allocations.Valid;
            long allocated = counting ? _allocations.CurrentValue : 0;
            try
            {
                Run(epoch);
            }
            catch (System.Exception e)
            {
                session.AddError("ui_exception", e.ToString());
                enabled = false;
            }
            finally
            {
                if (counting)
                {
                    long bytes = System.Math.Max(0, _allocations.CurrentValue - allocated);
                    allocatedBytes += bytes;
                    if (bytes > 0)
                        passesAllocating++;
                    if (bytes > maxAllocatedBytes)
                        maxAllocatedBytes = bytes;
                }
                Marker.End();
            }
        }

        void Run(int epoch)
        {
            long start = Stopwatch.GetTimestamp();
            _pass++;

            bool starting = _epoch == 0;
            bool snapshot = epoch != _epoch;
            _epoch = epoch;

            int width = Screen.width, height = Screen.height;
            if (snapshot || width != _screenWidth || height != _screenHeight)
            {
                _screenWidth = width;
                _screenHeight = height;
                var screen = new UISample { op = UIOp.Screen, width = width, height = height };
                if (snapshot)
                    screen.flags = UIFlags.Snapshot;
                Write(ref screen);
            }

            if (snapshot && !starting)
            {
                for (int i = 0; i < _elements.Count; i++)
                {
                    if (!_elements[i].live)
                        continue;
                    Write(ref _elements[i].last);
                    var contents = _elements[i].contents;
                    if (contents != null)
                        foreach (var item in contents.Values)
                            if (item.live)
                                Write(ref item.last);
                }
            }
#if PURRNET_UGUI
            if (starting)
                UIContentSources.Use(true);
#endif

            if (_exclusions != DontRecordUI.version)
            {
                _exclusions = DontRecordUI.version;
                _recheckAll = true;
#if PURRNET_UGUI
                ExclusionsChanged();
#endif
            }

            bool everything = starting || _recheckAll;
            if (_recheckAll)
            {
                _recheckAll = false;
                for (int i = 0; i < _elements.Count; i++)
                    _elements[i].content = true;
            }

#if PURRNET_UGUI
            DiscoverCanvases();
#endif
#if PURRNET_UITK
            DiscoverPanels();
            RenumberPanels();
#endif
            Drain();

            for (int i = _hot.Count - 1; i >= 0; i--)
            {
                var element = _hot[i];
                if (!element.dropped)
                {
                    if (element.pass == _pass)
                        continue;
                    long before = rows;
                    Capture(element);
                    checks++;
                    if (rows != before)
                        continue;
                }
                element.hot = false;
                _hot[i] = _hot[_hot.Count - 1];
                _hot.RemoveAt(_hot.Count - 1);
            }

            long deadline = everything
                ? long.MaxValue
                : Stopwatch.GetTimestamp() + (long)(_checkBudgetMs * 0.001 * Stopwatch.Frequency);
            int count = _elements.Count;
            for (int visited = 0; visited < count; visited++)
            {
                if (_cursor >= count)
                    _cursor = 0;
                var element = _elements[_cursor];
                if (element.pass != _pass && !element.dropped)
                {
                    long before = rows;
                    Capture(element);
                    checks++;
                    if (rows != before && !element.hot && !element.dropped)
                    {
                        element.hot = true;
                        _hot.Add(element);
                    }
                    if ((checks & 7) == 0 && Stopwatch.GetTimestamp() > deadline)
                        break;
                }
                _cursor++;
            }
            Drain();
#if PURRNET_UITK
            RenumberPanels();
#endif

            if (_anyDropped)
            {
                _anyDropped = false;
                int kept = 0;
                for (int i = 0; i < _elements.Count; i++)
                    if (!_elements[i].dropped)
                        _elements[kept++] = _elements[i];
                _elements.RemoveRange(kept, _elements.Count - kept);
            }

            double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
            passes++;
            totalMs += ms;
            if (ms > maxMs)
                maxMs = ms;
        }

        void Drain()
        {
            for (int i = 0; i < _dirty.Count; i++)
            {
                var element = _dirty[i];
                element.queued = false;
                if (element.pass != _pass && !element.dropped)
                    Capture(element);
            }
            _dirty.Clear();
        }

        void Queue(Element element)
        {
            if (element.queued)
                return;
            element.queued = true;
            _dirty.Add(element);
        }

        Element Add(bool toolkit)
        {
            var element = new Element { id = _nextId++, toolkit = toolkit };
            _elements.Add(element);
            Queue(element);
            return element;
        }

        void Capture(Element element)
        {
            element.pass = _pass;
            // The parent first: an element's row is relative to where its parent is shown, and a parent that moves
            // this pass moves it too. Without this, a child read before its parent would be written twice.
            var parent = element.parent;
            if (parent != null && parent.pass != _pass && !parent.dropped)
                Capture(parent);
#if PURRNET_UITK
            if (element.toolkit)
            {
                CaptureToolkit(element);
                return;
            }
#endif
#if PURRNET_UGUI
            CaptureCanvasElement(element);
#endif
        }

        void CaptureFirst(Element element)
        {
            if (element.pass != _pass && !element.dropped)
                Capture(element);
        }

        void Commit(Element element, ref UISample sample)
        {
            sample.op = UIOp.Update;
            sample.id = element.id;
            sample.parent = element.parent != null ? element.parent.id : 0;
            sample.root = element.root != null ? element.root.id : element.id;
            element.absX = sample.x;
            element.absY = sample.y;
            var parent = element.parent;
            bool relative = parent != null && parent.live && parent.toolkit == element.toolkit;
            if (relative)
            {
                sample.x -= ShownX(parent);
                sample.y -= ShownY(parent);
                sample.flags |= UIFlags.ParentRelative;
            }
            Round(ref sample);
            if (element.live && Same(ref element.last, ref sample))
                return;
            element.last = sample;
            element.shownParent = relative ? parent : null;
            element.live = true;
            Write(ref sample);
        }

        /// <summary>
        /// Geometry rounded to power-of-two grids finer than what counts as a change (a quarter of a pixel): rects to
        /// 1/8192 of the screen, rotation to 1/64 of a degree, world-space canvases to 1/1024 of a unit and 1/16384
        /// per quaternion component. Raw floats carry noise in their last bits that no compressor can take out;
        /// rounded, the UI takes less than half the room. Every value stays exact in a float, so sums of rounded
        /// values (an element placed from its parent) stay on the grid.
        /// </summary>
        static void Round(ref UISample s)
        {
            s.x = Round(s.x, RectGrid);
            s.y = Round(s.y, RectGrid);
            s.width = Round(s.width, RectGrid);
            s.height = Round(s.height, RectGrid);
            s.fontSize = Round(s.fontSize, RectGrid);
            s.fillAmount = Round(s.fillAmount, RectGrid);
            s.rotation = Round(s.rotation, 64f);
            s.worldPosition = new Vector3(Round(s.worldPosition.x, 1024f), Round(s.worldPosition.y, 1024f), Round(s.worldPosition.z, 1024f));
            s.worldRotation = new Quaternion(Round(s.worldRotation.x, 16384f), Round(s.worldRotation.y, 16384f),
                Round(s.worldRotation.z, 16384f), Round(s.worldRotation.w, 16384f));
            s.worldSize = new Vector2(Round(s.worldSize.x, 1024f), Round(s.worldSize.y, 1024f));
        }

        const float RectGrid = 8192f;

        static float Round(float value, float grid) =>
            System.Math.Abs(value) < 16777216f / grid ? (float)((float)System.Math.Round(value * (double)grid) / grid) + 0f : value;

        /// <summary>Where the viewer shows the element: its last row, plus where its parent is shown if it is relative.</summary>
        static float ShownX(Element element)
        {
            float x = 0f;
            for (int depth = 0; element != null && depth < 256; depth++)
            {
                x += element.last.x;
                element = (element.last.flags & UIFlags.ParentRelative) != 0 ? element.shownParent : null;
            }
            return x;
        }

        static float ShownY(Element element)
        {
            float y = 0f;
            for (int depth = 0; element != null && depth < 256; depth++)
            {
                y += element.last.y;
                element = (element.last.flags & UIFlags.ParentRelative) != 0 ? element.shownParent : null;
            }
            return y;
        }

        void Hide(Element element)
        {
#if PURRNET_UGUI
            HideContents(element);
#endif
            if (!element.live)
                return;
            element.live = false;
            var sample = new UISample { op = UIOp.Remove, id = element.id };
            Write(ref sample);
        }

        void Drop(Element element)
        {
            if (element.dropped)
                return;
            Hide(element);
            element.dropped = true;
            _anyDropped = true;
        }

        void Write(ref UISample sample)
        {
            _session.WriteSample(Stream, sample);
            rows++;
        }

        void Forget()
        {
#if PURRNET_UGUI
            if (_epoch != 0)
                UIContentSources.Use(false);
#endif
            _epoch = 0;
            _screenWidth = _screenHeight = 0;
            _sampledTick = ulong.MaxValue;
            for (int i = 0; i < _elements.Count; i++)
            {
                _elements[i].live = false;
                _elements[i].content = true;
                var contents = _elements[i].contents;
                if (contents == null)
                    continue;
                _elements[i].contentsVersion = -1;
                foreach (var item in contents.Values)
                    item.live = false;
            }
        }

        static void SetRect(Element root, Vector2 topLeft, Vector2 topRight, Vector2 bottomLeft, ref UISample sample)
        {
            var across = topRight - topLeft;
            var down = bottomLeft - topLeft;
            float width = across.magnitude, height = down.magnitude;
            float angle = across.y == 0f && across.x >= 0f ? 0f : Mathf.Atan2(across.y, across.x) * Mathf.Rad2Deg;
            if (angle > -0.01f && angle < 0.01f)
                angle = 0f;
            var centre = topLeft + (across + down) * 0.5f;
            var rect = root.rootRect;
            float kx = rect.width > 0f ? root.screenWidth / rect.width : 0f;
            float ky = rect.height > 0f ? root.screenHeight / rect.height : 0f;
            sample.width = width * kx;
            sample.height = height * ky;
            sample.x = root.screenX + centre.x * kx - sample.width * 0.5f;
            sample.y = root.screenY + centre.y * ky - sample.height * 0.5f;
            sample.rotation = angle;
        }

        static UIColor ToColor(Color color, float alpha)
        {
            return new UIColor
            {
                r = ToByte(color.r), g = ToByte(color.g), b = ToByte(color.b), a = ToByte(color.a * alpha)
            };
        }

        static byte ToByte(float value) => (byte)(value <= 0f ? 0 : value >= 1f ? 255 : (int)(value * 255f + 0.5f));

        const float RectTolerancePixels = 0.25f;

        static bool Near(float a, float b, float tolerance)
        {
            float d = a - b;
            return d < tolerance && d > -tolerance;
        }

        bool Same(ref UISample a, ref UISample b)
        {
            float nearX = RectTolerancePixels / Mathf.Max(1, _screenWidth), nearY = RectTolerancePixels / Mathf.Max(1, _screenHeight);
            float nearFill = RectTolerancePixels / Mathf.Max(1f, Mathf.Max(b.width * _screenWidth, b.height * _screenHeight));
            // A turn that moves the element's corners by less than the rect tolerance is no change either (and
            // never less than 0.01 degree): small icons that wobble write far fewer rows.
            float halfPixels = 0.5f * Mathf.Max(1f, Mathf.Max(b.width * _screenWidth, b.height * _screenHeight));
            float nearAngle = Mathf.Max(0.01f, RectTolerancePixels / halfPixels * Mathf.Rad2Deg);
            return a.kind == b.kind && a.widget == b.widget && a.flags == b.flags && a.parent == b.parent &&
                   a.root == b.root && a.order == b.order &&
                   Near(a.x, b.x, nearX) && Near(a.y, b.y, nearY) && Near(a.width, b.width, nearX) && Near(a.height, b.height, nearY) &&
                   Mathf.Abs(a.rotation - b.rotation) < nearAngle &&
                   Same(a.color, b.color) && Same(a.textColor, b.textColor) && Near(a.fontSize, b.fontSize, nearY) &&
                   a.align == b.align && a.fill == b.fill && a.fillOrigin == b.fillOrigin &&
                   Near(a.fillAmount, b.fillAmount, nearFill) && a.value == b.value &&
                   a.worldPosition.x == b.worldPosition.x && a.worldPosition.y == b.worldPosition.y &&
                   a.worldPosition.z == b.worldPosition.z &&
                   a.worldRotation.x == b.worldRotation.x && a.worldRotation.y == b.worldRotation.y &&
                   a.worldRotation.z == b.worldRotation.z && a.worldRotation.w == b.worldRotation.w &&
                   Mathf.Approximately(a.worldSize.x, b.worldSize.x) && Mathf.Approximately(a.worldSize.y, b.worldSize.y) &&
                   Same(a.name, b.name) && Same(a.type, b.type) && Same(a.sprite, b.sprite) && Same(a.text, b.text);
        }

        static bool Same(UIColor a, UIColor b) => a.r == b.r && a.g == b.g && a.b == b.b && a.a == b.a;

        static bool Same(string a, string b) => ReferenceEquals(a, b) || string.Equals(a, b);

        static readonly Dictionary<System.Type, string> TypeNames = new Dictionary<System.Type, string>();

        static string TypeName(System.Type type)
        {
            if (!TypeNames.TryGetValue(type, out var name))
                TypeNames[type] = name = type.Name;
            return name;
        }

        sealed class ReferenceComparer<T> : IEqualityComparer<T> where T : class
        {
            public static readonly ReferenceComparer<T> instance = new ReferenceComparer<T>();
            public bool Equals(T a, T b) => ReferenceEquals(a, b);
            public int GetHashCode(T value) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(value);
        }
    }
}
