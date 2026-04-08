// =============================================================================
//  Adaptive Futures Agent – Version 1
//  FeatureCalculator.cs
//
//  Purpose:
//      Calculates all NT8-side Version 1 features for MNQ intraday trading.
//      Keeps feature logic completely separate from execution logic.
//      Exposes clean properties that AdaptiveExecutionStrategy.cs can read
//      and forward to market_state.json via the file-based JSON bridge.
//
//  Usage:
//      1. Instantiate FeatureCalculator in OnStateChange (State.DataLoaded).
//      2. Call Update() once per bar in OnBarUpdate.
//      3. Read properties (ATRValue, VWAPValue, Phase, …) or call ToDict()
//         to get a serialisation-ready snapshot.
//
//  IMPORTANT – Timezone note:
//      Session boundary constants assume US/Eastern (ET).
//      NT8's Time[0] uses the machine timezone or the session template
//      timezone.  Adjust the static boundary TimeSpans below if your NT8
//      instance runs in a different zone (e.g. CT = subtract 1 hour).
//
//  Version 1 constraints: MNQ only · intraday only · no execution logic here
// =============================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
#endregion

namespace NinjaTrader.NinjaScript.Strategies.AdaptiveFutures
{
    // =========================================================================
    //  Enumerations
    // =========================================================================

    /// <summary>Broad intraday session phases (ET-based).</summary>
    public enum SessionPhase
    {
        PreMarket,      // before 09:30
        OpeningRange,   // 09:30 – OR end (default 10:00)
        AmSession,      // 10:00 – 12:00
        Midday,         // 12:00 – 14:00
        PmSession,      // 14:00 – 15:30
        Close,          // 15:30 – 16:00
        PostMarket      // after 16:00
    }

    /// <summary>Current price position relative to the Opening Range.</summary>
    public enum ORBreakStatus
    {
        Unset,      // OR not yet established (still inside OR window)
        Inside,     // price is within the OR high/low
        AboveHigh,  // close broke above OR high
        BelowLow    // close broke below OR low
    }


    // =========================================================================
    //  FeatureCalculator
    // =========================================================================

    /// <summary>
    /// Stateful, session-aware feature calculator for Adaptive Futures Agent v1.
    /// Plain C# class – no NinjaScript inheritance – designed to be owned by
    /// AdaptiveExecutionStrategy and called from its OnBarUpdate.
    /// </summary>
    public class FeatureCalculator
    {
        // =====================================================================
        //  PRIVATE FIELDS
        // =====================================================================

        #region ── Parent strategy reference ───────────────────────────────────
        // Read-only; gives access to Close[], High[], Low[], Volume[], Time[],
        // Bars.IsFirstBarOfSession, CurrentBar, etc.
        private readonly StrategyBase _s;
        #endregion

        #region ── Indicator references (passed in from strategy) ───────────────
        // Using NinjaScript indicator objects so we can index back in time
        // (e.g. _ema[3] = EMA value 3 bars ago) without a secondary data series.

        private readonly ATR _atr;           // short-period ATR  (e.g. ATR(14))
        private readonly ATR _atrBaseline;   // long-period ATR   (e.g. ATR(50))
        private readonly EMA _ema;           // trend EMA         (e.g. EMA(20))
        #endregion

        #region ── Session boundary constants (ET) ─────────────────────────────
        private static readonly TimeSpan TsRthOpen  = new TimeSpan( 9, 30, 0);
        private static readonly TimeSpan TsRthClose = new TimeSpan(16,  0, 0);
        private static readonly TimeSpan TsAmEnd    = new TimeSpan(12,  0, 0);
        private static readonly TimeSpan TsMiddayEnd= new TimeSpan(14,  0, 0);
        private static readonly TimeSpan TsPmEnd    = new TimeSpan(15, 30, 0);
        #endregion

        #region ── Configurable parameters ─────────────────────────────────────
        private readonly TimeSpan _orEndTime;       // end of opening-range window
        private readonly int      _emaSlopeLookback;
        private readonly int      _vwapSlopeLookback;
        #endregion

        #region ── VWAP state ──────────────────────────────────────────────────
        private double        _vwapNumer;        // Σ(typicalPrice × volume)
        private double        _vwapDenom;        // Σ(volume)
        private List<double>  _vwapHistory;      // rolling buffer for slope calc
        #endregion

        #region ── Opening Range state ─────────────────────────────────────────
        private double _orBuildHigh;    // accumulator while inside OR window
        private double _orBuildLow;
        private bool   _orLocked;       // true once OR window has closed
        #endregion

        #region ── Session High / Low state ────────────────────────────────────
        private double _sessHigh;
        private double _sessLow;
        #endregion

        #region ── Prior Day state ─────────────────────────────────────────────
        // We derive prior-day H/L by capturing the completed session at each
        // session boundary.  On day 1 these remain 0 until a full session completes.
        private double _priorDayHigh;
        private double _priorDayLow;
        private bool   _priorDaySet;
        #endregion


        // =====================================================================
        //  CONSTRUCTOR
        // =====================================================================

        /// <summary>
        /// Create a FeatureCalculator.  Call this inside OnStateChange when
        /// State == State.DataLoaded (after indicators are ready).
        /// </summary>
        /// <param name="strategy">Owning strategy – must NOT be null.</param>
        /// <param name="atr">ATR instance, e.g. ATR(14).</param>
        /// <param name="atrBaseline">
        ///     Wider ATR for ATRRelative, e.g. ATR(50).  Pass null to skip.
        /// </param>
        /// <param name="ema">EMA instance, e.g. EMA(20).</param>
        /// <param name="orEndTime">
        ///     End of opening-range window in ET.  Defaults to 10:00.
        /// </param>
        /// <param name="emaSlopeLookback">Bars back for EMA slope.  Default 3.</param>
        /// <param name="vwapSlopeLookback">Bars back for VWAP slope.  Default 5.</param>
        public FeatureCalculator(
            StrategyBase strategy,
            ATR          atr,
            ATR          atrBaseline,
            EMA          ema,
            TimeSpan?    orEndTime        = null,
            int          emaSlopeLookback  = 3,
            int          vwapSlopeLookback = 5)
        {
            _s                 = strategy   ?? throw new ArgumentNullException("strategy");
            _atr               = atr        ?? throw new ArgumentNullException("atr");
            _atrBaseline       = atrBaseline;   // nullable – relative ATR is optional
            _ema               = ema        ?? throw new ArgumentNullException("ema");
            _orEndTime         = orEndTime ?? new TimeSpan(10, 0, 0);
            _emaSlopeLookback  = emaSlopeLookback;
            _vwapSlopeLookback = vwapSlopeLookback;

            _vwapHistory = new List<double>();
            ResetSession(savePriorDay: false);
        }


        // =====================================================================
        //  PUBLIC PROPERTIES  (all written exclusively inside Update())
        // =====================================================================

        // ── ATR ──────────────────────────────────────────────────────────────
        /// <summary>Current ATR value (short-period, e.g. 14).</summary>
        public double ATRValue { get; private set; }

        /// <summary>
        /// Short ATR / baseline ATR.  Values >1 indicate elevated volatility vs
        /// the baseline period.  Returns -1 when baseline is unavailable or zero.
        /// </summary>
        public double ATRRelative { get; private set; }

        // ── VWAP ─────────────────────────────────────────────────────────────
        /// <summary>Current session VWAP (typical-price, resets each RTH session).</summary>
        public double VWAPValue { get; private set; }

        /// <summary>
        /// (Close − VWAP) / ATR.  Positive = price above VWAP.
        /// Normalised so Python can compare across instruments/sessions.
        /// Returns 0 when ATR is zero.
        /// </summary>
        public double VWAPDistanceATR { get; private set; }

        /// <summary>
        /// VWAP change over the last N bars (vwapSlopeLookback).
        /// Positive = rising.  Returns -999 while insufficient history exists.
        /// </summary>
        public double VWAPSlope { get; private set; }

        // ── EMA ───────────────────────────────────────────────────────────────
        /// <summary>Current EMA value (period set by caller, e.g. 20).</summary>
        public double EMAValue { get; private set; }

        /// <summary>
        /// EMA[0] − EMA[emaSlopeLookback].  Positive = rising.
        /// Returns -999 while insufficient history exists.
        /// </summary>
        public double EMASlope { get; private set; }

        // ── Opening Range ─────────────────────────────────────────────────────
        /// <summary>Opening range high.  0 until OR window closes.</summary>
        public double ORHigh { get; private set; }

        /// <summary>Opening range low.  0 until OR window closes.</summary>
        public double ORLow  { get; private set; }

        /// <summary>ORHigh − ORLow in points.  0 until OR window closes.</summary>
        public double ORSize { get; private set; }

        /// <summary>Current OR break status.</summary>
        public ORBreakStatus ORBreak { get; private set; }

        // ── Session High / Low ────────────────────────────────────────────────
        /// <summary>Session high observed so far today.</summary>
        public double SessionHigh  { get; private set; }

        /// <summary>Session low observed so far today.</summary>
        public double SessionLow   { get; private set; }

        /// <summary>SessionHigh − SessionLow in points.</summary>
        public double SessionRange { get; private set; }

        // ── Prior Day ─────────────────────────────────────────────────────────
        /// <summary>Prior RTH session high.  0 on day 1 until a full session completes.</summary>
        public double PriorDayHigh { get; private set; }

        /// <summary>Prior RTH session low.  0 on day 1 until a full session completes.</summary>
        public double PriorDayLow  { get; private set; }

        /// <summary>
        /// (Close − PriorDayHigh) / ATR.  Negative = below PDH.
        /// Returns 0 when prior day data is unavailable.
        /// </summary>
        public double PriorDayHighDistATR { get; private set; }

        /// <summary>
        /// (Close − PriorDayLow) / ATR.  Positive = above PDL.
        /// Returns 0 when prior day data is unavailable.
        /// </summary>
        public double PriorDayLowDistATR  { get; private set; }

        // ── Session Timing ────────────────────────────────────────────────────
        /// <summary>Current intraday session phase.</summary>
        public SessionPhase Phase { get; private set; }

        /// <summary>Minutes elapsed since 09:30 RTH open.  0 pre-market.</summary>
        public double MinutesSinceOpen  { get; private set; }

        /// <summary>Minutes remaining until 16:00 RTH close.  0 outside RTH.</summary>
        public double MinutesUntilClose { get; private set; }

        // ── Relative Volume (STUB) ────────────────────────────────────────────
        /// <summary>
        /// [STUB – V1]
        /// Current bar volume / average volume at this time-of-day bucket.
        /// Returns -1 until a historical volume profile is implemented (V2+).
        /// TODO V2: load a CSV/JSON volume profile keyed by 30-min bucket.
        /// </summary>
        public double RelativeVolume { get; private set; }


        // =====================================================================
        //  UPDATE  (call once per bar from OnBarUpdate)
        // =====================================================================

        /// <summary>
        /// Recalculates all features for the current bar.
        /// Must be called from the primary data series only
        /// (guard with: if (BarsInProgress != 0) return;).
        /// </summary>
        public void Update()
        {
            // Snapshot current bar values into locals for clarity
            DateTime barTime = _s.Time[0];
            TimeSpan ts      = barTime.TimeOfDay;
            double   close   = _s.Close[0];
            double   high    = _s.High[0];
            double   low     = _s.Low[0];
            double   volume  = _s.Volume[0];

            // ── 1. Session boundary ──────────────────────────────────────────
            // IsFirstBarOfSession fires on the very first bar of each new RTH
            // session (or overnight session depending on template config).
            if (_s.Bars.IsFirstBarOfSession)
                ResetSession(savePriorDay: true);

            // ── 2. Session High / Low ────────────────────────────────────────
            if (high > _sessHigh) _sessHigh = high;
            if (low  < _sessLow)  _sessLow  = low;

            // ── 3. ATR ───────────────────────────────────────────────────────
            CalcATR();

            // ── 4. Timing ────────────────────────────────────────────────────
            CalcTiming(ts);

            // ── 5. VWAP ──────────────────────────────────────────────────────
            CalcVWAP(high, low, close, volume);

            // ── 6. EMA ───────────────────────────────────────────────────────
            CalcEMA();

            // ── 7. Opening Range ─────────────────────────────────────────────
            CalcOpeningRange(ts, high, low, close);

            // ── 8. Session aggregate outputs ─────────────────────────────────
            SessionHigh  = (_sessHigh != double.MinValue) ? _sessHigh : 0;
            SessionLow   = (_sessLow  != double.MaxValue) ? _sessLow  : 0;
            SessionRange = (SessionHigh > 0 && SessionLow > 0)
                           ? SessionHigh - SessionLow
                           : 0;

            // ── 9. Prior Day distances ────────────────────────────────────────
            CalcPriorDay(close);

            // ── 10. Relative Volume (STUB) ────────────────────────────────────
            // Replace with real volume-profile logic in V2.
            RelativeVolume = -1;
        }


        // =====================================================================
        //  SERIALISATION HELPER
        // =====================================================================

        /// <summary>
        /// Returns all features as a flat Dictionary ready for JSON serialisation
        /// into market_state.json.
        /// Sentinel values (-1 or -999) indicate unavailable/stub features;
        /// the Python side should handle these gracefully.
        /// </summary>
        public Dictionary<string, object> ToDict()
        {
            return new Dictionary<string, object>
            {
                // ATR
                { "atr",                       Round4(ATRValue)              },
                { "atr_relative",              Round4(ATRRelative)           },

                // VWAP
                { "vwap",                      Round2(VWAPValue)             },
                { "vwap_distance_atr",         Round4(VWAPDistanceATR)       },
                { "vwap_slope",                Round4(VWAPSlope)             },

                // EMA
                { "ema",                       Round2(EMAValue)              },
                { "ema_slope",                 Round4(EMASlope)              },

                // Opening Range
                { "or_high",                   Round2(ORHigh)                },
                { "or_low",                    Round2(ORLow)                 },
                { "or_size",                   Round2(ORSize)                },
                { "or_break",                  ORBreak.ToString()            },

                // Session H/L
                { "session_high",              Round2(SessionHigh)           },
                { "session_low",               Round2(SessionLow)            },
                { "session_range",             Round2(SessionRange)          },

                // Prior Day
                { "prior_day_high",            Round2(PriorDayHigh)          },
                { "prior_day_low",             Round2(PriorDayLow)           },
                { "prior_day_high_dist_atr",   Round4(PriorDayHighDistATR)   },
                { "prior_day_low_dist_atr",    Round4(PriorDayLowDistATR)    },

                // Timing
                { "session_phase",             Phase.ToString()              },
                { "minutes_since_open",        Round1(MinutesSinceOpen)      },
                { "minutes_until_close",       Round1(MinutesUntilClose)     },

                // Relative Volume (stub)
                { "relative_volume",           RelativeVolume                },
            };
        }


        // =====================================================================
        //  PRIVATE CALCULATION METHODS
        // =====================================================================

        #region ── Session reset ────────────────────────────────────────────────

        /// <summary>
        /// Resets all intraday accumulators.
        /// If savePriorDay is true, the completed session H/L is captured first
        /// so that prior-day distances are available during the new session.
        /// </summary>
        private void ResetSession(bool savePriorDay)
        {
            if (savePriorDay && _sessHigh != double.MinValue)
            {
                _priorDayHigh = _sessHigh;
                _priorDayLow  = _sessLow;
                _priorDaySet  = true;
            }

            _vwapNumer   = 0;
            _vwapDenom   = 0;
            _vwapHistory.Clear();
            _orBuildHigh = double.MinValue;
            _orBuildLow  = double.MaxValue;
            _orLocked    = false;
            _sessHigh    = double.MinValue;
            _sessLow     = double.MaxValue;

            // Reset published OR properties; H/L/range are reset via _sess vars above
            ORHigh   = 0;
            ORLow    = 0;
            ORSize   = 0;
            ORBreak  = ORBreakStatus.Unset;
            VWAPSlope = -999;
            EMASlope  = -999;
        }

        #endregion

        #region ── ATR ──────────────────────────────────────────────────────────

        private void CalcATR()
        {
            ATRValue = _atr[0];

            if (_atrBaseline != null && _atrBaseline[0] > 0)
                ATRRelative = ATRValue / _atrBaseline[0];
            else
                ATRRelative = -1; // baseline not available
        }

        #endregion

        #region ── Timing ───────────────────────────────────────────────────────

        private void CalcTiming(TimeSpan ts)
        {
            // Session phase
            if      (ts < TsRthOpen)  Phase = SessionPhase.PreMarket;
            else if (ts < _orEndTime) Phase = SessionPhase.OpeningRange;
            else if (ts < TsAmEnd)    Phase = SessionPhase.AmSession;
            else if (ts < TsMiddayEnd)Phase = SessionPhase.Midday;
            else if (ts < TsPmEnd)    Phase = SessionPhase.PmSession;
            else if (ts < TsRthClose) Phase = SessionPhase.Close;
            else                      Phase = SessionPhase.PostMarket;

            // Minutes since RTH open
            MinutesSinceOpen  = (ts >= TsRthOpen)
                                ? Math.Max(0, (ts - TsRthOpen).TotalMinutes)
                                : 0;

            // Minutes until RTH close
            MinutesUntilClose = (ts >= TsRthOpen && ts < TsRthClose)
                                ? (TsRthClose - ts).TotalMinutes
                                : 0;
        }

        #endregion

        #region ── VWAP ─────────────────────────────────────────────────────────

        private void CalcVWAP(double high, double low, double close, double volume)
        {
            double typical  = (high + low + close) / 3.0;
            _vwapNumer     += typical * volume;
            _vwapDenom     += volume;

            double vwap = (_vwapDenom > 0) ? _vwapNumer / _vwapDenom : close;
            VWAPValue = vwap;

            // VWAP distance in ATR units
            VWAPDistanceATR = (ATRValue > 0) ? (close - vwap) / ATRValue : 0;

            // VWAP slope: keep a rolling list of recent VWAP values
            _vwapHistory.Add(vwap);
            if (_vwapHistory.Count > _vwapSlopeLookback + 1)
                _vwapHistory.RemoveAt(0);

            VWAPSlope = (_vwapHistory.Count == _vwapSlopeLookback + 1)
                        ? vwap - _vwapHistory[0]   // current minus oldest in buffer
                        : -999;                     // insufficient history
        }

        #endregion

        #region ── EMA ──────────────────────────────────────────────────────────

        private void CalcEMA()
        {
            EMAValue = _ema[0];

            // _ema[n] is the EMA value n bars ago – standard NinjaScript indexing
            EMASlope = (_s.CurrentBar >= _emaSlopeLookback)
                       ? _ema[0] - _ema[_emaSlopeLookback]
                       : -999;
        }

        #endregion

        #region ── Opening Range ─────────────────────────────────────────────────

        private void CalcOpeningRange(TimeSpan ts, double high, double low, double close)
        {
            // Accumulate OR while inside the opening-range window
            bool insideORWindow = (ts >= TsRthOpen && ts < _orEndTime);

            if (!_orLocked && insideORWindow)
            {
                if (high > _orBuildHigh) _orBuildHigh = high;
                if (low  < _orBuildLow)  _orBuildLow  = low;
            }

            // Lock the OR the moment the window closes (and we have valid data)
            if (!_orLocked && ts >= _orEndTime && _orBuildHigh != double.MinValue)
            {
                _orLocked = true;
                ORHigh    = _orBuildHigh;
                ORLow     = _orBuildLow;
                ORSize    = ORHigh - ORLow;
            }

            // Evaluate break status
            if (_orLocked)
            {
                if      (close > ORHigh) ORBreak = ORBreakStatus.AboveHigh;
                else if (close < ORLow)  ORBreak = ORBreakStatus.BelowLow;
                else                     ORBreak = ORBreakStatus.Inside;
            }
            // else ORBreak stays Unset (set in ResetSession)
        }

        #endregion

        #region ── Prior Day distances ──────────────────────────────────────────

        private void CalcPriorDay(double close)
        {
            if (_priorDaySet && ATRValue > 0)
            {
                PriorDayHigh        = _priorDayHigh;
                PriorDayLow         = _priorDayLow;
                PriorDayHighDistATR = (close - _priorDayHigh) / ATRValue;
                PriorDayLowDistATR  = (close - _priorDayLow)  / ATRValue;
            }
            else
            {
                PriorDayHigh        = 0;
                PriorDayLow         = 0;
                PriorDayHighDistATR = 0;
                PriorDayLowDistATR  = 0;
            }
        }

        #endregion

        #region ── Rounding helpers ─────────────────────────────────────────────

        private static double Round1(double v) => Math.Round(v, 1);
        private static double Round2(double v) => Math.Round(v, 2);
        private static double Round4(double v) => Math.Round(v, 4);

        #endregion
    }
}
