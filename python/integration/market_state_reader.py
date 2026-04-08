"""Read and validate market_state.json for the Adaptive Futures Agent.

Version 1 goals:
- Fail safely on missing/malformed/incomplete input
- Return a clean dictionary for downstream logic
- Keep implementation simple and modular
"""

from __future__ import annotations

import json
import logging
from pathlib import Path
from typing import Any

logger = logging.getLogger(__name__)

# Required top-level keys for Version 1.
REQUIRED_FIELDS: tuple[str, ...] = (
    "timestamp_et",
    "instrument",
    "bar_interval",
    "session_phase",
    "time_since_open_minutes",
    "time_until_close_minutes",
    "price",
    "indicators",
    "session_structure",
    "volume",
    "strategy_state",
    "integration_state",
)


def configure_default_logging(level: int = logging.INFO) -> None:
    """Set up basic logging if the host app has not configured logging yet."""
    root_logger = logging.getLogger()
    if not root_logger.handlers:
        logging.basicConfig(
            level=level,
            format="%(asctime)s | %(levelname)s | %(name)s | %(message)s",
        )


def read_market_state(file_path: str | Path = "market_state.json") -> dict[str, Any] | None:
    """Read, parse, and validate market state JSON.

    Args:
        file_path: Path to market_state.json (absolute or relative).

    Returns:
        A validated market-state dictionary when successful.
        None when file is missing, malformed, or invalid.
    """
    path = Path(file_path)

    raw_data = _read_json_file(path)
    if raw_data is None:
        return None

    if not _validate_market_state(raw_data, path):
        return None

    return raw_data


def _read_json_file(path: Path) -> dict[str, Any] | None:
    """Load JSON from disk and ensure top-level object is a dictionary."""
    if not path.exists():
        logger.warning("Market state file not found: %s", path)
        return None

    if not path.is_file():
        logger.warning("Market state path is not a file: %s", path)
        return None

    try:
        with path.open("r", encoding="utf-8") as f:
            payload = json.load(f)
    except json.JSONDecodeError as exc:
        logger.warning("Invalid JSON in market state file %s: %s", path, exc)
        return None
    except OSError as exc:
        logger.warning("Unable to read market state file %s: %s", path, exc)
        return None

    if not isinstance(payload, dict):
        logger.warning(
            "Invalid market state format in %s: expected top-level object, got %s",
            path,
            type(payload).__name__,
        )
        return None

    return payload


def _validate_market_state(payload: dict[str, Any], source_path: Path) -> bool:
    """Check required fields and simple Version 1 constraints."""
    missing_fields = [
        field for field in REQUIRED_FIELDS if field not in payload]
    if missing_fields:
        logger.warning(
            "Market state validation failed (%s): missing required fields: %s",
            source_path,
            ", ".join(missing_fields),
        )
        return False

    # Keep type checks pragmatic for V1: just confirm key nested structures are dicts.
    nested_dict_fields = (
        "price",
        "indicators",
        "session_structure",
        "volume",
        "strategy_state",
        "integration_state",
    )
    for field in nested_dict_fields:
        if not isinstance(payload[field], dict):
            logger.warning(
                "Market state validation failed (%s): field '%s' must be an object/dict.",
                source_path,
                field,
            )
            return False

    # Light-touch scalar checks to catch empty or obviously bad values.
    scalar_string_fields = ("timestamp_et", "instrument",
                            "bar_interval", "session_phase")
    for field in scalar_string_fields:
        value = payload.get(field)
        if not isinstance(value, str) or not value.strip():
            logger.warning(
                "Market state validation failed (%s): field '%s' must be a non-empty string.",
                source_path,
                field,
            )
            return False

    numeric_fields = ("time_since_open_minutes", "time_until_close_minutes")
    for field in numeric_fields:
        value = payload.get(field)
        if not isinstance(value, (int, float)):
            logger.warning(
                "Market state validation failed (%s): field '%s' must be numeric.",
                source_path,
                field,
            )
            return False

    return True


__all__ = ["REQUIRED_FIELDS", "configure_default_logging", "read_market_state"]
