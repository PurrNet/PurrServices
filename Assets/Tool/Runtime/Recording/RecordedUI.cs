using System;
using UnityEngine;

namespace PurrNet.Recording
{
    public enum UIOp : byte
    {
        Update,
        Remove,
        Screen
    }

    public enum UIKind : byte
    {
        Element,
        Canvas,
        Panel,
        Image,
        RawImage,
        Text
    }

    public enum UIWidget : byte
    {
        None,
        Button,
        Toggle,
        Slider,
        Scrollbar,
        Dropdown,
        InputField,
        ProgressBar,
        Other
    }

    [Flags]
    public enum UIFlags : ushort
    {
        Visible = 1,
        Interactable = 2,
        Pickable = 4,
        WorldSpace = 8,
        Redacted = 16,
        Snapshot = 32,
        UIToolkit = 64,
        /// <summary>
        /// x and y are from the parent's x and y (as shown: the parent's own may be from its parent), not from the
        /// root. A panel that slides or sways then writes one row, not one for every element in it.
        /// </summary>
        ParentRelative = 128
    }

    public enum UIAlign : byte
    {
        UpperLeft,
        UpperCenter,
        UpperRight,
        MiddleLeft,
        MiddleCenter,
        MiddleRight,
        LowerLeft,
        LowerCenter,
        LowerRight
    }

    public enum UIFill : byte
    {
        None,
        Horizontal,
        Vertical,
        Radial
    }

    public struct UIColor
    {
        public byte r, g, b, a;
    }

    public struct UISample
    {
        public UIOp op;
        public UIKind kind;
        public UIWidget widget;
        public UIFlags flags;
        [RecordKey] public int id;
        public int parent;
        public int root;
        public int order;
        public float x, y, width, height;
        public float rotation;
        public UIColor color;
        public UIColor textColor;
        public float fontSize;
        public UIAlign align;
        public UIFill fill;
        public byte fillOrigin;
        public float fillAmount;
        public float value;
        public Vector3 worldPosition;
        public Quaternion worldRotation;
        public Vector2 worldSize;
        public string name;
        public string type;
        public string sprite;
        public string text;
    }
}
