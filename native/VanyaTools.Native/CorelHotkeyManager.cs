using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace VanyaTools.Native
{
    internal sealed class CorelHotkeyManager
    {
        private const int WmHotkey = 0x0312;
        private const int FirstId = 0x7100;
        private const uint Alt = 0x0001;
        private const uint Control = 0x0002;
        private const uint Shift = 0x0004;
        private const uint NoRepeat = 0x4000;

        private readonly Action[] _actions;
        private readonly Action<string, bool> _status;
        private readonly Hotkey[] _bindings = new Hotkey[2];
        private readonly bool[] _registered = new bool[2];
        private readonly bool[] _warned = new bool[2];
        private readonly TextBox[] _fields = new TextBox[2];
        private readonly TextBlock _message;
        private readonly DispatcherTimer _timer;
        private readonly string _settingsPath;
        private FrameworkElement _host;
        private HwndSource _source;
        private bool _capturing;

        internal FrameworkElement SettingsView { get; }

        internal CorelHotkeyManager(Action trim, Action fitFrame, Action<string, bool> status)
        {
            _actions = new[] { trim, fitFrame };
            _status = status;
            _settingsPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VanyaTools", "hotkeys.json");
            Load();

            var panel = new StackPanel { Margin = new Thickness(4, 7, 4, 7) };
            panel.Children.Add(new TextBlock
            {
                Text = "Горячие клавиши",
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 5)
            });
            AddRow(panel, 0, "Обрезать растр");
            AddRow(panel, 1, "Подогнать рамку");
            panel.Children.Add(new TextBlock
            {
                Text = "Нажмите в поле Ctrl+Alt+клавишу. Delete — убрать. Работает при открытом докере CorelDRAW.",
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            });
            _message = new TextBlock
            {
                FontSize = 10,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0)
            };
            panel.Children.Add(_message);
            SettingsView = panel;

            _timer = new DispatcherTimer(DispatcherPriority.Background)
            {
                Interval = TimeSpan.FromMilliseconds(300)
            };
            _timer.Tick += (_, __) => SafePoll();
        }

        internal void Attach(FrameworkElement host)
        {
            _host = host;
            SafePoll();
            _timer.Start();
        }

        internal void Detach()
        {
            _timer.Stop();
            UnregisterAll();
            if (_source != null)
            {
                try { _source.RemoveHook(OnWindowMessage); }
                catch (Exception error) { Log.Error("Could not remove hotkey window hook.", error); }
            }
            _source = null;
            _host = null;
        }

        private void AddRow(Panel parent, int index, string label)
        {
            var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
            row.Children.Add(new TextBlock
            {
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11
            });
            var field = new TextBox
            {
                IsReadOnly = true,
                Text = _bindings[index].Display,
                FontSize = 11,
                Padding = new Thickness(4, 2, 4, 2),
                ToolTip = "Нажмите Ctrl+Alt+клавишу; Delete удалит сочетание."
            };
            field.GotKeyboardFocus += (_, __) =>
            {
                _capturing = true;
                UnregisterAll();
                ShowMessage("Нажмите новое сочетание.", false);
            };
            field.LostKeyboardFocus += (_, __) =>
            {
                _capturing = false;
                SafePoll();
            };
            field.PreviewKeyDown += (_, e) => Capture(index, e);
            _fields[index] = field;
            Grid.SetColumn(field, 1);
            row.Children.Add(field);
            parent.Children.Add(row);
        }

        private void Capture(int index, KeyEventArgs e)
        {
            e.Handled = true;
            Key key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Escape)
            {
                Keyboard.ClearFocus();
                return;
            }
            if (key == Key.Delete || key == Key.Back)
            {
                _bindings[index] = default(Hotkey);
                _fields[index].Text = _bindings[index].Display;
                if (Save()) ShowMessage("Сочетание удалено.", false);
                Keyboard.ClearFocus();
                return;
            }
            if (IsModifier(key)) return;

            ModifierKeys modifiers = Keyboard.Modifiers;
            if ((modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) !=
                (ModifierKeys.Control | ModifierKeys.Alt) ||
                (modifiers & ModifierKeys.Windows) != 0)
            {
                ShowMessage("Используйте Ctrl+Alt+клавишу; Shift можно добавить.", true);
                return;
            }

            uint flags = Control | Alt;
            if ((modifiers & ModifierKeys.Shift) != 0) flags |= Shift;
            int virtualKey = KeyInterop.VirtualKeyFromKey(key);
            if (virtualKey <= 0 || virtualKey > 255)
            {
                ShowMessage("Эту клавишу назначить нельзя.", true);
                return;
            }
            var candidate = new Hotkey(flags, (uint)virtualKey);
            if (candidate.Equals(_bindings[1 - index]))
            {
                ShowMessage("Это сочетание уже назначено другой кнопке.", true);
                return;
            }
            if (_source != null && !RegisterHotKey(_source.Handle, FirstId + index,
                    candidate.Modifiers | NoRepeat, candidate.VirtualKey))
            {
                ShowMessage("Сочетание занято Windows или другой программой.", true);
                return;
            }
            if (_source != null) UnregisterHotKey(_source.Handle, FirstId + index);

            _bindings[index] = candidate;
            _warned[index] = false;
            _fields[index].Text = candidate.Display;
            if (Save()) ShowMessage("Сочетание сохранено.", false);
            Keyboard.ClearFocus();
        }

        private void Poll()
        {
            if (_host == null) return;
            var nextSource = PresentationSource.FromVisual(_host) as HwndSource;
            if (!ReferenceEquals(nextSource, _source))
            {
                UnregisterAll();
                if (_source != null) _source.RemoveHook(OnWindowMessage);
                _source = nextSource;
                if (_source != null) _source.AddHook(OnWindowMessage);
            }
            if (_source == null || _capturing || !CorelIsForeground())
            {
                UnregisterAll();
                return;
            }
            for (int i = 0; i < _bindings.Length; i++)
            {
                if (_registered[i] || _bindings[i].IsEmpty) continue;
                if (RegisterHotKey(_source.Handle, FirstId + i,
                    _bindings[i].Modifiers | NoRepeat, _bindings[i].VirtualKey))
                {
                    _registered[i] = true;
                    _warned[i] = false;
                }
                else if (!_warned[i])
                {
                    _warned[i] = true;
                    ShowMessage("Не удалось активировать " + _bindings[i].Display +
                        ": сочетание занято.", true);
                }
            }
        }

        private void SafePoll()
        {
            try { Poll(); }
            catch (Exception error)
            {
                Log.Error("Could not update Corel hotkeys.", error);
                UnregisterAll();
                ShowMessage("Не удалось включить горячие клавиши.", true);
            }
        }

        private IntPtr OnWindowMessage(IntPtr hwnd, int message, IntPtr wParam,
            IntPtr lParam, ref bool handled)
        {
            if (message != WmHotkey || !CorelIsForeground()) return IntPtr.Zero;
            int index = wParam.ToInt32() - FirstId;
            if (index < 0 || index >= _actions.Length) return IntPtr.Zero;
            handled = true;
            try { _actions[index](); }
            catch (Exception error)
            {
                Log.Error("Hotkey action failed.", error);
                _status(error.Message, true);
            }
            return IntPtr.Zero;
        }

        private void UnregisterAll()
        {
            if (_source == null) return;
            for (int i = 0; i < _registered.Length; i++)
            {
                if (!_registered[i]) continue;
                UnregisterHotKey(_source.Handle, FirstId + i);
                _registered[i] = false;
            }
        }

        private static bool CorelIsForeground()
        {
            IntPtr window = GetForegroundWindow();
            if (window == IntPtr.Zero) return false;
            uint processId;
            GetWindowThreadProcessId(window, out processId);
            return processId == (uint)Process.GetCurrentProcess().Id;
        }

        private static bool IsModifier(Key key)
        {
            return key == Key.LeftCtrl || key == Key.RightCtrl ||
                   key == Key.LeftAlt || key == Key.RightAlt ||
                   key == Key.LeftShift || key == Key.RightShift ||
                   key == Key.LWin || key == Key.RWin;
        }

        private void ShowMessage(string text, bool error)
        {
            _message.Text = text;
            _message.Foreground = error
                ? System.Windows.Media.Brushes.Firebrick
                : System.Windows.Media.Brushes.SlateGray;
            if (error) _status(text, true);
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_settingsPath)) return;
                var saved = new JavaScriptSerializer().Deserialize<Dictionary<string, string>>(
                    File.ReadAllText(_settingsPath));
                if (saved == null) return;
                string value;
                if (saved.TryGetValue("trim", out value)) _bindings[0] = Hotkey.Parse(value);
                if (saved.TryGetValue("fitFrame", out value)) _bindings[1] = Hotkey.Parse(value);
                if (_bindings[0].Equals(_bindings[1])) _bindings[1] = default(Hotkey);
            }
            catch (Exception error) { Log.Error("Could not load hotkeys.", error); }
        }

        private bool Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath));
                var data = new Dictionary<string, string>
                {
                    { "trim", _bindings[0].Stored },
                    { "fitFrame", _bindings[1].Stored }
                };
                File.WriteAllText(_settingsPath, new JavaScriptSerializer().Serialize(data));
                return true;
            }
            catch (Exception error)
            {
                Log.Error("Could not save hotkeys.", error);
                ShowMessage("Не удалось сохранить сочетания клавиш.", true);
                return false;
            }
        }

        private struct Hotkey : IEquatable<Hotkey>
        {
            internal readonly uint Modifiers;
            internal readonly uint VirtualKey;
            internal bool IsEmpty => VirtualKey == 0;
            internal string Stored => IsEmpty ? "" : Modifiers + ":" + VirtualKey;
            internal string Display => IsEmpty ? "Не назначено" :
                "Ctrl+Alt+" + ((Modifiers & Shift) != 0 ? "Shift+" : "") +
                KeyInterop.KeyFromVirtualKey((int)VirtualKey);

            internal Hotkey(uint modifiers, uint virtualKey)
            {
                Modifiers = modifiers;
                VirtualKey = virtualKey;
            }

            internal static Hotkey Parse(string value)
            {
                var parts = (value ?? "").Split(':');
                uint modifiers, virtualKey;
                if (parts.Length != 2 || !UInt32.TryParse(parts[0], out modifiers) ||
                    !UInt32.TryParse(parts[1], out virtualKey) ||
                    (modifiers & (Alt | Control)) != (Alt | Control) ||
                    (modifiers & ~(Alt | Control | Shift)) != 0 ||
                    virtualKey == 0 || virtualKey > 255)
                    return default(Hotkey);
                return new Hotkey(modifiers, virtualKey);
            }

            public bool Equals(Hotkey other)
            {
                return Modifiers == other.Modifiers && VirtualKey == other.VirtualKey;
            }
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint virtualKey);

        [DllImport("user32.dll")]
        private static extern bool UnregisterHotKey(IntPtr window, int id);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    }
}
