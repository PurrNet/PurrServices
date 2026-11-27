#if PURRNET_INPUT_SYSTEM && ENABLE_INPUT_SYSTEM
#define PURRCAP_INPUT_SYSTEM
#elif ENABLE_LEGACY_INPUT_MANAGER
#define PURRCAP_LEGACY_INPUT
#endif
using System;
using UnityEngine;
#if PURRCAP_INPUT_SYSTEM
using System.Collections.Generic;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
#endif
#if PURRNET_UITK && !PURRCAP_INPUT_SYSTEM
using System.Collections.Generic;
#endif
#if PURRNET_UGUI
using UnityEngine.EventSystems;
#endif
#if PURRNET_UITK
using UnityEngine.UIElements;
#endif

namespace PurrNet.Services.Capture
{
    /// <summary>
    /// What the player's devices did, gathered every frame and written once per tick to the "input" stream
    /// (pointer movement, scroll, mouse buttons, held keys, the gamepad) and the "actions" stream (Input System
    /// actions by name). A key or button pressed and released between two ticks is still in the next row. A row is
    /// only written when it differs from the one before. While a text field has keyboard focus no keys are
    /// gathered; the row says the player was typing instead.
    /// </summary>
    internal sealed class CaptureInput
    {
        public const ushort Typing = 1, HasKeyboard = 2, HasPointer = 4, HasGamepad = 8, Focused = 16,
            FromInputSystem = 32, FromLegacyInput = 64, CursorLocked = 128, CursorConfined = 256, CursorHidden = 512;

        public const byte ActionPerformed = 1, ActionDisabled = 2;

        public static readonly PartColumn[] InputColumns =
        {
            PartColumn.Bits("flags"), PartColumn.Bits("mouseButtons"),
            new PartColumn("mouseDeltaX", 16), new PartColumn("mouseDeltaY", 16),
            new PartColumn("pointerX", 16384), new PartColumn("pointerY", 16384),
            new PartColumn("scrollX", 16), new PartColumn("scrollY", 16),
            PartColumn.Bits("keys0"), PartColumn.Bits("keys1"),
            new PartColumn("leftStickX", 16384), new PartColumn("leftStickY", 16384),
            new PartColumn("rightStickX", 16384), new PartColumn("rightStickY", 16384),
            new PartColumn("leftTrigger", 16384), new PartColumn("rightTrigger", 16384),
            PartColumn.Bits("gamepadButtons"), PartColumn.Bits("joystickButtons")
        };

        public static readonly PartColumn[] ActionColumns =
        {
            new PartColumn("id"), PartColumn.Bits("phase"), PartColumn.Bits("flags"),
            new PartColumn("x", 1024), new PartColumn("y", 1024), new PartColumn("z", 1024)
        };

#if PURRCAP_INPUT_SYSTEM
        public const string Backend = "inputSystem";
#elif PURRCAP_LEGACY_INPUT
        public const string Backend = "legacy";
#else
        public const string Backend = "none";
#endif

        ushort _flags;
        byte _mouseButtons;
        Vector2 _mouseDelta, _pointer, _scroll;
        ulong _keys0, _keys1;
        Vector2 _leftStick, _rightStick;
        float _leftTrigger, _rightTrigger;
        uint _gamepadButtons, _joystickButtons;
        bool _typing;
        bool _listening;
        int _file;

        readonly long[] _row = new long[18];
        readonly long[] _actionRow = new long[6];

        /// <summary>Starts a new file: every action is declared and written again in it.</summary>
        public void Begin()
        {
            _file++;
            Clear();
#if PURRCAP_INPUT_SYSTEM
            if (!_listening)
            {
                InputSystem.onActionChange += OnActionChange;
                _listening = true;
            }
#endif
        }

        public void End()
        {
#if PURRCAP_INPUT_SYSTEM
            if (_listening)
            {
                InputSystem.onActionChange -= OnActionChange;
                _listening = false;
            }
#endif
            Clear();
        }

        void Clear()
        {
            _flags = 0;
            _mouseButtons = 0;
            _mouseDelta = _pointer = _scroll = default;
            _keys0 = _keys1 = 0;
            _leftStick = _rightStick = default;
            _leftTrigger = _rightTrigger = 0;
            _gamepadButtons = _joystickButtons = 0;
            _typing = false;
        }

        static long Fixed(float value, double scale)
        {
            double scaled = value * scale;
            if (double.IsNaN(scaled) || scaled > 4e15 || scaled < -4e15)
                return 0;
            return (long)Math.Round(scaled);
        }

        /// <summary>Writes this tick's rows and starts gathering the next tick's.</summary>
        public void WriteTick(PartWriter writer, int inputStream, int actionsStream, long tick)
        {
#if PURRCAP_INPUT_SYSTEM
            PollActions();
#endif
            if (_typing)
            {
                _keys0 = _keys1 = 0;
                _flags |= Typing;
            }
            if (Application.isFocused)
                _flags |= Focused;
            var lockState = UnityEngine.Cursor.lockState;
            if (lockState == CursorLockMode.Locked)
                _flags |= CursorLocked;
            else if (lockState == CursorLockMode.Confined)
                _flags |= CursorConfined;
            if (!UnityEngine.Cursor.visible)
                _flags |= CursorHidden;

            var row = _row;
            row[0] = _flags;
            row[1] = _mouseButtons;
            row[2] = Fixed(_mouseDelta.x, 16);
            row[3] = Fixed(_mouseDelta.y, 16);
            row[4] = Fixed(_pointer.x, 16384);
            row[5] = Fixed(_pointer.y, 16384);
            row[6] = Fixed(_scroll.x, 16);
            row[7] = Fixed(_scroll.y, 16);
            row[8] = unchecked((long)_keys0);
            row[9] = unchecked((long)_keys1);
            row[10] = Fixed(_leftStick.x, 16384);
            row[11] = Fixed(_leftStick.y, 16384);
            row[12] = Fixed(_rightStick.x, 16384);
            row[13] = Fixed(_rightStick.y, 16384);
            row[14] = Fixed(_leftTrigger, 16384);
            row[15] = Fixed(_rightTrigger, 16384);
            row[16] = _gamepadButtons;
            row[17] = _joystickButtons;
            writer.WriteChanged(inputStream, tick, row);
#if PURRCAP_INPUT_SYSTEM
            WriteActions(writer, actionsStream, tick);
#endif
            Clear();
        }

        void SetKey(int key)
        {
            if (key < 64)
                _keys0 |= 1ul << key;
            else if (key < 128)
                _keys1 |= 1ul << (key - 64);
        }

#if PURRCAP_INPUT_SYSTEM
        static readonly GamepadButton[] GamepadButtons =
        {
            GamepadButton.DpadUp, GamepadButton.DpadDown, GamepadButton.DpadLeft, GamepadButton.DpadRight,
            GamepadButton.North, GamepadButton.East, GamepadButton.South, GamepadButton.West,
            GamepadButton.LeftStick, GamepadButton.RightStick, GamepadButton.LeftShoulder, GamepadButton.RightShoulder,
            GamepadButton.Start, GamepadButton.Select, GamepadButton.LeftTrigger, GamepadButton.RightTrigger
        };

        /// <summary>Reads the devices. Called every frame.</summary>
        public void Poll()
        {
            _flags |= FromInputSystem;

            var keyboard = Keyboard.current;
            if (keyboard != null)
            {
                _flags |= HasKeyboard;
                bool active = keyboard.wasUpdatedThisFrame || keyboard.anyKey.isPressed;
                if (IsTyping(active))
                    _typing = true;
                else if (active)
                {
                    var keys = keyboard.allKeys;
                    for (int i = 0; i < keys.Count; i++)
                    {
                        var key = keys[i];
                        if (key != null && (key.isPressed || key.wasPressedThisFrame))
                            SetKey((int)key.keyCode);
                    }
                }
            }

            var mouse = Mouse.current;
            var pointer = (Pointer)mouse ?? Pointer.current;
            if (pointer != null)
            {
                _flags |= HasPointer;
                _mouseDelta += pointer.delta.ReadValue();
                SetPointer(pointer.position.ReadValue());
                if (mouse != null)
                {
                    _scroll += mouse.scroll.ReadValue();
                    Button(mouse.leftButton, 0);
                    Button(mouse.rightButton, 1);
                    Button(mouse.middleButton, 2);
                    Button(mouse.backButton, 3);
                    Button(mouse.forwardButton, 4);
                }
                else
                    Button(pointer.press, 0);
            }

            var pad = Gamepad.current;
            if (pad != null)
            {
                _flags |= HasGamepad;
                _leftStick = pad.leftStick.ReadValue();
                _rightStick = pad.rightStick.ReadValue();
                _leftTrigger = pad.leftTrigger.ReadValue();
                _rightTrigger = pad.rightTrigger.ReadValue();
                for (int i = 0; i < GamepadButtons.Length; i++)
                {
                    var button = pad[GamepadButtons[i]];
                    if (button.isPressed || button.wasPressedThisFrame)
                        _gamepadButtons |= 1u << i;
                }
            }

            AccumulateActions();
        }

        void Button(UnityEngine.InputSystem.Controls.ButtonControl button, int bit)
        {
            if (button.isPressed || button.wasPressedThisFrame)
                _mouseButtons |= (byte)(1 << bit);
        }

        sealed class ActionState
        {
            public int id;
            public InputAction action;
            public string name, type;
            public byte kind;
            public bool unreadable;
            public int file;
            public int frame;
            public Vector3 value, sum, written;
            public bool accumulates;
            public bool summed;
            public int summedFrame = -1;
            public byte phase, writtenPhase;
            public bool performed;
        }

        readonly Dictionary<InputAction, ActionState> _actions = new Dictionary<InputAction, ActionState>();
        readonly List<ActionState> _actionList = new List<ActionState>();
        readonly List<InputAction> _enabledActions = new List<InputAction>();
        readonly List<ActionState> _summedActions = new List<ActionState>();

        ActionState StateOf(InputAction action)
        {
            if (_actions.TryGetValue(action, out var state))
                return state;
            var map = action.actionMap;
            state = new ActionState
            {
                id = _actionList.Count + 1,
                name = map != null && !string.IsNullOrEmpty(map.name) ? map.name + "/" + action.name : action.name,
                type = action.expectedControlType ?? "",
                kind = ValueKind(action.expectedControlType)
            };
            _actions[action] = state;
            _actionList.Add(state);
            return state;
        }

        void OnActionChange(object changed, InputActionChange change)
        {
            if (change == InputActionChange.ActionPerformed && changed is InputAction action)
                StateOf(action).performed = true;
        }

        static bool ReadAction(InputAction action, ActionState state, out Vector3 value)
        {
            value = default;
            if (state.unreadable || state.kind == 0)
                return false;
            try
            {
                switch (state.kind)
                {
                    case 1: value.x = action.ReadValue<float>(); break;
                    case 2: value = action.ReadValue<Vector2>(); break;
                    default: value = action.ReadValue<Vector3>(); break;
                }
                return true;
            }
            catch (Exception)
            {
                state.unreadable = true;
                return false;
            }
        }

        /// <summary>
        /// Actions bound to a delta (mouse movement) are summed every frame, so a tick's row holds all the movement
        /// since the last one; every other action is read once per tick.
        /// </summary>
        void AccumulateActions()
        {
            if (_typing)
                return;
            int frame = Time.frameCount;
            for (int i = 0; i < _summedActions.Count; i++)
            {
                var state = _summedActions[i];
                if (ReadAction(state.action, state, out var value))
                    state.sum += value;
                state.summedFrame = frame;
            }
        }

        static byte ValueKind(string controlType)
        {
            switch (controlType)
            {
                case "Button":
                case "Axis":
                case "Analog":
                case "Key":
                    return 1;
                case "Vector2":
                case "Stick":
                case "Dpad":
                case "Delta":
                    return 2;
                case "Vector3":
                    return 3;
                default:
                    return 0;
            }
        }

        void PollActions()
        {
            _enabledActions.Clear();
            InputSystem.ListEnabledActions(_enabledActions);
            int frame = Time.frameCount;
            for (int i = 0; i < _enabledActions.Count; i++)
            {
                var action = _enabledActions[i];
                var state = StateOf(action);

                state.frame = frame;
                if (_typing)
                {
                    state.value = default;
                    state.phase = (byte)InputActionPhase.Waiting;
                    continue;
                }

                ReadAction(action, state, out var value);
                var control = action.activeControl;
                state.accumulates = control != null && control.name == "delta";
                if (state.accumulates && !state.summed)
                {
                    state.summed = true;
                    state.action = action;
                    _summedActions.Add(state);
                }
                state.value = value;
                if (state.summedFrame != frame)
                    state.sum += value;
                state.phase = (byte)action.phase;
                state.performed |= action.triggered;
            }
        }

        void WriteActions(PartWriter writer, int stream, long tick)
        {
            int frame = Time.frameCount;
            var row = _actionRow;
            for (int i = 0; i < _actionList.Count; i++)
            {
                var state = _actionList[i];
                bool enabled = state.frame == frame;
                var value = !enabled ? default : state.accumulates ? state.sum : state.value;
                byte phase = enabled ? state.phase : (byte)InputActionPhase.Disabled;
                bool first = state.file != _file;
                state.sum = default;

                if (first)
                {
                    if (!enabled)
                    {
                        state.performed = false;
                        continue;
                    }
                    state.file = _file;
                    writer.WriteNote(new PartJson().BeginObject().Key("action").BeginObject()
                        .Prop("id", state.id).Prop("name", state.name).Prop("type", state.type)
                        .EndObject().EndObject().ToString());
                }
                else if (!state.performed && phase == state.writtenPhase && value == state.written)
                    continue;

                byte flags = state.performed ? ActionPerformed : (byte)0;
                if (!enabled)
                    flags |= ActionDisabled;
                row[0] = state.id;
                row[1] = phase;
                row[2] = flags;
                row[3] = Fixed(value.x, 1024);
                row[4] = Fixed(value.y, 1024);
                row[5] = Fixed(value.z, 1024);
                writer.WriteRow(stream, tick, row);
                state.written = value;
                state.writtenPhase = phase;
                state.performed = false;
            }
        }
#elif PURRCAP_LEGACY_INPUT
        static readonly (KeyCode code, byte key)[] Keys =
        {
            (KeyCode.Space, 1), (KeyCode.Return, 2), (KeyCode.Tab, 3), (KeyCode.BackQuote, 4), (KeyCode.Quote, 5),
            (KeyCode.Semicolon, 6), (KeyCode.Comma, 7), (KeyCode.Period, 8), (KeyCode.Slash, 9), (KeyCode.Backslash, 10),
            (KeyCode.LeftBracket, 11), (KeyCode.RightBracket, 12), (KeyCode.Minus, 13), (KeyCode.Equals, 14),
            (KeyCode.A, 15), (KeyCode.B, 16), (KeyCode.C, 17), (KeyCode.D, 18), (KeyCode.E, 19), (KeyCode.F, 20),
            (KeyCode.G, 21), (KeyCode.H, 22), (KeyCode.I, 23), (KeyCode.J, 24), (KeyCode.K, 25), (KeyCode.L, 26),
            (KeyCode.M, 27), (KeyCode.N, 28), (KeyCode.O, 29), (KeyCode.P, 30), (KeyCode.Q, 31), (KeyCode.R, 32),
            (KeyCode.S, 33), (KeyCode.T, 34), (KeyCode.U, 35), (KeyCode.V, 36), (KeyCode.W, 37), (KeyCode.X, 38),
            (KeyCode.Y, 39), (KeyCode.Z, 40),
            (KeyCode.Alpha1, 41), (KeyCode.Alpha2, 42), (KeyCode.Alpha3, 43), (KeyCode.Alpha4, 44), (KeyCode.Alpha5, 45),
            (KeyCode.Alpha6, 46), (KeyCode.Alpha7, 47), (KeyCode.Alpha8, 48), (KeyCode.Alpha9, 49), (KeyCode.Alpha0, 50),
            (KeyCode.LeftShift, 51), (KeyCode.RightShift, 52), (KeyCode.LeftAlt, 53), (KeyCode.RightAlt, 54),
            (KeyCode.AltGr, 54), (KeyCode.LeftControl, 55), (KeyCode.RightControl, 56),
            (KeyCode.LeftCommand, 57), (KeyCode.LeftWindows, 57), (KeyCode.RightCommand, 58), (KeyCode.RightWindows, 58),
            (KeyCode.Menu, 59), (KeyCode.Escape, 60), (KeyCode.LeftArrow, 61), (KeyCode.RightArrow, 62),
            (KeyCode.UpArrow, 63), (KeyCode.DownArrow, 64), (KeyCode.Backspace, 65), (KeyCode.PageDown, 66),
            (KeyCode.PageUp, 67), (KeyCode.Home, 68), (KeyCode.End, 69), (KeyCode.Insert, 70), (KeyCode.Delete, 71),
            (KeyCode.CapsLock, 72), (KeyCode.Numlock, 73), (KeyCode.Print, 74), (KeyCode.ScrollLock, 75),
            (KeyCode.Pause, 76), (KeyCode.KeypadEnter, 77), (KeyCode.KeypadDivide, 78), (KeyCode.KeypadMultiply, 79),
            (KeyCode.KeypadPlus, 80), (KeyCode.KeypadMinus, 81), (KeyCode.KeypadPeriod, 82), (KeyCode.KeypadEquals, 83),
            (KeyCode.Keypad0, 84), (KeyCode.Keypad1, 85), (KeyCode.Keypad2, 86), (KeyCode.Keypad3, 87),
            (KeyCode.Keypad4, 88), (KeyCode.Keypad5, 89), (KeyCode.Keypad6, 90), (KeyCode.Keypad7, 91),
            (KeyCode.Keypad8, 92), (KeyCode.Keypad9, 93),
            (KeyCode.F1, 94), (KeyCode.F2, 95), (KeyCode.F3, 96), (KeyCode.F4, 97), (KeyCode.F5, 98), (KeyCode.F6, 99),
            (KeyCode.F7, 100), (KeyCode.F8, 101), (KeyCode.F9, 102), (KeyCode.F10, 103), (KeyCode.F11, 104),
            (KeyCode.F12, 105), (KeyCode.F13, 112), (KeyCode.F14, 113), (KeyCode.F15, 114)
        };

        const int MouseButtons = 5, JoystickButtons = 20;
#if !UNITY_6000_0_OR_NEWER
        Vector3 _lastMouse;
        bool _hasLastMouse;
#endif

        /// <summary>Reads the devices. Called every frame.</summary>
        public void Poll()
        {
            _flags |= FromLegacyInput | HasKeyboard;

            bool active = Input.anyKey || Input.anyKeyDown;
            if (IsTyping(active))
                _typing = true;
            else if (active)
            {
                for (int i = 0; i < Keys.Length; i++)
                {
                    var code = Keys[i].code;
                    if (Input.GetKey(code) || Input.GetKeyDown(code))
                        SetKey(Keys[i].key);
                }
            }

            if (active)
            {
                for (int i = 0; i < JoystickButtons; i++)
                {
                    var code = KeyCode.JoystickButton0 + i;
                    if (Input.GetKey(code) || Input.GetKeyDown(code))
                        _joystickButtons |= 1u << i;
                }
            }

            if (!Input.mousePresent)
                return;
            _flags |= HasPointer;
            var position = Input.mousePosition;
#if UNITY_6000_0_OR_NEWER
            _mouseDelta += (Vector2)Input.mousePositionDelta;
#else
            if (_hasLastMouse)
                _mouseDelta += (Vector2)(position - _lastMouse);
            _lastMouse = position;
            _hasLastMouse = true;
#endif
            _scroll += Input.mouseScrollDelta;
            SetPointer(position);
            if (active)
            {
                for (int i = 0; i < MouseButtons; i++)
                    if (Input.GetMouseButton(i) || Input.GetMouseButtonDown(i))
                        _mouseButtons |= (byte)(1 << i);
            }
        }
#else
        /// <summary>Reads the devices. Called every frame.</summary>
        public void Poll()
        {
            if (IsTyping(false))
                _typing = true;
        }
#endif

        void SetPointer(Vector2 position)
        {
            int width = Screen.width, height = Screen.height;
            _pointer = width > 0 && height > 0 ? new Vector2(position.x / width, position.y / height) : default;
        }

#if PURRNET_UITK
        readonly List<UIDocument> _documents = new List<UIDocument>();
        float _documentsAt = float.NegativeInfinity;
#endif

        bool IsTyping(bool keysActive)
        {
#if PURRNET_UGUI
            var events = EventSystem.current;
            if (events)
            {
                var selected = events.currentSelectedGameObject;
                if (selected)
                {
                    if (selected.TryGetComponent<UnityEngine.UI.InputField>(out var field) && field.isFocused)
                        return true;
#if PURRNET_TMP
                    if (selected.TryGetComponent<TMPro.TMP_InputField>(out var tmp) && tmp.isFocused)
                        return true;
#endif
                }
            }
#endif
#if PURRNET_IMGUI
            if (GUIUtility.keyboardControl != 0)
                return true;
#endif
            if (TouchScreenKeyboard.visible)
                return true;
#if PURRNET_UITK
            if (keysActive && Time.unscaledTime - _documentsAt > 1f)
            {
                _documentsAt = Time.unscaledTime;
                _documents.Clear();
                _documents.AddRange(Resources.FindObjectsOfTypeAll<UIDocument>());
            }
            for (int i = 0; i < _documents.Count; i++)
            {
                var document = _documents[i];
                if (!document || !document.isActiveAndEnabled)
                    continue;
                var focused = document.rootVisualElement?.panel?.focusController?.focusedElement as VisualElement;
                for (; focused != null; focused = focused.hierarchy.parent)
                {
                    if (focused is TextInputBaseField<string> || focused is TextInputBaseField<int> ||
                        focused is TextInputBaseField<long> || focused is TextInputBaseField<float> ||
                        focused is TextInputBaseField<double>)
                        return true;
                }
            }
#endif
            return false;
        }
    }
}
