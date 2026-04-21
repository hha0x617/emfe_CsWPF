// Copyright 2026 hha0x617
// Licensed under the Apache License, Version 2.0 (the "License");
// you may not use this file except in compliance with the License.
// You may obtain a copy of the License at
//
//     http://www.apache.org/licenses/LICENSE-2.0
//
// Unless required by applicable law or agreed to in writing, software
// distributed under the License is distributed on an "AS IS" BASIS,
// WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
// See the License for the specific language governing permissions and
// limitations under the License.

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;

namespace emfe;

public partial class ConsoleWindow : Window
{
    private readonly Action<char>? _sendChar;
    private readonly Vt100Terminal _terminal = new(80, 25, 2000);
    private readonly Queue<char> _outputQueue = new();
    private readonly object _outputLock = new();
    private readonly DispatcherTimer _renderTimer;

    // Search state
    private bool _searchMode;
    private int _searchIndex;
    private string _lastSearchText = string.Empty;

    public ConsoleWindow(Action<char>? sendChar = null)
    {
        InitializeComponent();
        _sendChar = sendChar;
        Loaded += (_, _) => ThemeHelper.ApplyTitleBar(this, ThemeHelper.IsDarkMode);

        Title = $"Console ({_terminal.Cols}x{_terminal.Rows})";

        _renderTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
        _renderTimer.Tick += OnRenderTick;
        _renderTimer.Start();
    }

    public void AppendChar(char ch)
    {
        lock (_outputLock)
        {
            if (_outputQueue.Count < 65536)
                _outputQueue.Enqueue(ch);
        }
    }

    // ----- Context menu: Copy / Paste --------------------------------------

    private CancellationTokenSource? _pasteCts;

    private void OnConsoleMenuCopy(object sender, RoutedEventArgs e)
    {
        // Fall back to copying everything visible if the user hasn't selected
        // a range — otherwise TextBox.Copy would silently no-op and the menu
        // feels broken.
        if (OutputBox.SelectionLength > 0)
            OutputBox.Copy();
        else if (!string.IsNullOrEmpty(OutputBox.Text))
            try { Clipboard.SetText(OutputBox.Text); }
            catch { /* Clipboard access can fail transiently; ignore. */ }
    }

    private async void OnConsoleMenuPaste(object sender, RoutedEventArgs e)
    {
        if (_sendChar == null) return;
        if (!Clipboard.ContainsText()) return;

        string raw;
        try { raw = Clipboard.GetText(); }
        catch { return; }
        if (string.IsNullOrEmpty(raw)) return;

        // Normalize line endings to LF only (per user convention for the
        // emulated UART).
        string normalized = raw.Replace("\r\n", "\n").Replace('\r', '\n');

        // Cancel any in-flight paste so we don't interleave characters.
        _pasteCts?.Cancel();
        _pasteCts = new CancellationTokenSource();
        var ct = _pasteCts.Token;

        // Burst-feed characters: push up to PasteBurstSize at the machine's
        // full speed, then yield for 1 ms so the guest CPU's UART ISR can
        // drain the receive FIFO before the next burst.  64 matches the
        // 16550 FIFO depth used by the 68030 plugin, giving ~64 chars/ms
        // (≈ 6 Mbps equivalent) — effectively instant for pasted text.
        //
        // TODO: when the plugin ABI gains a console-tx-space query, use
        // that for real backpressure instead of the fixed burst size.
        const int PasteBurstSize = 64;
        try
        {
            int count = 0;
            foreach (char ch in normalized)
            {
                if (ct.IsCancellationRequested) break;
                _sendChar(ch);
                if (++count >= PasteBurstSize)
                {
                    count = 0;
                    await Task.Delay(1, ct);
                }
            }
        }
        catch (TaskCanceledException) { /* paste interrupted — expected */ }
    }

    private void OnConsoleMenuSelectAll(object sender, RoutedEventArgs e)
        => OutputBox.SelectAll();

    private void OnRenderTick(object? sender, EventArgs e)
    {
        // Drain output queue into terminal
        lock (_outputLock)
        {
            while (_outputQueue.Count > 0)
                _terminal.Write(_outputQueue.Dequeue());
        }

        if (!_terminal.IsDirty) return;
        _terminal.ClearDirty();

        OutputBox.Text = _terminal.RenderFullWithCursor();
        OutputBox.CaretIndex = OutputBox.Text.Length;
        OutputBox.ScrollToEnd();
    }

    private void OnKeyInput(object sender, KeyEventArgs e)
    {
        // Ctrl+Shift+F opens search
        if (e.Key == Key.F && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                           && Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
        {
            OpenSearch();
            e.Handled = true;
            return;
        }

        // F3 / Shift+F3 when search is active
        if (_searchMode && e.Key == Key.F3)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                FindPrev();
            else
                FindNext();
            e.Handled = true;
            return;
        }

        // Escape closes search when active
        if (_searchMode && e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
            return;
        }

        if (_sendChar == null) return;

        // Special keys that PreviewTextInput wouldn't produce (or would
        // surface as control codes).  All printable characters — including
        // punctuation, uppercase letters, numpad, and IME output — go
        // through OnTextInput below.
        char ch = '\0';
        if (e.Key == Key.Enter) ch = '\n';
        else if (e.Key == Key.Back) ch = '\b';
        else if (e.Key == Key.Escape) ch = (char)0x1B;
        else if (e.Key == Key.Tab) ch = '\t';
        else if (e.Key == Key.Space) ch = ' ';  // PreviewTextInput is unreliable for Space on TextBox
        else if (e.Key == Key.Up) { SendEscSeq(_terminal.ApplicationCursorKeys ? "\x1BOA" : "\x1B[A"); e.Handled = true; return; }
        else if (e.Key == Key.Down) { SendEscSeq(_terminal.ApplicationCursorKeys ? "\x1BOB" : "\x1B[B"); e.Handled = true; return; }
        else if (e.Key == Key.Right) { SendEscSeq(_terminal.ApplicationCursorKeys ? "\x1BOC" : "\x1B[C"); e.Handled = true; return; }
        else if (e.Key == Key.Left) { SendEscSeq(_terminal.ApplicationCursorKeys ? "\x1BOD" : "\x1B[D"); e.Handled = true; return; }
        else if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control)
                 && e.Key >= Key.A && e.Key <= Key.Z)
        {
            // Ctrl+A..Z → control codes 0x01..0x1A
            ch = (char)(e.Key - Key.A + 1);
        }

        if (ch != '\0')
        {
            _sendChar(ch);
            e.Handled = true;
        }
    }

    // Printable-character path: honors keyboard layout, Shift, numpad, IME.
    private void OnTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_sendChar == null) return;
        foreach (char c in e.Text)
        {
            // OnKeyInput already delivered control codes; skip them here
            // to avoid double-send for Enter/Tab/Backspace etc.
            if (c < 0x20) continue;
            _sendChar(c);
        }
        e.Handled = true;
    }

    private void SendEscSeq(string seq)
    {
        if (_sendChar == null) return;
        foreach (char c in seq)
            _sendChar(c);
    }

    // --- Search feature ---

    private void OpenSearch()
    {
        _searchMode = true;
        SearchBar.Visibility = Visibility.Visible;

        // Pre-fill with selection if any
        var sel = OutputBox.SelectedText;
        if (!string.IsNullOrEmpty(sel))
            SearchBox.Text = sel;

        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void CloseSearch()
    {
        _searchMode = false;
        _searchIndex = 0;
        _lastSearchText = string.Empty;
        SearchBar.Visibility = Visibility.Collapsed;
        SearchStatus.Text = string.Empty;
        OutputBox.Focus();
    }

    private void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                FindPrev();
            else
                FindNext();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            CloseSearch();
            e.Handled = true;
        }
        else if (e.Key == Key.F3)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Shift))
                FindPrev();
            else
                FindNext();
            e.Handled = true;
        }
    }

    private void OnSearchNext(object sender, RoutedEventArgs e) => FindNext();
    private void OnSearchPrev(object sender, RoutedEventArgs e) => FindPrev();
    private void OnSearchClose(object sender, RoutedEventArgs e) => CloseSearch();

    private List<(int Position, int Length)> CollectMatches(string text, string query)
    {
        var matches = new List<(int, int)>();
        if (string.IsNullOrEmpty(query) || string.IsNullOrEmpty(text))
            return matches;

        bool useRegex = RegexToggle.IsChecked == true;
        bool caseSensitive = CaseSensitiveToggle.IsChecked == true;

        if (useRegex)
        {
            try
            {
                var options = RegexOptions.None;
                if (!caseSensitive)
                    options |= RegexOptions.IgnoreCase;
                var regex = new Regex(query, options);
                foreach (Match m in regex.Matches(text))
                {
                    if (m.Length > 0)
                        matches.Add((m.Index, m.Length));
                }
            }
            catch (ArgumentException)
            {
                // Invalid regex pattern — return empty
            }
        }
        else
        {
            var comparison = caseSensitive
                ? StringComparison.Ordinal
                : StringComparison.OrdinalIgnoreCase;
            int pos = 0;
            while (pos < text.Length)
            {
                int idx = text.IndexOf(query, pos, comparison);
                if (idx < 0) break;
                matches.Add((idx, query.Length));
                pos = idx + 1;
            }
        }

        return matches;
    }

    private void FindNext()
    {
        var query = SearchBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            SearchStatus.Text = string.Empty;
            return;
        }

        var text = OutputBox.Text ?? string.Empty;
        var matches = CollectMatches(text, query);

        if (matches.Count == 0)
        {
            SearchStatus.Text = "Not found";
            _searchIndex = 0;
            _lastSearchText = query;
            return;
        }

        // If query changed, reset index
        if (query != _lastSearchText)
        {
            _searchIndex = 0;
            _lastSearchText = query;
        }
        else
        {
            _searchIndex++;
            if (_searchIndex >= matches.Count)
                _searchIndex = 0; // wrap around
        }

        HighlightMatch(matches[_searchIndex].Position, matches[_searchIndex].Length);
        SearchStatus.Text = $"{_searchIndex + 1}/{matches.Count}";
    }

    private void FindPrev()
    {
        var query = SearchBox.Text;
        if (string.IsNullOrEmpty(query))
        {
            SearchStatus.Text = string.Empty;
            return;
        }

        var text = OutputBox.Text ?? string.Empty;
        var matches = CollectMatches(text, query);

        if (matches.Count == 0)
        {
            SearchStatus.Text = "Not found";
            _searchIndex = 0;
            _lastSearchText = query;
            return;
        }

        // If query changed, start from last match
        if (query != _lastSearchText)
        {
            _searchIndex = matches.Count - 1;
            _lastSearchText = query;
        }
        else
        {
            _searchIndex--;
            if (_searchIndex < 0)
                _searchIndex = matches.Count - 1; // wrap around
        }

        HighlightMatch(matches[_searchIndex].Position, matches[_searchIndex].Length);
        SearchStatus.Text = $"{_searchIndex + 1}/{matches.Count}";
    }

    private void HighlightMatch(int position, int length)
    {
        OutputBox.Focus();
        OutputBox.Select(position, length);
    }
}
