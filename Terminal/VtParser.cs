using System;
using System.Text;

namespace KillerScan.Terminal
{

    internal interface IVtHandler
    {

        void Print(int codepoint);

        void Execute(byte control);

        void CsiDispatch(char final, int[] pars, char prefix, char intermediate);

        void EscDispatch(char final, char intermediate);

        void OscDispatch(int command, string data);
    }

    internal sealed class VtParser(IVtHandler handler)
    {
        private enum S
        {
            Ground, Escape, EscInt,
            CsiEntry, CsiParam, CsiInt, CsiIgnore,
            OscString,
            DcsEntry, DcsParam, DcsInt, DcsPass, DcsIgnore,
            SosPmApc,
        }

        private readonly IVtHandler _h = handler;
        private S _state = S.Ground;

        private const int MaxParams = 32;
        private readonly int[] _pars = new int[MaxParams];
        private int _parCount;
        private bool _parSeen;          // distinguishes "CSI m" from "CSI 0 m"
        private char _prefix, _inter;

        private readonly StringBuilder _osc = new();
        private int _oscCmd;
        private bool _oscCmdDone;

        private int _utfLeft, _utfCp, _utfMin;

        public void Feed(byte[] buf, int count)
        {
            for (int i = 0; i < count; i++) Step(buf[i]);
        }

        private void Step(byte b)
        {

            if (_utfLeft > 0)
            {
                if ((b & 0xC0) == 0x80)
                {
                    _utfCp = (_utfCp << 6) | (b & 0x3F);
                    if (--_utfLeft == 0)
                    {

                        if (_utfCp < _utfMin || (_utfCp >= 0xD800 && _utfCp <= 0xDFFF) || _utfCp > 0x10FFFF)
                            _h.Print(0xFFFD);
                        else
                            _h.Print(_utfCp);
                    }
                    return;
                }
                _utfLeft = 0;
                _h.Print(0xFFFD);

            }

            if (b == 0x1B) { Clear(); _state = S.Escape; return; }
            if (b == 0x18 || b == 0x1A) { _state = S.Ground; _h.Execute(b); return; }

            switch (_state)
            {
                case S.Ground: Ground(b); break;

                case S.Escape:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b < 0x30) { _inter = (char)b; _state = S.EscInt; break; }
                    switch (b)
                    {
                        case (byte)'[': _state = S.CsiEntry; break;
                        case (byte)']': _oscCmd = 0; _oscCmdDone = false; _osc.Clear(); _state = S.OscString; break;
                        case (byte)'P': _state = S.DcsEntry; break;
                        case (byte)'X': case (byte)'^': case (byte)'_': _state = S.SosPmApc; break;
                        default: _h.EscDispatch((char)b, '\0'); _state = S.Ground; break;
                    }
                    break;

                case S.EscInt:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b < 0x30) { _inter = (char)b; break; }
                    _h.EscDispatch((char)b, _inter);
                    _state = S.Ground;
                    break;

                case S.CsiEntry:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b >= 0x3C && b <= 0x3F) { _prefix = (char)b; _state = S.CsiParam; break; }
                    if (b == ';' || (b >= '0' && b <= '9')) { _state = S.CsiParam; Param(b); break; }
                    if (b < 0x30) { _inter = (char)b; _state = S.CsiInt; break; }
                    Csi(b);
                    break;

                case S.CsiParam:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b == ';' || (b >= '0' && b <= '9')) { Param(b); break; }
                    if (b >= 0x3C && b <= 0x3F) { _state = S.CsiIgnore; break; }   // prefix after params is malformed
                    if (b < 0x30) { _inter = (char)b; _state = S.CsiInt; break; }
                    Csi(b);
                    break;

                case S.CsiInt:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b < 0x30) { _inter = (char)b; break; }
                    if (b < 0x40) { _state = S.CsiIgnore; break; }
                    Csi(b);
                    break;

                case S.CsiIgnore:
                    if (b < 0x20) { _h.Execute(b); break; }
                    if (b >= 0x40) _state = S.Ground;
                    break;

                case S.OscString:

                    if (b == 0x07) { FlushOsc(); break; }
                    if (!_oscCmdDone)
                    {
                        if (b >= '0' && b <= '9') { _oscCmd = _oscCmd * 10 + (b - '0'); break; }
                        if (b == ';') { _oscCmdDone = true; break; }
                        _oscCmdDone = true;   // no numeric command, treat the rest as data
                    }
                    if (_osc.Length < 4096) _osc.Append((char)b);
                    break;

                case S.DcsEntry:
                    if (b >= 0x3C && b <= 0x3F) { _state = S.DcsParam; break; }
                    if (b == ';' || (b >= '0' && b <= '9')) { _state = S.DcsParam; break; }
                    if (b < 0x30) { _state = S.DcsInt; break; }
                    _state = S.DcsPass;
                    break;
                case S.DcsParam:
                    if (b == ';' || (b >= '0' && b <= '9')) break;
                    if (b < 0x30) { _state = S.DcsInt; break; }
                    _state = b < 0x40 ? S.DcsIgnore : S.DcsPass;
                    break;
                case S.DcsInt:
                    if (b < 0x30) break;
                    _state = b < 0x40 ? S.DcsIgnore : S.DcsPass;
                    break;
                case S.DcsPass:
                case S.DcsIgnore:
                case S.SosPmApc:
                    break;   // ends at the ESC that starts ST, handled above
            }
        }

        private void Ground(byte b)
        {
            if (b < 0x20 || b == 0x7F) { _h.Execute(b); return; }

            if (b < 0x80) { _h.Print(b); return; }

            if ((b & 0xE0) == 0xC0)      { _utfLeft = 1; _utfCp = b & 0x1F; _utfMin = 0x80; }
            else if ((b & 0xF0) == 0xE0) { _utfLeft = 2; _utfCp = b & 0x0F; _utfMin = 0x800; }
            else if ((b & 0xF8) == 0xF0) { _utfLeft = 3; _utfCp = b & 0x07; _utfMin = 0x10000; }
            else _h.Print(0xFFFD);       // a stray continuation byte or an illegal lead
        }

        private void Param(byte b)
        {
            _parSeen = true;
            if (b == ';')
            {
                if (_parCount < MaxParams - 1) _parCount++;
                return;
            }
            if (_parCount >= MaxParams) return;

            long v = (long)_pars[_parCount] * 10 + (b - '0');
            _pars[_parCount] = v > 65535 ? 65535 : (int)v;
        }

        private void Csi(byte final)
        {
            int n = _parSeen ? _parCount + 1 : 0;
            var pars = new int[n];
            Array.Copy(_pars, pars, n);
            _h.CsiDispatch((char)final, pars, _prefix, _inter);
            _state = S.Ground;
        }

        private void FlushOsc()
        {
            _h.OscDispatch(_oscCmd, _osc.ToString());
            _osc.Clear();
            _state = S.Ground;
        }

        private void Clear()
        {
            Array.Clear(_pars, 0, _pars.Length);
            _parCount = 0;
            _parSeen = false;
            _prefix = '\0';
            _inter = '\0';
        }
    }
}
