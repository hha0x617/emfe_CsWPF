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
using System.Text;

namespace emfe;

/// <summary>
/// Minimal VT100/ANSI terminal emulator with character cell screen buffer.
/// Supports cursor movement, screen/line clearing, scrolling regions,
/// and line-drawing characters -- sufficient for curses-based applications
/// like NetBSD sysinst.
/// </summary>
public class Vt100Terminal
{
    // Parser state machine
    private enum State { Normal, Esc, Csi, EscParen, StringSeq }

    // Screen dimensions
    private readonly int _cols;
    private readonly int _rows;

    // Screen buffer: row-major, index = row * _cols + col
    private readonly char[] _screen;

    // Soft-wrap flags: true if this row is a continuation of the previous row
    private readonly bool[] _softWrap;

    // Cursor position
    private int _cursorRow;
    private int _cursorCol;

    private bool _dirty = true;

    // Scrollback ring buffer
    private string[] _scrollback;
    private bool[] _scrollbackSoftWrap;
    private int _scrollbackHead;
    private int _scrollbackCount;
    private int _maxScrollback;

    // Parser state
    private State _state = State.Normal;
    private readonly List<int> _csiParams = new();
    private int _currentParam;
    private bool _hasCurrentParam;
    private bool _csiQuestion; // CSI ? prefix

    // Scroll region (0-based, inclusive)
    private int _scrollTop;
    private int _scrollBottom;

    // Saved cursor position (DECSC / DECRC)
    private int _savedRow;
    private int _savedCol;

    // DEC Cursor Key Mode
    private bool _applicationCursorKeys;

    // Alternate character set (line drawing)
    private bool _alternateCharset;

    // OSC / DCS / PM / APC string sequence state
    private bool _stringSeqEsc;

    // UTF-8 decoder state: accumulates multibyte sequences across Write(char) calls.
    // Mirrors Em68030_CsWPF/Em68030/Views/Vt100Terminal.cs.
    private int _utf8Remaining;
    private int _utf8CodePoint;

    // Line drawing character map (ASCII -> ASCII approximation)
    private static readonly Dictionary<char, char> LineDrawingMap = new()
    {
        ['j'] = '+', // corner (box drawings light up and left)
        ['k'] = '+', // corner (box drawings light down and left)
        ['l'] = '+', // corner (box drawings light down and right)
        ['m'] = '+', // corner (box drawings light up and right)
        ['n'] = '+', // cross (box drawings light vertical and horizontal)
        ['q'] = '-', // horizontal (box drawings light horizontal)
        ['t'] = '+', // tee (box drawings light vertical and right)
        ['u'] = '+', // tee (box drawings light vertical and left)
        ['v'] = '+', // tee (box drawings light up and horizontal)
        ['w'] = '+', // tee (box drawings light down and horizontal)
        ['x'] = '|', // vertical (box drawings light vertical)
        ['a'] = '#', // shade (medium shade)
        ['f'] = 'o', // degree (degree sign)
        ['g'] = '+', // plus-minus
        ['~'] = '.', // middle dot
        ['y'] = '<', // less-equal
        ['z'] = '>', // greater-equal
    };

    public int Cols => _cols;
    public int Rows => _rows;
    public bool IsDirty => _dirty;
    public void ClearDirty() => _dirty = false;
    public void SetDirty() => _dirty = true;
    public int ScrollbackLineCount => _scrollbackCount;
    public int CursorRow => _cursorRow;
    public int CursorCol => _cursorCol;
    public bool ApplicationCursorKeys => _applicationCursorKeys;

    public Vt100Terminal(int cols = 80, int rows = 25, int maxScrollback = 2000)
    {
        _cols = cols;
        _rows = rows;
        _maxScrollback = Math.Clamp(maxScrollback, 0, 100000);
        _scrollBottom = rows - 1;

        int sbSize = _maxScrollback > 0 ? _maxScrollback : 1;
        _scrollback = new string[sbSize];
        _scrollbackSoftWrap = new bool[sbSize];
        _screen = new char[rows * cols];
        Array.Fill(_screen, ' ');
        _softWrap = new bool[rows];
    }

    // Screen cell access
    private ref char ScreenAt(int row, int col) => ref _screen[row * _cols + col];
    private char ScreenAtValue(int row, int col) => _screen[row * _cols + col];

    // ========================================================================
    // Write
    // ========================================================================

    public void Write(char ch)
    {
        // UTF-8 decode: the caller passes raw bytes as char values.
        // Multibyte sequences (0x80+) need to be accumulated and decoded so
        // that a single ─ (U+2500, 3 bytes) occupies one cell instead of
        // three, and so that continuation bytes aren't dropped by the
        // `ch >= ' '` filter in ProcessNormal.  Mirrors the C# port in
        // Em68030_CsWPF/Em68030/Views/Vt100Terminal.cs:Write.
        int b = ch & 0xFF;
        if (_utf8Remaining > 0)
        {
            if ((b & 0xC0) == 0x80)
            {
                _utf8CodePoint = (_utf8CodePoint << 6) | (b & 0x3F);
                _utf8Remaining--;
                if (_utf8Remaining == 0)
                    ch = _utf8CodePoint <= 0xFFFF ? (char)_utf8CodePoint : '?';
                else
                    return;
            }
            else
            {
                // Invalid continuation byte: abandon the in-progress sequence
                // and reparse this byte from scratch as if it were fresh.
                _utf8Remaining = 0;
            }
        }
        else if (b >= 0xC0 && b <= 0xDF) // 2-byte sequence start
        {
            _utf8CodePoint = b & 0x1F;
            _utf8Remaining = 1;
            return;
        }
        else if (b >= 0xE0 && b <= 0xEF) // 3-byte sequence start
        {
            _utf8CodePoint = b & 0x0F;
            _utf8Remaining = 2;
            return;
        }
        else if (b >= 0xF0 && b <= 0xF7) // 4-byte sequence start
        {
            _utf8CodePoint = b & 0x07;
            _utf8Remaining = 3;
            return;
        }

        switch (_state)
        {
            case State.Normal:
                ProcessNormal(ch);
                break;
            case State.Esc:
                ProcessEsc(ch);
                break;
            case State.Csi:
                ProcessCsi(ch);
                break;
            case State.EscParen:
                // ESC ( or ESC ) -- character set designation, consume one more char
                _state = State.Normal;
                break;
            case State.StringSeq:
                ProcessStringSeq(ch);
                break;
        }
    }

    public void Write(string s)
    {
        foreach (char ch in s)
            Write(ch);
    }

    // ========================================================================
    // Render
    // ========================================================================

    /// <summary>
    /// Render the full terminal output (scrollback + screen) with a block cursor.
    /// </summary>
    public string RenderFullWithCursor()
    {
        var sb = new StringBuilder(
            _scrollbackCount * (_cols + 1) + _rows * (_cols + 1));

        // Scrollback
        for (int i = 0; i < _scrollbackCount; i++)
        {
            int idx = (_scrollbackHead + i) % _maxScrollback;
            if (i > 0 && !_scrollbackSoftWrap[idx])
                sb.Append('\n');
            if (_scrollback[idx] != null)
                sb.Append(_scrollback[idx]);
        }

        // Live screen
        for (int r = 0; r < _rows; r++)
        {
            if (_scrollbackCount > 0 || r > 0)
            {
                if (!_softWrap[r])
                    sb.Append('\n');
            }

            // Trim trailing spaces, but ensure cursor column is included
            int lastCol = _cols - 1;
            while (lastCol >= 0 && ScreenAtValue(r, lastCol) == ' ') lastCol--;
            if (r == _cursorRow && _cursorCol > lastCol) lastCol = _cursorCol;

            for (int c = 0; c <= lastCol; c++)
            {
                if (r == _cursorRow && c == _cursorCol)
                    sb.Append('\u2588'); // Full block cursor
                else
                    sb.Append(ScreenAtValue(r, c));
            }
        }

        return sb.ToString();
    }

    // ========================================================================
    // Normal character processing
    // ========================================================================

    private void ProcessNormal(char ch)
    {
        switch (ch)
        {
            case '\x1B': // ESC
                _state = State.Esc;
                break;
            case '\r': // CR
                _cursorCol = 0;
                _dirty = true;
                break;
            case '\n': // LF -- also do CR
                _cursorCol = 0;
                LineFeed();
                _softWrap[_cursorRow] = false;
                break;
            case '\b': // BS
                if (_cursorCol > 0) _cursorCol--;
                _dirty = true;
                break;
            case '\t': // TAB
                _cursorCol = Math.Min((_cursorCol / 8 + 1) * 8, _cols - 1);
                _dirty = true;
                break;
            case '\x07': // BEL
                break;
            case '\x0E': // SO -- Switch to alternate character set
                _alternateCharset = true;
                break;
            case '\x0F': // SI -- Switch to standard character set
                _alternateCharset = false;
                break;
            default:
                if (ch >= ' ')
                    PutChar(ch);
                break;
        }
    }

    private void PutChar(char ch)
    {
        if (_alternateCharset && LineDrawingMap.TryGetValue(ch, out char mapped))
            ch = mapped;

        if (_cursorCol >= _cols)
        {
            // Auto-wrap
            _cursorCol = 0;
            LineFeed();
            _softWrap[_cursorRow] = true;
        }

        ScreenAt(_cursorRow, _cursorCol) = ch;
        _cursorCol++;
        _dirty = true;
    }

    private void LineFeed()
    {
        if (_cursorRow == _scrollBottom)
            ScrollUp(1);
        else if (_cursorRow < _rows - 1)
            _cursorRow++;
        _dirty = true;
    }

    // ========================================================================
    // ESC sequence processing
    // ========================================================================

    private void ProcessEsc(char ch)
    {
        switch (ch)
        {
            case '[': // CSI
                _state = State.Csi;
                _csiParams.Clear();
                _currentParam = 0;
                _hasCurrentParam = false;
                _csiQuestion = false;
                break;
            case '(':
            case ')': // Character set designation
                _state = State.EscParen;
                break;
            case '7': // DECSC -- Save cursor
                _savedRow = _cursorRow;
                _savedCol = _cursorCol;
                _state = State.Normal;
                break;
            case '8': // DECRC -- Restore cursor
                _cursorRow = Math.Clamp(_savedRow, 0, _rows - 1);
                _cursorCol = Math.Clamp(_savedCol, 0, _cols - 1);
                _dirty = true;
                _state = State.Normal;
                break;
            case 'M': // RI -- Reverse Index
                if (_cursorRow == _scrollTop)
                    ScrollDown(1);
                else if (_cursorRow > 0)
                    _cursorRow--;
                _dirty = true;
                _state = State.Normal;
                break;
            case 'D': // IND -- Index
                LineFeed();
                _state = State.Normal;
                break;
            case 'E': // NEL -- Next Line
                _cursorCol = 0;
                LineFeed();
                _state = State.Normal;
                break;
            case ']': // OSC
            case 'P': // DCS
            case '^': // PM
            case '_': // APC
                _state = State.StringSeq;
                _stringSeqEsc = false;
                break;
            case '=':
            case '>': // Keypad modes -- ignore
                _state = State.Normal;
                break;
            case 'c': // RIS -- Full reset
                Reset();
                _state = State.Normal;
                break;
            default:
                _state = State.Normal;
                break;
        }
    }

    // ========================================================================
    // OSC / DCS / PM / APC string sequence processing
    // ========================================================================

    private void ProcessStringSeq(char ch)
    {
        if (_stringSeqEsc)
        {
            if (ch == '\\')
            {
                // ESC \ = ST (String Terminator)
                _state = State.Normal;
            }
            else
            {
                // ESC followed by something else -- treat as new ESC sequence
                _state = State.Esc;
                ProcessEsc(ch);
            }
            _stringSeqEsc = false;
            return;
        }

        if (ch == '\x1B')
            _stringSeqEsc = true;
        else if (ch == '\x07')
            _state = State.Normal; // BEL terminates OSC
        else if (ch == '\x9C')
            _state = State.Normal; // 8-bit ST
    }

    // ========================================================================
    // CSI sequence processing
    // ========================================================================

    private void ProcessCsi(char ch)
    {
        if (ch == '?')
        {
            _csiQuestion = true;
            return;
        }

        if (ch >= '0' && ch <= '9')
        {
            _currentParam = _currentParam * 10 + (ch - '0');
            _hasCurrentParam = true;
            return;
        }

        if (ch == ';' || ch == ':')
        {
            _csiParams.Add(_hasCurrentParam ? _currentParam : 0);
            _currentParam = 0;
            _hasCurrentParam = false;
            return;
        }

        // Final character -- execute
        if (_hasCurrentParam)
            _csiParams.Add(_currentParam);

        ExecuteCsi(ch);
        _state = State.Normal;
    }

    private int Param(int index, int defaultValue = 1)
    {
        if (index < _csiParams.Count && _csiParams[index] > 0)
            return _csiParams[index];
        return defaultValue;
    }

    private void ExecuteCsi(char cmd)
    {
        if (_csiQuestion)
        {
            // DEC private modes
            int mode = Param(0);
            if (cmd == 'h') // Set mode
            {
                if (mode == 1) _applicationCursorKeys = true; // DECCKM
            }
            else if (cmd == 'l') // Reset mode
            {
                if (mode == 1) _applicationCursorKeys = false; // DECCKM
            }
            return;
        }

        switch (cmd)
        {
            case 'A': // CUU -- Cursor Up
                _cursorRow = Math.Max(_cursorRow - Param(0), 0);
                _dirty = true;
                break;

            case 'B': // CUD -- Cursor Down
                _cursorRow = Math.Min(_cursorRow + Param(0), _rows - 1);
                _dirty = true;
                break;

            case 'C': // CUF -- Cursor Forward
                _cursorCol = Math.Min(_cursorCol + Param(0), _cols - 1);
                _dirty = true;
                break;

            case 'D': // CUB -- Cursor Backward
                _cursorCol = Math.Max(_cursorCol - Param(0), 0);
                _dirty = true;
                break;

            case 'H': // CUP -- Cursor Position (1-based)
            case 'f':
                _cursorRow = Math.Clamp(Param(0) - 1, 0, _rows - 1);
                _cursorCol = Math.Clamp(Param(1, 1) - 1, 0, _cols - 1);
                _dirty = true;
                break;

            case 'G': // CHA -- Cursor Horizontal Absolute (1-based)
                _cursorCol = Math.Clamp(Param(0) - 1, 0, _cols - 1);
                _dirty = true;
                break;

            case 'd': // VPA -- Cursor Vertical Absolute (1-based)
                _cursorRow = Math.Clamp(Param(0) - 1, 0, _rows - 1);
                _dirty = true;
                break;

            case 'J': // ED -- Erase in Display
                EraseInDisplay(Param(0, 0));
                break;

            case 'K': // EL -- Erase in Line
                EraseInLine(Param(0, 0));
                break;

            case 'L': // IL -- Insert Lines
                InsertLines(Param(0));
                break;

            case 'M': // DL -- Delete Lines
                DeleteLines(Param(0));
                break;

            case 'P': // DCH -- Delete Characters
                DeleteChars(Param(0));
                break;

            case '@': // ICH -- Insert Blank Characters
                InsertChars(Param(0));
                break;

            case 'X': // ECH -- Erase Characters
                EraseChars(Param(0));
                break;

            case 'r': // DECSTBM -- Set Scrolling Region (1-based)
                _scrollTop = Math.Clamp(Param(0) - 1, 0, _rows - 1);
                _scrollBottom = Math.Clamp(Param(1, _rows) - 1, 0, _rows - 1);
                if (_scrollTop > _scrollBottom)
                    (_scrollTop, _scrollBottom) = (_scrollBottom, _scrollTop);
                _cursorRow = 0;
                _cursorCol = 0;
                _dirty = true;
                break;

            case 'S': // SU -- Scroll Up
                ScrollUp(Param(0));
                break;

            case 'T': // SD -- Scroll Down
                ScrollDown(Param(0));
                break;

            case 'm': // SGR -- ignore
                break;

            case 'h':
            case 'l': // SM/RM -- ignore
                break;

            case 'n': // DSR -- ignore
                break;

            case 's': // SCP -- Save Cursor Position
                _savedRow = _cursorRow;
                _savedCol = _cursorCol;
                break;

            case 'u': // RCP -- Restore Cursor Position
                _cursorRow = Math.Clamp(_savedRow, 0, _rows - 1);
                _cursorCol = Math.Clamp(_savedCol, 0, _cols - 1);
                _dirty = true;
                break;
        }
    }

    // ========================================================================
    // Erase operations
    // ========================================================================

    private void EraseInDisplay(int mode)
    {
        switch (mode)
        {
            case 0: // Erase from cursor to end of screen
                ClearRange(_cursorRow, _cursorCol, _rows - 1, _cols - 1);
                break;
            case 1: // Erase from start of screen to cursor
                ClearRange(0, 0, _cursorRow, _cursorCol);
                break;
            case 2: // Erase entire screen
                ClearScreen();
                break;
        }
        _dirty = true;
    }

    private void EraseInLine(int mode)
    {
        switch (mode)
        {
            case 0: // Erase from cursor to end of line
                for (int c = _cursorCol; c < _cols; c++)
                    ScreenAt(_cursorRow, c) = ' ';
                break;
            case 1: // Erase from start of line to cursor
                for (int c = 0; c <= _cursorCol && c < _cols; c++)
                    ScreenAt(_cursorRow, c) = ' ';
                break;
            case 2: // Erase entire line
                for (int c = 0; c < _cols; c++)
                    ScreenAt(_cursorRow, c) = ' ';
                break;
        }
        _dirty = true;
    }

    private void EraseChars(int n)
    {
        for (int i = 0; i < n && _cursorCol + i < _cols; i++)
            ScreenAt(_cursorRow, _cursorCol + i) = ' ';
        _dirty = true;
    }

    // ========================================================================
    // Insert/Delete operations
    // ========================================================================

    private void InsertLines(int n)
    {
        int bottom = _scrollBottom;
        for (int i = 0; i < n; i++)
        {
            for (int r = bottom; r > _cursorRow; r--)
                for (int c = 0; c < _cols; c++)
                    ScreenAt(r, c) = ScreenAtValue(r - 1, c);
            for (int c = 0; c < _cols; c++)
                ScreenAt(_cursorRow, c) = ' ';
        }
        _dirty = true;
    }

    private void DeleteLines(int n)
    {
        int bottom = _scrollBottom;
        for (int i = 0; i < n; i++)
        {
            for (int r = _cursorRow; r < bottom; r++)
                for (int c = 0; c < _cols; c++)
                    ScreenAt(r, c) = ScreenAtValue(r + 1, c);
            for (int c = 0; c < _cols; c++)
                ScreenAt(bottom, c) = ' ';
        }
        _dirty = true;
    }

    private void DeleteChars(int n)
    {
        for (int i = _cursorCol; i < _cols; i++)
        {
            int src = i + n;
            ScreenAt(_cursorRow, i) = src < _cols ? ScreenAtValue(_cursorRow, src) : ' ';
        }
        _dirty = true;
    }

    private void InsertChars(int n)
    {
        for (int i = _cols - 1; i >= _cursorCol + n; i--)
            ScreenAt(_cursorRow, i) = ScreenAtValue(_cursorRow, i - n);
        for (int i = 0; i < n && _cursorCol + i < _cols; i++)
            ScreenAt(_cursorRow, _cursorCol + i) = ' ';
        _dirty = true;
    }

    // ========================================================================
    // Scrolling
    // ========================================================================

    private void ScrollUp(int n)
    {
        for (int i = 0; i < n; i++)
        {
            // Save the top line to scrollback
            if (_scrollTop == 0 && _maxScrollback > 0)
            {
                var line = new StringBuilder(_cols);
                for (int c = 0; c < _cols; c++)
                    line.Append(ScreenAtValue(_scrollTop, c));

                // Trim trailing spaces
                string trimmed = line.ToString().TrimEnd(' ');

                int writeIdx = (_scrollbackHead + _scrollbackCount) % _maxScrollback;
                _scrollback[writeIdx] = trimmed;
                _scrollbackSoftWrap[writeIdx] = _softWrap[_scrollTop];
                if (_scrollbackCount < _maxScrollback)
                    _scrollbackCount++;
                else
                    _scrollbackHead = (_scrollbackHead + 1) % _maxScrollback;
            }

            // Shift rows up within scroll region
            for (int r = _scrollTop; r < _scrollBottom; r++)
            {
                for (int c = 0; c < _cols; c++)
                    ScreenAt(r, c) = ScreenAtValue(r + 1, c);
                _softWrap[r] = _softWrap[r + 1];
            }
            for (int c = 0; c < _cols; c++)
                ScreenAt(_scrollBottom, c) = ' ';
            _softWrap[_scrollBottom] = false;
        }
        _dirty = true;
    }

    private void ScrollDown(int n)
    {
        for (int i = 0; i < n; i++)
        {
            for (int r = _scrollBottom; r > _scrollTop; r--)
            {
                for (int c = 0; c < _cols; c++)
                    ScreenAt(r, c) = ScreenAtValue(r - 1, c);
                _softWrap[r] = _softWrap[r - 1];
            }
            for (int c = 0; c < _cols; c++)
                ScreenAt(_scrollTop, c) = ' ';
            _softWrap[_scrollTop] = false;
        }
        _dirty = true;
    }

    // ========================================================================
    // Helpers
    // ========================================================================

    private void ClearScreen()
    {
        Array.Fill(_screen, ' ');
        Array.Fill(_softWrap, false);
        _dirty = true;
    }

    private void ClearRange(int r1, int c1, int r2, int c2)
    {
        for (int r = r1; r <= r2 && r < _rows; r++)
        {
            int startC = (r == r1) ? c1 : 0;
            int endC = (r == r2) ? c2 : _cols - 1;
            for (int c = startC; c <= endC && c < _cols; c++)
                ScreenAt(r, c) = ' ';
        }
    }

    private void Reset()
    {
        ClearScreen();
        _cursorRow = 0;
        _cursorCol = 0;
        _scrollTop = 0;
        _scrollBottom = _rows - 1;
        _alternateCharset = false;
        _state = State.Normal;
        _utf8Remaining = 0;
        _utf8CodePoint = 0;
        _dirty = true;
    }
}
