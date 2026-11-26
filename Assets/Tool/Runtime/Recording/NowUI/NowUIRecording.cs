using System.Collections.Generic;
using NowUI;
using UnityEngine;
using UnityEngine.UI;

namespace PurrNet.Recording
{
    /// <summary>
    /// Records what NowUI draws. A NowUI host on a canvas (NowGraphic, NowLayoutGraphic) draws its whole UI into one
    /// mesh, so the UI recorder would see one box; NowUI's capture API reports its controls, rectangles, text and
    /// images instead, and each becomes a recorded element under the host. NowUI collects them only while a UI
    /// recorder is recording.
    /// </summary>
    static class NowUIRecording
    {
        static readonly Dictionary<Graphic, UIContents> Latest = new Dictionary<Graphic, UIContents>();
        static readonly List<Graphic> Gone = new List<Graphic>();
        static bool _listening;
        static int _frames;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterAssembliesLoaded)]
        static void Register()
        {
            SetActive(false);
            UIContentSources.Unregister(Read, SetActive);
            UIContentSources.Register(Read, SetActive);
        }

        static UIContents Read(Graphic graphic) =>
            graphic is not null && Latest.TryGetValue(graphic, out var contents) ? contents : null;

        static void SetActive(bool active)
        {
            if (active == _listening)
                return;
            _listening = active;
            if (active)
                NowCapture.onFrame += OnFrame;
            else
            {
                NowCapture.onFrame -= OnFrame;
                Latest.Clear();
            }
        }

        static void OnFrame(NowCaptureFrame frame)
        {
            if (frame.host is not Graphic graphic || graphic == null)
                return;
            if (!Latest.TryGetValue(graphic, out var contents))
                Latest[graphic] = contents = new UIContents();

            var bounds = frame.bounds;
            contents.size = new Vector2(bounds.width, bounds.height);
            var items = contents.items;
            items.Clear();
            for (int i = 0; i < frame.count; i++)
            {
                ref readonly var item = ref frame[i];
                items.Add(Convert(in item, bounds));
            }
            contents.version++;
            UIContentSources.Changed(graphic);

            if ((++_frames & 255) == 0)
                Prune();
        }

        static UIContentItem Convert(in NowCaptureItem item, NowRect bounds)
        {
            var rect = new Rect(item.rect.x - bounds.x, item.rect.y - bounds.y, item.rect.width, item.rect.height);
            if (item.kind == NowCaptureKind.Rect || item.kind == NowCaptureKind.Image)
            {
                // What a clip hides is not shown; text is kept whole, a clipped word is still that word.
                var clip = new Rect(item.clip.x - bounds.x, item.clip.y - bounds.y, item.clip.width, item.clip.height);
                float xMin = Mathf.Max(rect.xMin, clip.xMin), yMin = Mathf.Max(rect.yMin, clip.yMin);
                float xMax = Mathf.Min(rect.xMax, clip.xMax), yMax = Mathf.Min(rect.yMax, clip.yMax);
                if (xMax > xMin && yMax > yMin && item.rotation == 0f)
                    rect = Rect.MinMaxRect(xMin, yMin, xMax, yMax);
            }

            bool control = item.kind == NowCaptureKind.Control;
            string kind = control ? item.control ?? "Control" : KindName(item.kind);
            return new UIContentItem
            {
                id = item.id,
                parent = item.parent,
                kind = item.kind switch
                {
                    NowCaptureKind.Text => UIKind.Text,
                    NowCaptureKind.Rect => UIKind.Image,
                    NowCaptureKind.Image => UIKind.RawImage,
                    _ => UIKind.Element
                },
                widget = control ? Widget(item.control) : UIWidget.None,
                rect = rect,
                rotation = item.rotation,
                color = item.color,
                fontSize = item.fontSize,
                value = item.value,
                name = kind,
                type = "NowUI." + kind,
                text = (item.flags & NowCaptureFlags.Secret) != 0 ? null : item.text,
                sprite = item.texture,
                interactable = control && (item.flags & NowCaptureFlags.Disabled) == 0,
                editable = (item.flags & NowCaptureFlags.Editable) != 0,
                secret = (item.flags & NowCaptureFlags.Secret) != 0
            };
        }

        static string KindName(NowCaptureKind kind) => kind switch
        {
            NowCaptureKind.Rect => "Rect",
            NowCaptureKind.Text => "Text",
            NowCaptureKind.Image => "Image",
            _ => "Control"
        };

        static UIWidget Widget(string control) => control switch
        {
            "Button" or "MenuItem" or "Chip" or "Tab" or "DropdownItem" => UIWidget.Button,
            "Toggle" or "Radio" or "Switch" or "Foldout" => UIWidget.Toggle,
            "Slider" or "Splitter" => UIWidget.Slider,
            "Scrollbar" or "ScrollView" => UIWidget.Scrollbar,
            "Dropdown" or "ComboBox" or "MaskField" => UIWidget.Dropdown,
            "TextField" or "NumberField" or "TextArea" or "CodeEditor" or "KeyBindingField" => UIWidget.InputField,
            "ProgressBar" => UIWidget.ProgressBar,
            _ => UIWidget.Other
        };

        /// <summary>Hosts that were destroyed are let go of.</summary>
        static void Prune()
        {
            Gone.Clear();
            foreach (var graphic in Latest.Keys)
                if (graphic == null)
                    Gone.Add(graphic);
            for (int i = 0; i < Gone.Count; i++)
                Latest.Remove(Gone[i]);
        }
    }
}
