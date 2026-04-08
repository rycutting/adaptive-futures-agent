"""Adaptive Futures Agent — Version 1 main orchestration loop.

Responsibilities:
  - Poll market_state.json written by NinjaTrader 8
  - Validate the state via market_state_reader
  - Run each processing layer in order (stubbed in V1)
  - Write a structured decision_response.json for NT8 to read

Design rules:
  - Fail safely on any missing or malformed input
  - Default to no-trade when any layer is unavailable
  - Keep each layer call isolated so it can be replaced without
    touching the rest of the loop
  - No execution logic lives here — this is orchestration only

Version 1 stubs:
  event_engine, regime_engine, trend_pullback_specialist,
  policy_selector, and risk_overlay are all placeholder functions.
  Each one is clearly marked with a TODO comment so they can be
  replaced with real modules without changing the loop structure.
"""

from __future__ import annotations

import logging
import time
from datetime import datetime
from pathlib import Path
from typing import Any
from zoneinfo import ZoneInfo

from python.integration.market_state_reader import (
    configure_default_logging,
    read_market_state,
)
from python.integration.decision_writer import write_decision_response

logger = logging.getLogger(__name__)


# =============================================================================
#  Configuration
# =============================================================================

# File paths — these must match IntegrationFolder in
# AdaptiveExecutionStrategy.cs exactly.
MARKET_STATE_PATH = Path(
    (
        r"C:\Users\Ryan\OneDrive\Adaptive_Futures_Agent"
        r"\runtime\integration\market_state.json"
    )
)
DECISION_RESPONSE_PATH = Path(
    (
        r"C:\Users\Ryan\OneDrive\Adaptive_Futures_Agent"
        r"\runtime\integration\decision_response.json"
    )
)

# Polling interval in seconds.
# Bar-based decisions do not need sub-second polling.
POLL_INTERVAL_SECONDS: float = 1.0

# Instrument this loop is authorised to trade
INSTRUMENT: str = "MNQ"

# Eastern timezone — used for all decision timestamps
ET = ZoneInfo("America/New_York")


# =============================================================================
#  Decision response helpers
# =============================================================================

def _now_et_str() -> str:
    """Return the current Eastern time as an ISO-8601 string."""
    return datetime.now(tz=ET).isoformat()


def _safe_no_trade_decision(
    source_market_timestamp_et: str,
    reason: str,
) -> dict[str, Any]:
    """Build a conservative no-trade decision payload.

    This is the default output for every cycle where a real decision
    cannot be made — missing state, invalid input, stubbed layers, etc.

    The payload satisfies the full V1 decision_response contract so
    NT8's IntegrationStateManager can parse it without errors.
    AllowsNewTrade on the C# side will evaluate to False because:
      - policy.allowed = false
      - risk_overlay.allow_new_trade = false
      - decision_status = "BLOCK"
      - execution_plan.action = "NO_TRADE"
      - execution_plan.contracts = 0
    """
    return {
        "timestamp_et": _now_et_str(),
        "source_market_timestamp_et": source_market_timestamp_et,
        "instrument": INSTRUMENT,
        "decision_status": "BLOCK",
        "regime": {
            "label": "UNKNOWN",
            "confidence": 0.0,
            "reason": reason,
        },
        "event_state": {
            "status": "UNKNOWN",
            "reason": reason,
        },
        "specialist": {
            "name": "trend_pullback_v1",
            "setup_valid": False,
            "direction": "NONE",
            "reason": reason,
            "stop_distance": 0.0,
            "target_distance": 0.0,
        },
        "policy": {
            "allowed": False,
            "reason": reason,
        },
        "risk_overlay": {
            "allow_new_trade": False,
            "shutdown_flag": False,
            "reason": reason,
        },
        "execution_plan": {
            "action": "NO_TRADE",
            "contracts": 0,
            "stop_distance_points": 0.0,
            "target_distance_points": 0.0,
        },
        "integration_meta": {
            "loop_version": "1.0",
            "decision_source": "main_loop_placeholder",
        },
    }


# =============================================================================
#  V1 Processing layer stubs
# =============================================================================

def run_event_engine(market_state: dict[str, Any]) -> dict[str, Any]:
    """Evaluate scheduled event state and blackout rules."""
    return {
        "status": "CLEAR",
        "reason": "event_engine not yet implemented (V1 stub)",
    }


def run_regime_engine(
    market_state: dict[str, Any],
    event_state: dict[str, Any],
) -> dict[str, Any]:
    """Classify the current market regime."""
    return {
        "label": "UNKNOWN",
        "confidence": 0.0,
        "reason": "regime_engine not yet implemented (V1 stub)",
    }


def run_specialist(
    market_state: dict[str, Any],
    regime: dict[str, Any],
    event_state: dict[str, Any],
) -> dict[str, Any]:
    """Evaluate the Trend Pullback Continuation specialist."""
    return {
        "name": "trend_pullback_v1",
        "setup_valid": False,
        "direction": "NONE",
        "reason": (
            "trend_pullback_specialist not yet implemented "
            "(V1 stub)"
        ),
        "stop_distance": 0.0,
        "target_distance": 0.0,
    }


def run_policy_selector(
    market_state: dict[str, Any],
    regime: dict[str, Any],
    event_state: dict[str, Any],
    specialist: dict[str, Any],
) -> dict[str, Any]:
    """Determine whether the specialist is permitted to operate."""
    return {
        "allowed": False,
        "reason": "policy_selector not yet implemented (V1 stub)",
    }


def run_risk_overlay(
    market_state: dict[str, Any],
    regime: dict[str, Any],
    event_state: dict[str, Any],
    specialist: dict[str, Any],
    policy: dict[str, Any],
) -> dict[str, Any]:
    """Apply the hard risk engine."""
    return {
        "allow_new_trade": False,
        "shutdown_flag": False,
        "reason": "risk_overlay not yet implemented (V1 stub)",
    }


def build_execution_plan(
    specialist: dict[str, Any],
    policy: dict[str, Any],
    risk_overlay: dict[str, Any],
) -> dict[str, Any]:
    """Construct the final execution plan from layer outputs."""
    can_trade = (
        policy.get("allowed", False)
        and risk_overlay.get("allow_new_trade", False)
        and not risk_overlay.get("shutdown_flag", True)
        and specialist.get("setup_valid", False)
        and specialist.get("stop_distance", 0.0) > 0
    )

    if not can_trade:
        return {
            "action": "NO_TRADE",
            "contracts": 0,
            "stop_distance_points": 0.0,
            "target_distance_points": 0.0,
        }

    direction = specialist.get("direction", "NONE").upper()
    if direction not in ("LONG", "SHORT"):
        return {
            "action": "NO_TRADE",
            "contracts": 0,
            "stop_distance_points": 0.0,
            "target_distance_points": 0.0,
        }

    return {
        "action": f"ENTER_{direction}",
        "contracts": 1,
        "stop_distance_points": specialist["stop_distance"],
        "target_distance_points": specialist.get(
            "target_distance",
            0.0,
        ),
    }


def derive_decision_status(
    policy: dict[str, Any],
    risk_overlay: dict[str, Any],
    execution_plan: dict[str, Any],
) -> str:
    """Derive the decision_status string from layer outputs."""
    if risk_overlay.get("shutdown_flag", False):
        return "SHUTDOWN"

    if not risk_overlay.get("allow_new_trade", False):
        return "BLOCK"

    if not policy.get("allowed", False):
        return "BLOCK"

    if execution_plan.get("action") == "NO_TRADE":
        return "BLOCK"

    return "ALLOW"


# =============================================================================
#  Single decision cycle
# =============================================================================

def run_one_cycle(
    market_state_path: Path,
    decision_response_path: Path,
) -> None:
    """Execute one full decision cycle."""
    market_state = read_market_state(market_state_path)

    if market_state is None:
        logger.warning(
            "Market state unavailable — writing safe no-trade response."
        )
        decision = _safe_no_trade_decision(
            source_market_timestamp_et="",
            reason="market_state unavailable or invalid",
        )
        write_decision_response(decision, decision_response_path)
        return

    source_ts = market_state.get("timestamp_et", "")

    logger.debug(
        "Market state read OK | instrument=%s | phase=%s | bar_time=%s",
        market_state.get("instrument"),
        market_state.get("session_phase"),
        source_ts,
    )

    event_state = run_event_engine(market_state)
    logger.debug("Event state: %s", event_state.get("status"))

    regime = run_regime_engine(market_state, event_state)
    logger.debug(
        "Regime: %s (confidence=%.2f)",
        regime.get("label"),
        regime.get("confidence", 0.0),
    )

    specialist = run_specialist(market_state, regime, event_state)
    logger.debug(
        "Specialist: setup_valid=%s direction=%s",
        specialist.get("setup_valid"),
        specialist.get("direction"),
    )

    policy = run_policy_selector(
        market_state,
        regime,
        event_state,
        specialist,
    )
    logger.debug("Policy: allowed=%s", policy.get("allowed"))

    risk_overlay = run_risk_overlay(
        market_state,
        regime,
        event_state,
        specialist,
        policy,
    )
    logger.debug(
        "Risk overlay: allow_new_trade=%s shutdown=%s",
        risk_overlay.get("allow_new_trade"),
        risk_overlay.get("shutdown_flag"),
    )

    execution_plan = build_execution_plan(
        specialist,
        policy,
        risk_overlay,
    )
    decision_status = derive_decision_status(
        policy,
        risk_overlay,
        execution_plan,
    )

    logger.info(
        "Decision | status=%s action=%s contracts=%d",
        decision_status,
        execution_plan.get("action"),
        execution_plan.get("contracts", 0),
    )

    decision = {
        "timestamp_et": _now_et_str(),
        "source_market_timestamp_et": source_ts,
        "instrument": INSTRUMENT,
        "decision_status": decision_status,
        "regime": regime,
        "event_state": event_state,
        "specialist": specialist,
        "policy": policy,
        "risk_overlay": risk_overlay,
        "execution_plan": execution_plan,
        "integration_meta": {
            "loop_version": "1.0",
            "decision_source": "main_loop",
        },
    }

    write_decision_response(decision, decision_response_path)


# =============================================================================
#  Main polling loop
# =============================================================================

def main() -> None:
    """Run the orchestration loop until interrupted."""
    configure_default_logging()
    logger.info("Adaptive Futures Agent v1 — main loop starting.")
    logger.info("Market state path: %s", MARKET_STATE_PATH)
    logger.info("Decision response path: %s", DECISION_RESPONSE_PATH)
    logger.info("Poll interval: %.1f s", POLL_INTERVAL_SECONDS)

    while True:
        try:
            run_one_cycle(MARKET_STATE_PATH, DECISION_RESPONSE_PATH)
        except KeyboardInterrupt:
            raise
        except Exception:
            logger.exception(
                "Unhandled exception in run_one_cycle — continuing loop."
            )

        time.sleep(POLL_INTERVAL_SECONDS)


if __name__ == "__main__":
    try:
        main()
    except KeyboardInterrupt:
        logger.info("Adaptive Futures Agent v1 — loop stopped by user.")
