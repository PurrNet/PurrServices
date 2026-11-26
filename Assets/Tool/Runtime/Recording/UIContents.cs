#if PURRNET_UGUI
using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace PurrNet.Recording
{
    /// <summary>
    /// One thing a graphic that draws a whole UI by itself (an immediate-mode UI such as NowUI) showed: a control, a
    /// rectangle, a text run or an image. Recorded as an element of its own under the graphic.
    /// </summary>
    public struct UIContentItem
    {
        /// <summary>Stable from one frame to the next: an item with the same id is the same element.</summary>
        public ulong id;

        /// <summary>Index in the same contents of the item this one is inside, or -1 for the graphic itself.</summary>
        public int parent;

        public UIKind kind;
        public UIWidget widget;

        /// <summary>
        /// Where it is within the graphic: top-left origin, y down, in the units of
        /// <see cref="UIContents.size"/> (the graphic's own rect is (0, 0, size)).
        /// </summary>
        public Rect rect;

        /// <summary>Degrees, clockwise on screen, about the centre of <see cref="rect"/>.</summary>
        public float rotation;

        /// <summary>Fill colour, or the text colour for text.</summary>
        public Color color;

        /// <summary>Text size in the units of <see cref="UIContents.size"/>.</summary>
        public float fontSize;

        /// <summary>Normalized slider or progress value, 1 or 0 for toggles.</summary>
        public float value;

        public string name;
        public string type;
        public string text;
        public string sprite;
        public bool interactable;

        /// <summary>Text the player types into: left out when the recorder redacts typed text.</summary>
        public bool editable;

        /// <summary>A password or other masked field: its text is never recorded.</summary>
        public bool secret;
    }

    /// <summary>What such a graphic showed last, kept by its integration until the next frame it draws.</summary>
    public sealed class UIContents
    {
        public Vector2 size;
        public readonly List<UIContentItem> items = new List<UIContentItem>();

        /// <summary>Changes whenever the items do; the recorder reads them again only then.</summary>
        public int version;
    }

    /// <summary>
    /// Where the UI recorder learns what graphics that draw a whole UI by themselves show. An integration (see the
    /// NowUI one) registers a source; the recorder asks it for every graphic it records, and the integration tells
    /// it with <see cref="Changed"/> when a graphic drew something new.
    /// </summary>
    public static class UIContentSources
    {
        static readonly List<Func<Graphic, UIContents>> Sources = new List<Func<Graphic, UIContents>>();
        static readonly List<Action<bool>> Switches = new List<Action<bool>>();
        static int _users;

        internal static Action<Graphic> onChanged;

        /// <summary>Whether any source is registered.</summary>
        public static bool any => Sources.Count > 0;

        /// <summary>Whether a recorder wants contents now. Integrations collect only then.</summary>
        public static bool active => _users > 0;

        /// <summary>
        /// Adds a source: <paramref name="read"/> gives a graphic's latest contents, or null if it is not one of
        /// its graphics. <paramref name="setActive"/> is told when recorders start and stop wanting contents.
        /// </summary>
        public static void Register(Func<Graphic, UIContents> read, Action<bool> setActive = null)
        {
            if (read == null || Sources.Contains(read))
                return;
            Sources.Add(read);
            if (setActive != null)
            {
                Switches.Add(setActive);
                if (_users > 0)
                    setActive(true);
            }
        }

        public static void Unregister(Func<Graphic, UIContents> read, Action<bool> setActive = null)
        {
            Sources.Remove(read);
            if (setActive != null && Switches.Remove(setActive) && _users > 0)
                setActive(false);
        }

        /// <summary>A graphic drew something new: recorders read its contents on their next pass.</summary>
        public static void Changed(Graphic graphic) => onChanged?.Invoke(graphic);

        internal static UIContents Read(Graphic graphic)
        {
            for (int i = 0; i < Sources.Count; i++)
            {
                var contents = Sources[i](graphic);
                if (contents != null)
                    return contents;
            }
            return null;
        }

        internal static void Use(bool used)
        {
            int before = _users;
            _users = Math.Max(0, _users + (used ? 1 : -1));
            if ((before > 0) == (_users > 0))
                return;
            for (int i = 0; i < Switches.Count; i++)
            {
                try
                {
                    Switches[i](_users > 0);
                }
                catch (Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void ResetUsers()
        {
            _users = 0;
            onChanged = null;
        }
    }
}
#endif
