using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

public class CPHInline
{
    // =========================================================================
    // CONSTANTS & MODELS
    // =========================================================================
    private const string ScriptVersion = "2.4.0";
    private const string GitHubRepoOwner = "neoshadow02";
    private const string GitHubRepoName = "sbot_bankheist";
    private const string GitHubLatestReleaseUrl = "https://api.github.com/repos/" + GitHubRepoOwner + "/" + GitHubRepoName + "/releases/latest";
    private const string GitHubReleasesFallbackUrl = "https://github.com/" + GitHubRepoOwner + "/" + GitHubRepoName + "/releases/latest";
    private const string GitHubReleaseUrlPrefix = "https://github.com/" + GitHubRepoOwner + "/" + GitHubRepoName + "/";
    private const string GitHubRawFileUrlTemplate = "https://raw.githubusercontent.com/" + GitHubRepoOwner + "/" + GitHubRepoName + "/{0}/{1}";
    private const string UpdateDownloadFolderName = "Bankheist_Updates";
    private const string GitHubTokenFileName = "github_token.txt";
    private const string GitHubTokenEnvVar = "SBOT_BANKHEIST_GITHUB_TOKEN";

    private const int UpdateCheckIntervalHours = 24;
    private const int ManualUpdateCooldownSeconds = 15;
    private const int UpdateCheckTimeoutMs = 3000;
    private const int UpdateDownloadTimeoutMs = 10000;
    private const int MaxChatMessageLength = 490;
    private const int MaxReleaseJsonBytes = 512 * 1024;             // 512 KB
    private const int MaxImportDownloadBytes = 2 * 1024 * 1024;     // 2 MB
    private const int MaxImportDecompressedBytes = 4 * 1024 * 1024; // 4 MB (zip-bomb protection)

    private const string DefaultCommand = "!heist";
    private const string DefaultJoinTimerName = "[MINIGAME] Bankheist Multilingual - Timer - Join";
    private const string DefaultCooldownTimerName = "[MINIGAME] Bankheist Multilingual - Timer - Cooldown";

    private const string VarHeistActive = "heist_active";
    private const string VarCooldownActive = "heist_cooldown_active";
    private const string VarParticipants = "heist_participants";
    private const string VarStartTimeIso = "heist_start_time_iso";
    private const string VarLastRunIso = "heist_last_run_iso";
    private const string VarUsedCommand = "heist_used_command";
    private const string VarScriptVersion = "heist_script_version";
    private const string VarLatestVersion = "heist_latest_version";
    private const string VarUpdateAvailable = "heist_update_available";
    private const string VarLastUpdateCheckIso = "heist_last_update_check_iso";
    private const string VarLastManualUpdateIso = "heist_last_manual_update_iso";

    private const string RoleBroadcaster = "broadcaster";
    private const string RoleMod = "mod";
    private const string RoleVip = "vip";
    private const string RoleUser = "user";

    private static readonly object StateLock = new object();
    private static readonly object RngLock = new object();
    private static readonly Random Rng = new Random();

    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(200);

    private static readonly Regex StrictTagNameRegex = new Regex(
        @"^[vV]?\d+\.\d+(?:\.\d+){0,2}(?:-[0-9A-Za-z.-]+)?$",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly Regex StrictCleanVersionRegex = new Regex(
        @"^\d+\.\d+(?:\.\d+){0,2}$",
        RegexOptions.Compiled,
        RegexTimeout);

    private static readonly string[] MessageKeys = new string[]
    {
        "heistMsgSetupStarted", "heistMsgSetupCompleted", "heistMsgSetupDenied",
        "heistMsgStart", "heistMsgJoined", "heistMsgReminder",
        "heistMsgNotEnoughPoints", "heistMsgInvalidBet", "heistMsgCooldown",
        "heistMsgAlreadyIn", "heistMsgNotEnoughPlayers", "heistMsgProgress",
        "heistMsgSuccess", "heistMsgPartial", "heistMsgFailed",
        "heistMsgStatusActive", "heistMsgStatusCooldown", "heistMsgStatusIdle",
        "heistMsgNobodySurvivors", "heistMsgNobodyCaught", "heistMsgPointsApiError",
        "heistMsgStatusNotJoined",
        "heistMsgUpdateAvailable", "heistMsgUpdateUpToDate", "heistMsgUpdateCheckFailed", "heistMsgUpdateDenied",
        "heistMsgUpdateDownloadSuccess", "heistMsgUpdateDownloadFileOnly", "heistMsgUpdateDownloadFailed", "heistMsgUpdateDownloadDenied"
    };

    private sealed class HeistParticipant
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public long Bet { get; set; }
        public string Role { get; set; }
    }

    // =========================================================================
    // ENTRY POINT 1: CHAT COMMAND
    // =========================================================================
    public bool Execute()
    {
        string usedCommand = GetArgString("command");
        if (string.IsNullOrEmpty(usedCommand))
            usedCommand = DefaultCommand;

        string rawInput = GetArgString("rawInput").ToLowerInvariant();
        string[] inputTokens = rawInput.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        string lang = GetLanguage();

        // Sync any settings passed via Sub-Action arguments into persistent globals
        // so timer-triggered methods (ExecuteHeist / ResetCooldown / CheckForUpdate) always see fresh values
        SyncSettingsFromArgs(lang);

        // Setup trigger
        string statusParam = GetArgString("status").ToLowerInvariant();
        if (statusParam == "setup" || rawInput == "status setup" || rawInput == "setup")
        {
            HandleSetupCommand(usedCommand, lang);
            return true;
        }

        // Manual update / version check & download aliases (e.g. "!heist update", "!heist version", "!heist update get [force] [full]")
        if (inputTokens.Length > 0 && (inputTokens[0] == "update" || inputTokens[0] == "version"))
        {
            if (inputTokens.Length >= 2 && (inputTokens[1] == "get" || inputTokens[1] == "getupdate" || inputTokens[1] == "download"))
            {
                ParseUpdateDownloadFlags(rawInput, out bool forceAlias, out bool fullAlias);
                HandleGetUpdateCommand(usedCommand, lang, forceAlias, fullAlias);
                return true;
            }

            HandleUpdateCommand(usedCommand, lang);
            return true;
        }

        // 1-Click Update Download (Broadcaster / Editor test only):
        //   !heist getupdate [force] [full]
        if (inputTokens.Length > 0 && inputTokens[0] == "getupdate")
        {
            ParseUpdateDownloadFlags(rawInput, out bool forceDownload, out bool fullImport);
            HandleGetUpdateCommand(usedCommand, lang, forceDownload, fullImport);
            return true;
        }

        // Automatic cached update check (at most once every 24 hours)
        TryRunScheduledUpdateCheck(usedCommand, lang);

        // Configuration
        string pointsVariable = GetConfigString("heistPointsVariable", "userPoints");
        long defaultPoints = Math.Max(0, GetConfigLong("heistDefaultPoints", 1000));
        long minBet = Math.Max(1, GetConfigLong("heistMinBet", 50));
        long maxBet = Math.Max(minBet, GetConfigLong("heistMaxBet", 10000));
        int joinTimeSeconds = Math.Max(1, GetConfigInt("heistJoinTime", 60));
        int cooldownSeconds = Math.Max(0, GetConfigInt("heistCooldown", 300));

        string joinTimerId = GetConfigString("heistJoinTimerId", "").Trim();

        // Status query
        if (rawInput == "status")
        {
            ProcessStatus(usedCommand, joinTimeSeconds, cooldownSeconds, minBet, maxBet, lang);
            return true;
        }

        // Cooldown check with self-healing if the timer got stuck
        if (TryGetRemainingCooldown(cooldownSeconds, out int remainingCooldown))
        {
            string msgCooldown = Msg("heistMsgCooldown", lang);
            SafeSendMessage(
                msgCooldown
                    .Replace("%cooldown%", remainingCooldown.ToString(CultureInfo.InvariantCulture))
                    .Replace("%command%", usedCommand));
            return true;
        }

        string currentUserId = GetArgString("userId");
        string currentUserName = SanitizeUserName(GetArgString("user"));
        if (string.IsNullOrEmpty(currentUserName) && string.IsNullOrEmpty(currentUserId))
            return true;
        if (string.IsNullOrEmpty(currentUserName))
            currentUserName = SanitizeUserName(currentUserId);

        var participants = LoadParticipants();
        string participantKey = BuildParticipantKey(currentUserId, currentUserName);
        if (participants.ContainsKey(participantKey))
        {
            string msgAlreadyIn = Msg("heistMsgAlreadyIn", lang);
            SafeSendMessage(
                msgAlreadyIn
                    .Replace("%user%", currentUserName)
                    .Replace("%command%", usedCommand));
            return true;
        }

        // Points lookup (HTTP I/O happens outside StateLock)
        long userPoints = GetUserPoints(currentUserId, currentUserName, pointsVariable, defaultPoints);
        if (userPoints < 0)
        {
            string msgPointsApiError = Msg("heistMsgPointsApiError", lang);
            SafeSendMessage(
                msgPointsApiError
                    .Replace("%user%", currentUserName)
                    .Replace("%command%", usedCommand));
            return true;
        }

        // Bet parsing & validation
        long betAmount;
        bool isAllIn = rawInput == "all" || rawInput == "allin";
        if (isAllIn)
        {
            if (userPoints < minBet)
            {
                string msgNotEnoughPoints = Msg("heistMsgNotEnoughPoints", lang);
                SafeSendMessage(
                    msgNotEnoughPoints
                        .Replace("%user%", currentUserName)
                        .Replace("%currentPoints%", userPoints.ToString(CultureInfo.InvariantCulture))
                        .Replace("%command%", usedCommand));
                return true;
            }

            betAmount = Math.Min(userPoints, maxBet);
        }
        else if (!long.TryParse(rawInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out betAmount))
        {
            string msgInvalidBet = Msg("heistMsgInvalidBet", lang);
            SafeSendMessage(
                msgInvalidBet
                    .Replace("%min%", minBet.ToString(CultureInfo.InvariantCulture))
                    .Replace("%max%", maxBet.ToString(CultureInfo.InvariantCulture))
                    .Replace("%command%", usedCommand));
            return true;
        }

        if (betAmount < minBet || betAmount > maxBet)
        {
            string msgInvalidBet = Msg("heistMsgInvalidBet", lang);
            SafeSendMessage(
                msgInvalidBet
                    .Replace("%min%", minBet.ToString(CultureInfo.InvariantCulture))
                    .Replace("%max%", maxBet.ToString(CultureInfo.InvariantCulture))
                    .Replace("%command%", usedCommand));
            return true;
        }

        if (userPoints < betAmount)
        {
            string msgNotEnoughPoints = Msg("heistMsgNotEnoughPoints", lang);
            SafeSendMessage(
                msgNotEnoughPoints
                    .Replace("%user%", currentUserName)
                    .Replace("%currentPoints%", userPoints.ToString(CultureInfo.InvariantCulture))
                    .Replace("%command%", usedCommand));
            return true;
        }

        // Deduct bet through the selected points system (HTTP I/O outside StateLock)
        if (!TryDeductUserPoints(currentUserId, currentUserName, pointsVariable, userPoints, betAmount))
        {
            string msgPointsApiError = Msg("heistMsgPointsApiError", lang);
            SafeSendMessage(
                msgPointsApiError
                    .Replace("%user%", currentUserName)
                    .Replace("%command%", usedCommand));
            return true;
        }

        bool startedNewLobby = false;
        lock (StateLock)
        {
            participants = LoadParticipants();
            participants[participantKey] = new HeistParticipant
            {
                UserId = currentUserId,
                UserName = currentUserName,
                Bet = betAmount,
                Role = GetCurrentUserRole()
            };
            SaveParticipants(participants);

            bool isLobbyActive = CPH.GetGlobalVar<bool>(VarHeistActive, false);
            if (!isLobbyActive)
            {
                CPH.SetGlobalVar(VarHeistActive, true, false);
                CPH.SetGlobalVar(VarStartTimeIso, GetUtcNow().ToString("o", CultureInfo.InvariantCulture), false);
                CPH.SetGlobalVar(VarUsedCommand, usedCommand, false);
                startedNewLobby = true;
            }
        }

        if (startedNewLobby)
        {
            string msgStart = Msg("heistMsgStart", lang);
            SafeSendMessage(
                msgStart
                    .Replace("%user%", currentUserName)
                    .Replace("%remainingTime%", joinTimeSeconds.ToString(CultureInfo.InvariantCulture))
                    .Replace("%command%", usedCommand));

            StartTimer(joinTimerId, DefaultJoinTimerName, joinTimeSeconds);
        }
        else
        {
            string msgJoined = Msg("heistMsgJoined", lang);
            SafeSendMessage(
                msgJoined
                    .Replace("%user%", currentUserName)
                    .Replace("%amount%", betAmount.ToString(CultureInfo.InvariantCulture))
                    .Replace("%command%", usedCommand));
        }

        return true;
    }

    // =========================================================================
    // ENTRY POINT 2: METHOD EXECUTE - join timer elapsed
    // =========================================================================
    public bool ExecuteHeist()
    {
        string lang = GetStoredLanguageFast();
        string joinTimerId = GetConfigString("heistJoinTimerId", "").Trim();
        StopTimer(joinTimerId, DefaultJoinTimerName);

        string usedCommand = CPH.GetGlobalVar<string>(VarUsedCommand, false);
        if (string.IsNullOrEmpty(usedCommand))
            usedCommand = DefaultCommand;

        string pointsVariable = GetConfigString("heistPointsVariable", "userPoints");
        int minParticipants = Math.Max(1, GetConfigInt("heistMinParticipants", 1));
        double winMultiplier = GetConfigDouble("heistWinMultiplier", 1.8);
        if (winMultiplier <= 0)
            winMultiplier = 1.8;

        int cooldownSeconds = Math.Max(0, GetConfigInt("heistCooldown", 300));
        string cooldownTimerId = GetConfigString("heistCooldownTimerId", "").Trim();
        int chanceBroadcaster = ClampPercentage(GetConfigInt("heistChanceBroadcaster", 95));
        int chanceMod = ClampPercentage(GetConfigInt("heistChanceMod", 80));
        int chanceVip = ClampPercentage(GetConfigInt("heistChanceVip", 70));
        int chanceUser = ClampPercentage(GetConfigInt("heistChanceUser", 50));

        string nobodySurvivors = Msg("heistMsgNobodySurvivors", lang);
        string nobodyCaught = Msg("heistMsgNobodyCaught", lang);
        string msgPointsApiError = Msg("heistMsgPointsApiError", lang);
        string msgNotEnoughPlayers = Msg("heistMsgNotEnoughPlayers", lang);
        string msgProgress = Msg("heistMsgProgress", lang);
        string msgSuccess = Msg("heistMsgSuccess", lang);
        string msgPartial = Msg("heistMsgPartial", lang);
        string msgFailed = Msg("heistMsgFailed", lang);

        Dictionary<string, HeistParticipant> participants;
        lock (StateLock)
        {
            participants = LoadParticipants();
        }

        if (participants.Count < minParticipants)
        {
            SafeSendMessage(
                msgNotEnoughPlayers
                    .Replace("%minPlayers%", minParticipants.ToString(CultureInfo.InvariantCulture))
                    .Replace("%command%", usedCommand));

            bool refundError = false;
            foreach (var participant in participants.Values)
            {
                if (participant == null || participant.Bet <= 0)
                    continue;

                DebugLog($"Cancelled. Refunding {participant.Bet} points to {SanitizeLogText(participant.UserName)}...");
                if (!AddUserPoints(participant.UserId, participant.UserName, pointsVariable, participant.Bet))
                    refundError = true;
            }

            if (refundError)
                SafeSendMessage(msgPointsApiError.Replace("%command%", usedCommand));

            ClearLobbyState();
            return true;
        }

        SafeSendMessage(msgProgress.Replace("%command%", usedCommand));

        var survivors = new List<string>();
        var caught = new List<string>();
        long totalPayout = 0;
        bool payoutError = false;

        foreach (var participant in participants.Values)
        {
            if (participant == null || participant.Bet <= 0)
                continue;

            int survivalChance = GetSurvivalChanceForRole(
                participant.Role,
                chanceBroadcaster,
                chanceMod,
                chanceVip,
                chanceUser);

            string safeUser = SanitizeUserName(participant.UserName);
            int roll = NextRandomPercent();
            if (roll <= survivalChance)
            {
                long winAmount = CalculatePayout(participant.Bet, winMultiplier);
                DebugLog($"{SanitizeLogText(safeUser)} ({participant.Role}, {survivalChance}%) WON! Roll: {roll} | Bet: {participant.Bet} | Multiplier: {winMultiplier} | Payout: {winAmount}");

                if (!AddUserPoints(participant.UserId, participant.UserName, pointsVariable, winAmount))
                {
                    CPH.LogError($"[Bankheist] Failed to add {winAmount} points to {SanitizeLogText(safeUser)}.");
                    payoutError = true;
                }

                survivors.Add(safeUser);
                totalPayout = SafeAdd(totalPayout, winAmount);
            }
            else
            {
                DebugLog($"{SanitizeLogText(safeUser)} ({participant.Role}, {survivalChance}%) lost (caught). Roll: {roll} | Bet: {participant.Bet}.");
                caught.Add(safeUser);
            }
        }

        string survivorsText = survivors.Count > 0 ? string.Join(", ", survivors) : nobodySurvivors;
        string caughtText = caught.Count > 0 ? string.Join(", ", caught) : nobodyCaught;

        string selectedTemplate;
        if (survivors.Count > 0 && caught.Count == 0)
            selectedTemplate = msgSuccess;
        else if (survivors.Count > 0 && caught.Count > 0)
            selectedTemplate = msgPartial;
        else
            selectedTemplate = msgFailed;

        string finalMsg = selectedTemplate
            .Replace("%survivors%", survivorsText)
            .Replace("%caught%", caughtText)
            .Replace("%payout%", totalPayout.ToString(CultureInfo.InvariantCulture))
            .Replace("%command%", usedCommand);

        SafeSendMessage(finalMsg);
        if (payoutError)
            SafeSendMessage(msgPointsApiError.Replace("%command%", usedCommand));

        ClearLobbyState();

        if (cooldownSeconds > 0)
        {
            lock (StateLock)
            {
                CPH.SetGlobalVar(VarCooldownActive, true, false);
                CPH.SetGlobalVar(VarLastRunIso, GetUtcNow().ToString("o", CultureInfo.InvariantCulture), true);
            }
            StartTimer(cooldownTimerId, DefaultCooldownTimerName, cooldownSeconds);
        }
        else
        {
            ResetCooldownInternal(false);
        }

        return true;
    }

    // =========================================================================
    // ENTRY POINT 3: METHOD EXECUTE - cooldown timer elapsed
    // =========================================================================
    public bool ResetCooldown()
    {
        return ResetCooldownInternal(true);
    }

    private bool ResetCooldownInternal(bool announce)
    {
        lock (StateLock)
        {
            CPH.SetGlobalVar(VarCooldownActive, false, false);
        }
        string cooldownTimerId = GetConfigString("heistCooldownTimerId", "").Trim();
        StopTimer(cooldownTimerId, DefaultCooldownTimerName);

        if (announce)
        {
            string lang = GetStoredLanguageFast();
            string usedCommand = CPH.GetGlobalVar<string>(VarUsedCommand, false);
            if (string.IsNullOrEmpty(usedCommand))
                usedCommand = DefaultCommand;

            long minBet = Math.Max(1, GetConfigLong("heistMinBet", 50));
            long maxBet = Math.Max(minBet, GetConfigLong("heistMaxBet", 10000));
            string msgReminder = Msg("heistMsgReminder", lang);
            if (!string.IsNullOrEmpty(msgReminder))
            {
                SafeSendMessage(
                    msgReminder
                        .Replace("%command%", usedCommand)
                        .Replace("%min%", minBet.ToString(CultureInfo.InvariantCulture))
                        .Replace("%max%", maxBet.ToString(CultureInfo.InvariantCulture)));
            }
        }

        return true;
    }

    // =========================================================================
    // ENTRY POINT 4: METHOD EXECUTE - Streamer.bot Started (optional)
    // =========================================================================
    public bool CheckForUpdate()
    {
        try
        {
            string lang = GetLanguage();
            SyncSettingsFromArgs(lang);
            if (!IsConfigBoolTrue("heistCheckForUpdates", true))
                return true;

            string usedCommand = CPH.GetGlobalVar<string>(VarUsedCommand, false);
            if (string.IsNullOrEmpty(usedCommand))
                usedCommand = DefaultCommand;

            RunUpdateCheck(usedCommand, lang, isManualCommand: false);
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] Error in CheckForUpdate: {SanitizeLogText(ex.Message)}");
        }
        return true;
    }

    // =========================================================================
    // ENTRY POINT 5: METHOD EXECUTE - Manual Update Check (optional standalone)
    // =========================================================================
    public bool CheckUpdateManual()
    {
        try
        {
            string usedCommand = GetArgString("command");
            if (string.IsNullOrEmpty(usedCommand))
                usedCommand = CPH.GetGlobalVar<string>(VarUsedCommand, false);
            if (string.IsNullOrEmpty(usedCommand))
                usedCommand = DefaultCommand;

            string lang = GetLanguage();
            SyncSettingsFromArgs(lang);

            string rawInput = GetArgString("rawInput").ToLowerInvariant();
            string[] tokens = rawInput.Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (tokens.Length > 0 && (tokens[0] == "get" || tokens[0] == "getupdate" || tokens[0] == "download"))
            {
                ParseUpdateDownloadFlags(rawInput, out bool forceDownload, out bool fullImport);
                HandleGetUpdateCommand(usedCommand, lang, forceDownload, fullImport);
                return true;
            }

            HandleUpdateCommand(usedCommand, lang);
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] Error in CheckUpdateManual: {SanitizeLogText(ex.Message)}");
        }
        return true;
    }

    // =========================================================================
    // ENTRY POINT 6: METHOD EXECUTE - 1-Click Update Download (optional standalone)
    // =========================================================================
    public bool DownloadUpdate()
    {
        try
        {
            string usedCommand = GetArgString("command");
            if (string.IsNullOrEmpty(usedCommand))
                usedCommand = CPH.GetGlobalVar<string>(VarUsedCommand, false);
            if (string.IsNullOrEmpty(usedCommand))
                usedCommand = DefaultCommand;

            string lang = GetLanguage();
            SyncSettingsFromArgs(lang);
            ParseUpdateDownloadFlags(GetArgString("rawInput"), out bool forceDownload, out bool fullImport);
            HandleGetUpdateCommand(usedCommand, lang, forceDownload, fullImport);
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] Error in DownloadUpdate: {SanitizeLogText(ex.Message)}");
        }
        return true;
    }

    // =========================================================================
    // COMMAND HANDLERS & UPDATE SYSTEM
    // =========================================================================
    private bool HasAdminPermission(out string userName)
    {
        string userId = GetArgString("userId");
        userName = GetArgString("user");
        bool isDirectEditorTest = string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(userName);
        bool isBroadcaster = IsArgTrue("isBroadcaster") || IsArgTrue("isOwner");
        bool isMod = IsArgTrue("isMod") || IsArgTrue("isModerator");
        return isBroadcaster || isMod || isDirectEditorTest;
    }

    private void HandleSetupCommand(string usedCommand, string lang)
    {
        if (!HasAdminPermission(out string userName))
        {
            string msgSetupDenied = Msg("heistMsgSetupDenied", lang);
            SafeSendMessage(
                msgSetupDenied
                    .Replace("%user%", SanitizeUserName(userName))
                    .Replace("%command%", usedCommand));
            return;
        }

        RunFullSetup(usedCommand, lang);
    }

    private bool HasBroadcasterPermission(out string userName)
    {
        string userId = GetArgString("userId");
        userName = GetArgString("user");
        bool isDirectEditorTest = string.IsNullOrEmpty(userId) && string.IsNullOrEmpty(userName);
        bool isBroadcaster = IsArgTrue("isBroadcaster") || IsArgTrue("isOwner");
        return isBroadcaster || isDirectEditorTest;
    }

    private void HandleUpdateCommand(string usedCommand, string lang)
    {
        if (!HasAdminPermission(out string userName))
        {
            string msgDenied = Msg("heistMsgUpdateDenied", lang);
            SafeSendMessage(
                msgDenied
                    .Replace("%user%", SanitizeUserName(userName))
                    .Replace("%command%", usedCommand));
            return;
        }

        if (IsManualUpdateRateLimited())
        {
            CPH.LogInfo("[Bankheist] Manual update check throttled (cooldown active).");
            return;
        }

        CPH.SetGlobalVar(VarLastManualUpdateIso, GetUtcNow().ToString("o", CultureInfo.InvariantCulture), true);
        RunUpdateCheck(usedCommand, lang, isManualCommand: true);
    }

    private void ParseUpdateDownloadFlags(string rawInput, out bool forceDownload, out bool fullImport)
    {
        forceDownload = false;
        fullImport = false;
        if (string.IsNullOrWhiteSpace(rawInput))
            return;

        string[] parts = rawInput.ToLowerInvariant().Split(new char[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string p = parts[i];
            if (p == "force")
                forceDownload = true;
            else if (p == "full" || p == "all" || p == "init" || p == "initial")
                fullImport = true;
        }
    }

    private void HandleGetUpdateCommand(string usedCommand, string lang, bool forceDownload, bool fullImport = false)
    {
        if (!HasBroadcasterPermission(out string userName))
        {
            string msgDenied = Msg("heistMsgUpdateDownloadDenied", lang);
            SafeSendMessage(
                msgDenied
                    .Replace("%user%", SanitizeUserName(userName))
                    .Replace("%command%", usedCommand));
            return;
        }

        string cleanLatest = ScriptVersion;
        try
        {
            string json = FetchLatestReleaseJson(GitHubLatestReleaseUrl, UpdateCheckTimeoutMs);
            if (string.IsNullOrEmpty(json))
            {
                CPH.LogWarn("[Bankheist] Update download aborted: could not query GitHub latest release.");
                string msgCheckFailed = Msg("heistMsgUpdateCheckFailed", lang);
                SafeSendMessage(
                    msgCheckFailed
                        .Replace("%currentVersion%", ScriptVersion)
                        .Replace("%command%", usedCommand));
                return;
            }

            JObject obj = JObject.Parse(json);
            string tagName = obj["tag_name"] != null ? obj["tag_name"].ToString().Trim() : "";
            string releaseUrl = ValidateReleaseUrl(obj["html_url"] != null ? obj["html_url"].ToString().Trim() : "");

            cleanLatest = NormalizeVersionString(tagName);
            if (!IsSafeTagName(tagName) || !IsSafeCleanVersion(cleanLatest))
            {
                CPH.LogWarn($"[Bankheist] Update download aborted: invalid tag_name '{SanitizeLogText(tagName)}'.");
                string msgFailed = Msg("heistMsgUpdateDownloadFailed", lang);
                SafeSendMessage(
                    msgFailed
                        .Replace("%latestVersion%", ScriptVersion)
                        .Replace("%currentVersion%", ScriptVersion)
                        .Replace("%command%", usedCommand));
                return;
            }

            bool updateAvailable = IsNewerVersion(ScriptVersion, cleanLatest);
            CPH.SetGlobalVar(VarScriptVersion, ScriptVersion, true);
            CPH.SetGlobalVar(VarLatestVersion, cleanLatest, true);
            CPH.SetGlobalVar(VarUpdateAvailable, updateAvailable ? "true" : "false", true);
            CPH.SetGlobalVar(VarLastUpdateCheckIso, GetUtcNow().ToString("o", CultureInfo.InvariantCulture), true);

            if (!updateAvailable && !forceDownload)
            {
                string msgUpToDate = Msg("heistMsgUpdateUpToDate", lang);
                SafeSendMessage(
                    msgUpToDate
                        .Replace("%currentVersion%", ScriptVersion)
                        .Replace("%latestVersion%", cleanLatest)
                        .Replace("%url%", releaseUrl)
                        .Replace("%command%", usedCommand));
                return;
            }

            // Default: download Safe Core-Update (update.sb) that preserves user Settings/Triggers.
            // Automatic fallback to full import.sb if release has no update.sb, or when 'full' is requested.
            string targetFileKind = fullImport ? "import" : "update";
            string rawUrl = string.Format(CultureInfo.InvariantCulture, GitHubRawFileUrlTemplate, Uri.EscapeDataString(tagName), targetFileKind + ".sb");
            string importContent = DownloadImportString(rawUrl, UpdateDownloadTimeoutMs);

            if (string.IsNullOrEmpty(importContent) && !fullImport)
            {
                CPH.LogInfo($"[Bankheist] No update.sb found for {SanitizeLogText(tagName)}, falling back to full import.sb.");
                targetFileKind = "import";
                rawUrl = string.Format(CultureInfo.InvariantCulture, GitHubRawFileUrlTemplate, Uri.EscapeDataString(tagName), "import.sb");
                importContent = DownloadImportString(rawUrl, UpdateDownloadTimeoutMs);
            }

            if (string.IsNullOrEmpty(importContent))
            {
                CPH.LogError($"[Bankheist] Failed to download {targetFileKind}.sb from {SanitizeLogText(rawUrl)}");
                string msgFailed = Msg("heistMsgUpdateDownloadFailed", lang);
                SafeSendMessage(
                    msgFailed
                        .Replace("%latestVersion%", cleanLatest)
                        .Replace("%currentVersion%", ScriptVersion)
                        .Replace("%command%", usedCommand));
                return;
            }

            importContent = importContent.Trim();
            if (!ValidateStreamerBotImportPayload(importContent, out string validationError))
            {
                CPH.LogError($"[Bankheist] Downloaded {targetFileKind}.sb failed integrity check: {SanitizeLogText(validationError)}");
                string msgFailed = Msg("heistMsgUpdateDownloadFailed", lang);
                SafeSendMessage(
                    msgFailed
                        .Replace("%latestVersion%", cleanLatest)
                        .Replace("%currentVersion%", ScriptVersion)
                        .Replace("%command%", usedCommand));
                return;
            }

            string outputDir = Path.GetFullPath(GetUpdateOutputDirectory());
            Directory.CreateDirectory(outputDir);
            string filePrefix = targetFileKind == "update" ? "update_v" : "import_v";
            string filePath = Path.GetFullPath(Path.Combine(outputDir, filePrefix + cleanLatest + ".sb"));

            string expectedPrefix = outputDir.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
                ? outputDir
                : outputDir + Path.DirectorySeparatorChar;
            if (!filePath.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                CPH.LogError("[Bankheist] Update download aborted: resolved path escaped output directory.");
                string msgFailed = Msg("heistMsgUpdateDownloadFailed", lang);
                SafeSendMessage(
                    msgFailed
                        .Replace("%latestVersion%", cleanLatest)
                        .Replace("%currentVersion%", ScriptVersion)
                        .Replace("%command%", usedCommand));
                return;
            }

            File.WriteAllText(filePath, importContent, Encoding.ASCII);

            bool copiedToClipboard = TryCopyToClipboard(importContent);
            CPH.LogInfo($"[Bankheist] Downloaded v{cleanLatest} {targetFileKind}.sb to '{SanitizeLogText(filePath)}' (clipboard: {copiedToClipboard}).");

            string templateKey = copiedToClipboard ? "heistMsgUpdateDownloadSuccess" : "heistMsgUpdateDownloadFileOnly";
            string msgResult = Msg(templateKey, lang);
            SafeSendMessage(
                msgResult
                    .Replace("%latestVersion%", cleanLatest)
                    .Replace("%currentVersion%", ScriptVersion)
                    .Replace("%filePath%", FormatChatSafePath(filePath, false))
                    .Replace("%url%", releaseUrl)
                    .Replace("%command%", usedCommand));
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] Update download error: {SanitizeLogText(ex.Message)}");
            string msgFailed = Msg("heistMsgUpdateDownloadFailed", lang);
            SafeSendMessage(
                msgFailed
                    .Replace("%latestVersion%", cleanLatest)
                    .Replace("%currentVersion%", ScriptVersion)
                    .Replace("%command%", usedCommand));
        }
    }

    private void TryRunScheduledUpdateCheck(string usedCommand, string lang)
    {
        try
        {
            if (!IsConfigBoolTrue("heistCheckForUpdates", true))
                return;

            string rawLastCheck = GetGlobalStringRaw(VarLastUpdateCheckIso);
            if (TryParseIsoUtc(rawLastCheck, out DateTime lastCheckUtc))
            {
                if ((GetUtcNow() - lastCheckUtc).TotalHours < UpdateCheckIntervalHours)
                    return;
            }

            RunUpdateCheck(usedCommand, lang, isManualCommand: false);
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Scheduled update check skipped: {SanitizeLogText(ex.Message)}");
        }
    }

    private void RunUpdateCheck(string usedCommand, string lang, bool isManualCommand)
    {
        CPH.SetGlobalVar(VarScriptVersion, ScriptVersion, true);
        CPH.SetGlobalVar(VarLastUpdateCheckIso, GetUtcNow().ToString("o", CultureInfo.InvariantCulture), true);

        try
        {
            string json = FetchLatestReleaseJson(GitHubLatestReleaseUrl, UpdateCheckTimeoutMs);
            if (string.IsNullOrEmpty(json))
            {
                CPH.LogWarn($"[Bankheist] Update check failed (no response from GitHub). Current version: v{ScriptVersion}");
                if (isManualCommand)
                {
                    string msgFailed = Msg("heistMsgUpdateCheckFailed", lang);
                    SafeSendMessage(
                        msgFailed
                            .Replace("%currentVersion%", ScriptVersion)
                            .Replace("%command%", usedCommand));
                }
                return;
            }

            JObject obj = JObject.Parse(json);
            string tagName = obj["tag_name"] != null ? obj["tag_name"].ToString().Trim() : "";
            string releaseUrl = ValidateReleaseUrl(obj["html_url"] != null ? obj["html_url"].ToString().Trim() : "");
            string cleanLatest = NormalizeVersionString(tagName);

            if (!IsSafeCleanVersion(cleanLatest))
            {
                CPH.LogWarn($"[Bankheist] Update check returned invalid tag_name '{SanitizeLogText(tagName)}'. Current version: v{ScriptVersion}");
                if (isManualCommand)
                {
                    string msgFailed = Msg("heistMsgUpdateCheckFailed", lang);
                    SafeSendMessage(
                        msgFailed
                            .Replace("%currentVersion%", ScriptVersion)
                            .Replace("%command%", usedCommand));
                }
                return;
            }

            bool updateAvailable = IsNewerVersion(ScriptVersion, cleanLatest);
            CPH.SetGlobalVar(VarLatestVersion, cleanLatest, true);
            CPH.SetGlobalVar(VarUpdateAvailable, updateAvailable ? "true" : "false", true);

            if (updateAvailable)
            {
                CPH.LogWarn($"[Bankheist] Update available! Current: v{ScriptVersion} -> Latest: v{cleanLatest} | {releaseUrl}");

                bool notifyChat = isManualCommand || IsConfigBoolTrue("heistUpdateNotifyChat", true);
                if (notifyChat)
                {
                    string msgUpdate = Msg("heistMsgUpdateAvailable", lang);
                    SafeSendMessage(
                        msgUpdate
                            .Replace("%currentVersion%", ScriptVersion)
                            .Replace("%latestVersion%", cleanLatest)
                            .Replace("%url%", releaseUrl)
                            .Replace("%command%", usedCommand));
                }
            }
            else
            {
                CPH.LogInfo($"[Bankheist] Up to date (v{ScriptVersion}, latest release: v{cleanLatest}).");
                if (isManualCommand)
                {
                    string msgUpToDate = Msg("heistMsgUpdateUpToDate", lang);
                    SafeSendMessage(
                        msgUpToDate
                            .Replace("%currentVersion%", ScriptVersion)
                            .Replace("%latestVersion%", cleanLatest)
                            .Replace("%url%", releaseUrl)
                            .Replace("%command%", usedCommand));
                }
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Update check failed: {SanitizeLogText(ex.Message)}");
            if (isManualCommand)
            {
                string msgFailed = Msg("heistMsgUpdateCheckFailed", lang);
                SafeSendMessage(
                    msgFailed
                        .Replace("%currentVersion%", ScriptVersion)
                        .Replace("%command%", usedCommand));
            }
        }
    }

    // -------------------------------------------------------------------------
    // PROTECTED VIRTUAL HOOKS (for deterministic C# unit tests)
    // -------------------------------------------------------------------------
    protected virtual DateTime GetUtcNow()
    {
        return DateTime.UtcNow;
    }

    protected virtual string FetchLatestReleaseJson(string apiUrl, int timeoutMs)
    {
        return HttpGetBounded(apiUrl, "api.github.com", "application/vnd.github+json", timeoutMs, MaxReleaseJsonBytes);
    }

    protected virtual string DownloadImportString(string rawUrl, int timeoutMs)
    {
        return HttpGetBounded(rawUrl, "raw.githubusercontent.com", "text/plain", timeoutMs, MaxImportDownloadBytes);
    }

    protected virtual string GetUpdateOutputDirectory()
    {
        return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, UpdateDownloadFolderName);
    }

    protected virtual string GetOptionalGitHubToken()
    {
        // Export-safe private repo support: read optional Fine-Grained PAT ONLY from local file or env var,
        // never from Streamer.bot arguments or global variables.
        try
        {
            string outputDir = GetUpdateOutputDirectory();
            if (!string.IsNullOrEmpty(outputDir))
            {
                string tokenPath = Path.Combine(outputDir, GitHubTokenFileName);
                if (File.Exists(tokenPath))
                {
                    string fileToken = File.ReadAllText(tokenPath, Encoding.UTF8);
                    string cleaned = StripControlChars(fileToken).Trim();
                    if (!string.IsNullOrEmpty(cleaned))
                        return cleaned;
                }
            }
        }
        catch
        {
            // Ignore local token file errors and continue unauthenticated
        }

        try
        {
            string envToken = Environment.GetEnvironmentVariable(GitHubTokenEnvVar);
            string cleaned = StripControlChars(envToken).Trim();
            if (!string.IsNullOrEmpty(cleaned))
                return cleaned;
        }
        catch
        {
            // Ignore environment variable read errors
        }

        return "";
    }

    protected virtual bool TryCopyToClipboard(string text)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        try
        {
            string systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
            string clipPath = !string.IsNullOrEmpty(systemDir) ? Path.Combine(systemDir, "clip.exe") : "clip.exe";
            if (!File.Exists(clipPath))
                return false;

            var startInfo = new ProcessStartInfo
            {
                FileName = clipPath,
                UseShellExecute = false,
                RedirectStandardInput = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(startInfo))
            {
                if (process == null)
                    return false;

                using (StreamWriter writer = process.StandardInput)
                {
                    writer.Write(text);
                }

                if (!process.WaitForExit(3000))
                {
                    try { process.Kill(); } catch { }
                    return false;
                }

                return process.ExitCode == 0;
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Could not copy import string to clipboard: {SanitizeLogText(ex.Message)}");
            return false;
        }
    }

    // -------------------------------------------------------------------------
    // SECURITY & VALIDATION HELPERS
    // -------------------------------------------------------------------------
    private string HttpGetBounded(string url, string expectedHost, string acceptHeader, int timeoutMs, int maxBytes)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out Uri parsedUri) ||
                !string.Equals(parsedUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(parsedUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase))
            {
                CPH.LogError($"[Bankheist] Blocked unexpected URL/host: '{SanitizeLogText(url)}'");
                return null;
            }

            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
                // Ignore if OS/runtime restricts manual TLS configuration
            }

            var request = (HttpWebRequest)WebRequest.Create(parsedUri);
            request.Method = "GET";
            if (!string.IsNullOrEmpty(acceptHeader))
                request.Accept = acceptHeader;
            request.UserAgent = "Streamer.bot-" + GitHubRepoName + "/" + ScriptVersion;
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;

            // Attach optional private-repo Bearer token ONLY for whitelisted GitHub hosts
            if (string.Equals(parsedUri.Host, "api.github.com", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(parsedUri.Host, "raw.githubusercontent.com", StringComparison.OrdinalIgnoreCase))
            {
                string token = GetOptionalGitHubToken();
                if (!string.IsNullOrEmpty(token))
                    request.Headers["Authorization"] = "Bearer " + token;
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            {
                Uri finalUri = response.ResponseUri;
                if (finalUri == null ||
                    !string.Equals(finalUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                    (!string.Equals(finalUri.Host, expectedHost, StringComparison.OrdinalIgnoreCase) &&
                     !finalUri.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase)))
                {
                    CPH.LogError("[Bankheist] Blocked unexpected HTTP redirect target.");
                    return null;
                }

                using (var responseStream = response.GetResponseStream())
                {
                    return responseStream == null ? "" : ReadStreamBoundedUtf8(responseStream, maxBytes);
                }
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] HTTP GET failed for '{expectedHost}': {SanitizeLogText(ex.Message)}");
            return null;
        }
    }

    private string ReadStreamBoundedUtf8(Stream stream, int maxBytes)
    {
        using (var ms = new MemoryStream())
        {
            byte[] buffer = new byte[8192];
            int totalRead = 0;
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
            {
                totalRead += read;
                if (totalRead > maxBytes)
                    throw new InvalidOperationException("HTTP response exceeded maximum allowed size.");
                ms.Write(buffer, 0, read);
            }
            return Encoding.UTF8.GetString(ms.ToArray());
        }
    }

    private bool ValidateStreamerBotImportPayload(string importContent, out string error)
    {
        error = null;
        if (string.IsNullOrEmpty(importContent) || !importContent.StartsWith("U0JBRR", StringComparison.Ordinal))
        {
            error = "Missing Streamer.bot SBAE header.";
            return false;
        }

        if (importContent.Length > MaxImportDownloadBytes)
        {
            error = "Import string exceeds maximum allowed length.";
            return false;
        }

        try
        {
            byte[] rawBytes = Convert.FromBase64String(importContent);
            if (rawBytes.Length < 14 ||
                rawBytes[0] != (byte)'S' || rawBytes[1] != (byte)'B' ||
                rawBytes[2] != (byte)'A' || rawBytes[3] != (byte)'E' ||
                rawBytes[4] != 0x1F || rawBytes[5] != 0x8B)
            {
                error = "Invalid SBAE/GZip binary header.";
                return false;
            }

            string jsonText;
            using (var inputMs = new MemoryStream(rawBytes, 4, rawBytes.Length - 4))
            using (var gzip = new GZipStream(inputMs, CompressionMode.Decompress))
            {
                jsonText = ReadStreamBoundedUtf8(gzip, MaxImportDecompressedBytes);
            }

            JObject parsed = JObject.Parse(jsonText);
            if (parsed["version"] == null || parsed["meta"] == null || parsed["data"] == null)
            {
                error = "Decompressed JSON missing required 'version', 'meta', or 'data' fields.";
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

    private string ValidateReleaseUrl(string rawUrl)
    {
        if (!string.IsNullOrEmpty(rawUrl) &&
            rawUrl.StartsWith(GitHubReleaseUrlPrefix, StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(rawUrl, UriKind.Absolute, out Uri uri) &&
            string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return rawUrl;
        }
        return GitHubReleasesFallbackUrl;
    }

    private bool IsSafeTagName(string tagName)
    {
        return !string.IsNullOrEmpty(tagName) && tagName.Length <= 32 && StrictTagNameRegex.IsMatch(tagName);
    }

    private bool IsSafeCleanVersion(string cleanVersion)
    {
        return !string.IsNullOrEmpty(cleanVersion) && cleanVersion.Length <= 24 && StrictCleanVersionRegex.IsMatch(cleanVersion);
    }

    private bool IsManualUpdateRateLimited()
    {
        string raw = GetGlobalStringRaw(VarLastManualUpdateIso);
        if (TryParseIsoUtc(raw, out DateTime lastUtc))
        {
            double delta = (GetUtcNow() - lastUtc).TotalSeconds;
            return delta >= 0 && delta < ManualUpdateCooldownSeconds;
        }
        return false;
    }

    private string FormatChatSafePath(string fullPath, bool isDirectory)
    {
        if (string.IsNullOrWhiteSpace(fullPath))
            return isDirectory ? ".\\" : "";

        try
        {
            string normalizedTarget = Path.GetFullPath(fullPath).TrimEnd('\\', '/');
            string baseDirRaw = AppDomain.CurrentDomain.BaseDirectory;
            if (!string.IsNullOrWhiteSpace(baseDirRaw))
            {
                string baseDir = Path.GetFullPath(baseDirRaw).TrimEnd('\\', '/');
                string basePrefix = baseDir + Path.DirectorySeparatorChar;
                if (normalizedTarget.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                {
                    string rel = normalizedTarget.Substring(basePrefix.Length).Replace('/', '\\').Trim('\\');
                    string baseFolderName = Path.GetFileName(baseDir);
                    string combined = !string.IsNullOrEmpty(baseFolderName) ? ".\\" + baseFolderName + "\\" + rel : ".\\" + rel;
                    return isDirectory ? combined + "\\" : combined;
                }
            }

            if (isDirectory)
            {
                string leaf = Path.GetFileName(normalizedTarget);
                return !string.IsNullOrEmpty(leaf) ? ".\\" + leaf + "\\" : ".\\";
            }

            string fileName = Path.GetFileName(normalizedTarget);
            string parentDir = Path.GetDirectoryName(normalizedTarget);
            string parentName = !string.IsNullOrEmpty(parentDir) ? Path.GetFileName(parentDir.TrimEnd('\\', '/')) : "";
            return !string.IsNullOrEmpty(parentName) && !string.IsNullOrEmpty(fileName)
                ? ".\\" + parentName + "\\" + fileName
                : (!string.IsNullOrEmpty(fileName) ? ".\\" + fileName : ".\\");
        }
        catch
        {
            return isDirectory ? ".\\" + UpdateDownloadFolderName + "\\" : "update.sb";
        }
    }

    private string NormalizeVersionString(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "";

        string s = raw.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s.Substring(1);

        int suffixIdx = s.IndexOfAny(new char[] { '-', '+' });
        if (suffixIdx >= 0)
            s = s.Substring(0, suffixIdx);

        return s.Trim();
    }

    private bool IsNewerVersion(string currentRaw, string latestRaw)
    {
        string currentClean = NormalizeVersionString(currentRaw);
        string latestClean = NormalizeVersionString(latestRaw);

        if (Version.TryParse(currentClean, out Version currentVer) &&
            Version.TryParse(latestClean, out Version latestVer))
        {
            return latestVer > currentVer;
        }

        return false;
    }

    private string StripControlChars(string input)
    {
        if (string.IsNullOrEmpty(input))
            return "";

        var sb = new StringBuilder(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            if (c == '\r' || c == '\n' || c == '\t')
                sb.Append(' ');
            else if (!char.IsControl(c))
                sb.Append(c);
        }
        return sb.ToString();
    }

    private string SanitizeLogText(string input)
    {
        string cleaned = StripControlChars(input).Trim();
        if (cleaned.Length > 300)
            cleaned = cleaned.Substring(0, 300) + "...";
        return cleaned;
    }

    private string SanitizeUserName(string userName)
    {
        string cleaned = StripControlChars(userName).Trim();
        if (cleaned.Length > 64)
            cleaned = cleaned.Substring(0, 64);
        return cleaned;
    }

    private void SafeSendMessage(string message)
    {
        if (string.IsNullOrEmpty(message))
            return;

        string sanitized = StripControlChars(message).Trim();
        if (string.IsNullOrEmpty(sanitized))
            return;

        if (sanitized.StartsWith("/", StringComparison.Ordinal) || sanitized.StartsWith(".", StringComparison.Ordinal))
            sanitized = "[Bankheist] " + sanitized;

        if (sanitized.Length > MaxChatMessageLength)
            sanitized = sanitized.Substring(0, MaxChatMessageLength);

        try
        {
            CPH.SendMessage(sanitized, true);
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Could not send chat notification (Twitch chat may not be connected yet): {SanitizeLogText(ex.Message)}");
        }
    }

    private bool IsConfigBoolTrue(string key, bool defaultValue)
    {
        string raw = GetConfigString(key, defaultValue ? "true" : "false").Trim().ToLowerInvariant();
        if (raw == "true" || raw == "1" || raw == "yes")
            return true;
        if (raw == "false" || raw == "0" || raw == "no")
            return false;
        return defaultValue;
    }

    private void ProcessStatus(string usedCommand, int joinTimeSec, int cooldownSec, long minBet, long maxBet, string lang)
    {
        string userId = GetArgString("userId");
        string userName = SanitizeUserName(GetArgString("user"));
        bool isLobbyActive = CPH.GetGlobalVar<bool>(VarHeistActive, false);

        if (isLobbyActive)
        {
            var participants = LoadParticipants();
            string rawIsoTime = CPH.GetGlobalVar<string>(VarStartTimeIso, false);
            int remainingSec = joinTimeSec;

            if (TryParseIsoUtc(rawIsoTime, out DateTime startTimeUtc))
            {
                int elapsed = (int)(GetUtcNow() - startTimeUtc).TotalSeconds;
                remainingSec = Math.Max(0, joinTimeSec - elapsed);
            }

            long totalBet = 0;
            long userBet = 0;
            string currentKey = BuildParticipantKey(userId, userName);

            foreach (var kvp in participants)
            {
                var participant = kvp.Value;
                if (participant == null)
                    continue;

                totalBet = SafeAdd(totalBet, participant.Bet);

                bool isSameUser =
                    (!string.IsNullOrEmpty(currentKey) && string.Equals(kvp.Key, currentKey, StringComparison.OrdinalIgnoreCase)) ||
                    (!string.IsNullOrEmpty(userId) && string.Equals(participant.UserId, userId, StringComparison.Ordinal)) ||
                    (!string.IsNullOrEmpty(userName) && string.Equals(participant.UserName, userName, StringComparison.OrdinalIgnoreCase));

                if (isSameUser)
                    userBet = participant.Bet;
            }

            string pointsWord = Msg("heistWordPoints", lang);
            string notJoined = Msg("heistMsgStatusNotJoined", lang);
            string userBetStr = userBet > 0
                ? userBet.ToString(CultureInfo.InvariantCulture) + " " + pointsWord
                : notJoined;

            string msgActive = Msg("heistMsgStatusActive", lang);
            string msg = msgActive
                .Replace("%remainingTime%", remainingSec.ToString(CultureInfo.InvariantCulture))
                .Replace("%userBet%", userBetStr)
                .Replace("%totalBet%", totalBet.ToString(CultureInfo.InvariantCulture))
                .Replace("%players%", participants.Count.ToString(CultureInfo.InvariantCulture))
                .Replace("%user%", userName)
                .Replace("%command%", usedCommand);

            SafeSendMessage(msg);
            return;
        }

        if (TryGetRemainingCooldown(cooldownSec, out int remainingCooldown))
        {
            string msgCooldown = Msg("heistMsgStatusCooldown", lang);
            SafeSendMessage(
                msgCooldown
                    .Replace("%cooldown%", remainingCooldown.ToString(CultureInfo.InvariantCulture))
                    .Replace("%command%", usedCommand));
            return;
        }

        string msgIdle = Msg("heistMsgStatusIdle", lang);
        SafeSendMessage(
            msgIdle
                .Replace("%command%", usedCommand)
                .Replace("%user%", userName)
                .Replace("%min%", minBet.ToString(CultureInfo.InvariantCulture))
                .Replace("%max%", maxBet.ToString(CultureInfo.InvariantCulture)));
    }

    private void SyncSettingsFromArgs(string lang)
    {
        GetPointsSystem();
        SyncOptionalStringArg("heistPointsVariable");
        SyncOptionalLongArg("heistDefaultPoints");
        SyncOptionalLongArg("heistMinBet");
        SyncOptionalLongArg("heistMaxBet");
        SyncOptionalIntArg("heistMinParticipants");
        SyncOptionalDoubleArg("heistWinMultiplier");
        SyncOptionalIntArg("heistJoinTime");
        SyncOptionalIntArg("heistCooldown");
        SyncOptionalStringArg("heistJoinTimerId");
        SyncOptionalStringArg("heistCooldownTimerId");
        SyncOptionalIntArg("heistChanceBroadcaster");
        SyncOptionalIntArg("heistChanceMod");
        SyncOptionalIntArg("heistChanceVip");
        SyncOptionalIntArg("heistChanceUser");
        SyncOptionalStringArg("heistStreamElementsChannelId");
        SyncOptionalStringArg("heistStreamElementsJwt");
        SyncOptionalIntArg("heistPointsApiTimeoutMs");
        SyncOptionalStringArg("heistDebugApiErrors");
        SyncOptionalStringArg("heistCheckForUpdates");
        SyncOptionalStringArg("heistUpdateNotifyChat");

        // Handle heistWordPoints explicitly: non-empty persists, explicitly empty arg resets to language default
        if (CPH.TryGetArg("heistWordPoints", out object rawWordObj))
        {
            string wordVal = rawWordObj != null ? rawWordObj.ToString().Trim() : "";
            if (!string.IsNullOrEmpty(wordVal))
                CPH.SetGlobalVar("heistWordPoints", wordVal, true);
            else
                CPH.UnsetGlobalVar("heistWordPoints", true);
        }

        // In custom mode, persist all custom message arguments so timer callbacks (ExecuteHeist / ResetCooldown) use them
        if (NormalizeLanguage(lang) == "custom")
        {
            foreach (string key in MessageKeys)
                SyncOptionalStringArg(key);
        }
    }

    private void RunFullSetup(string usedCommand, string lang)
    {
        SafeSendMessage(Msg("heistMsgSetupStarted", lang));

        GetLanguage();
        GetPointsSystem();
        GetConfigString("heistPointsVariable", "userPoints");
        GetConfigLong("heistDefaultPoints", 1000);
        GetConfigLong("heistMinBet", 50);
        GetConfigLong("heistMaxBet", 10000);
        GetConfigInt("heistMinParticipants", 1);
        GetConfigDouble("heistWinMultiplier", 1.8);
        int joinTime = Math.Max(1, GetConfigInt("heistJoinTime", 60));
        int cooldownTime = Math.Max(0, GetConfigInt("heistCooldown", 300));
        string joinTimerId = GetConfigString("heistJoinTimerId", "").Trim();
        string cooldownTimerId = GetConfigString("heistCooldownTimerId", "").Trim();
        GetConfigInt("heistChanceBroadcaster", 95);
        GetConfigInt("heistChanceMod", 80);
        GetConfigInt("heistChanceVip", 70);
        GetConfigInt("heistChanceUser", 50);
        GetConfigString("heistStreamElementsChannelId", "");
        GetConfigString("heistStreamElementsJwt", "");
        GetConfigInt("heistPointsApiTimeoutMs", 7000);
        GetConfigString("heistDebugApiErrors", "false");
        GetConfigString("heistCheckForUpdates", "true");
        GetConfigString("heistUpdateNotifyChat", "true");
        CPH.SetGlobalVar(VarScriptVersion, ScriptVersion, true);

        foreach (string key in MessageKeys)
        {
            string existingVal = GetOptionalConfigString(key);
            if (string.IsNullOrEmpty(existingVal))
            {
                string fallbackLang = (lang == "custom") ? "de" : lang;
                string defaultText = GetBuiltInMessage(key, fallbackLang);
                if (!string.IsNullOrEmpty(defaultText))
                    CPH.SetGlobalVar(key, defaultText, true);
            }
        }

        ClearLobbyState();
        lock (StateLock)
        {
            CPH.SetGlobalVar(VarCooldownActive, false, false);
        }

        ConfigureTimerInterval(joinTimerId, joinTime);
        if (cooldownTime > 0)
            ConfigureTimerInterval(cooldownTimerId, cooldownTime);

        StopTimer(joinTimerId, DefaultJoinTimerName);
        StopTimer(cooldownTimerId, DefaultCooldownTimerName);

        SafeSendMessage(Msg("heistMsgSetupCompleted", lang));

        if (IsConfigBoolTrue("heistCheckForUpdates", true))
            RunUpdateCheck(usedCommand, lang, isManualCommand: false);
    }

    private Dictionary<string, HeistParticipant> LoadParticipants()
    {
        try
        {
            string json = CPH.GetGlobalVar<string>(VarParticipants, false);
            if (string.IsNullOrEmpty(json))
                return new Dictionary<string, HeistParticipant>(StringComparer.OrdinalIgnoreCase);

            var parsed = JsonConvert.DeserializeObject<Dictionary<string, HeistParticipant>>(json);
            if (parsed != null)
                return new Dictionary<string, HeistParticipant>(parsed, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] Failed to deserialize participants: {SanitizeLogText(ex.Message)}");
        }

        return new Dictionary<string, HeistParticipant>(StringComparer.OrdinalIgnoreCase);
    }

    private void SaveParticipants(Dictionary<string, HeistParticipant> participants)
    {
        try
        {
            string json = JsonConvert.SerializeObject(participants);
            CPH.SetGlobalVar(VarParticipants, json, false);
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] Failed to serialize participants: {SanitizeLogText(ex.Message)}");
        }
    }

    private void ClearLobbyState()
    {
        lock (StateLock)
        {
            CPH.SetGlobalVar(VarHeistActive, false, false);
            CPH.UnsetGlobalVar(VarParticipants, false);
            CPH.UnsetGlobalVar(VarStartTimeIso, false);
        }
    }

    private bool TryGetRemainingCooldown(int cooldownSeconds, out int remainingSeconds)
    {
        remainingSeconds = 0;
        bool isCooldown = CPH.GetGlobalVar<bool>(VarCooldownActive, false);
        if (!isCooldown)
            return false;

        if (cooldownSeconds <= 0)
        {
            ResetCooldownInternal(false);
            return false;
        }

        string rawLastRun = CPH.GetGlobalVar<string>(VarLastRunIso, true);
        int remaining = cooldownSeconds;

        if (TryParseIsoUtc(rawLastRun, out DateTime lastRunUtc))
        {
            int elapsed = (int)(GetUtcNow() - lastRunUtc).TotalSeconds;
            remaining = cooldownSeconds - elapsed;
            if (remaining <= 0)
            {
                ResetCooldownInternal(false);
                return false;
            }
        }

        remainingSeconds = remaining;
        return true;
    }

    private string BuildParticipantKey(string userId, string userName)
    {
        if (!string.IsNullOrEmpty(userId))
            return "id:" + userId;
        if (!string.IsNullOrEmpty(userName))
            return "name:" + userName.Trim().ToLowerInvariant();
        return "";
    }

    private string GetCurrentUserRole()
    {
        if (IsArgTrue("isBroadcaster") || IsArgTrue("isOwner"))
            return RoleBroadcaster;
        if (IsArgTrue("isMod") || IsArgTrue("isModerator"))
            return RoleMod;
        if (IsArgTrue("isVip"))
            return RoleVip;
        return RoleUser;
    }

    private int GetSurvivalChanceForRole(string role, int chanceBroadcaster, int chanceMod, int chanceVip, int chanceUser)
    {
        if (string.Equals(role, RoleBroadcaster, StringComparison.OrdinalIgnoreCase))
            return chanceBroadcaster;
        if (string.Equals(role, RoleMod, StringComparison.OrdinalIgnoreCase))
            return chanceMod;
        if (string.Equals(role, RoleVip, StringComparison.OrdinalIgnoreCase))
            return chanceVip;
        return chanceUser;
    }

    private int ClampPercentage(int value)
    {
        if (value < 0)
            return 0;
        if (value > 100)
            return 100;
        return value;
    }

    private int NextRandomPercent()
    {
        lock (RngLock)
        {
            return Rng.Next(1, 101);
        }
    }

    private long CalculatePayout(long bet, double winMultiplier)
    {
        double raw = Math.Round(bet * winMultiplier, MidpointRounding.AwayFromZero);
        if (double.IsNaN(raw) || double.IsInfinity(raw) || raw >= long.MaxValue)
            return long.MaxValue;
        if (raw <= 0)
            return 0;
        return (long)raw;
    }

    private long SafeAdd(long a, long b)
    {
        if (b > 0 && a > long.MaxValue - b)
            return long.MaxValue;
        return a + b;
    }

    private bool TryParseIsoUtc(string raw, out DateTime utcTime)
    {
        utcTime = DateTime.MinValue;
        if (string.IsNullOrEmpty(raw))
            return false;

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out DateTime parsed))
        {
            utcTime = parsed.Kind == DateTimeKind.Utc ? parsed : parsed.ToUniversalTime();
            return true;
        }

        return false;
    }

    // =========================================================================
    // TIMER HELPERS
    // =========================================================================
    private void StartTimer(string timerId, string fallbackTimerName, int intervalSeconds)
    {
        if (!string.IsNullOrEmpty(timerId))
        {
            CPH.DisableTimerById(timerId);
            ConfigureTimerInterval(timerId, intervalSeconds);
            CPH.EnableTimerById(timerId);
        }
        else
        {
            CPH.DisableTimer(fallbackTimerName);
            CPH.EnableTimer(fallbackTimerName);
        }
    }

    private void StopTimer(string timerId, string fallbackTimerName)
    {
        if (!string.IsNullOrEmpty(timerId))
            CPH.DisableTimerById(timerId);
        else
            CPH.DisableTimer(fallbackTimerName);
    }

    private void ConfigureTimerInterval(string timerId, int intervalSeconds)
    {
        if (!string.IsNullOrEmpty(timerId) && intervalSeconds > 0)
            CPH.SetTimerInterval(timerId, intervalSeconds);
    }

    // =========================================================================
    // POINTS SYSTEM ADAPTERS
    // heistPointsSystem values: streamerbot, streamelements
    // =========================================================================
    private string GetPointsSystem()
    {
        string argSystem = GetArgString("heistPointsSystem");
        string globalSystem = GetGlobalStringRaw("heistPointsSystem");
        DebugLog($"PointsSystem lookup: arg='{argSystem}', global='{globalSystem}'.");

        if (!string.IsNullOrEmpty(argSystem))
        {
            string normalizedArg = NormalizePointsSystem(argSystem);
            CPH.SetGlobalVar("heistPointsSystem", normalizedArg, true);
            return normalizedArg;
        }

        if (!string.IsNullOrEmpty(globalSystem))
            return NormalizePointsSystem(globalSystem);

        CPH.SetGlobalVar("heistPointsSystem", "streamerbot", true);
        return "streamerbot";
    }

    private string NormalizePointsSystem(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "streamerbot";

        string s = raw.Trim().ToLowerInvariant().Replace(" ", "").Replace("_", "").Replace("-", "");
        if (s == "streamelements" || s == "streamelement" || s == "elements" || s == "se")
            return "streamelements";

        return "streamerbot";
    }

    private long GetUserPoints(string userId, string userName, string varName, long defaultPointsIfMissing)
    {
        string system = GetPointsSystem();
        if (system == "streamelements")
            return GetStreamElementsPoints(userName);

        return GetStreamerBotUserPoints(userId, userName, varName, defaultPointsIfMissing);
    }

    private bool TryDeductUserPoints(string userId, string userName, string varName, long currentPoints, long amount)
    {
        if (amount <= 0)
            return true;

        string system = GetPointsSystem();
        if (system == "streamelements")
            return StreamElementsAdjustPoints(userName, -amount);

        return SetStreamerBotUserPoints(userId, userName, varName, Math.Max(0, currentPoints - amount));
    }

    private bool AddUserPoints(string userId, string userName, string varName, long amount)
    {
        if (amount <= 0)
            return true;

        string system = GetPointsSystem();
        DebugLog($"AddUserPoints: user='{userName}', amount={amount}, system='{system}'.");

        if (system == "streamelements")
            return StreamElementsAdjustPoints(userName, amount);

        long current = GetStreamerBotUserPoints(userId, userName, varName, 0);
        if (current < 0)
            return false;

        return SetStreamerBotUserPoints(userId, userName, varName, SafeAdd(current, amount));
    }

    private long GetStreamerBotUserPoints(string userId, string userName, string varName, long defaultPointsIfMissing)
    {
        try
        {
            if (!string.IsNullOrEmpty(userId))
            {
                string rawById = CPH.GetTwitchUserVarById<string>(userId, varName, true);
                if (!string.IsNullOrEmpty(rawById) && TryConvertToLong(rawById, out long parsedById))
                    return parsedById;
            }

            if (!string.IsNullOrEmpty(userName))
            {
                string rawByName = CPH.GetTwitchUserVar<string>(userName, varName, true);
                if (!string.IsNullOrEmpty(rawByName) && TryConvertToLong(rawByName, out long parsedByName))
                    return parsedByName;
            }

            if (!SetStreamerBotUserPoints(userId, userName, varName, defaultPointsIfMissing))
                return -1;

            return defaultPointsIfMissing;
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] Error reading Streamer.bot user points for '{userName}': {ex.Message}");
            return -1;
        }
    }

    private bool SetStreamerBotUserPoints(string userId, string userName, string varName, long amount)
    {
        try
        {
            if (!string.IsNullOrEmpty(userId))
            {
                CPH.SetTwitchUserVarById(userId, varName, amount, true);
                return true;
            }

            if (!string.IsNullOrEmpty(userName))
            {
                CPH.SetTwitchUserVar(userName, varName, amount, true);
                return true;
            }

            CPH.LogError("[Bankheist] Cannot set Streamer.bot user points: both userId and userName are empty.");
            return false;
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] Error writing Streamer.bot user points for '{userName}': {ex.Message}");
            return false;
        }
    }

    private long GetStreamElementsPoints(string userName)
    {
        string channelId = GetConfigString("heistStreamElementsChannelId", "").Trim();
        string jwt = GetConfigString("heistStreamElementsJwt", "").Trim();
        string user = NormalizeApiUserName(userName);

        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(jwt) || string.IsNullOrEmpty(user))
        {
            CPH.LogWarn("[Bankheist] StreamElements points selected, but channel ID, JWT, or username is missing.");
            return -1;
        }

        string url = "https://api.streamelements.com/kappa/v2/points/" + UrlEncode(channelId) + "/" + UrlEncode(user);
        string json = HttpRequest("GET", url, null, "application/json", jwt);
        if (string.IsNullOrEmpty(json))
            return -1;

        return ExtractJsonLong(json, "points", -1);
    }

    private bool StreamElementsAdjustPoints(string userName, long delta)
    {
        string channelId = GetConfigString("heistStreamElementsChannelId", "").Trim();
        string jwt = GetConfigString("heistStreamElementsJwt", "").Trim();
        string user = NormalizeApiUserName(userName);

        if (string.IsNullOrEmpty(channelId) || string.IsNullOrEmpty(jwt) || string.IsNullOrEmpty(user))
        {
            CPH.LogWarn("[Bankheist] StreamElements adjustment failed: channel ID, JWT, or username is missing.");
            return false;
        }

        string url = "https://api.streamelements.com/kappa/v2/points/"
            + UrlEncode(channelId) + "/"
            + UrlEncode(user) + "/"
            + delta.ToString(CultureInfo.InvariantCulture);

        string json = HttpRequest("PUT", url, null, null, jwt);
        DebugLog($"StreamElementsAdjustPoints: user='{user}', delta={delta}, result={(json != null ? "Success" : "Failed")}");
        return json != null;
    }

    private string HttpRequest(string method, string url, string body, string contentType, string bearerToken)
    {
        try
        {
            try
            {
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
            }
            catch
            {
                // Ignore if OS/runtime restricts manual TLS configuration
            }

            int timeoutMs = Math.Max(1000, GetConfigInt("heistPointsApiTimeoutMs", 7000));
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Accept = "application/json";
            request.UserAgent = "Streamer.bot Bankheist";
            request.Timeout = timeoutMs;
            request.ReadWriteTimeout = timeoutMs;

            if (!string.IsNullOrEmpty(bearerToken))
                request.Headers["Authorization"] = "Bearer " + bearerToken;

            if (body != null)
            {
                byte[] bytes = Encoding.UTF8.GetBytes(body);
                if (!string.IsNullOrEmpty(contentType))
                    request.ContentType = contentType;
                request.ContentLength = bytes.Length;

                if (bytes.Length > 0)
                {
                    using (Stream stream = request.GetRequestStream())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                    }
                }
            }

            using (var response = (HttpWebResponse)request.GetResponse())
            using (var responseStream = response.GetResponseStream())
            {
                if (responseStream == null)
                    return "";

                using (var reader = new StreamReader(responseStream, Encoding.UTF8))
                {
                    return reader.ReadToEnd();
                }
            }
        }
        catch (WebException ex)
        {
            string responseText = "";
            try
            {
                if (ex.Response != null)
                {
                    using (var errStream = ex.Response.GetResponseStream())
                    {
                        if (errStream != null)
                        {
                            using (var reader = new StreamReader(errStream, Encoding.UTF8))
                            {
                                responseText = reader.ReadToEnd();
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore secondary stream read errors
            }

            CPH.LogError($"[Bankheist] HTTP {method} failed: {ex.Message}{(string.IsNullOrEmpty(responseText) ? "" : " | " + responseText)}");
            return null;
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] HTTP {method} failed: {ex.Message}");
            return null;
        }
    }

    private string NormalizeApiUserName(string userName)
    {
        if (string.IsNullOrEmpty(userName))
            return "";

        string s = userName.Trim();
        if (s.StartsWith("@", StringComparison.Ordinal))
            s = s.Substring(1);

        return s.ToLowerInvariant();
    }

    private string UrlEncode(string value)
    {
        return Uri.EscapeDataString(value ?? "");
    }

    private long ExtractJsonLong(string json, string fieldName, long fallback)
    {
        if (string.IsNullOrEmpty(json))
            return fallback;

        try
        {
            JObject obj = JObject.Parse(json);
            JToken token = obj[fieldName];
            if (token != null && TryConvertToLong(token.ToString(), out long value))
                return value;
        }
        catch (Exception ex)
        {
            CPH.LogError($"[Bankheist] Failed to parse JSON field '{fieldName}': {ex.Message}");
        }

        return fallback;
    }

    private void DebugLog(string message)
    {
        string debug = GetOptionalConfigString("heistDebugApiErrors").ToLowerInvariant();
        if (debug == "true" || debug == "1" || debug == "yes")
            CPH.LogInfo("[Bankheist Debug] " + message);
    }

    // =========================================================================
    // LANGUAGE / MESSAGE HELPERS
    // =========================================================================
    private string GetLanguage()
    {
        string argLang = GetArgString("heistLanguage");
        if (!string.IsNullOrEmpty(argLang))
        {
            string normalizedArg = NormalizeLanguage(argLang);
            CPH.SetGlobalVar("heistLanguage", normalizedArg, true);
            return normalizedArg;
        }

        string globalLang = GetGlobalStringRaw("heistLanguage");
        if (!string.IsNullOrEmpty(globalLang))
            return NormalizeLanguage(globalLang);

        CPH.SetGlobalVar("heistLanguage", "de", true);
        return "de";
    }

    private string GetStoredLanguageFast()
    {
        string globalLang = GetGlobalStringRaw("heistLanguage");
        return !string.IsNullOrEmpty(globalLang) ? NormalizeLanguage(globalLang) : "de";
    }

    private string NormalizeLanguage(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return "de";

        string s = raw.Trim().ToLowerInvariant().Replace("_", "-");
        switch (s)
        {
            case "custom":
            case "eigen":
            case "benutzerdefiniert":
                return "custom";
            case "de":
            case "german":
            case "deutsch":
            case "deu":
                return "de";
            case "en":
            case "english":
            case "eng":
                return "en";
            case "es":
            case "spanish":
            case "espanol":
            case "español":
            case "esp":
                return "es";
            case "fr":
            case "french":
            case "francais":
            case "français":
            case "fra":
                return "fr";
            case "pt":
            case "ptbr":
            case "pt-br":
            case "portuguese":
            case "portugues":
            case "português":
            case "por":
                return "pt-br";
            case "tr":
            case "turkish":
            case "turkce":
            case "türkçe":
            case "tur":
                return "tr";
            case "nl":
            case "dutch":
            case "nederlands":
            case "nld":
                return "nl";
            case "pl":
            case "polish":
            case "polski":
            case "pol":
                return "pl";
            default:
                return "de";
        }
    }

    private string Msg(string key, string lang)
    {
        lang = NormalizeLanguage(lang);

        // Always allow heistWordPoints (e.g., "Rupees") to override the currency label in all modes
        if (key == "heistWordPoints")
        {
            string customWord = GetOptionalConfigString("heistWordPoints");
            if (!string.IsNullOrEmpty(customWord))
                return customWord;

            string fallbackLang = lang == "custom" ? "de" : lang;
            string builtInWord = GetBuiltInMessage("heistWordPoints", fallbackLang);
            return !string.IsNullOrEmpty(builtInWord) ? builtInWord : "points";
        }

        string template;

        // Native custom mode
        if (lang == "custom")
        {
            string customMsg = GetOptionalConfigString(key);
            if (!string.IsNullOrEmpty(customMsg))
            {
                template = customMsg;
            }
            else
            {
                string customMsgSuffix = GetOptionalConfigString(key + "_custom");
                if (!string.IsNullOrEmpty(customMsgSuffix))
                {
                    template = customMsgSuffix;
                }
                else
                {
                    string builtInFallback = GetBuiltInMessage(key, "de");
                    template = !string.IsNullOrEmpty(builtInFallback)
                        ? builtInFallback
                        : GetBuiltInMessage(key, "en");
                }
            }
        }
        else
        {
            // Built-in mode (de, en, es, fr, pt-br, tr, nl, pl)
            string builtIn = GetBuiltInMessage(key, lang);
            if (!string.IsNullOrEmpty(builtIn))
            {
                template = builtIn;
            }
            else
            {
                string english = GetBuiltInMessage(key, "en");
                template = !string.IsNullOrEmpty(english) ? english : key;
            }
        }

        return ApplyPointsWord(template, lang);
    }

    private string ApplyPointsWord(string template, string lang)
    {
        if (string.IsNullOrEmpty(template) || template.IndexOf("%pointsWord", StringComparison.Ordinal) < 0)
            return template;

        string customWord = GetOptionalConfigString("heistWordPoints");
        string effectiveLang = lang == "custom" ? "de" : lang;
        string defaultWord = GetBuiltInMessage("heistWordPoints", effectiveLang);
        if (string.IsNullOrEmpty(defaultWord))
            defaultWord = "points";

        string word = !string.IsNullOrEmpty(customWord) ? customWord : defaultWord;
        string wordDativeDe = !string.IsNullOrEmpty(customWord) ? customWord : "Punkten";
        string wordGenitivePl = !string.IsNullOrEmpty(customWord) ? customWord : "punktów";
        string wordPossessiveTr = !string.IsNullOrEmpty(customWord) ? customWord : "puanın";

        return template
            .Replace("%pointsWordDative%", wordDativeDe)
            .Replace("%pointsWordGenitive%", wordGenitivePl)
            .Replace("%pointsWordTrPossessive%", wordPossessiveTr)
            .Replace("%pointsWord%", word);
    }

    private string GetBuiltInMessage(string key, string lang)
    {
        switch (NormalizeLanguage(lang))
        {
            case "de":
                return GetBuiltInDe(key);
            case "en":
                return GetBuiltInEn(key);
            case "es":
                return GetBuiltInEs(key);
            case "fr":
                return GetBuiltInFr(key);
            case "pt-br":
                return GetBuiltInPtBr(key);
            case "tr":
                return GetBuiltInTr(key);
            case "nl":
                return GetBuiltInNl(key);
            case "pl":
                return GetBuiltInPl(key);
            default:
                return GetBuiltInEn(key);
        }
    }


    private string GetBuiltInDe(string key)
    {
        switch (key)
        {
            case "heistMsgSetupStarted":
                return "⚙️ Starte automatisches Heist-System Setup...";
            case "heistMsgSetupCompleted":
                return "✅ Setup abgeschlossen! Sprache und Punktesystem-Einstellungen wurden gespeichert.";
            case "heistMsgSetupDenied":
                return "❌ %user% Du hast keine Berechtigung, das Setup für %command% auszuführen! (Nur Moderatoren/Broadcaster)";
            case "heistMsgStart":
                return "🚨 BANKRAUB! %user% plant einen Raubzug! Tippe %command% <einsatz> um mitzumachen! Ihr habt %remainingTime% Sekunden Zeit!";
            case "heistMsgJoined":
                return "✅ %user% ist dem Raubzug mit %amount% %pointsWordDative% beigetreten!";
            case "heistMsgReminder":
                return "⏰ ACHTUNG: Der Raubzug kann wieder starten! Tippe %command% <einsatz> (%min% - %max%) zum Beitreten!";
            case "heistMsgNotEnoughPoints":
                return "❌ %user%, du hast nicht genügend %pointsWord%! Du besitzt: %currentPoints%.";
            case "heistMsgInvalidBet":
                return "❌ Ungültiger Einsatz! Der Einsatz muss zwischen %min% und %max% %pointsWordDative% liegen (oder 'all').";
            case "heistMsgCooldown":
                return "⏳ Die Bank ist nach dem letzten Überfall noch auf der Hut! Warte noch %cooldown% Sekunden.";
            case "heistMsgAlreadyIn":
                return "⚠️ %user%, du bist bereits beim Raubzug dabei!";
            case "heistMsgNotEnoughPlayers":
                return "❌ Der Raubzug wurde abgebrochen. Es wurden mindestens %minPlayers% Teilnehmer benötigt.";
            case "heistMsgProgress":
                return "💰 Die Crew ist in die Bank eingedrungen! Die Alarmanlage schlägt an...";
            case "heistMsgSuccess":
                return "🏆 DER RAUB WAR ERFOLGREICH! Entkommen: %survivors%. Gefasst: %caught%. Gesamtbeute: %payout% %pointsWord%!";
            case "heistMsgPartial":
                return "⚠️ DER RAUB WAR NUR TEILERFOLGREICH! Entkommen: %survivors%. Gefasst: %caught%. Erbeutete %pointsWord%: %payout%!";
            case "heistMsgFailed":
                return "💀 DER RAUB IST KOMPLETT GESCHEITERT! Die Polizeieinheiten haben alle Räuber (%caught%) gefasst! Die Bank bleibt gesichert.";
            case "heistMsgStatusActive":
                return "🔍 STATUS: Raubzug läuft! Verbleibende Beitrittszeit: %remainingTime%s | Dein Einsatz: %userBet% | Gesamtpott: %totalBet% %pointsWord% (%players% Räuber)";
            case "heistMsgStatusCooldown":
                return "⏳ STATUS: Aktuell läuft kein Raub. Die Bank ist noch %cooldown% Sekunden lang gesperrt.";
            case "heistMsgStatusIdle":
                return "ℹ️ Aktuell ist kein Raubzug aktiv. Starte einen mit %command% <einsatz> (%min% - %max% %pointsWord%)!";
            case "heistMsgNobodySurvivors":
                return "Niemand";
            case "heistMsgNobodyCaught":
                return "Niemand";
            case "heistMsgPointsApiError":
                return "❌ Das Punktesystem ist gerade nicht erreichbar oder nicht korrekt eingerichtet. Bitte später erneut versuchen.";
            case "heistMsgStatusNotJoined":
                return "Nicht dabei";
            case "heistMsgUpdateAvailable":
                return "🔔 Bankheist-Update verfügbar! Aktuell: v%currentVersion% → Neu: v%latestVersion% | %url%";
            case "heistMsgUpdateUpToDate":
                return "✅ Bankheist ist auf dem neuesten Stand (v%currentVersion%).";
            case "heistMsgUpdateCheckFailed":
                return "⚠️ Update-Prüfung fehlgeschlagen (aktuell: v%currentVersion%). Details im Streamer.bot-Log.";
            case "heistMsgUpdateDenied":
                return "❌ %user%, du hast keine Berechtigung, den Update-Check für %command% auszuführen! (Nur Moderatoren/Broadcaster)";
            case "heistMsgUpdateDownloadSuccess":
                return "📦 Update v%latestVersion% gespeichert (%filePath%) & in die Zwischenablage kopiert! In Streamer.bot: Import → Strg+V → Import.";
            case "heistMsgUpdateDownloadFileOnly":
                return "📦 Update v%latestVersion% gespeichert unter: %filePath% (Inhalt kopieren → Streamer.bot → Import).";
            case "heistMsgUpdateDownloadFailed":
                return "❌ Download von Update v%latestVersion% fehlgeschlagen. Details im Streamer.bot-Log.";
            case "heistMsgUpdateDownloadDenied":
                return "❌ %user%, nur der Broadcaster darf %command% getupdate ausführen!";
            case "heistWordPoints":
                return "Punkte";
            default:
                return "";
        }
    }

    private string GetBuiltInEn(string key)
    {
        switch (key)
        {
            case "heistMsgSetupStarted":
                return "⚙️ Starting automatic heist system setup...";
            case "heistMsgSetupCompleted":
                return "✅ Setup complete! Language and points-system settings have been saved.";
            case "heistMsgSetupDenied":
                return "❌ %user%, you are not allowed to run setup for %command%! (Moderators/Broadcaster only)";
            case "heistMsgStart":
                return "🚨 BANK HEIST! %user% is planning a robbery! Type %command% <bet> to join! You have %remainingTime% seconds!";
            case "heistMsgJoined":
                return "✅ %user% joined the heist with %amount% %pointsWord%!";
            case "heistMsgReminder":
                return "⏰ The bank can be robbed again! Type %command% <bet> (%min% - %max%) to join!";
            case "heistMsgNotEnoughPoints":
                return "❌ %user%, you don't have enough %pointsWord%! You currently have: %currentPoints%.";
            case "heistMsgInvalidBet":
                return "❌ Invalid bet! Your bet must be between %min% and %max% %pointsWord% (or 'all').";
            case "heistMsgCooldown":
                return "⏳ The bank is still on guard after the last heist! Wait %cooldown% more seconds.";
            case "heistMsgAlreadyIn":
                return "⚠️ %user%, you are already in the heist!";
            case "heistMsgNotEnoughPlayers":
                return "❌ The heist was cancelled. At least %minPlayers% participant(s) were required.";
            case "heistMsgProgress":
                return "💰 The crew has entered the bank! The alarm is going off...";
            case "heistMsgSuccess":
                return "🏆 THE HEIST WAS SUCCESSFUL! Escaped: %survivors%. Caught: %caught%. Total loot: %payout% %pointsWord%!";
            case "heistMsgPartial":
                return "⚠️ THE HEIST WAS ONLY PARTLY SUCCESSFUL! Escaped: %survivors%. Caught: %caught%. Looted %pointsWord%: %payout%!";
            case "heistMsgFailed":
                return "💀 THE HEIST FAILED COMPLETELY! The police caught every robber (%caught%)! The bank stays secure.";
            case "heistMsgStatusActive":
                return "🔍 STATUS: Heist in progress! Join time left: %remainingTime%s | Your bet: %userBet% | Total pot: %totalBet% %pointsWord% (%players% robbers)";
            case "heistMsgStatusCooldown":
                return "⏳ STATUS: No heist is running. The bank is locked down for %cooldown% more seconds.";
            case "heistMsgStatusIdle":
                return "ℹ️ No heist is active right now. Start one with %command% <bet> (%min% - %max% %pointsWord%)!";
            case "heistMsgNobodySurvivors":
                return "Nobody";
            case "heistMsgNobodyCaught":
                return "Nobody";
            case "heistMsgPointsApiError":
                return "❌ The points service is not available or is not configured correctly. Please try again later.";
            case "heistMsgStatusNotJoined":
                return "Not joined";
            case "heistMsgUpdateAvailable":
                return "🔔 Bankheist update available! Current: v%currentVersion% → Latest: v%latestVersion% | %url%";
            case "heistMsgUpdateUpToDate":
                return "✅ Bankheist is up to date (v%currentVersion%).";
            case "heistMsgUpdateCheckFailed":
                return "⚠️ Update check failed (current: v%currentVersion%). Check the Streamer.bot log for details.";
            case "heistMsgUpdateDenied":
                return "❌ %user%, you are not allowed to run the update check for %command%! (Moderators/Broadcaster only)";
            case "heistMsgUpdateDownloadSuccess":
                return "📦 Update v%latestVersion% saved (%filePath%) & copied to clipboard! In Streamer.bot: Import → Ctrl+V → Import.";
            case "heistMsgUpdateDownloadFileOnly":
                return "📦 Update v%latestVersion% saved to: %filePath% (copy contents → Streamer.bot → Import).";
            case "heistMsgUpdateDownloadFailed":
                return "❌ Failed to download update v%latestVersion%. Check the Streamer.bot log for details.";
            case "heistMsgUpdateDownloadDenied":
                return "❌ %user%, only the broadcaster is allowed to run %command% getupdate!";
            case "heistWordPoints":
                return "points";
            default:
                return "";
        }
    }

    private string GetBuiltInEs(string key)
    {
        switch (key)
        {
            case "heistMsgSetupStarted":
                return "⚙️ Iniciando la configuración automática del sistema de atracos...";
            case "heistMsgSetupCompleted":
                return "✅ Configuración completada. Se guardaron el idioma y el sistema de puntos.";
            case "heistMsgSetupDenied":
                return "❌ %user%, no tienes permiso para ejecutar la configuración de %command%. (Solo moderadores/broadcaster)";
            case "heistMsgStart":
                return "🚨 ¡ATRACO AL BANCO! %user% está planeando un golpe. Escribe %command% <apuesta> para unirte. ¡Tienes %remainingTime% segundos!";
            case "heistMsgJoined":
                return "✅ %user% se unió al atraco con %amount% %pointsWord%.";
            case "heistMsgReminder":
                return "⏰ ¡El banco puede volver a ser atracado! Escribe %command% <apuesta> (%min% - %max%) para unirte.";
            case "heistMsgNotEnoughPoints":
                return "❌ %user%, no tienes suficientes %pointsWord%. Tienes: %currentPoints%.";
            case "heistMsgInvalidBet":
                return "❌ Apuesta no válida. Debe estar entre %min% y %max% %pointsWord% (o 'all').";
            case "heistMsgCooldown":
                return "⏳ ¡El banco sigue en alerta tras el último atraco! Espera %cooldown% segundos más.";
            case "heistMsgAlreadyIn":
                return "⚠️ %user%, ya estás dentro del atraco.";
            case "heistMsgNotEnoughPlayers":
                return "❌ El atraco fue cancelado. Se necesitaban al menos %minPlayers% participante(s).";
            case "heistMsgProgress":
                return "💰 ¡La banda entró al banco! La alarma está sonando...";
            case "heistMsgSuccess":
                return "🏆 ¡EL ATRACO FUE UN ÉXITO! Escaparon: %survivors%. Capturados: %caught%. Botín total: %payout% %pointsWord%.";
            case "heistMsgPartial":
                return "⚠️ ¡EL ATRACO FUE PARCIALMENTE EXITOSO! Escaparon: %survivors%. Capturados: %caught%. Botín robado: %payout% %pointsWord%.";
            case "heistMsgFailed":
                return "💀 ¡EL ATRACO FRACASÓ POR COMPLETO! La policía atrapó a todos los ladrones (%caught%). El banco sigue protegido.";
            case "heistMsgStatusActive":
                return "🔍 ESTADO: ¡Atraco en curso! Tiempo para unirse: %remainingTime%s | Tu apuesta: %userBet% | Bote total: %totalBet% %pointsWord% (%players% ladrones)";
            case "heistMsgStatusCooldown":
                return "⏳ ESTADO: No hay ningún atraco activo. El banco seguirá bloqueado durante %cooldown% segundos.";
            case "heistMsgStatusIdle":
                return "ℹ️ No hay ningún atraco activo. Inicia uno con %command% <apuesta> (%min% - %max% %pointsWord%).";
            case "heistMsgNobodySurvivors":
                return "Nadie";
            case "heistMsgNobodyCaught":
                return "Nadie";
            case "heistMsgPointsApiError":
                return "❌ El servicio de puntos no está disponible o no está configurado correctamente. Inténtalo más tarde.";
            case "heistMsgStatusNotJoined":
                return "No unido";
            case "heistMsgUpdateAvailable":
                return "🔔 ¡Actualización de Bankheist disponible! Actual: v%currentVersion% → Nueva: v%latestVersion% | %url%";
            case "heistMsgUpdateUpToDate":
                return "✅ Bankheist está actualizado (v%currentVersion%).";
            case "heistMsgUpdateCheckFailed":
                return "⚠️ Error al comprobar actualizaciones (actual: v%currentVersion%). Revisa el registro de Streamer.bot.";
            case "heistMsgUpdateDenied":
                return "❌ %user%, no tienes permiso para comprobar actualizaciones de %command%. (Solo moderadores/broadcaster)";
            case "heistMsgUpdateDownloadSuccess":
                return "📦 Actualización v%latestVersion% guardada (%filePath%) y copiada al portapapeles. En Streamer.bot: Import → Ctrl+V → Import.";
            case "heistMsgUpdateDownloadFileOnly":
                return "📦 Actualización v%latestVersion% guardada en: %filePath% (copia el contenido → Streamer.bot → Import).";
            case "heistMsgUpdateDownloadFailed":
                return "❌ Error al descargar la actualización v%latestVersion%. Revisa el registro de Streamer.bot.";
            case "heistMsgUpdateDownloadDenied":
                return "❌ %user%, solo el broadcaster puede ejecutar %command% getupdate.";
            case "heistWordPoints":
                return "puntos";
            default:
                return "";
        }
    }

    private string GetBuiltInFr(string key)
    {
        switch (key)
        {
            case "heistMsgSetupStarted":
                return "⚙️ Lancement de la configuration automatique du système de braquage...";
            case "heistMsgSetupCompleted":
                return "✅ Configuration terminée ! La langue et le système de points ont été enregistrés.";
            case "heistMsgSetupDenied":
                return "❌ %user%, tu n'as pas l'autorisation de lancer la configuration de %command% ! (Modérateurs/Broadcaster uniquement)";
            case "heistMsgStart":
                return "🚨 BRAQUAGE DE BANQUE ! %user% prépare un coup ! Tape %command% <mise> pour participer ! Vous avez %remainingTime% secondes !";
            case "heistMsgJoined":
                return "✅ %user% rejoint le braquage avec %amount% %pointsWord% !";
            case "heistMsgReminder":
                return "⏰ La banque peut être braquée à nouveau ! Tape %command% <mise> (%min% - %max%) pour participer !";
            case "heistMsgNotEnoughPoints":
                return "❌ %user%, tu n'as pas assez de %pointsWord% ! Tu as actuellement : %currentPoints%.";
            case "heistMsgInvalidBet":
                return "❌ Mise invalide ! Elle doit être comprise entre %min% et %max% %pointsWord% (ou 'all').";
            case "heistMsgCooldown":
                return "⏳ La banque est encore sur ses gardes après le dernier braquage ! Attends encore %cooldown% secondes.";
            case "heistMsgAlreadyIn":
                return "⚠️ %user%, tu participes déjà au braquage !";
            case "heistMsgNotEnoughPlayers":
                return "❌ Le braquage a été annulé. Il fallait au moins %minPlayers% participant(s).";
            case "heistMsgProgress":
                return "💰 L'équipe est entrée dans la banque ! L'alarme se déclenche...";
            case "heistMsgSuccess":
                return "🏆 LE BRAQUAGE EST UNE RÉUSSITE ! Évadés : %survivors%. Arrêtés : %caught%. Butin total : %payout% %pointsWord% !";
            case "heistMsgPartial":
                return "⚠️ LE BRAQUAGE N'A RÉUSSI QU'EN PARTIE ! Évadés : %survivors%. Arrêtés : %caught%. Butin volé : %payout% %pointsWord% !";
            case "heistMsgFailed":
                return "💀 LE BRAQUAGE A COMPLÈTEMENT ÉCHOUÉ ! La police a arrêté tous les braqueurs (%caught%) ! La banque reste sécurisée.";
            case "heistMsgStatusActive":
                return "🔍 STATUT : Braquage en cours ! Temps restant pour rejoindre : %remainingTime%s | Ta mise : %userBet% | Pot total : %totalBet% %pointsWord% (%players% braqueurs)";
            case "heistMsgStatusCooldown":
                return "⏳ STATUT : Aucun braquage en cours. La banque est verrouillée encore %cooldown% secondes.";
            case "heistMsgStatusIdle":
                return "ℹ️ Aucun braquage actif pour le moment. Lance-en un avec %command% <mise> (%min% - %max% %pointsWord%) !";
            case "heistMsgNobodySurvivors":
                return "Personne";
            case "heistMsgNobodyCaught":
                return "Personne";
            case "heistMsgPointsApiError":
                return "❌ Le service de points n'est pas disponible ou n'est pas configuré correctement. Réessaie plus tard.";
            case "heistMsgStatusNotJoined":
                return "Pas inscrit";
            case "heistMsgUpdateAvailable":
                return "🔔 Mise à jour Bankheist disponible ! Actuelle : v%currentVersion% → Nouvelle : v%latestVersion% | %url%";
            case "heistMsgUpdateUpToDate":
                return "✅ Bankheist est à jour (v%currentVersion%).";
            case "heistMsgUpdateCheckFailed":
                return "⚠️ Échec de la vérification de mise à jour (actuelle : v%currentVersion%). Voir les logs Streamer.bot.";
            case "heistMsgUpdateDenied":
                return "❌ %user%, tu n'as pas l'autorisation de vérifier les mises à jour pour %command% ! (Modérateurs/Broadcaster uniquement)";
            case "heistMsgUpdateDownloadSuccess":
                return "📦 Mise à jour v%latestVersion% enregistrée (%filePath%) et copiée dans le presse-papiers ! Dans Streamer.bot : Import → Ctrl+V → Import.";
            case "heistMsgUpdateDownloadFileOnly":
                return "📦 Mise à jour v%latestVersion% enregistrée dans : %filePath% (copie le contenu → Streamer.bot → Import).";
            case "heistMsgUpdateDownloadFailed":
                return "❌ Échec du téléchargement de la mise à jour v%latestVersion%. Voir les logs Streamer.bot.";
            case "heistMsgUpdateDownloadDenied":
                return "❌ %user%, seul le broadcaster peut exécuter %command% getupdate !";
            case "heistWordPoints":
                return "points";
            default:
                return "";
        }
    }

    private string GetBuiltInPtBr(string key)
    {
        switch (key)
        {
            case "heistMsgSetupStarted":
                return "⚙️ Iniciando a configuração automática do sistema de assalto...";
            case "heistMsgSetupCompleted":
                return "✅ Configuração concluída! Idioma e sistema de pontos foram salvos.";
            case "heistMsgSetupDenied":
                return "❌ %user%, você não tem permissão para executar a configuração de %command%! (Somente moderadores/broadcaster)";
            case "heistMsgStart":
                return "🚨 ASSALTO AO BANCO! %user% está planejando um roubo! Digite %command% <aposta> para participar! Vocês têm %remainingTime% segundos!";
            case "heistMsgJoined":
                return "✅ %user% entrou no assalto com %amount% %pointsWord%!";
            case "heistMsgReminder":
                return "⏰ O banco já pode ser assaltado de novo! Digite %command% <aposta> (%min% - %max%) para participar!";
            case "heistMsgNotEnoughPoints":
                return "❌ %user%, você não tem %pointsWord% suficientes! Você tem: %currentPoints%.";
            case "heistMsgInvalidBet":
                return "❌ Aposta inválida! A aposta deve ficar entre %min% e %max% %pointsWord% (ou 'all').";
            case "heistMsgCooldown":
                return "⏳ O banco ainda está em alerta depois do último assalto! Aguarde mais %cooldown% segundos.";
            case "heistMsgAlreadyIn":
                return "⚠️ %user%, você já está participando do assalto!";
            case "heistMsgNotEnoughPlayers":
                return "❌ O assalto foi cancelado. Eram necessários pelo menos %minPlayers% participante(s).";
            case "heistMsgProgress":
                return "💰 A equipe entrou no banco! O alarme disparou...";
            case "heistMsgSuccess":
                return "🏆 O ASSALTO FOI UM SUCESSO! Escaparam: %survivors%. Presos: %caught%. Saque total: %payout% %pointsWord%!";
            case "heistMsgPartial":
                return "⚠️ O ASSALTO TEVE SUCESSO PARCIAL! Escaparam: %survivors%. Presos: %caught%. Saque roubado: %payout% %pointsWord%!";
            case "heistMsgFailed":
                return "💀 O ASSALTO FALHOU COMPLETAMENTE! A polícia pegou todos os ladrões (%caught%)! O banco continua seguro.";
            case "heistMsgStatusActive":
                return "🔍 STATUS: Assalto em andamento! Tempo para entrar: %remainingTime%s | Sua aposta: %userBet% | Pote total: %totalBet% %pointsWord% (%players% ladrões)";
            case "heistMsgStatusCooldown":
                return "⏳ STATUS: Nenhum assalto em andamento. O banco ficará bloqueado por mais %cooldown% segundos.";
            case "heistMsgStatusIdle":
                return "ℹ️ Nenhum assalto ativo no momento. Comece um com %command% <aposta> (%min% - %max% %pointsWord%)!";
            case "heistMsgNobodySurvivors":
                return "Ninguém";
            case "heistMsgNobodyCaught":
                return "Ninguém";
            case "heistMsgPointsApiError":
                return "❌ O serviço de pontos não está disponível ou não foi configurado corretamente. Tente novamente mais tarde.";
            case "heistMsgStatusNotJoined":
                return "Não entrou";
            case "heistMsgUpdateAvailable":
                return "🔔 Atualização do Bankheist disponível! Atual: v%currentVersion% → Nova: v%latestVersion% | %url%";
            case "heistMsgUpdateUpToDate":
                return "✅ O Bankheist está atualizado (v%currentVersion%).";
            case "heistMsgUpdateCheckFailed":
                return "⚠️ Falha ao verificar atualizações (atual: v%currentVersion%). Veja o log do Streamer.bot.";
            case "heistMsgUpdateDenied":
                return "❌ %user%, você não tem permissão para verificar atualizações de %command%! (Somente moderadores/broadcaster)";
            case "heistMsgUpdateDownloadSuccess":
                return "📦 Atualização v%latestVersion% salva (%filePath%) e copiada para a área de transferência! No Streamer.bot: Import → Ctrl+V → Import.";
            case "heistMsgUpdateDownloadFileOnly":
                return "📦 Atualização v%latestVersion% salva em: %filePath% (copie o conteúdo → Streamer.bot → Import).";
            case "heistMsgUpdateDownloadFailed":
                return "❌ Falha ao baixar a atualização v%latestVersion%. Veja o log do Streamer.bot.";
            case "heistMsgUpdateDownloadDenied":
                return "❌ %user%, somente o broadcaster pode executar %command% getupdate!";
            case "heistWordPoints":
                return "pontos";
            default:
                return "";
        }
    }

    private string GetBuiltInTr(string key)
    {
        switch (key)
        {
            case "heistMsgSetupStarted":
                return "⚙️ Otomatik soygun sistemi kurulumu başlatılıyor...";
            case "heistMsgSetupCompleted":
                return "✅ Kurulum tamamlandı! Dil ve puan sistemi ayarları kaydedildi.";
            case "heistMsgSetupDenied":
                return "❌ %user%, %command% kurulumunu çalıştırma iznin yok! (Sadece moderatörler/yayıncı)";
            case "heistMsgStart":
                return "🚨 BANKA SOYGUNU! %user% bir soygun planlıyor! Katılmak için %command% <bahis> yaz! %remainingTime% saniyeniz var!";
            case "heistMsgJoined":
                return "✅ %user% soyguna %amount% %pointsWord% ile katıldı!";
            case "heistMsgReminder":
                return "⏰ Banka tekrar soyulabilir! Katılmak için %command% <bahis> (%min% - %max%) yaz!";
            case "heistMsgNotEnoughPoints":
                return "❌ %user%, yeterli %pointsWordTrPossessive% yok! Şu anki: %currentPoints%.";
            case "heistMsgInvalidBet":
                return "❌ Geçersiz bahis! Bahis %min% ile %max% %pointsWord% arasında olmalı (veya 'all').";
            case "heistMsgCooldown":
                return "⏳ Banka son soygundan sonra hâlâ alarmda! %cooldown% saniye daha bekle.";
            case "heistMsgAlreadyIn":
                return "⚠️ %user%, zaten soyguna katıldın!";
            case "heistMsgNotEnoughPlayers":
                return "❌ Soygun iptal edildi. En az %minPlayers% katılımcı gerekiyordu.";
            case "heistMsgProgress":
                return "💰 Ekip bankaya girdi! Alarm çalmaya başladı...";
            case "heistMsgSuccess":
                return "🏆 SOYGUN BAŞARILI! Kaçanlar: %survivors%. Yakalananlar: %caught%. Toplam ganimet: %payout% %pointsWord%!";
            case "heistMsgPartial":
                return "⚠️ SOYGUN KISMEN BAŞARILI! Kaçanlar: %survivors%. Yakalananlar: %caught%. Çalınan %pointsWord%: %payout%!";
            case "heistMsgFailed":
                return "💀 SOYGUN TAMAMEN BAŞARISIZ OLDU! Polis tüm soyguncuları (%caught%) yakaladı! Banka güvende kaldı.";
            case "heistMsgStatusActive":
                return "🔍 DURUM: Soygun devam ediyor! Katılım süresi: %remainingTime%s | Bahsin: %userBet% | Toplam kasa: %totalBet% %pointsWord% (%players% soyguncu)";
            case "heistMsgStatusCooldown":
                return "⏳ DURUM: Şu anda soygun yok. Banka %cooldown% saniye daha kilitli.";
            case "heistMsgStatusIdle":
                return "ℹ️ Şu anda aktif soygun yok. %command% <bahis> (%min% - %max% %pointsWord%) ile başlat!";
            case "heistMsgNobodySurvivors":
                return "Kimse";
            case "heistMsgNobodyCaught":
                return "Kimse";
            case "heistMsgPointsApiError":
                return "❌ Puan servisi şu anda kullanılamıyor veya doğru yapılandırılmamış. Lütfen daha sonra tekrar dene.";
            case "heistMsgStatusNotJoined":
                return "Katılmadı";
            case "heistMsgUpdateAvailable":
                return "🔔 Bankheist güncellemesi mevcut! Mevcut: v%currentVersion% → Yeni: v%latestVersion% | %url%";
            case "heistMsgUpdateUpToDate":
                return "✅ Bankheist güncel (v%currentVersion%).";
            case "heistMsgUpdateCheckFailed":
                return "⚠️ Güncelleme kontrolü başarısız (mevcut: v%currentVersion%). Detaylar Streamer.bot günlüğünde.";
            case "heistMsgUpdateDenied":
                return "❌ %user%, %command% güncelleme kontrolünü çalıştırma iznin yok! (Sadece moderatörler/yayıncı)";
            case "heistMsgUpdateDownloadSuccess":
                return "📦 v%latestVersion% güncellemesi kaydedildi (%filePath%) ve panoya kopyalandı! Streamer.bot: Import → Ctrl+V → Import.";
            case "heistMsgUpdateDownloadFileOnly":
                return "📦 v%latestVersion% güncellemesi kaydedildi: %filePath% (içeriği kopyala → Streamer.bot → Import).";
            case "heistMsgUpdateDownloadFailed":
                return "❌ v%latestVersion% güncellemesi indirilemedi. Detaylar Streamer.bot günlüğünde.";
            case "heistMsgUpdateDownloadDenied":
                return "❌ %user%, %command% getupdate komutunu sadece yayıncı çalıştırabilir!";
            case "heistWordPoints":
                return "puan";
            default:
                return "";
        }
    }

    private string GetBuiltInNl(string key)
    {
        switch (key)
        {
            case "heistMsgSetupStarted":
                return "⚙️ Automatische bankoverval-setup wordt gestart...";
            case "heistMsgSetupCompleted":
                return "✅ Setup voltooid! Taal en puntensysteem zijn opgeslagen.";
            case "heistMsgSetupDenied":
                return "❌ %user%, je mag de setup voor %command% niet uitvoeren! (Alleen moderators/broadcaster)";
            case "heistMsgStart":
                return "🚨 BANKOVERVAL! %user% plant een overval! Typ %command% <inzet> om mee te doen! Jullie hebben %remainingTime% seconden!";
            case "heistMsgJoined":
                return "✅ %user% doet mee aan de overval met %amount% %pointsWord%!";
            case "heistMsgReminder":
                return "⏰ De bank kan weer worden overvallen! Typ %command% <inzet> (%min% - %max%) om mee te doen!";
            case "heistMsgNotEnoughPoints":
                return "❌ %user%, je hebt niet genoeg %pointsWord%! Je hebt nu: %currentPoints%.";
            case "heistMsgInvalidBet":
                return "❌ Ongeldige inzet! De inzet moet tussen %min% en %max% %pointsWord% liggen (of 'all').";
            case "heistMsgCooldown":
                return "⏳ De bank is na de laatste overval nog op zijn hoede! Wacht nog %cooldown% seconden.";
            case "heistMsgAlreadyIn":
                return "⚠️ %user%, je doet al mee aan de overval!";
            case "heistMsgNotEnoughPlayers":
                return "❌ De overval is geannuleerd. Er waren minimaal %minPlayers% deelnemer(s) nodig.";
            case "heistMsgProgress":
                return "💰 De crew is de bank binnen! Het alarm gaat af...";
            case "heistMsgSuccess":
                return "🏆 DE OVERVAL IS GELUKT! Ontsnapt: %survivors%. Gepakt: %caught%. Totale buit: %payout% %pointsWord%!";
            case "heistMsgPartial":
                return "⚠️ DE OVERVAL WAS GEDEELTELIJK GELUKT! Ontsnapt: %survivors%. Gepakt: %caught%. Gestolen %pointsWord%: %payout%!";
            case "heistMsgFailed":
                return "💀 DE OVERVAL IS VOLLEDIG MISLUKT! De politie heeft alle overvallers (%caught%) gepakt! De bank blijft beveiligd.";
            case "heistMsgStatusActive":
                return "🔍 STATUS: Overval bezig! Tijd om mee te doen: %remainingTime%s | Jouw inzet: %userBet% | Totale pot: %totalBet% %pointsWord% (%players% overvallers)";
            case "heistMsgStatusCooldown":
                return "⏳ STATUS: Er loopt geen overval. De bank is nog %cooldown% seconden gesloten.";
            case "heistMsgStatusIdle":
                return "ℹ️ Er is nu geen overval actief. Start er een met %command% <inzet> (%min% - %max% %pointsWord%)!";
            case "heistMsgNobodySurvivors":
                return "Niemand";
            case "heistMsgNobodyCaught":
                return "Niemand";
            case "heistMsgPointsApiError":
                return "❌ De puntenservice is niet beschikbaar of niet goed ingesteld. Probeer het later opnieuw.";
            case "heistMsgStatusNotJoined":
                return "Niet meegedaan";
            case "heistMsgUpdateAvailable":
                return "🔔 Bankheist-update beschikbaar! Huidig: v%currentVersion% → Nieuw: v%latestVersion% | %url%";
            case "heistMsgUpdateUpToDate":
                return "✅ Bankheist is up-to-date (v%currentVersion%).";
            case "heistMsgUpdateCheckFailed":
                return "⚠️ Updatecontrole mislukt (huidig: v%currentVersion%). Bekijk het Streamer.bot-logboek.";
            case "heistMsgUpdateDenied":
                return "❌ %user%, je mag de updatecontrole voor %command% niet uitvoeren! (Alleen moderators/broadcaster)";
            case "heistMsgUpdateDownloadSuccess":
                return "📦 Update v%latestVersion% opgeslagen (%filePath%) en naar het klembord gekopieerd! In Streamer.bot: Import → Ctrl+V → Import.";
            case "heistMsgUpdateDownloadFileOnly":
                return "📦 Update v%latestVersion% opgeslagen in: %filePath% (kopieer de inhoud → Streamer.bot → Import).";
            case "heistMsgUpdateDownloadFailed":
                return "❌ Downloaden van update v%latestVersion% mislukt. Bekijk het Streamer.bot-logboek.";
            case "heistMsgUpdateDownloadDenied":
                return "❌ %user%, alleen de broadcaster mag %command% getupdate uitvoeren!";
            case "heistWordPoints":
                return "punten";
            default:
                return "";
        }
    }

    private string GetBuiltInPl(string key)
    {
        switch (key)
        {
            case "heistMsgSetupStarted":
                return "⚙️ Uruchamianie automatycznej konfiguracji napadu...";
            case "heistMsgSetupCompleted":
                return "✅ Konfiguracja zakończona! Język i system punktów zostały zapisane.";
            case "heistMsgSetupDenied":
                return "❌ %user%, nie masz uprawnień do uruchomienia konfiguracji %command%! (Tylko moderatorzy/broadcaster)";
            case "heistMsgStart":
                return "🚨 NAPAD NA BANK! %user% planuje skok! Wpisz %command% <stawka>, aby dołączyć! Macie %remainingTime% sekund!";
            case "heistMsgJoined":
                return "✅ %user% dołącza do napadu ze stawką %amount% %pointsWordGenitive%!";
            case "heistMsgReminder":
                return "⏰ Bank można znowu obrabować! Wpisz %command% <stawka> (%min% - %max%), aby dołączyć!";
            case "heistMsgNotEnoughPoints":
                return "❌ %user%, nie masz wystarczająco %pointsWordGenitive%! Masz teraz: %currentPoints%.";
            case "heistMsgInvalidBet":
                return "❌ Nieprawidłowa stawka! Musi wynosić od %min% do %max% %pointsWordGenitive% (albo 'all').";
            case "heistMsgCooldown":
                return "⏳ Bank nadal jest czujny po ostatnim napadzie! Poczekaj jeszcze %cooldown% sekund.";
            case "heistMsgAlreadyIn":
                return "⚠️ %user%, już bierzesz udział w napadzie!";
            case "heistMsgNotEnoughPlayers":
                return "❌ Napad został anulowany. Wymagano co najmniej %minPlayers% uczestnika/uczestników.";
            case "heistMsgProgress":
                return "💰 Ekipa weszła do banku! Alarm się włączył...";
            case "heistMsgSuccess":
                return "🏆 NAPAD SIĘ UDAŁ! Uciekli: %survivors%. Złapani: %caught%. Łączny łup: %payout% %pointsWordGenitive%!";
            case "heistMsgPartial":
                return "⚠️ NAPAD UDAŁ SIĘ CZĘŚCIOWO! Uciekli: %survivors%. Złapani: %caught%. Zdobyte %pointsWord%: %payout%!";
            case "heistMsgFailed":
                return "💀 NAPAD CAŁKOWICIE SIĘ NIE UDAŁ! Policja złapała wszystkich rabusiów (%caught%)! Bank pozostaje bezpieczny.";
            case "heistMsgStatusActive":
                return "🔍 STATUS: Napad trwa! Czas na dołączenie: %remainingTime%s | Twoja stawka: %userBet% | Cała pula: %totalBet% %pointsWordGenitive% (%players% rabusiów)";
            case "heistMsgStatusCooldown":
                return "⏳ STATUS: Żaden napad nie trwa. Bank będzie zamknięty jeszcze przez %cooldown% sekund.";
            case "heistMsgStatusIdle":
                return "ℹ️ Teraz nie ma aktywnego napadu. Rozpocznij go przez %command% <stawka> (%min% - %max% %pointsWordGenitive%)!";
            case "heistMsgNobodySurvivors":
                return "Nikt";
            case "heistMsgNobodyCaught":
                return "Nikt";
            case "heistMsgPointsApiError":
                return "❌ Usługa punktów jest niedostępna albo nie jest poprawnie skonfigurowana. Spróbuj ponownie później.";
            case "heistMsgStatusNotJoined":
                return "Nie dołączono";
            case "heistMsgUpdateAvailable":
                return "🔔 Dostępna aktualizacja Bankheist! Obecna: v%currentVersion% → Nowa: v%latestVersion% | %url%";
            case "heistMsgUpdateUpToDate":
                return "✅ Bankheist jest aktualny (v%currentVersion%).";
            case "heistMsgUpdateCheckFailed":
                return "⚠️ Sprawdzanie aktualizacji nie powiodło się (obecna: v%currentVersion%). Sprawdź log Streamer.bot.";
            case "heistMsgUpdateDenied":
                return "❌ %user%, nie masz uprawnień do sprawdzenia aktualizacji %command%! (Tylko moderatorzy/broadcaster)";
            case "heistMsgUpdateDownloadSuccess":
                return "📦 Aktualizacja v%latestVersion% zapisana (%filePath%) i skopiowana do schowka! W Streamer.bot: Import → Ctrl+V → Import.";
            case "heistMsgUpdateDownloadFileOnly":
                return "📦 Aktualizacja v%latestVersion% zapisana w: %filePath% (skopiuj zawartość → Streamer.bot → Import).";
            case "heistMsgUpdateDownloadFailed":
                return "❌ Pobieranie aktualizacji v%latestVersion% nie powiodło się. Sprawdź log Streamer.bot.";
            case "heistMsgUpdateDownloadDenied":
                return "❌ %user%, tylko broadcaster może uruchomić %command% getupdate!";
            case "heistWordPoints":
                return "punkty";
            default:
                return "";
        }
    }

    // =========================================================================
    // ARGUMENT & CONFIG HELPERS
    // =========================================================================
    private string GetArgString(string key)
    {
        try
        {
            if (CPH.TryGetArg(key, out string strVal) && strVal != null)
                return strVal.Trim();

            if (CPH.TryGetArg(key, out object objVal) && objVal != null)
                return objVal.ToString().Trim();
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Failed to read argument '{key}': {ex.Message}");
        }

        return "";
    }

    private bool IsArgTrue(string key)
    {
        try
        {
            if (CPH.TryGetArg(key, out bool boolVal))
                return boolVal;

            string strVal = GetArgString(key).ToLowerInvariant();
            return strVal == "true" || strVal == "1";
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Failed to evaluate bool argument '{key}': {ex.Message}");
            return false;
        }
    }

    private string GetGlobalStringRaw(string key)
    {
        try
        {
            string raw = CPH.GetGlobalVar<string>(key, true);
            if (!string.IsNullOrEmpty(raw))
            {
                string val = raw.Trim();
                if (!string.IsNullOrEmpty(val))
                    return val;
            }

            string rawNonPersisted = CPH.GetGlobalVar<string>(key, false);
            if (!string.IsNullOrEmpty(rawNonPersisted))
            {
                string val = rawNonPersisted.Trim();
                if (!string.IsNullOrEmpty(val))
                    return val;
            }
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Failed to read global '{key}': {ex.Message}");
        }

        return "";
    }

    private string GetOptionalConfigString(string key)
    {
        try
        {
            string argVal = GetArgString(key);
            if (!string.IsNullOrEmpty(argVal))
            {
                CPH.SetGlobalVar(key, argVal, true);
                return argVal;
            }

            string globalVal = GetGlobalStringRaw(key);
            if (!string.IsNullOrEmpty(globalVal))
                return globalVal;
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Failed to read optional config '{key}': {ex.Message}");
        }

        return "";
    }

    private string GetConfigString(string key, string defaultValue)
    {
        try
        {
            string argVal = GetArgString(key);
            if (!string.IsNullOrEmpty(argVal))
            {
                CPH.SetGlobalVar(key, argVal, true);
                return argVal;
            }

            string globalVal = GetGlobalStringRaw(key);
            if (!string.IsNullOrEmpty(globalVal))
                return globalVal;

            CPH.SetGlobalVar(key, defaultValue, true);
            return defaultValue;
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Failed to read string config '{key}': {ex.Message}");
            return defaultValue;
        }
    }

    private long GetConfigLong(string key, long defaultValue)
    {
        try
        {
            string argVal = GetArgString(key);
            if (!string.IsNullOrEmpty(argVal))
            {
                if (TryConvertToLong(argVal, out long parsedArg))
                {
                    CPH.SetGlobalVar(key, parsedArg, true);
                    return parsedArg;
                }

                CPH.LogWarn($"[Bankheist] Invalid long argument '{key}'='{argVal}', falling back.");
            }

            string raw = GetGlobalStringRaw(key);
            if (!string.IsNullOrEmpty(raw) && TryConvertToLong(raw, out long parsedGlobal))
                return parsedGlobal;

            CPH.SetGlobalVar(key, defaultValue, true);
            return defaultValue;
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Failed to read long config '{key}': {ex.Message}");
            return defaultValue;
        }
    }

    private int GetConfigInt(string key, int defaultValue)
    {
        try
        {
            string argVal = GetArgString(key);
            if (!string.IsNullOrEmpty(argVal))
            {
                if (TryConvertToInt(argVal, out int parsedArg))
                {
                    CPH.SetGlobalVar(key, parsedArg, true);
                    return parsedArg;
                }

                CPH.LogWarn($"[Bankheist] Invalid int argument '{key}'='{argVal}', falling back.");
            }

            string raw = GetGlobalStringRaw(key);
            if (!string.IsNullOrEmpty(raw) && TryConvertToInt(raw, out int parsedGlobal))
                return parsedGlobal;

            CPH.SetGlobalVar(key, defaultValue, true);
            return defaultValue;
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Failed to read int config '{key}': {ex.Message}");
            return defaultValue;
        }
    }

    private double GetConfigDouble(string key, double defaultValue)
    {
        try
        {
            string argVal = GetArgString(key);
            if (!string.IsNullOrEmpty(argVal))
            {
                if (TryConvertToDouble(argVal, out double parsedArg))
                {
                    CPH.SetGlobalVar(key, parsedArg.ToString(CultureInfo.InvariantCulture), true);
                    return parsedArg;
                }

                CPH.LogWarn($"[Bankheist] Invalid double argument '{key}'='{argVal}', falling back.");
            }

            string raw = GetGlobalStringRaw(key);
            if (!string.IsNullOrEmpty(raw) && TryConvertToDouble(raw, out double parsedGlobal))
                return parsedGlobal;

            CPH.SetGlobalVar(key, defaultValue.ToString(CultureInfo.InvariantCulture), true);
            return defaultValue;
        }
        catch (Exception ex)
        {
            CPH.LogWarn($"[Bankheist] Failed to read double config '{key}': {ex.Message}");
            return defaultValue;
        }
    }

    private void SyncOptionalStringArg(string key)
    {
        string argVal = GetArgString(key);
        if (!string.IsNullOrEmpty(argVal))
            CPH.SetGlobalVar(key, argVal, true);
    }

    private void SyncOptionalLongArg(string key)
    {
        string argVal = GetArgString(key);
        if (string.IsNullOrEmpty(argVal))
            return;

        if (TryConvertToLong(argVal, out long parsed))
            CPH.SetGlobalVar(key, parsed, true);
        else
            CPH.LogWarn($"[Bankheist] Invalid long argument '{key}'='{argVal}', keeping previous value.");
    }

    private void SyncOptionalIntArg(string key)
    {
        string argVal = GetArgString(key);
        if (string.IsNullOrEmpty(argVal))
            return;

        if (TryConvertToInt(argVal, out int parsed))
            CPH.SetGlobalVar(key, parsed, true);
        else
            CPH.LogWarn($"[Bankheist] Invalid int argument '{key}'='{argVal}', keeping previous value.");
    }

    private void SyncOptionalDoubleArg(string key)
    {
        string argVal = GetArgString(key);
        if (string.IsNullOrEmpty(argVal))
            return;

        if (TryConvertToDouble(argVal, out double parsed))
            CPH.SetGlobalVar(key, parsed.ToString(CultureInfo.InvariantCulture), true);
        else
            CPH.LogWarn($"[Bankheist] Invalid double argument '{key}'='{argVal}', keeping previous value.");
    }

    private string CleanNumericString(object raw)
    {
        if (raw == null)
            return "";

        return raw.ToString().Trim().Trim('"', '\'').Trim();
    }

    private bool TryConvertToLong(object raw, out long value)
    {
        value = 0;
        if (raw == null)
            return false;

        if (raw is long l)
        {
            value = l;
            return true;
        }

        if (raw is int i)
        {
            value = i;
            return true;
        }

        string str = CleanNumericString(raw);
        return long.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private bool TryConvertToInt(object raw, out int value)
    {
        value = 0;
        if (raw == null)
            return false;

        if (raw is int i)
        {
            value = i;
            return true;
        }

        string str = CleanNumericString(raw);
        return int.TryParse(str, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }

    private bool TryConvertToDouble(object raw, out double value)
    {
        value = 0;
        if (raw == null)
            return false;

        if (raw is double d)
        {
            value = d;
            return true;
        }

        string str = CleanNumericString(raw).Replace(',', '.');
        return double.TryParse(str, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }
}
