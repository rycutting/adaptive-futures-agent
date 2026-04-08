using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AdaptiveFuturesAgent.NinjaTrader.Integration
{
    /// <summary>
    /// Version 1 JSON file integration manager for NT8 <-> Python communication.
    /// Safe defaults: if data is missing, malformed, stale, or invalid, downstream should not trade.
    /// </summary>
    public class IntegrationStateManager
    {
        private static readonly string[] RequiredDecisionFields =
        {
            "timestamp_et",
            "source_market_timestamp_et",
            "instrument",
            "decision_status",
            "regime",
            "event_state",
            "specialist",
            "policy",
            "risk_overlay",
            "execution_plan",
            "integration_meta"
        };

        public string MarketStatePath { get; }
        public string DecisionResponsePath { get; }

        public IntegrationStateManager(string marketStatePath, string decisionResponsePath)
        {
            if (string.IsNullOrWhiteSpace(marketStatePath))
                throw new ArgumentException("marketStatePath is required.", nameof(marketStatePath));
            if (string.IsNullOrWhiteSpace(decisionResponsePath))
                throw new ArgumentException("decisionResponsePath is required.", nameof(decisionResponsePath));

            MarketStatePath = marketStatePath;
            DecisionResponsePath = decisionResponsePath;
        }

        /// <summary>
        /// Writes market_state.json using atomic file replacement.
        /// </summary>
        public bool WriteMarketState(IDictionary<string, object> marketState, out string error)
        {
            error = string.Empty;

            if (marketState == null)
            {
                error = "marketState is null.";
                return false;
            }

            try
            {
                EnsureParentDirectoryExists(MarketStatePath);
                string json = JsonConvert.SerializeObject(marketState, Formatting.Indented);
                return SafeAtomicWrite(MarketStatePath, json, out error);
            }
            catch (Exception ex)
            {
                error = "WriteMarketState failed: " + ex.Message;
                return false;
            }
        }

        public bool DecisionResponseExists()
        {
            return File.Exists(DecisionResponsePath);
        }

        /// <summary>
        /// Reads and validates decision_response.json against the V1 contract.
        /// If anything fails, returns a no-trade-safe response.
        /// </summary>
        public DecisionResponseV1 ReadDecisionResponse(string expectedInstrument)
        {
            if (!DecisionResponseExists())
                return DecisionResponseV1.NoTrade("Decision file not found.");

            try
            {
                if (!TryReadAllTextShared(DecisionResponsePath, out var rawJson, out var readError))
                    return DecisionResponseV1.NoTrade("Failed reading decision file: " + readError);

                if (!TryParseJsonObject(rawJson, out var root, out var parseError))
                    return DecisionResponseV1.NoTrade("Malformed decision JSON: " + parseError);

                if (!HasRequiredTopLevelFields(root, out var missingFieldError))
                    return DecisionResponseV1.NoTrade(missingFieldError);

                if (!TryBuildResponse(root, rawJson, expectedInstrument, out var response, out var buildError))
                    return DecisionResponseV1.NoTrade(buildError);

                return response;
            }
            catch (Exception ex)
            {
                return DecisionResponseV1.NoTrade("ReadDecisionResponse exception: " + ex.Message);
            }
        }

        /// <summary>
        /// Freshness check based on response timestamp_et.
        /// </summary>
        public bool IsDecisionResponseFresh(DecisionResponseV1 response, TimeSpan maxAge, DateTime utcNow, out string reason)
        {
            reason = string.Empty;

            if (response == null)
            {
                reason = "Decision response is null.";
                return false;
            }

            if (!response.IsValid)
            {
                reason = "Decision response invalid: " + response.Error;
                return false;
            }

            if (response.TimestampUtc == DateTime.MinValue)
            {
                reason = "Decision response timestamp is missing/invalid.";
                return false;
            }

            var age = utcNow - response.TimestampUtc;
            if (age < TimeSpan.Zero)
            {
                reason = "Decision response timestamp is in the future.";
                return false;
            }

            if (age > maxAge)
            {
                reason = "Decision response is stale. Age=" + age + ", max=" + maxAge;
                return false;
            }

            return true;
        }

        private static bool TryBuildResponse(
            JObject root,
            string rawJson,
            string expectedInstrument,
            out DecisionResponseV1 response,
            out string error)
        {
            response = null;
            error = string.Empty;

            string instrument = root.Value<string>("instrument") ?? string.Empty;
            string decisionStatus = root.Value<string>("decision_status") ?? string.Empty;
            string timestampEt = root.Value<string>("timestamp_et") ?? string.Empty;
            string sourceMarketTimestampEt = root.Value<string>("source_market_timestamp_et") ?? string.Empty;

            if (string.IsNullOrWhiteSpace(instrument))
            {
                error = "Decision response missing instrument.";
                return false;
            }

            if (!string.IsNullOrWhiteSpace(expectedInstrument) &&
                !string.Equals(instrument.Trim(), expectedInstrument.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                error = "Instrument mismatch. Expected=" + expectedInstrument + ", got=" + instrument;
                return false;
            }

            if (!TryParseEtTimestampToUtc(timestampEt, out var timestampUtc))
            {
                error = "Invalid timestamp_et in decision response.";
                return false;
            }

            if (!TryParseEtTimestampToUtc(sourceMarketTimestampEt, out var sourceMarketTimestampUtc))
            {
                error = "Invalid source_market_timestamp_et in decision response.";
                return false;
            }

            if (!TryGetObject(root, "policy", out var policyObj, out error))
                return false;
            if (!TryGetObject(root, "risk_overlay", out var riskObj, out error))
                return false;
            if (!TryGetObject(root, "execution_plan", out var executionObj, out error))
                return false;
            if (!TryGetObject(root, "regime", out var regimeObj, out error))
                return false;
            if (!TryGetObject(root, "event_state", out var eventStateObj, out error))
                return false;
            if (!TryGetObject(root, "specialist", out var specialistObj, out error))
                return false;

            if (!TryGetBoolean(policyObj, "allowed", out var policyAllowed, out error))
                return false;
            if (!TryGetBoolean(riskObj, "allow_new_trade", out var allowNewTrade, out error))
                return false;
            if (!TryGetBoolean(riskObj, "shutdown_flag", out var shutdownFlag, out error))
                return false;

            string action = (executionObj.Value<string>("action") ?? string.Empty).Trim().ToUpperInvariant();
            if (string.IsNullOrWhiteSpace(action))
            {
                error = "execution_plan.action is required.";
                return false;
            }

            string normalizedStatus = decisionStatus.Trim().ToUpperInvariant();
            if (!IsSupportedDecisionStatus(normalizedStatus))
            {
                error = "Unsupported decision_status: " + decisionStatus;
                return false;
            }

            if (!IsSupportedExecutionAction(action))
            {
                error = "Unsupported execution_plan.action: " + action;
                return false;
            }

            int contracts = executionObj.Value<int?>("contracts") ?? 0;
            double stopDistancePoints = executionObj.Value<double?>("stop_distance_points") ?? 0.0;
            double targetDistancePoints = executionObj.Value<double?>("target_distance_points") ?? 0.0;

            response = new DecisionResponseV1
            {
                IsValid = true,
                Error = string.Empty,
                RawJson = rawJson,

                TimestampEt = timestampEt,
                SourceMarketTimestampEt = sourceMarketTimestampEt,
                TimestampUtc = timestampUtc,
                SourceMarketTimestampUtc = sourceMarketTimestampUtc,

                Instrument = instrument.Trim(),
                DecisionStatus = normalizedStatus,
                Regime = new RegimeInfo { Data = regimeObj },
                EventState = new EventStateInfo { Data = eventStateObj },
                Specialist = new SpecialistInfo { Data = specialistObj },

                Policy = new PolicyInfo { Allowed = policyAllowed },
                RiskOverlay = new RiskOverlayInfo
                {
                    AllowNewTrade = allowNewTrade,
                    ShutdownFlag = shutdownFlag
                },
                ExecutionPlan = new ExecutionPlanInfo
                {
                    Action = action,
                    Contracts = contracts,
                    StopDistancePoints = stopDistancePoints,
                    TargetDistancePoints = targetDistancePoints
                }
            };

            return true;
        }

        private static bool HasRequiredTopLevelFields(JObject root, out string error)
        {
            foreach (var field in RequiredDecisionFields)
            {
                if (root[field] == null)
                {
                    error = "Decision response missing required field: " + field;
                    return false;
                }
            }

            error = string.Empty;
            return true;
        }

        private static bool TryGetObject(JObject root, string fieldName, out JObject value, out string error)
        {
            value = root[fieldName] as JObject;
            if (value == null)
            {
                error = fieldName + " must be a JSON object.";
                return false;
            }

            error = string.Empty;
            return true;
        }

        private static bool TryGetBoolean(JObject obj, string fieldName, out bool value, out string error)
        {
            value = false;
            var token = obj[fieldName];
            if (token == null || token.Type != JTokenType.Boolean)
            {
                error = "Field '" + fieldName + "' must be boolean.";
                return false;
            }

            value = token.Value<bool>();
            error = string.Empty;
            return true;
        }

        private static bool IsSupportedDecisionStatus(string status)
        {
            return string.Equals(status, DecisionStatuses.Allow, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, DecisionStatuses.Block, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, DecisionStatuses.Reduce, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, DecisionStatuses.Pause, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(status, DecisionStatuses.Shutdown, StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsSupportedExecutionAction(string action)
        {
            return string.Equals(action, ExecutionActions.EnterLong, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(action, ExecutionActions.EnterShort, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(action, ExecutionActions.NoTrade, StringComparison.OrdinalIgnoreCase);
        }

        private static bool TryParseEtTimestampToUtc(string rawValue, out DateTime utc)
        {
            utc = DateTime.MinValue;

            if (string.IsNullOrWhiteSpace(rawValue))
                return false;

            // Case 1: timestamp includes offset (preferred).
            if (DateTimeOffset.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var dto))
            {
                utc = dto.UtcDateTime;
                return true;
            }

            // Case 2: plain ET local time (no offset).
            if (!DateTime.TryParse(rawValue, CultureInfo.InvariantCulture, DateTimeStyles.None, out var localEtTime))
                return false;

            try
            {
                var etZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
                var unspecified = DateTime.SpecifyKind(localEtTime, DateTimeKind.Unspecified);
                utc = TimeZoneInfo.ConvertTimeToUtc(unspecified, etZone);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private static bool SafeAtomicWrite(string targetPath, string content, out string error)
        {
            error = string.Empty;
            string tempPath = targetPath + ".tmp";

            try
            {
                File.WriteAllText(tempPath, content, Encoding.UTF8);

                if (File.Exists(targetPath))
                    File.Replace(tempPath, targetPath, null);
                else
                    File.Move(tempPath, targetPath);

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;

                try
                {
                    if (File.Exists(tempPath))
                        File.Delete(tempPath);
                }
                catch
                {
                    // Best-effort cleanup.
                }

                return false;
            }
        }

        private static bool TryReadAllTextShared(string path, out string content, out string error)
        {
            content = string.Empty;
            error = string.Empty;

            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                using (var reader = new StreamReader(stream, Encoding.UTF8, true))
                {
                    content = reader.ReadToEnd();
                }

                if (string.IsNullOrWhiteSpace(content))
                {
                    error = "File is empty.";
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static bool TryParseJsonObject(string rawJson, out JObject obj, out string error)
        {
            obj = null;
            error = string.Empty;

            try
            {
                var token = JToken.Parse(rawJson);
                if (token.Type != JTokenType.Object)
                {
                    error = "Top-level JSON must be an object.";
                    return false;
                }

                obj = (JObject)token;
                return true;
            }
            catch (JsonException ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void EnsureParentDirectoryExists(string filePath)
        {
            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                Directory.CreateDirectory(directory);
        }
    }

    public class DecisionResponseV1
    {
        public bool IsValid { get; set; }
        public string Error { get; set; }
        public string RawJson { get; set; }

        public string TimestampEt { get; set; }
        public string SourceMarketTimestampEt { get; set; }
        public DateTime TimestampUtc { get; set; }
        public DateTime SourceMarketTimestampUtc { get; set; }

        public string Instrument { get; set; }
        public string DecisionStatus { get; set; }
        public RegimeInfo Regime { get; set; }
        public EventStateInfo EventState { get; set; }
        public SpecialistInfo Specialist { get; set; }

        public PolicyInfo Policy { get; set; }
        public RiskOverlayInfo RiskOverlay { get; set; }
        public ExecutionPlanInfo ExecutionPlan { get; set; }

        /// <summary>
        /// Version 1 gate for whether NT8 should even consider opening a new trade.
        /// </summary>
        public bool AllowsNewTrade
        {
            get
            {
                if (!IsValid || Policy == null || RiskOverlay == null || ExecutionPlan == null)
                    return false;

                if (!Policy.Allowed || !RiskOverlay.AllowNewTrade || RiskOverlay.ShutdownFlag)
                    return false;

                if (!string.Equals(DecisionStatus, DecisionStatuses.Allow, StringComparison.OrdinalIgnoreCase))
                    return false;

                if (!string.Equals(ExecutionPlan.Action, ExecutionActions.EnterLong, StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(ExecutionPlan.Action, ExecutionActions.EnterShort, StringComparison.OrdinalIgnoreCase))
                    return false;

                return ExecutionPlan.Contracts > 0;
            }
        }

        public static DecisionResponseV1 NoTrade(string error)
        {
            return new DecisionResponseV1
            {
                IsValid = false,
                Error = error ?? "Unknown integration error.",
                RawJson = string.Empty,
                TimestampEt = string.Empty,
                SourceMarketTimestampEt = string.Empty,
                TimestampUtc = DateTime.MinValue,
                SourceMarketTimestampUtc = DateTime.MinValue,
                Instrument = string.Empty,
                DecisionStatus = DecisionStatuses.Block,
                Regime = new RegimeInfo { Data = new JObject() },
                EventState = new EventStateInfo { Data = new JObject() },
                Specialist = new SpecialistInfo { Data = new JObject() },
                Policy = new PolicyInfo { Allowed = false },
                RiskOverlay = new RiskOverlayInfo { AllowNewTrade = false, ShutdownFlag = true },
                ExecutionPlan = new ExecutionPlanInfo
                {
                    Action = ExecutionActions.NoTrade,
                    Contracts = 0,
                    StopDistancePoints = 0,
                    TargetDistancePoints = 0
                }
            };
        }
    }

    public static class DecisionStatuses
    {
        public const string Allow = "ALLOW";
        public const string Block = "BLOCK";
        public const string Reduce = "REDUCE";
        public const string Pause = "PAUSE";
        public const string Shutdown = "SHUTDOWN";
    }

    public static class ExecutionActions
    {
        public const string EnterLong = "ENTER_LONG";
        public const string EnterShort = "ENTER_SHORT";
        public const string NoTrade = "NO_TRADE";
    }

    public class ExecutionPlanInfo
    {
        public string Action { get; set; }
        public int Contracts { get; set; }
        public double StopDistancePoints { get; set; }
        public double TargetDistancePoints { get; set; }
    }

    public class PolicyInfo
    {
        public bool Allowed { get; set; }
    }

    public class RiskOverlayInfo
    {
        public bool AllowNewTrade { get; set; }
        public bool ShutdownFlag { get; set; }
    }

    public class RegimeInfo
    {
        public JObject Data { get; set; }
    }

    public class EventStateInfo
    {
        public JObject Data { get; set; }
    }

    public class SpecialistInfo
    {
        public JObject Data { get; set; }
    }
}