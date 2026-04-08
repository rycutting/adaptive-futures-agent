// =============================================================================
//  Adaptive Futures Agent – Version 1
//  AdaptiveExecutionStrategy.cs
//
//  Instrument: MNQ only
//  Session:    Intraday RTH (no first / last 15 min)
//  Positions:  One at a time, managed orders
//
//  Dependencies (must be compiled in the same NT8 project):
//    FeatureCalculator.cs       – namespace NinjaTrader.NinjaScript.Strategies.AdaptiveFutures
//    IntegrationStateManager.cs – namespace AdaptiveFuturesAgent.NinjaTrader.Integration
//
//  JSON contract:
//    NT8 writes  → market_state.json      (Python reads via market_state_reader.py)
//    Python writes → decision_response.json (NT8 reads via IntegrationStateManager)
// =============================================================================

#region Using declarations
using System;
using System.Collections.Generic;
using System.IO;
using NinjaTrader.Cbi;
using NinjaTrader.Data;
using System.ComponentModel.DataAnnotations;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.Indicators;
using NinjaTrader.NinjaScript.Strategies;

// Custom project namespaces — defined in companion .cs files
using AdaptiveFuturesAgent.NinjaTrader.Integration;           // IntegrationStateManager, DecisionResponseV1, ExecutionActions
using NinjaTrader.NinjaScript.Strategies.AdaptiveFutures;     // FeatureCalculator, SessionPhase
#endregion

namespace NinjaTrader.NinjaScript.Strategies
{
    public class AdaptiveExecutionStrategy : Strategy
    {
        // ── Instrument constant ───────────────────────────────────────────────
        private const string INSTRUMENT = "MNQ";

        // ── NT8 indicator references ──────────────────────────────────────────
        // Created in Configure, passed into FeatureCalculator.
        // NT8 manages their lifetime; we hold references only.
        private ATR _atr;           // fast ATR   (default 14)
        private ATR _atrBaseline;   // slow ATR   (default 50)
        private EMA _ema;           // trend EMA  (default 20)

        // ── Sub-components ────────────────────────────────────────────────────
        private FeatureCalculator       _features;
        private IntegrationStateManager _integrationMgr;

        // ── Session boundary (ET) ─────────────────────────────────────────────
        // FeatureCalculator already uses these same constants internally;
        // we keep them here for the strategy-side timing gate.
        private static readonly TimeSpan RthOpen  = new TimeSpan( 9, 30, 0);
        private static readonly TimeSpan RthClose = new TimeSpan(16,  0, 0);

        // ── Deduplication ─────────────────────────────────────────────────────
        // DecisionResponseV1 has no decision_id / IntegrationMeta property.
        // TimestampEt is the next best unique-per-cycle identifier.
        private string _lastDecisionTimestampEt = string.Empty;

        // ── Intraday trade / P&L trackers ─────────────────────────────────────
        // Reset at the start of each RTH session (detected via IsFirstBarOfSession).
        // Written into strategy_state so Python can apply daily-limit policy.
        private int    _tradeCountToday      = 0;
        private int    _consecutiveLosses    = 0;
        private double _realizedPnlToday     = 0.0;
        private double _highWaterPnlToday    = 0.0;
        private bool   _sessionResetPending  = true;   // reset on first bar of each session

        // ── Configurable parameters (NT8 strategy UI) ─────────────────────────
        [NinjaScriptProperty]
        [Display(Name = "Integration folder", Order = 1, GroupName = "File Integration")]
        public string IntegrationFolder { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "Max decision age (seconds)", Order = 2, GroupName = "File Integration")]
        public int MaxDecisionAgeSeconds { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "No-trade buffer (minutes)", Order = 3, GroupName = "Risk")]
        public int NoTradeBufferMinutes { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR fast period", Order = 4, GroupName = "Indicators")]
        public int AtrFastPeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "ATR baseline period", Order = 5, GroupName = "Indicators")]
        public int AtrBaselinePeriod { get; set; }

        [NinjaScriptProperty]
        [Display(Name = "EMA period", Order = 6, GroupName = "Indicators")]
        public int EmaPeriod { get; set; }


        // =====================================================================
        //  NT8 LIFECYCLE
        // =====================================================================

        protected override void OnStateChange()
        {
            if (State == State.SetDefaults)
            {
                Description          = "Adaptive Futures Agent v1 — MNQ execution shell";
                Name                 = "AdaptiveExecutionStrategy";
                Calculate            = Calculate.OnBarClose;
                IsOverlay            = false;
                IsUnmanaged          = false;   // managed order API

                IntegrationFolder    = @"C:\Users\Ryan\OneDrive\Adaptive_Futures_Agent\runtime\integration";
                MaxDecisionAgeSeconds = 10;
                NoTradeBufferMinutes = 15;
                AtrFastPeriod        = 14;
                AtrBaselinePeriod    = 50;
                EmaPeriod            = 20;
            }
            else if (State == State.Configure)
            {
                // Attach indicators to the primary series.
                // Must happen in Configure so NT8 sets up look-back correctly.
                _atr         = ATR(AtrFastPeriod);
                _atrBaseline = ATR(AtrBaselinePeriod);
                _ema         = EMA(EmaPeriod);
            }
            else if (State == State.DataLoaded)
            {
                // FeatureCalculator signature (from source):
                //   FeatureCalculator(StrategyBase, ATR, ATR, EMA,
                //                     TimeSpan? orEndTime, int emaSlopeLookback, int vwapSlopeLookback)
                // orEndTime / slope lookbacks use defaults.
                _features = new FeatureCalculator(
                    this,
                    _atr,
                    _atrBaseline,
                    _ema
                );

                // IntegrationStateManager signature (from source):
                //   IntegrationStateManager(string marketStatePath, string decisionResponsePath)
                _integrationMgr = new IntegrationStateManager(
                    Path.Combine(IntegrationFolder, "market_state.json"),
                    Path.Combine(IntegrationFolder, "decision_response.json")
                );

                EnsureIntegrationFolder();
                Print("[AFA] Initialised. Integration folder: " + IntegrationFolder);
            }
            else if (State == State.Terminated)
            {
                TryWriteShutdownState();
            }
        }


        // =====================================================================
        //  MAIN BAR LOOP
        // =====================================================================

        protected override void OnBarUpdate()
        {
            // Primary series only
            if (BarsInProgress != 0)
                return;

            // Wait for the slowest indicator to warm up
            if (CurrentBar < AtrBaselinePeriod + 2)
                return;

            // Reset intraday trackers on the first bar of each new session
            if (Bars.IsFirstBarOfSession)
            {
                if (_sessionResetPending)
                {
                    ResetSessionTrackers();
                    _sessionResetPending = false;
                }
            }
            else
            {
                _sessionResetPending = true;
            }

            // ── Step 1: Refresh features ──────────────────────────────────────
            // FeatureCalculator.Update() reads current bar values from its
            // indicator references and recalculates all derived fields.
            _features.Update();

            // ── Step 2: Write market_state.json ───────────────────────────────
            WriteMarketState();

            // ── Step 3: Read decision_response.json ───────────────────────────
            // ReadDecisionResponse(expectedInstrument) validates instrument match
            // and returns DecisionResponseV1.NoTrade(...) on any failure —
            // never returns null.
            DecisionResponseV1 decision = _integrationMgr.ReadDecisionResponse(INSTRUMENT);

            // ── Step 4: Validate all gates ────────────────────────────────────
            if (!IsDecisionActionable(decision))
                return;

            // ── Step 5: Execute ───────────────────────────────────────────────
            string action = decision.ExecutionPlan.Action;  // already normalised to upper

            if (string.Equals(action, ExecutionActions.EnterLong, StringComparison.OrdinalIgnoreCase))
                EnterTradeLong(decision);
            else if (string.Equals(action, ExecutionActions.EnterShort, StringComparison.OrdinalIgnoreCase))
                EnterTradeShort(decision);
            // ExecutionActions.NoTrade falls through — nothing to do

            // Stamp so we do not re-act on the same decision next bar
            _lastDecisionTimestampEt = decision.TimestampEt;
        }


        // =====================================================================
        //  STEP 2 — BUILD AND WRITE market_state.json
        //
        //  Field layout must satisfy market_state_reader.py REQUIRED_FIELDS:
        //    timestamp_et, instrument, bar_interval, session_phase,
        //    time_since_open_minutes, time_until_close_minutes,
        //    price (dict), indicators (dict), session_structure (dict),
        //    volume (dict), strategy_state (dict), integration_state (dict)
        //
        //  Feature values come exclusively from _features.ToDict() —
        //  do not re-read indicator objects directly here.
        // =====================================================================

        private void WriteMarketState()
        {
            try
            {
                // Pull the full feature snapshot once; distribute into sub-dicts below.
                Dictionary<string, object> fd = _features.ToDict();

                // Execution context
                bool   inPos   = Position.MarketPosition != MarketPosition.Flat;
                string posDir  = Position.MarketPosition == MarketPosition.Long  ? "long"
                               : Position.MarketPosition == MarketPosition.Short ? "short"
                               : "flat";
                double unrealPnl = inPos
                    ? Position.GetUnrealizedProfitLoss(PerformanceUnit.Currency, Close[0])
                    : 0.0;

                // ── Payload ───────────────────────────────────────────────────
                // Top-level scalar fields match REQUIRED_FIELDS exactly.
                // session_phase and time_* come from FeatureCalculator so they
                // are consistent with the features block.
                var payload = new Dictionary<string, object>
                {
                    // ── Required top-level scalars ────────────────────────────
                    // timestamp_et: Time[0] is in ET for MNQ (CME Globex / RT session)
                    ["timestamp_et"]             = Time[0].ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["instrument"]               = INSTRUMENT,
                    // bar_interval: BarsPeriod.Value is the period count (e.g. 1 for 1-min)
                    ["bar_interval"]             = BarsPeriod.Value.ToString(),
                    // session_phase, time_* forwarded from FeatureCalculator
                    // (avoids duplicating the phase logic)
                    ["session_phase"]            = _features.Phase.ToString(),
                    ["time_since_open_minutes"]  = _features.MinutesSinceOpen,
                    ["time_until_close_minutes"] = _features.MinutesUntilClose,

                    // ── price (required dict) ─────────────────────────────────
                    ["price"] = new Dictionary<string, object>
                    {
                        ["open"]  = Open[0],
                        ["high"]  = High[0],
                        ["low"]   = Low[0],
                        ["close"] = Close[0]
                    },

                    // ── indicators (required dict) ────────────────────────────
                    // ATR, VWAP, EMA features from ToDict()
                    ["indicators"] = new Dictionary<string, object>
                    {
                        ["atr"]               = fd["atr"],
                        ["atr_relative"]      = fd["atr_relative"],
                        ["vwap"]              = fd["vwap"],
                        ["vwap_distance_atr"] = fd["vwap_distance_atr"],
                        ["vwap_slope"]        = fd["vwap_slope"],
                        ["ema"]               = fd["ema"],
                        ["ema_slope"]         = fd["ema_slope"]
                    },

                    // ── session_structure (required dict) ─────────────────────
                    // Opening range, session H/L, prior day distances from ToDict()
                    ["session_structure"] = new Dictionary<string, object>
                    {
                        ["or_high"]                  = fd["or_high"],
                        ["or_low"]                   = fd["or_low"],
                        ["or_size"]                  = fd["or_size"],
                        ["or_break"]                 = fd["or_break"],
                        ["session_high"]             = fd["session_high"],
                        ["session_low"]              = fd["session_low"],
                        ["session_range"]            = fd["session_range"],
                        ["prior_day_high"]           = fd["prior_day_high"],
                        ["prior_day_low"]            = fd["prior_day_low"],
                        ["prior_day_high_dist_atr"]  = fd["prior_day_high_dist_atr"],
                        ["prior_day_low_dist_atr"]   = fd["prior_day_low_dist_atr"]
                    },

                    // ── volume (required dict) ────────────────────────────────
                    // relative_volume is a stub (-1) in V1 per FeatureCalculator
                    ["volume"] = new Dictionary<string, object>
                    {
                        ["current_bar"]     = (long)Volume[0],
                        ["relative_volume"] = fd["relative_volume"]
                    },

                    // ── strategy_state (required dict) ────────────────────────
                    // Execution-side state; consumed by Python risk engine
                    ["strategy_state"] = new Dictionary<string, object>
                    {
                        ["in_position"]          = inPos,
                        ["position_direction"]   = posDir,
                        ["position_quantity"]    = Position.Quantity,
                        ["position_avg_price"]   = inPos ? Position.AveragePrice : 0.0,
                        ["unrealized_pnl"]       = unrealPnl,
                        ["trade_count_today"]    = _tradeCountToday,
                        ["consecutive_losses"]   = _consecutiveLosses,
                        ["realized_pnl_today"]   = _realizedPnlToday,
                        ["high_water_pnl_today"] = _highWaterPnlToday,
                        ["can_trade_now"]        = IsWithinTradeableWindow()
                    },

                    // ── integration_state (required dict) ─────────────────────
                    // Health / diagnostic fields for Python to inspect
                    ["integration_state"] = new Dictionary<string, object>
                    {
                        ["nt8_status"]              = "active",
                        ["bar_index"]               = CurrentBar,
                        ["last_decision_timestamp"] = _lastDecisionTimestampEt
                    }
                };

                string writeError;
                bool ok = _integrationMgr.WriteMarketState(payload, out writeError);
                if (!ok)
                    Print("[AFA] WriteMarketState failed: " + writeError);
            }
            catch (Exception ex)
            {
                Print("[AFA] WriteMarketState exception: " + ex.Message);
            }
        }


        // =====================================================================
        //  STEP 4 — VALIDATION GATES
        //  Returns false if any gate blocks execution for this bar.
        // =====================================================================

        private bool IsDecisionActionable(DecisionResponseV1 decision)
        {
            // ── Gate A: freshness ─────────────────────────────────────────────
            // IsDecisionResponseFresh(response, maxAge, utcNow, out reason)
            string freshnessReason;
            bool fresh = _integrationMgr.IsDecisionResponseFresh(
                decision,
                TimeSpan.FromSeconds(MaxDecisionAgeSeconds),
                DateTime.UtcNow,
                out freshnessReason
            );
            if (!fresh)
            {
                // Avoid flooding the log — only print when there is a real message
                if (!string.IsNullOrEmpty(freshnessReason))
                    Print("[AFA] Decision not fresh: " + freshnessReason);
                return false;
            }

            // ── Gate B: deduplication ─────────────────────────────────────────
            // Use TimestampEt as the per-cycle unique key (no IntegrationMeta in model)
            if (!string.IsNullOrEmpty(decision.TimestampEt) &&
                decision.TimestampEt == _lastDecisionTimestampEt)
                return false;   // silent — normal between bars

            // ── Gate C: AllowsNewTrade (Python's aggregate gate) ──────────────
            // Checks: IsValid, Policy.Allowed, RiskOverlay.AllowNewTrade,
            //         !ShutdownFlag, DecisionStatus == ALLOW,
            //         Action is ENTER_LONG/SHORT, Contracts > 0
            if (!decision.AllowsNewTrade)
            {
                _lastDecisionTimestampEt = decision.TimestampEt;
                return false;
            }

            // ── Gate D: one position at a time (local hard rule) ──────────────
            if (Position.MarketPosition != MarketPosition.Flat)
            {
                _lastDecisionTimestampEt = decision.TimestampEt;
                return false;
            }

            // ── Gate E: session timing window (local hard rule) ───────────────
            if (!IsWithinTradeableWindow())
            {
                Print("[AFA] Outside tradeable window — no entry.");
                _lastDecisionTimestampEt = decision.TimestampEt;
                return false;
            }

            // ── Gate F: StopDistancePoints sanity ─────────────────────────────
            // ExecutionPlanInfo exposes StopDistancePoints, not an absolute price.
            // A zero or negative stop distance is a bad signal — reject it.
            if (decision.ExecutionPlan.StopDistancePoints <= 0)
            {
                Print("[AFA] ExecutionPlan.StopDistancePoints must be > 0 — skipping.");
                _lastDecisionTimestampEt = decision.TimestampEt;
                return false;
            }

            return true;
        }


        // =====================================================================
        //  STEP 5 — TRADE ENTRY HELPERS
        //
        //  ExecutionPlanInfo provides distances in points, not absolute prices.
        //  We compute absolute stop / target from Close[0] as the entry reference.
        //  V1 uses market orders; a later version can switch to limit entry.
        // =====================================================================

        private void EnterTradeLong(DecisionResponseV1 decision)
        {
            if (Position.MarketPosition == MarketPosition.Long)
                return;  // defensive double-check

            ExecutionPlanInfo plan = decision.ExecutionPlan;
            int    qty         = plan.Contracts;
            double stopPrice   = Close[0] - plan.StopDistancePoints;
            double targetPrice = (plan.TargetDistancePoints > 0)
                                 ? Close[0] + plan.TargetDistancePoints
                                 : 0.0;

            Print(string.Format(
                "[AFA] ENTER LONG  | qty={0} | ref={1:F2} | stop={2:F2} | target={3:F2}",
                qty, Close[0], stopPrice, targetPrice
            ));

            // Market entry (V1)
            EnterLong(qty, "AFA_Long");

            // Attach stop immediately — stop distance was validated > 0 above
            ExitLongStopMarket(qty, stopPrice, "AFA_Long_SL", "AFA_Long");

            // Attach target only when Python provided one
            if (targetPrice > 0)
                ExitLongLimit(qty, targetPrice, "AFA_Long_TP", "AFA_Long");

            _tradeCountToday++;
        }

        private void EnterTradeShort(DecisionResponseV1 decision)
        {
            if (Position.MarketPosition == MarketPosition.Short)
                return;  // defensive double-check

            ExecutionPlanInfo plan = decision.ExecutionPlan;
            int    qty         = plan.Contracts;
            double stopPrice   = Close[0] + plan.StopDistancePoints;
            double targetPrice = (plan.TargetDistancePoints > 0)
                                 ? Close[0] - plan.TargetDistancePoints
                                 : 0.0;

            Print(string.Format(
                "[AFA] ENTER SHORT | qty={0} | ref={1:F2} | stop={2:F2} | target={3:F2}",
                qty, Close[0], stopPrice, targetPrice
            ));

            EnterShort(qty, "AFA_Short");

            ExitShortStopMarket(qty, stopPrice, "AFA_Short_SL", "AFA_Short");

            if (targetPrice > 0)
                ExitShortLimit(qty, targetPrice, "AFA_Short_TP", "AFA_Short");

            _tradeCountToday++;
        }


        // =====================================================================
        //  POSITION UPDATE — maintain P&L trackers
        // =====================================================================

        protected override void OnPositionUpdate(
            Position       position,
            double         averagePrice,
            int            quantity,
            MarketPosition marketPosition)
        {
            // When the position goes flat a round-trip has completed.
            // Capture the P&L from the most recently closed trade in
            // SystemPerformance so the strategy_state block stays accurate.
            if (marketPosition != MarketPosition.Flat)
                return;

            try
            {
                int tradeCount = SystemPerformance.AllTrades.TradesPerformance.NumberOfTrades;
                if (tradeCount < 1)
                    return;

                // Retrieve the last completed trade
                Trade lastTrade = SystemPerformance.AllTrades.Trades[tradeCount - 1];
                double tradePnl = lastTrade.ProfitCurrency;

                _realizedPnlToday  += tradePnl;
                _highWaterPnlToday  = Math.Max(_highWaterPnlToday, _realizedPnlToday);

                if (tradePnl < 0)
                    _consecutiveLosses++;
                else
                    _consecutiveLosses = 0;
            }
            catch (Exception ex)
            {
                Print("[AFA] OnPositionUpdate P&L tracking error: " + ex.Message);
            }
        }


        // =====================================================================
        //  HELPERS
        // =====================================================================

        /// <summary>
        /// Returns true when Time[0] is inside the tradeable window:
        ///   RTH open + buffer  →  RTH close − buffer
        /// </summary>
        private bool IsWithinTradeableWindow()
        {
            TimeSpan now   = Time[0].TimeOfDay;
            TimeSpan entry = RthOpen  + TimeSpan.FromMinutes(NoTradeBufferMinutes);
            TimeSpan exit  = RthClose - TimeSpan.FromMinutes(NoTradeBufferMinutes);
            return now >= entry && now < exit;
        }

        /// <summary>
        /// Resets all intraday counters.
        /// Called on the first bar of each new RTH session.
        /// Also clears _lastDecisionTimestampEt so the first valid
        /// decision of the day is never filtered as a duplicate.
        /// </summary>
        private void ResetSessionTrackers()
        {
            _tradeCountToday         = 0;
            _consecutiveLosses       = 0;
            _realizedPnlToday        = 0.0;
            _highWaterPnlToday       = 0.0;
            _lastDecisionTimestampEt = string.Empty;
        }

        private void EnsureIntegrationFolder()
        {
            try
            {
                if (!Directory.Exists(IntegrationFolder))
                    Directory.CreateDirectory(IntegrationFolder);
            }
            catch (Exception ex)
            {
                Print("[AFA] Cannot create integration folder: " + ex.Message);
            }
        }

        /// <summary>
        /// Best-effort shutdown notification.
        /// Writes a minimal payload so Python can detect NT8 has stopped.
        /// </summary>
        private void TryWriteShutdownState()
        {
            try
            {
                var payload = new Dictionary<string, object>
                {
                    // Satisfy REQUIRED_FIELDS with safe placeholder values
                    ["timestamp_et"]             = DateTime.Now.ToString("yyyy-MM-ddTHH:mm:ss"),
                    ["instrument"]               = INSTRUMENT,
                    ["bar_interval"]             = "0",
                    ["session_phase"]            = "Terminated",
                    ["time_since_open_minutes"]  = 0.0,
                    ["time_until_close_minutes"] = 0.0,
                    ["price"]             = new Dictionary<string, object>(),
                    ["indicators"]        = new Dictionary<string, object>(),
                    ["session_structure"] = new Dictionary<string, object>(),
                    ["volume"]            = new Dictionary<string, object>(),
                    ["strategy_state"]    = new Dictionary<string, object>(),
                    ["integration_state"] = new Dictionary<string, object>
                    {
                        ["nt8_status"] = "terminated"
                    }
                };

                string writeError;
                if (_integrationMgr != null)
    			_integrationMgr.WriteMarketState(payload, out writeError);
            }
            catch { /* swallow — strategy is terminating */ }
        }
    }
}
